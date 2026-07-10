using CortexBridge.Api.Auth;
using CortexBridge.Api.Common;
using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using CortexBridge.Api.Sessions;
using CortexBridge.Api.Tmux;

namespace CortexBridge.Api.Endpoints;

/// <summary>
/// POST /api/sessions/{projectId}/interrupt — fast-cancel the current CC turn
/// from the PWA stop button. Parity with the CC CLI's ESC keystroke: PWA users
/// stuck watching CC drift wrong direction now have a stop signal.
///
/// Mechanism: 2× Escape via TmuxClient.SendKeyAsync. CC CLI convention is two
/// ESCs to interrupt a running turn (single ESC dismisses interactive pickers —
/// already handled by /cancel-picker). 80ms gap between keys gives tmux time to
/// process the first before the second arrives.
///
/// Idempotency: 409 if state.Processing(projectId) is already false. The
/// `processing` flag is hook-authoritative (see [[hook-driven turn-state
/// machine]]); refusing the no-op avoids accidental key-spam into an idle CC.
/// </summary>
public static class InterruptEndpoint
{
    public static void MapInterrupt(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sessions/{projectId}/interrupt", PostInterrupt);
    }

    private static async Task<IResult> PostInterrupt(
        string projectId,
        HttpContext ctx,
        TmuxClient tmux,
        ProjectReplyMutex mutex,
        TokenRateLimiter limiter,
        SessionScanner scanner,
        SessionStateRegistry state,
        SessionQueue queue,
        BridgeDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Interrupt");

        var bearer = ctx.GetAuthToken();
        if (bearer is not null && !limiter.TryAcquire(bearer.Id))
            return ResultsHelpers.Error(429, "rate_limit.exceeded",
                "Too many requests — limit is 30 per minute per token");

        if (!await tmux.WindowExistsAsync(projectId, ct))
            return ResultsHelpers.Error(404, "tmux.window_missing",
                $"No tmux window named '{projectId}' in session '{tmux.SessionName}'");

        // ADR-016 Slice 2: ?session= must match the live-slot UID. Interrupt is
        // destructive (stops a running turn), so a stale UID guard avoids
        // hitting the wrong session. Mirrors ReplyEndpoints precondition order.
        var (sErr, active) = await CheckSessionAsync(ctx, scanner, projectId, ct);
        if (sErr is not null) return sErr;

        // No-op guard: refuse interrupt when CC is idle. Avoids spurious key
        // injection that could glitch a freshly-opened menu. Processing is the
        // hook-authoritative flag set by UserPromptSubmit/PreToolUse/PostToolUse.
        if (!state.Processing(projectId, active?.SessionUuid))
            return ResultsHelpers.Error(409, "interrupt.not_processing",
                "No active turn to interrupt — CC is idle");

        using var lk = mutex.TryAcquire(projectId);
        if (lk is null)
            return ResultsHelpers.Error(409, "reply.in_flight",
                "Another reply is being delivered — try again");

        try
        {
            // 2× ESC with 80ms gap: tmux processes the first Escape before the
            // second arrives. Single ESC may only dismiss a picker (covered by
            // /cancel-picker); double ESC is CC's convention for interrupting
            // an in-progress turn.
            await tmux.SendKeyAsync(projectId, "Escape", ct);
            await Task.Delay(TimeSpan.FromMilliseconds(80), ct);
            await tmux.SendKeyAsync(projectId, "Escape", ct);
        }
        catch (TmuxException ex)
        {
            log.LogError(ex, "tmux interrupt failed for {Project}", projectId);
            await AuditAsync(db, bearer, projectId, active?.SessionUuid,
                "interrupt", null, "error", ex.Message, ct);
            return ResultsHelpers.Error(500, "tmux.send_failed", "tmux invocation failed");
        }

        // Optimistically clear processing so the composer unlocks immediately;
        // the Stop hook (if it fires) will re-confirm. Watchdog backstops if
        // the hook never lands (CC sometimes skips Stop on hard interrupts).
        state.SetProcessing(projectId, false, active?.SessionUuid);

        // Interrupt clears any queued reply — the user's intent is to STOP,
        // not let stale context paste a minute later. Audit the drop with the
        // prior payload hash so the trail shows what was lost.
        var dropped = queue.TryDequeue(projectId);
        if (dropped is not null)
        {
            await AuditAsync(db, bearer, projectId, dropped.SessionUuid,
                "queue.cleared_by_interrupt", dropped.PayloadHash, "ok", null, ct);
        }

        await AuditAsync(db, bearer, projectId, active?.SessionUuid,
            "interrupt", null, "ok", null, ct);

        return Results.Json(new { acceptedAt = DateTimeOffset.UtcNow.ToString("o") },
            Json.Default, statusCode: 202);
    }

    private static async Task AuditAsync(
        BridgeDbContext db, BearerToken? token, string projectId, string? sessionUuid,
        string action, string? payloadHash, string result, string? detail, CancellationToken ct)
    {
        // Audit contract: projectId + tokenId + action + result. PayloadHash
        // populated for queue.cleared_by_interrupt (the dropped buffered text
        // — trail shows what was lost); null for the interrupt event itself
        // (only Escape keystrokes, no user content to hash).
        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTimeOffset.UtcNow,
            ProjectId = projectId,
            SessionUuid = sessionUuid,
            Action = action,
            TokenId = token?.Id,
            PayloadHash = payloadHash,
            Result = result,
            Detail = detail,
        });
        await db.SaveChangesAsync(ct);
    }

    // ADR-016 Slice 2 ?session= precondition — duplicated from ReplyEndpoints
    // intentionally (project rule: avoid premature abstraction until 3+ copies).
    // Queue endpoint (sub-arc 2) will be the 3rd — refactor then.
    private static async Task<(IResult? Error, SessionScanner.ActiveSession? Active)>
        CheckSessionAsync(HttpContext ctx, SessionScanner scanner, string projectId, CancellationToken ct)
    {
        var requested = ctx.Request.Query["session"].ToString();
        if (!string.IsNullOrEmpty(requested) && !SessionMatch.IsValidUuidShape(requested))
            return (ResultsHelpers.Error(400, "session.bad_uuid",
                "session must match [A-Za-z0-9._-]{1,128}"), null);
        var active = await scanner.ResolveAsync(projectId, ct);
        if (SessionMatch.Check(requested, active?.SessionUuid) == SessionMatch.Result.Mismatch)
            return (Results.Json(new
            {
                error = new
                {
                    code = "session.not_live",
                    message = "Requested session is not the live slot UID for this project",
                    activeSessionUuid = active?.SessionUuid,
                },
            }, Json.Default, statusCode: 409), active);
        return (null, active);
    }
}
