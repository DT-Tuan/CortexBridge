using CortexBridge.Api.Auth;
using CortexBridge.Api.Common;
using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using CortexBridge.Api.Sessions;
using CortexBridge.Api.Tmux;

namespace CortexBridge.Api.Endpoints;

public static class RestartEndpoint
{
    public record RestartResponse(string AcceptedAt, string Window, bool Resumed, string? SessionUuid);

    public static void MapRestart(this IEndpointRouteBuilder app)
    {
        // POST /api/sessions/:projectId/restart — create tmux window + start CC.
        // If a previous session JSONL exists, run `claude --resume <uuid>`; otherwise fresh `claude`.
        // 409 if window already exists (use kill+restart sequence to force).
        app.MapPost("/api/sessions/{projectId}/restart", RestartHandler);

        // POST /api/sessions/:projectId/kill — kill the tmux window for this project.
        // Useful as a manual cleanup before restart, or to terminate a runaway CC.
        app.MapPost("/api/sessions/{projectId}/kill", KillHandler);
    }

    private static async Task<IResult> RestartHandler(
        string projectId,
        HttpContext ctx,
        BridgePaths paths,
        TmuxClient tmux,
        SessionScanner scanner,
        SessionOwnershipRegistry ownership,
        BridgeDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Restart");

        // Reject path traversal — projectId must be a single dir name
        if (projectId.Contains('/') || projectId.Contains('\\') || projectId.Contains(".."))
            return ResultsHelpers.Error(400, "project.bad_id", "projectId must be a single directory name");

        var workspaceDir = Path.Combine(paths.WorkspaceRoot, projectId);
        var resolved = Path.GetFullPath(workspaceDir);
        var rootResolved = Path.GetFullPath(paths.WorkspaceRoot);
        if (!resolved.StartsWith(rootResolved + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && resolved != rootResolved)
        {
            return ResultsHelpers.Error(400, "project.escape", "Path escapes workspace root");
        }
        if (!Directory.Exists(workspaceDir))
            return ResultsHelpers.Error(404, "project.not_found",
                $"Workspace directory '{projectId}' not found in {paths.WorkspaceRoot}");

        // SAFETY (ADR-017 §4) — authoritative two-process guard. In Mode B the
        // PC native CC (claude-vscode) owns the session and the bridge tmux
        // window is gone BY DESIGN. `running=false` is then NORMAL, but the PWA
        // would (used to) show a "CC stopped — restart?" affordance; a restart
        // here spawns `claude --resume <uuid>` alongside the live claude-vscode
        // → two `claude` on one UID, the "[Request interrupted]" interleave.
        // The ModeWatcher guard would eventually kill it, but we must never
        // knowingly create the hazard. Hard-refuse: the user's path back to
        // Bridge from Mode B is the guarded "Tiếp quản" handoff, not restart.
        var (owner, _, _) = await ownership.ResolveAsync(projectId, db, tmux, scanner, ct);
        if (owner == SessionOwnershipRegistry.Owner.Pc)
        {
            await Audit(db, ctx.GetAuthToken(), projectId, null, "restart", "refused", "owned_by_pc", ct);
            return ResultsHelpers.Error(409, "restart.owned_by_pc",
                "Session is running on PC (VS Code, Mode B). Restarting would put two "
                + "claude on one session. Close VS Code or use \"Tiếp quản\" to return to Bridge.");
        }

        // Refuse if window already exists — caller should kill first if intentional restart
        if (await tmux.WindowExistsAsync(projectId, ct))
            return ResultsHelpers.Error(409, "tmux.window_exists",
                $"Tmux window '{projectId}' already exists. Kill it first or use a different projectId.");

        // Resolve last session UUID for resume. If JSONL exists → resume; else fresh.
        var active = await scanner.ResolveAsync(projectId, ct);
        var sessionUuid = active?.SessionUuid;
        var resumed = !string.IsNullOrEmpty(sessionUuid);

        // tmux runs the command via /bin/sh -c — both forms work since `claude` is on PATH.
        var command = resumed ? $"claude --resume {sessionUuid}" : "claude";

        try
        {
            await tmux.NewWindowAsync(projectId, workspaceDir, command, ct);
        }
        catch (TmuxException ex)
        {
            log.LogError(ex, "Failed to create tmux window for {Project}", projectId);
            await Audit(db, ctx.GetAuthToken(), projectId, sessionUuid, "restart", "error", ex.Message, ct);
            return ResultsHelpers.Error(500, "tmux.new_window_failed", ex.Message);
        }

        await Audit(db, ctx.GetAuthToken(), projectId, sessionUuid, "restart", "ok",
            resumed ? $"resumed {sessionUuid}" : "fresh", ct);

        return Results.Json(
            new RestartResponse(
                AcceptedAt: DateTimeOffset.UtcNow.ToString("o"),
                Window: projectId,
                Resumed: resumed,
                SessionUuid: sessionUuid),
            Json.Default,
            statusCode: 202);
    }

    private static async Task<IResult> KillHandler(
        string projectId,
        HttpContext ctx,
        TmuxClient tmux,
        BridgeDbContext db,
        CancellationToken ct)
    {
        if (!await tmux.WindowExistsAsync(projectId, ct))
            return Results.Json(new { acceptedAt = DateTimeOffset.UtcNow.ToString("o"), windowExisted = false },
                Json.Default, statusCode: 200);

        await tmux.KillWindowAsync(projectId, ct);
        await Audit(db, ctx.GetAuthToken(), projectId, null, "kill", "ok", null, ct);

        return Results.Json(new { acceptedAt = DateTimeOffset.UtcNow.ToString("o"), windowExisted = true },
            Json.Default, statusCode: 200);
    }

    private static async Task Audit(
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
            Detail = detail
        });
        await db.SaveChangesAsync(ct);
    }
}
