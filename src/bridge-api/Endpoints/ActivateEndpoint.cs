using CortexBridge.Api.Auth;
using CortexBridge.Api.Common;
using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using CortexBridge.Api.Sessions;
using CortexBridge.Api.Tmux;
using System.Text.Json.Serialization;

namespace CortexBridge.Api.Endpoints;

/// <summary>
/// ADR-016 Slice 2 — explicit lifecycle: kill the project's current live UID
/// and bring a different (resumable) UID into the live slot. Owner-appropriate
/// termination: when owner=tmux, the bridge gracefully <c>/exit</c>+poll+
/// <c>kill-window</c>, then <c>claude --resume &lt;uid&gt;</c>. When owner=pc,
/// **hard-refuse 409** — the bridge cannot terminate the Anthropic native ext
/// (ADR-017); the user must close VS Code (auto B→A) or use "Tiếp quản (ép)"
/// first. Confirmation IS the explicit POST itself — the disruptive intent
/// lives in the PWA's confirm modal, never in a silent bypass flag.
/// </summary>
public static class ActivateEndpoint
{
    public record ActivateResponse(
        [property: JsonPropertyName("acceptedAt")] string AcceptedAt,
        [property: JsonPropertyName("activeSessionUuid")] string ActiveSessionUuid);

    public static void MapActivate(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sessions/{projectId}/activate/{sessionUuid}", PostActivate);
    }

    private static async Task<IResult> PostActivate(
        string projectId,
        string sessionUuid,
        HttpContext ctx,
        TmuxClient tmux,
        ProjectResumeMutex mutex,
        SessionScanner scanner,
        SessionOwnershipRegistry ownership,
        BridgePaths paths,
        BridgeDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Activate");
        var bearer = ctx.GetAuthToken();
        var client = ctx.Request.Headers["X-Client"].ToString() is { Length: > 0 } c ? c : "unknown";

        if (!SessionMatch.IsValidUuidShape(sessionUuid))
            return ResultsHelpers.Error(400, "activate.bad_uuid",
                "sessionUuid must match [A-Za-z0-9._-]{1,128}");

        // Owner check — load-bearing safety. The bridge cannot kill the
        // Anthropic native ext, so an activate while owner=pc is a hard refuse
        // per ADR-017 (never a silent forced kill).
        var (owner, _, _) = await ownership.ResolveAsync(projectId, db, tmux, scanner, ct);
        if (owner == SessionOwnershipRegistry.Owner.Pc)
        {
            await AuditAsync(db, bearer, projectId, sessionUuid,
                "session.activate", "denied",
                $"owned_by_pc target={sessionUuid}", ct);
            return ResultsHelpers.Error(409, "activate.owned_by_pc",
                "Project is owned by Mode B (Anthropic ext on PC). Close the "
                + "VS Code workspace (auto B→A) or use Tiếp quản (ép) before "
                + "activating a different session.");
        }

        // Validate the requested UID against the project's session list —
        // mirrors ResumeEndpoint's preflight (no imported, no empty, not the
        // already-live slot).
        var sessions = await scanner.ListAllAsync(projectId, db, ct);
        var session = sessions.FirstOrDefault(s =>
            string.Equals(s.SessionUuid, sessionUuid, StringComparison.OrdinalIgnoreCase));
        if (session is null)
            return ResultsHelpers.Error(404, "activate.unknown_session",
                $"No JSONL for session '{sessionUuid}' in project '{projectId}'");
        if (session.IsImported)
            return ResultsHelpers.Error(409, "activate.imported_session",
                "Imported (foreign cwd) sessions cannot be activated — read-only by design");
        if (session.IsActive)
            return ResultsHelpers.Error(409, "activate.already_active",
                "Selected session is already the live slot UID");
        if (session.MessageCount == 0)
            return ResultsHelpers.Error(409, "activate.empty_session",
                "Session has no records to resume from — start a new session instead");

        // Activate IS a resume — share the same mutex so a parallel resume/
        // activate is serialised (no two-process spawn race).
        using var lease = mutex.TryAcquire(projectId);
        if (lease is null)
            return ResultsHelpers.Error(409, "activate.in_flight",
                "Another resume/activate is already running for this project");

        // Owner-appropriate termination (tmux only — pc already refused above).
        // Graceful /exit then poll, with a hard kill-window backstop.
        if (await tmux.WindowExistsAsync(projectId, ct))
        {
            try
            {
                // Esc dismisses any open menu (a bracketed paste of "/exit" would
                // otherwise be eaten as cancel — same paste-vs-menu issue the
                // /choice fix addressed). Then type "/exit" + Enter literally.
                try { await tmux.SendKeyAsync(projectId, "Escape", ct); }
                catch (TmuxException) { /* nothing to dismiss — fine */ }
                await tmux.SendKeysAsync(projectId, "/exit", ct);
                await tmux.SendKeyAsync(projectId, "Enter", ct);
            }
            catch (TmuxException ex)
            {
                log.LogWarning(ex, "Activate /exit injection failed for {Project}", projectId);
                // Fall through — the hard kill-window backstop below is the
                // unconditional guarantee.
            }
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline
                   && await tmux.WindowExistsAsync(projectId, ct))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
            if (await tmux.WindowExistsAsync(projectId, ct))
            {
                log.LogInformation(
                    "Activate: tmux window {Project} did not exit after /exit, forcing kill",
                    projectId);
                await tmux.KillWindowAsync(projectId, ct);
            }
        }

        var workspaceDir = Path.Combine(paths.WorkspaceRoot, projectId);
        try
        {
            await tmux.NewWindowAsync(projectId, workspaceDir,
                $"claude --resume {sessionUuid}", ct);
        }
        catch (TmuxException ex)
        {
            log.LogError(ex, "Activate tmux launch failed for {Project}/{Uuid}",
                projectId, sessionUuid);
            await AuditAsync(db, bearer, projectId, sessionUuid,
                "session.activate", "error", $"err={ex.Message}", ct);
            return ResultsHelpers.Error(500, "tmux.send_failed",
                $"Failed to start claude --resume: {ex.Message}");
        }

        // Authoritative slot marker update — the new UID owns the live slot.
        await ownership.SetTmuxAsync(projectId, sessionUuid, client, db, ct);
        await AuditAsync(db, bearer, projectId, sessionUuid,
            "session.activate", "ok", $"target={sessionUuid} client={client}", ct);

        return Results.Json(new ActivateResponse(
            AcceptedAt: DateTimeOffset.UtcNow.ToString("o"),
            ActiveSessionUuid: sessionUuid), Json.Default, statusCode: 202);
    }

    private static async Task AuditAsync(
        BridgeDbContext db, BearerToken? token, string projectId, string? sessionUuid,
        string action, string result, string? detail, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTimeOffset.UtcNow,
            ProjectId = projectId,
            SessionUuid = sessionUuid,
            Action = action,
            TokenId = token?.Id,
            Result = result,
            Detail = detail,
        });
        await db.SaveChangesAsync(ct);
    }
}
