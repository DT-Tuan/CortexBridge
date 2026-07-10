using System.Text.Json;
using CortexBridge.Api.Common;
using CortexBridge.Api.Data;
using CortexBridge.Api.Hooks;
using CortexBridge.Api.Sessions;
using CortexBridge.Api.Tmux;
using Microsoft.EntityFrameworkCore;

namespace CortexBridge.Api.Endpoints;

public static class InternalHooksEndpoints
{
    public static void MapInternalHooks(this IEndpointRouteBuilder app, IConfiguration config)
    {
        app.MapPost("/internal/hooks/notification", NotificationHandler);
        app.MapPost("/internal/hooks/stop", StopHandler);
        app.MapPost("/internal/hooks/activity", ActivityHandler);
        app.MapPost("/internal/hooks/autoallow", AutoAllowHandler);
    }

    /// <summary>
    /// Async audit sink for the host auto-allow PreToolUse hook
    /// (cc-autoallow-hook.sh). The hook makes its allow decision synchronously on
    /// stdout and fires this POST in the background — so observability never sits
    /// on CC's critical path. One audit_log row per auto-allowed tool call.
    /// </summary>
    private static async Task<IResult> AutoAllowHandler(
        HttpContext ctx,
        HookTokenProvider tokens,
        BridgeDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("InternalHooks");
        if (!ValidateHookAuth(ctx, tokens))
            return ResultsHelpers.Error(401, "hook.invalid_token", "hook token invalid");

        AutoAllowPayload? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<AutoAllowPayload>(
                ctx.Request.Body, Json.Default, ct);
        }
        catch (JsonException)
        {
            return ResultsHelpers.Error(400, "hook.bad_json", "Invalid JSON body");
        }
        if (payload is null || string.IsNullOrEmpty(payload.ProjectId))
            return ResultsHelpers.Error(400, "hook.missing_project_id", "projectId is required");

        log.LogDebug("Auto-allow {Tool} for project={Project} ({Reason})",
            payload.Tool, payload.ProjectId, payload.Reason);
        db.AuditLogs.Add(new Data.Entities.AuditLog
        {
            Timestamp = DateTimeOffset.UtcNow,
            ProjectId = payload.ProjectId,
            Action = "tool.autoallow",
            Result = "allowed",
            // Tool name + match reason only — never the command itself (may hold secrets).
            Detail = $"{payload.Tool}: {payload.Reason}",
        });
        await db.SaveChangesAsync(ct);

