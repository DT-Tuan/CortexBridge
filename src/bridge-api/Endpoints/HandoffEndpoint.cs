using CortexBridge.Api.Auth;
using CortexBridge.Api.Common;
using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using CortexBridge.Api.Sessions;
using CortexBridge.Api.Tmux;
using System.Text.Json.Serialization;

namespace CortexBridge.Api.Endpoints;

/// <summary>
/// ADR-015 / spec 05 §"Bridge-api additions". Tracks which surface owns a given
/// project's claude session (Mode A = tmux, Mode B = pc). Switch is explicit;
/// bridge stops the tmux claude on hand-off, spawns it on take-over.
/// </summary>
public static class HandoffEndpoint
{
    public record HandoffRequest(
        [property: JsonPropertyName("to")] string To,
        [property: JsonPropertyName("confirmed")] bool? Confirmed,
        [property: JsonPropertyName("client")] string? Client,
        // ADR-017 §3: "Tiếp quản (ép)". User asserts PC CC is gone even though
        // the ide-lock is still present (VS Code window open, no CC on PC).
        // Implies confirmed=true AND sets the ForcedTmux override so ModeWatcher
        // won't auto-revert on the lock alone. Only the PWA's strongly-confirmed
        // force button sends this.
        [property: JsonPropertyName("force")] bool? Force);

    public record HandoffResponse(
        [property: JsonPropertyName("owner")] string Owner,
        [property: JsonPropertyName("sessionUuid")] string? SessionUuid,
        [property: JsonPropertyName("sinceUtc")] string SinceUtc);

    public record OwnerResponse(
        [property: JsonPropertyName("owner")] string Owner,
        [property: JsonPropertyName("sessionUuid")] string? SessionUuid,
        [property: JsonPropertyName("sinceUtc")] string SinceUtc,
        // ADR-017: true ⇔ ModeWatcher proved the PC side is gone, so the single
        // guarded "Tiếp quản" escape hatch may be enabled. Never a free toggle.
        [property: JsonPropertyName("takeoverSafe")] bool TakeoverSafe);

    public static void MapHandoff(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sessions/{projectId}/handoff", PostHandoff);
        app.MapGet("/api/sessions/{projectId}/owner", GetOwner);
    }

    private static async Task<IResult> GetOwner(
        string projectId,
        SessionOwnershipRegistry ownership,
        BridgeDbContext db,
        TmuxClient tmux,
        SessionScanner scanner,
        CancellationToken ct)
    {
        var (owner, uuid, since) = await ownership.ResolveAsync(projectId, db, tmux, scanner, ct);
        return Results.Json(new OwnerResponse(
            Owner: owner.ToString().ToLowerInvariant(),
            SessionUuid: uuid,
            SinceUtc: since.ToString("o"),
            TakeoverSafe: ownership.TakeoverSafe(projectId)
        ), Json.Default);
    }

    private static async Task<IResult> PostHandoff(
        string projectId,
        HandoffRequest body,
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
        var log = loggerFactory.CreateLogger("Handoff");
        var to = (body.To ?? string.Empty).Trim().ToLowerInvariant();
        if (to is not ("pc" or "tmux"))
            return ResultsHelpers.Error(400, "handoff.bad_target",
                "to must be 'pc' or 'tmux'");

        var (currentOwner, currentUuid, _) =
            await ownership.ResolveAsync(projectId, db, tmux, scanner, ct);

        var bearer = ctx.GetAuthToken();
        var client = body.Client ?? "unknown";

        if (to == "pc")
            return await HandoffToPc(projectId, currentOwner, currentUuid, client,
                bearer, tmux, scanner, ownership, db, log, ct);

        // to == "tmux"  — force implies confirmed (and sets the override).
        var force = body.Force ?? false;
        return await TakeoverToTmux(projectId, currentOwner, currentUuid,
            (body.Confirmed ?? false) || force, force, client, bearer, tmux, mutex,
            scanner, ownership, paths, db, log, ct);
    }

    private static async Task<IResult> HandoffToPc(
        string projectId,
        SessionOwnershipRegistry.Owner currentOwner,
        string? currentUuid,
        string client,
        BearerToken? bearer,
        TmuxClient tmux,
        SessionScanner scanner,
        SessionOwnershipRegistry ownership,
        BridgeDbContext db,
        ILogger log,
        CancellationToken ct)
    {
        if (currentOwner == SessionOwnershipRegistry.Owner.Pc)
        {
            // Idempotent — already on PC.
            await AuditAsync(db, bearer, projectId, currentUuid,
                "session_handoff", "ok", $"to=pc client={client} (no-op)", ct);
            return JsonOk(currentOwner, currentUuid, DateTimeOffset.UtcNow);
        }

        // Capture the UUID before /exit, so we know which session to resume later.
        var active = await scanner.ResolveAsync(projectId, ct);
        var uuid = active?.SessionUuid ?? currentUuid;

        // Try to gracefully end the tmux claude (if any). If window doesn't exist, skip.
        if (currentOwner == SessionOwnershipRegistry.Owner.Tmux
            && await tmux.WindowExistsAsync(projectId, ct))
        {
            try
            {
                // Raw keystrokes, NOT paste-buffer: if the tmux claude is sitting
                // in a permission menu a bracketed paste gets eaten as a cancel
                // (same paste-vs-menu issue as the /choice fix) and "/exit" is
                // lost. Esc first dismisses any open menu/dialog → composer
                // focused; then type "/exit" + Enter literally. The force
                // kill-window below is still the unconditional backstop.
                try { await tmux.SendKeyAsync(projectId, "Escape", ct); }
                catch (TmuxException) { /* nothing to dismiss — fine */ }
                await tmux.SendKeysAsync(projectId, "/exit", ct);
                await tmux.SendKeyAsync(projectId, "Enter", ct);
            }
            catch (TmuxException ex)
            {
                log.LogWarning(ex, "Handoff /exit injection failed for {Project}", projectId);
                // Fall through — we still record the user's intent even if the
                // graceful exit failed (window may be in a weird state).
            }

            // Poll up to ~3 s for the window to close.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline
                   && await tmux.WindowExistsAsync(projectId, ct))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }

