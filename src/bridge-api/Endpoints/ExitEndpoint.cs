using CortexBridge.Api.Auth;
using CortexBridge.Api.Common;
using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using CortexBridge.Api.Sessions;
using CortexBridge.Api.Tmux;
using Microsoft.EntityFrameworkCore;

namespace CortexBridge.Api.Endpoints;

/// <summary>
/// Explicit "Thoat phien" lifecycle - the deliberate counterpart to
/// <see cref="ActivateEndpoint"/>'s atomic kill+resume. User-flow: end the
/// live tmux claude gracefully, clear the ownership row so the project goes
/// to <see cref="SessionOwnershipRegistry.Owner.None"/>, then user picks
/// what to do next (new session, restart-resume, switch to a parked uid).
///
/// Distinct from <c>/api/sessions/{pid}/kill</c> (hard kill-window, no
/// graceful /exit, ownership untouched) and <c>/api/sessions/{pid}/restart</c>
/// (kill+spawn in one op). This endpoint is graceful + leaves the project in
/// a clean "no live session" state.
/// </summary>
public static class ExitEndpoint
{
    public static void MapExit(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sessions/{projectId}/exit", ExitHandler);
    }

    private static async Task<IResult> ExitHandler(
        string projectId,
        HttpContext ctx,
        TmuxClient tmux,
        SessionScanner scanner,
        SessionOwnershipRegistry ownership,
        SessionStateRegistry state,
        BridgeDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Exit");

        // Hard-refuse if owner=Pc (ADR-017): the bridge cannot terminate
        // the Anthropic native ext. Same shape as RestartEndpoint's check.
        var (owner, _, _) = await ownership.ResolveAsync(projectId, db, tmux, scanner, ct);
        if (owner == SessionOwnershipRegistry.Owner.Pc)
        {
            await Audit(db, ctx.GetAuthToken(), projectId, null,
                "session.exit", "denied", "owned_by_pc", ct);
            return ResultsHelpers.Error(409, "exit.owned_by_pc",
                "Session is on PC (Mode B). The bridge cannot exit it. Close "
                + "VS Code workspace first, or use the existing handoff flow.");
        }

        // Capture the UUID for audit BEFORE removing the row.
        var ownedUuid = (await db.SessionOwnerships
            .FirstOrDefaultAsync(x => x.ProjectId == projectId, ct))?.SessionUuid;

        // Graceful /exit pattern proven in HandoffEndpoint.HandoffToPc:
        // Esc dismisses any menu (a bracketed paste of "/exit" gets eaten as
        // cancel if a menu is open - same paste-vs-menu issue /choice fixed).
        // Then type "/exit" + Enter literally. Poll 3s. Force kill backstop.
        if (await tmux.WindowExistsAsync(projectId, ct))
        {
            try
            {
                try { await tmux.SendKeyAsync(projectId, "Escape", ct); }
                catch (TmuxException) { /* nothing to dismiss */ }
                await tmux.SendKeysAsync(projectId, "/exit", ct);
                await tmux.SendKeyAsync(projectId, "Enter", ct);
            }
            catch (TmuxException ex)
            {
                log.LogWarning(ex, "Exit /exit injection failed for {Project}", projectId);
                // Fall through - hard kill is the backstop.
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
                    "Exit: tmux window {Project} did not exit after /exit, forcing kill",
                    projectId);
                await tmux.KillWindowAsync(projectId, ct);
            }
        }

        // Persist an explicit owner='none' TOMBSTONE (do NOT delete the row).
        // The container bootstrap (cortex-bootstrap.sh) auto-resumes every
        // project that has a JSONL on each container start, skipping only if a
        // tmux window already exists. A deleted row reads as "untracked" → the
        // session the user just stopped gets resurrected on the next cc-bridge
        // rebuild/restart (live failure 2026-05-22: three rebuilds in a day each
        // re-spawned stopped sessions). The tombstone lets bootstrap tell
        // "user stopped this" (none/pc) from "was running" (tmux) and skip it.
        // ModeWatcher still won't re-cascade: none is terminal in Derive() — no
        // owner=Pc/Tmux to reconcile from.
        var row = await db.SessionOwnerships
            .FirstOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if (row is not null)
        {
            row.Owner = "none";
            row.SessionUuid = null;
            row.SinceUtc = DateTimeOffset.UtcNow;
            row.ChangedByClient = "exit-endpoint";
        }
        else
        {
            db.SessionOwnerships.Add(new SessionOwnership
            {
                ProjectId = projectId,
                Owner = "none",
                SessionUuid = null,
                SinceUtc = DateTimeOffset.UtcNow,
                ChangedByClient = "exit-endpoint",
            });
        }
        await db.SaveChangesAsync(ct);

        // Clear in-memory state so dashboard doesn't show stale
        // needsInput/processing dots.
        state.SetProcessing(projectId, false);
        state.SetNeedsInput(projectId, false);

        await Audit(db, ctx.GetAuthToken(), projectId, ownedUuid,
            "session.exit", "ok", ownedUuid is null ? "no-row" : $"row={ownedUuid}", ct);

        return Results.Json(
            new { acceptedAt = DateTimeOffset.UtcNow.ToString("o") },
            Json.Default, statusCode: 202);
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
            Detail = detail,
        });
        await db.SaveChangesAsync(ct);
    }
}