        return Results.Json(new { acceptedAt = DateTimeOffset.UtcNow.ToString("o") },
            Json.Default, statusCode: 202);
    }

    public record AutoAllowPayload(string? ProjectId, string? Tool, string? Reason);

    // CC lifecycle events that mean "no turn is running" — built-in slash
    // commands (/clear, /compact) resume via SessionStart, /exit emits
    // SessionEnd, a finished sub-agent emits SubagentStop. None of these fire
    // the Stop hook, so without treating them as terminal here the Processing
    // flag latches true and the PWA stays stuck "thinking".
    private static readonly HashSet<string> TerminalKinds =
        new(StringComparer.OrdinalIgnoreCase) { "SessionStart", "SessionEnd", "SubagentStop" };

    /// <summary>
    /// Turn-lifecycle heartbeat. UserPromptSubmit / PreToolUse / PostToolUse mark
    /// the project "processing" and refresh LastEventAt (a 6-minute doc-write turn
    /// is clearly alive, not a crash). SessionStart / SessionEnd / SubagentStop are
    /// terminal (slash commands have no Stop hook) and end processing. Stop also
    /// ends it; Notification pauses it.
    /// </summary>
    private static async Task<IResult> ActivityHandler(
        HttpContext ctx,
        HookTokenProvider tokens,
        SessionStateRegistry state,
        SessionScanner scanner,
        SessionOwnershipRegistry ownership,
        TmuxClient tmux,
        BridgeDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("InternalHooks");
        if (!ValidateHookAuth(ctx, tokens))
            return ResultsHelpers.Error(401, "hook.invalid_token", "hook token invalid");

        HookPayload? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<HookPayload>(
                ctx.Request.Body, Json.Default, ct);
        }
        catch (JsonException)
        {
            return ResultsHelpers.Error(400, "hook.bad_json", "Invalid JSON body");
        }
        if (payload is null || string.IsNullOrEmpty(payload.ProjectId))
            return ResultsHelpers.Error(400, "hook.missing_project_id", "projectId is required");

        log.LogDebug("Activity hook {Kind} for project={Project}", payload.Kind, payload.ProjectId);
        var terminal = payload.Kind is not null && TerminalKinds.Contains(payload.Kind);
        // Mid-turn hooks set processing (and refresh LastEventAt, the watchdog's
        // liveness pulse); terminal lifecycle hooks clear it precisely so the PWA
        // un-sticks the instant a /clear or /compact lands — without waiting for
        // the 2-min watchdog backstop.
        state.SetProcessing(payload.ProjectId, !terminal, payload.SessionId);

        // issue #1: /clear (and any CC-initiated session reset) starts a NEW
        // session UUID in the SAME tmux window and announces it via SessionStart
        // carrying the new session_id. Re-point the live-slot marker to it so
        // resolution / needsInput / push / session_switch stop keying onto the
        // dead pre-/clear UUID.
        if (string.Equals(payload.Kind, "SessionStart", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(payload.SessionId))
        {
            await MaybeRepointLiveSlotAsync(
                payload.ProjectId, payload.SessionId,
                scanner, ownership, tmux, db, log, ct);
        }

        return Results.Json(new { acceptedAt = DateTimeOffset.UtcNow.ToString("o") },
            Json.Default, statusCode: 202);
    }

    /// <summary>
    /// Re-points the per-project live-slot marker (session_ownership.session_uuid)
    /// to <paramref name="newSessionId"/> when CC has switched sessions on its own
    /// (e.g. /clear). Guarded so a PC-side CC's hooks can't hijack the marker, and
    /// so we only act once the new session's JSONL actually exists on this host.
    /// A no-op for untracked projects — those already follow newest-by-last-record.
    /// </summary>
    private static async Task MaybeRepointLiveSlotAsync(
        string projectId, string newSessionId,
        SessionScanner scanner, SessionOwnershipRegistry ownership, TmuxClient tmux,
        BridgeDbContext db, ILogger log, CancellationToken ct)
    {
        try
        {
            // Only when the bridge (tmux) owns the slot. PC-side hooks reach this
            // bridge too — they must not move the marker.
            var (owner, _, _) = await ownership.ResolveAsync(projectId, db, tmux, scanner, ct);
            if (owner != SessionOwnershipRegistry.Owner.Tmux)
                return;

            var row = await db.SessionOwnerships
                .FirstOrDefaultAsync(x => x.ProjectId == projectId, ct);
            if (row is null)
                return;  // untracked: Tier-2 (newest last-record) already follows the switch
            if (string.Equals(row.SessionUuid, newSessionId, StringComparison.OrdinalIgnoreCase))
                return;  // already pointed there

            // The new session's JSONL must exist in this project's dir — guards a
            // SessionStart that beats CC's first write, and foreign (PC) session ids.
            var active = await scanner.ResolveAsync(projectId, ct);
            if (active is null)
                return;
            var newPath = Path.Combine(active.EncodedCwdDir, newSessionId + ".jsonl");
            if (!File.Exists(newPath))
                return;

            var oldUuid = row.SessionUuid;
            row.SessionUuid = newSessionId;
            row.SinceUtc = DateTimeOffset.UtcNow;
            row.ChangedByClient = "activity-hook:sessionstart";
            await db.SaveChangesAsync(ct);

            // JsonlWatcher.PumpAsync now re-resolves to the new path and emits a
            // session_switch frame; the PWA reloads onto the new session.
            log.LogInformation(
                "Live-slot re-pointed for {Project}: {Old} -> {New} (SessionStart)",
                projectId, oldUuid, newSessionId);
        }
        catch (Exception ex)
        {
            // Non-fatal: a failed re-point just leaves the prior behaviour.
            log.LogWarning(ex, "Live-slot re-point failed for {Project}", projectId);
        }
    }

    private static async Task<IResult> NotificationHandler(
        HttpContext ctx,
        HookTokenProvider tokens,
        SessionStateRegistry state,
        SessionScanner scanner,
        WebPushSender webPush,
        BridgeDbContext db,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("InternalHooks");
        if (!ValidateHookAuth(ctx, tokens))
            return ResultsHelpers.Error(401, "hook.invalid_token", "hook token invalid");

        HookPayload? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<HookPayload>(
                ctx.Request.Body, Json.Default, ct);
        }
        catch (JsonException)
        {
            return ResultsHelpers.Error(400, "hook.bad_json", "Invalid JSON body");
        }
        if (payload is null || string.IsNullOrEmpty(payload.ProjectId))
            return ResultsHelpers.Error(400, "hook.missing_project_id", "projectId is required");

        log.LogInformation("Notification hook for project={Project} session={Session}",
            payload.ProjectId, payload.SessionId);

        // CC fires the Notification hook for TWO different events:
        //   1) Genuine input request — permission prompt, AskUserQuestion.
        //      Message ~= "Claude needs your permission to use <Tool>"
        //   2) Idle ping after 60s of inactivity even when CC isn't asking anything.
        //      Message == "Claude is waiting for your input"
        // The idle ping is a false positive for our needsInput banner — CC has nothing
        // pending, the user just hasn't typed anything. Detect by message text and
        // short-circuit so we don't flip needsInput=true or fan out Web Push.
        var msgLower = (payload.Message ?? "").ToLowerInvariant();
        var isIdlePing = msgLower.Contains("waiting for your input");
        if (isIdlePing)
        {
            log.LogDebug("Skipping idle ping for {Project} (message='{Message}')",
                payload.ProjectId, payload.Message);
            return Results.Json(new { acceptedAt = DateTimeOffset.UtcNow.ToString("o"), idlePing = true },
                Json.Default, statusCode: 202);
        }

        // Resolve the active VM session up-front. Used for TWO things:
        //   1) sessionId fallback — CC's Notification hook DOES NOT always
        //      populate session_id. Permission prompts ("Claude needs your
        //      permission to use Bash") arrive with session_id=null even
        //      though they are genuine "user needs to act" events. Live-caught
        //      2026-05-22 on CortexPlexus. The 2026-05-21 fix (`840fa0d`) that
        //      skipped notifications with no sessionId overfit on the
        //      symptom — it killed legitimate permission prompts too. Falling
        //      back to the scanner's view of the active JSONL session keeps
        //      `needsInput` keyed onto the same (projectId, sessionUuid) that
        //      the PWA queries (per ADR-016 SessionStateRegistry); without
        //      this the state writes to the project-wide bucket and the
        //      session-scoped query returns false (banner never renders).
        //   2) VM-session guard — a PC-side CC whose hooks reach this bridge
        //      has no JSONL here. scanner.ResolveAsync returns null → skip
        //      both needsInput latch + Web Push fan-out (would otherwise fan
        //      to all devices as "<pc-project> needs input", the stray push).
        var vmSession = await scanner.ResolveAsync(payload.ProjectId, ct);
        if (vmSession is null)
        {
            log.LogInformation(
                "Skipping needsInput + push for {Project}: no VM session dir "
                + "(likely a PC-side CC session whose hooks reach this bridge). "
                + "message='{Message}'",
                payload.ProjectId, payload.Message);
            return Results.Json(
                new { acceptedAt = DateTimeOffset.UtcNow.ToString("o"), skippedNonVmSession = true },
                Json.Default, statusCode: 202);
        }

        var sessionId = string.IsNullOrEmpty(payload.SessionId)
            ? vmSession.SessionUuid
            : payload.SessionId;

        // Dedup: if needsInput was already true, this is CC re-prompting the same question.
        // Skip outbound Web Push so user doesn't get a fresh notification on lockscreen
        // for a prompt they may already be looking at / replying to.
        // SSE status remains accurate (SetNeedsInput's prev==needs check skips the event too).
        var alreadyWaiting = state.NeedsInput(payload.ProjectId, sessionId);
        // Forward the notification message into state so SSE status frames carry it
        // — PWA banner uses this to tell user WHAT CC is asking (permission prompt etc).
        state.SetNeedsInput(payload.ProjectId, true, payload.Message, sessionId);
        if (alreadyWaiting)
        {
            log.LogDebug("Skipping push for {Project} — needsInput already true (dedup)", payload.ProjectId);
            return Results.Json(new { acceptedAt = DateTimeOffset.UtcNow.ToString("o"), deduped = true },
                Json.Default, statusCode: 202);
        }

        var publicBase = config["BRIDGE_PUBLIC_URL"]
            ?? Environment.GetEnvironmentVariable("BRIDGE_PUBLIC_URL");
        // ADR-016 Slice 1 (Step 4): deep-link to the EXACT session that fired
        // the Notification, not just the project. The PWA already consumes
        // ?session=<uuid> (read-only for a historical one; interactive when it
        // is the live/active session — the common "CC blocked on the live
        // session" case). Use the resolved sessionId (falls back from
        // scanner.ResolveAsync when the hook payload omits it).
        var clickUrl = !string.IsNullOrEmpty(publicBase)
            ? $"{publicBase.TrimEnd('/')}/sessions/{payload.ProjectId}"
              + (string.IsNullOrEmpty(sessionId)
                  ? ""
                  : $"?session={Uri.EscapeDataString(sessionId)}")
            : null;

        var msgText = payload.Message ?? "Claude needs input";

        // Fan out to all subscribed PWA clients via Web Push. Per-subscription
        // failures are non-fatal (stale subs auto-removed inside SendToAllAsync).
        try
        {
            await webPush.SendToAllAsync(
                db,
                payload.ProjectId,
                title: $"{payload.ProjectId} needs input",
                body: msgText,
                clickUrl: clickUrl,
                ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Web Push fanout failed for {Project}", payload.ProjectId);
        }

        return Results.Json(new { acceptedAt = DateTimeOffset.UtcNow.ToString("o") },
            Json.Default, statusCode: 202);
    }

    private static async Task<IResult> StopHandler(
        HttpContext ctx,
        HookTokenProvider tokens,
        SessionStateRegistry state,
        WebPushSender webPush,
        BridgeDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("InternalHooks");
        if (!ValidateHookAuth(ctx, tokens))
            return ResultsHelpers.Error(401, "hook.invalid_token", "hook token invalid");

        HookPayload? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<HookPayload>(
                ctx.Request.Body, Json.Default, ct);
        }
        catch (JsonException)
        {
            return ResultsHelpers.Error(400, "hook.bad_json", "Invalid JSON body");
        }
        if (payload is null || string.IsNullOrEmpty(payload.ProjectId))
            return ResultsHelpers.Error(400, "hook.missing_project_id", "projectId is required");

        log.LogInformation("Stop hook for project={Project}", payload.ProjectId);
        var wasWaiting = state.NeedsInput(payload.ProjectId, payload.SessionId);
        // Turn finished: not processing, not waiting.
        state.SetProcessing(payload.ProjectId, false, payload.SessionId);
        state.SetNeedsInput(payload.ProjectId, false, sessionUuid: payload.SessionId);

        // If we just transitioned from waiting → not-waiting, broadcast a "clear" push
        // so any device with a lingering lockscreen notification dismisses it. Best-effort.
        if (wasWaiting)
        {
            try { await webPush.SendClearAsync(db, payload.ProjectId, ct); }
            catch (Exception ex) { log.LogDebug(ex, "Clear push failed for {Project}", payload.ProjectId); }
        }

        return Results.Json(new { acceptedAt = DateTimeOffset.UtcNow.ToString("o") },
            Json.Default, statusCode: 202);
    }

    private static bool ValidateHookAuth(HttpContext ctx, HookTokenProvider tokens)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var presented = header["Bearer ".Length..].Trim();
        return tokens.Validate(presented);
    }

    public record HookPayload(
        string? ProjectId,
        string? SessionId,
        string? TranscriptPath,
        string? Cwd,
        string? HookEventName,
        string? Message,
        bool? StopHookActive,
        string? Kind = null
    );
}