            // Best-effort kill if still alive — user said go to PC, we honor it.
            if (await tmux.WindowExistsAsync(projectId, ct))
            {
                log.LogInformation("Handoff: tmux window {Project} did not exit after /exit, forcing kill", projectId);
                await tmux.KillWindowAsync(projectId, ct);
            }
        }

        await ownership.SetPcAsync(projectId, uuid, client, db, ct);
        await AuditAsync(db, bearer, projectId, uuid,
            "session_handoff", "ok", $"to=pc client={client}", ct);

        return JsonOk(SessionOwnershipRegistry.Owner.Pc, uuid, DateTimeOffset.UtcNow);
    }

    private static async Task<IResult> TakeoverToTmux(
        string projectId,
        SessionOwnershipRegistry.Owner currentOwner,
        string? currentUuid,
        bool confirmed,
        bool force,
        string client,
        BearerToken? bearer,
        TmuxClient tmux,
        ProjectResumeMutex mutex,
        SessionScanner scanner,
        SessionOwnershipRegistry ownership,
        BridgePaths paths,
        BridgeDbContext db,
        ILogger log,
        CancellationToken ct)
    {
        if (currentOwner == SessionOwnershipRegistry.Owner.Tmux)
        {
            // Idempotent — already on tmux. Still honor an explicit force: owner
            // may have momentarily flipped, but the user asserted PC is gone
            // and the lock is still present — pin the override regardless.
            if (force) ownership.SetForcedTmux(projectId, true);
            var active = await scanner.ResolveAsync(projectId, ct);
            await AuditAsync(db, bearer, projectId, active?.SessionUuid,
                "session_handoff", "ok", $"to=tmux client={client} (no-op)", ct);
            return JsonOk(currentOwner, active?.SessionUuid, DateTimeOffset.UtcNow);
        }

        if (currentOwner == SessionOwnershipRegistry.Owner.Pc && !confirmed)
        {
            // ADR-015: bridge cannot stop the Anthropic native extension's claude.
            // Require user confirmation that they've closed it; companion ext shows
            // a modal and re-sends with confirmed=true.
            return ResultsHelpers.Error(409, "handoff.manual_action_required",
                "Close Anthropic CC extension's chat panel on PC first, then retry with confirmed=true");
        }

        if (string.IsNullOrEmpty(currentUuid))
            return ResultsHelpers.Error(409, "handoff.no_session_to_resume",
                "No previous session UUID recorded — start a fresh session via /resume instead");

        // Re-use the resume mutex — a takeover IS a resume.
        using var lease = mutex.TryAcquire(projectId);
        if (lease is null)
            return ResultsHelpers.Error(409, "handoff.in_flight",
                "Another resume/takeover is already running for this project");

        // Kill any stale window (shouldn't exist if currentOwner != tmux, but be safe).
        await tmux.KillWindowAsync(projectId, ct);

        var workspaceDir = Path.Combine(paths.WorkspaceRoot, projectId);
        try
        {
            await tmux.NewWindowAsync(projectId, workspaceDir, $"claude --resume {currentUuid}", ct);
        }
        catch (TmuxException ex)
        {
            log.LogError(ex, "Takeover tmux launch failed for {Project}/{Uuid}",
                projectId, currentUuid);
            await AuditAsync(db, bearer, projectId, currentUuid,
                "session_handoff", "error", $"to=tmux client={client} err={ex.Message}", ct);
            return ResultsHelpers.Error(500, "tmux.send_failed",
                $"Failed to start claude --resume: {ex.Message}");
        }

        await ownership.SetTmuxAsync(projectId, currentUuid, client, db, ct);
        if (force)
        {
            // Pin the override AFTER SetTmuxAsync (which doesn't touch it) so the
            // next ModeWatcher scan won't A→B-revert on the still-present lock.
            ownership.SetForcedTmux(projectId, true);
            log.LogInformation(
                "Forced takeover {Project}: ForcedTmux pinned — ModeWatcher will "
                + "not auto-revert on ide-lock alone (yields only to claude-vscode)",
                projectId);
        }
        await AuditAsync(db, bearer, projectId, currentUuid,
            "session_handoff", "ok", $"to=tmux client={client} force={force}", ct);

        return JsonOk(SessionOwnershipRegistry.Owner.Tmux, currentUuid, DateTimeOffset.UtcNow);
    }

    private static IResult JsonOk(SessionOwnershipRegistry.Owner owner, string? uuid, DateTimeOffset since)
        => Results.Json(new HandoffResponse(
            Owner: owner.ToString().ToLowerInvariant(),
            SessionUuid: uuid,
            SinceUtc: since.ToString("o")
        ), Json.Default, statusCode: 202);

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
            PayloadHash = null,
            Result = result,
            Detail = detail,
        });
        await db.SaveChangesAsync(ct);
    }
}
