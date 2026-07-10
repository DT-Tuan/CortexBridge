using CortexBridge.Api.Common;
using CortexBridge.Api.Sessions;
using CortexBridge.Api.Tmux;

namespace CortexBridge.Api.Endpoints;

public static class SessionsEndpoints
{
    public static void MapSessions(this IEndpointRouteBuilder app)
    {
        // GET /api/sessions — merged list:
        //   - one entry per project that has CC sessions (JSONL in ~/.claude/projects/)
        //   - PLUS workspace dirs without sessions yet, so user can spawn CC from PWA
        //
        // Status: "running" (tmux window exists), "stopped" (JSONL exists but no tmux),
        //         "none" (workspace dir exists, never started CC here).
        app.MapGet("/api/sessions", async (
            SessionScanner scanner,
            SessionStateRegistry state,
            TmuxClient tmux,
            BridgePaths paths,
            CortexBridge.Api.Data.BridgeDbContext db,
            CancellationToken ct) =>
        {
            var actives = await scanner.ScanAsync(ct);
            // Resolve currently running tmux windows once for all projects (cheaper than per-call)
            List<string> runningWindows;
            try { runningWindows = await tmux.ListWindowsAsync(ct); }
            catch (TmuxException) { runningWindows = new List<string>(); }
            var runningSet = new HashSet<string>(runningWindows, StringComparer.OrdinalIgnoreCase);

            // All ownership markers in one query; owner is then derived per
            // project via the shared SessionOwnershipRegistry.Derive (same logic
            // as /owner + the SSE owner_change path — single source of truth).
            var ownershipRows = await Microsoft.EntityFrameworkCore
                .EntityFrameworkQueryableExtensions
                .ToListAsync(db.SessionOwnerships, ct);
            var rowByProject = ownershipRows.ToDictionary(
                r => r.ProjectId, r => r, StringComparer.OrdinalIgnoreCase);

            var items = new List<SessionListItem>();
            foreach (var s in actives)
            {
                var running = runningSet.Contains(s.ProjectId);
                // Tail-read the active JSONL to learn the live writer (cheap,
                // ≤256 KB) — distinguishes a PC native-ext session from a
                // bridge tmux one even when a stale tmux window coexists.
                var (entrypoint, lastTs) =
                    await SessionScanner.ReadLastRecordMetaAsync(s.JsonlPath, ct);
                rowByProject.TryGetValue(s.ProjectId, out var row);
                var owner = SessionOwnershipRegistry.Derive(row, entrypoint, lastTs, running);
                items.Add(new SessionListItem(
                    ProjectId: s.ProjectId,
                    SessionUuid: s.SessionUuid,
                    LastMessageAt: s.LastModified.ToString("o"),
                    Status: running ? "running" : "stopped",
                    NeedsInput: state.NeedsInput(s.ProjectId),
                    Owner: SessionOwnershipRegistry.ToWire(owner)));
            }

            string OwnerOf(string projectId)
            {
                rowByProject.TryGetValue(projectId, out var row);
                return SessionOwnershipRegistry.ToWire(
                    SessionOwnershipRegistry.Derive(
                        row, null, null, runningSet.Contains(projectId)));
            }

            // Merge in workspace dirs that have no JSONL yet (status="none").
            // Skip dotfiles (./.claude, ./.pnpm-store) and dirs already represented.
            if (Directory.Exists(paths.WorkspaceRoot))
            {
                var seen = new HashSet<string>(items.Select(i => i.ProjectId), StringComparer.OrdinalIgnoreCase);
                foreach (var dir in Directory.EnumerateDirectories(paths.WorkspaceRoot))
                {
                    var name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;
                    if (seen.Contains(name)) continue;
                    items.Add(new SessionListItem(
                        ProjectId: name,
                        SessionUuid: null,
                        LastMessageAt: null,
                        Status: runningSet.Contains(name) ? "running" : "none",
                        NeedsInput: false,
                        Owner: OwnerOf(name)));
                }
            }

            // Order: running first, then stopped (newest first), then none (alpha).
            var ordered = items
                .OrderBy(i => i.Status switch { "running" => 0, "stopped" => 1, "none" => 2, _ => 3 })
                .ThenByDescending(i => i.LastMessageAt ?? string.Empty)
                .ThenBy(i => i.ProjectId)
                .ToList();
            return Results.Json(ordered, Json.Default);
        });

        // GET /api/sessions/:projectId[?session=<uuid>] — transcript of either
        // the currently-active session (default) OR a specific historical session.
        // Spec 04 §"Endpoints". readOnly=true when ?session= addresses a non-active
        // session, telling the PWA to hide the composer.
        app.MapGet("/api/sessions/{projectId}", async (
            string projectId,
            HttpContext ctx,
            SessionScanner scanner,
            JsonlReader reader,
            TmuxClient tmux,
            CancellationToken ct) =>
        {
            var requestedSession = ctx.Request.Query["session"].ToString();
            // ?limit=N → tail-load only the last N records (huge transcripts: a
            // 37 MB session was a 57 MB payload; PWA opens on the tail, "load full
            // history" re-fetches without a limit). limit<=0/absent = full read.
            int.TryParse(ctx.Request.Query["limit"].ToString(), out var limit);
            var active = await scanner.ResolveAsync(projectId, ct);
            var tmuxAlive = await tmux.WindowExistsAsync(projectId, ct);

            // Mode A — no ?session=: serve the active session (legacy behavior)
            if (string.IsNullOrEmpty(requestedSession))
            {
                if (active is null)
                {
                    if (tmuxAlive)
                    {
                        return Results.Json(new TranscriptResponse(
                            ProjectId: projectId,
                            SessionUuid: null,
                            Messages: new List<SessionMessage>(),
                            ReadOnly: false), Json.Default);
                    }
                    return ResultsHelpers.Error(404, "session.not_found",
                        $"No active session for project '{projectId}'");
                }

                if (limit > 0)
                {
                    var (tail, total, truncated, tailOffset) = await reader.ReadTailAsync(
                        active.JsonlPath, projectId, limit, ct);
                    return Results.Json(new TranscriptResponse(
                        ProjectId: projectId,
                        SessionUuid: active.SessionUuid,
                        Messages: tail,
                        ReadOnly: false,
                        Total: total,
                        Truncated: truncated,
                        TailOffset: tailOffset), Json.Default);
                }

                var (messages, fullOffset) = await reader.ReadFromOffsetAsync(active.JsonlPath, 0, projectId, ct);
                return Results.Json(new TranscriptResponse(
                    ProjectId: projectId,
                    SessionUuid: active.SessionUuid,
                    Messages: messages,
                    ReadOnly: false,
                    TailOffset: fullOffset), Json.Default);
            }

            // Mode B — ?session=<uuid>: locate the specific JSONL and serve it.
            // readOnly=true unless this happens to be the same as the active session.
            if (active is null)
                return ResultsHelpers.Error(404, "session.no_project",
                    $"Project '{projectId}' has no sessions to query");

            var jsonlPath = Path.Combine(active.EncodedCwdDir, $"{requestedSession}.jsonl");
            if (!File.Exists(jsonlPath))
                return ResultsHelpers.Error(404, "session.unknown_uuid",
                    $"No JSONL for session '{requestedSession}' in project '{projectId}'");

            var isActive = tmuxAlive && string.Equals(requestedSession, active.SessionUuid,
                StringComparison.OrdinalIgnoreCase);
            if (limit > 0)
            {
                var (tail, total, truncated, tailOffset) = await reader.ReadTailAsync(
                    jsonlPath, projectId, limit, ct);
                return Results.Json(new TranscriptResponse(
                    ProjectId: projectId,
                    SessionUuid: requestedSession,
                    Messages: tail,
                    ReadOnly: !isActive,
                    Total: total,
                    Truncated: truncated,
                    TailOffset: tailOffset), Json.Default);
            }

            var (msgs, fullOffset2) = await reader.ReadFromOffsetAsync(jsonlPath, 0, projectId, ct);
            return Results.Json(new TranscriptResponse(
                ProjectId: projectId,
                SessionUuid: requestedSession,
                Messages: msgs,
                ReadOnly: !isActive,
                TailOffset: fullOffset2), Json.Default);
        });
    }
}
