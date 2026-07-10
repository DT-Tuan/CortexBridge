using System.Security.Cryptography;
using System.Text;
using CortexBridge.Api.Auth;
using CortexBridge.Api.Common;
using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using CortexBridge.Api.Sessions;
using CortexBridge.Api.Tmux;

namespace CortexBridge.Api.Endpoints;

/// <summary>
/// POST /api/sessions/{projectId}/queue — parity with the CC CLI's /btw
/// command: lets the PWA user "tape on" context for the next turn without
/// interrupting the current one.
///
/// Two paths depending on CC state at submit time:
///   - IDLE  → paste immediately (identical to /reply); returns mode="sent-immediate"
///   - BUSY  → buffer in SessionQueue (1 slot per project); returns mode="queued".
///             SessionQueueFlusher flushes the slot when processing flips false.
///
/// Replacing a pending entry returns replaced=true so the PWA can warn the
/// user (they lost the previously-queued message). The buffer survives
/// SSE disconnects but is dropped by TTL (5 min) so a forgotten queue
/// doesn't paste mid-week.
/// </summary>
public static class QueueEndpoint
{
    public record QueueRequest(string Text);

    public static void MapQueue(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sessions/{projectId}/queue", PostQueue);
    }

    private static async Task<IResult> PostQueue(
        string projectId,
        QueueRequest body,
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
        var log = loggerFactory.CreateLogger("Queue");
        if (string.IsNullOrEmpty(body.Text))
            return ResultsHelpers.Error(400, "queue.empty", "text is required");

        var bearer = ctx.GetAuthToken();
        if (bearer is not null && !limiter.TryAcquire(bearer.Id))
            return ResultsHelpers.Error(429, "rate_limit.exceeded",
                "Too many requests — limit is 30 per minute per token");

        if (!await tmux.WindowExistsAsync(projectId, ct))
            return ResultsHelpers.Error(404, "tmux.window_missing",
                $"No tmux window named '{projectId}' in session '{tmux.SessionName}'");

        var (sErr, active) = await CheckSessionAsync(ctx, scanner, projectId, ct);
        if (sErr is not null) return sErr;

        var hash = HashText(body.Text);

        // Idle path: paste immediately — same semantics as /reply.
        if (!state.Processing(projectId, active?.SessionUuid))
        {
            using var lk = mutex.TryAcquire(projectId);
            if (lk is null)
                return ResultsHelpers.Error(409, "reply.in_flight",
                    "Another reply is already being delivered for this project");

            try
            {
                // Option C: SendReplyWithPickerDismissAsync wraps the picker
                // dance. CC may have re-opened a picker between the Processing
                // check and now; defensive helper handles it.
                await tmux.SendReplyWithPickerDismissAsync(projectId, body.Text, ct);
            }
            catch (TmuxException ex)
            {
                log.LogError(ex, "queue immediate-paste failed for {Project}", projectId);
                await AuditAsync(db, bearer, projectId, active?.SessionUuid,
                    "queue.sent_immediate", hash, "error", ex.Message, ct);
                return ResultsHelpers.Error(500, "tmux.send_failed", "tmux invocation failed");
            }

            state.SetNeedsInput(projectId, false, sessionUuid: active?.SessionUuid);
            state.SetProcessing(projectId, true, active?.SessionUuid);
            await AuditAsync(db, bearer, projectId, active?.SessionUuid,
                "queue.sent_immediate", hash, "ok", null, ct);

            return Results.Json(new
            {
                acceptedAt = DateTimeOffset.UtcNow.ToString("o"),
                mode = "sent-immediate",
            }, Json.Default, statusCode: 202);
        }

        // Busy path: enqueue (or replace existing slot). Flusher will paste
        // when processing flips false.
        var entry = new SessionQueue.QueuedReply(
            projectId, body.Text, hash, bearer?.Id, active?.SessionUuid,
            DateTimeOffset.UtcNow);
        var prior = queue.PutOrReplace(entry);

        if (prior is not null)
            await AuditAsync(db, bearer, projectId, prior.SessionUuid,
                "queue.replaced", prior.PayloadHash, "ok", null, ct);
        await AuditAsync(db, bearer, projectId, active?.SessionUuid,
            "queue.enqueued", hash, "ok", null, ct);

        return Results.Json(new
        {
            acceptedAt = DateTimeOffset.UtcNow.ToString("o"),
            mode = "queued",
            replaced = prior is not null,
        }, Json.Default, statusCode: 202);
    }

    private static async Task AuditAsync(
        BridgeDbContext db, BearerToken? token, string projectId, string? sessionUuid,
        string action, string? payloadHash, string result, string? detail, CancellationToken ct)
    {
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

    private static string HashText(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }

    // ADR-016 Slice 2 ?session= precondition — 3rd copy (after ReplyEndpoints
    // and InterruptEndpoint). Per project rule "three similar lines is better
    // than a premature abstraction" THIS IS the threshold; an extraction
    // helper would be justified now, but is out of scope for this sub-arc
    // (would force a backward-compat ripple through ReplyEndpoints). Tracked
    // for a follow-up refactor commit.
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
