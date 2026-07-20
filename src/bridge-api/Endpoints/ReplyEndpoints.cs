using System.Security.Cryptography;
using System.Text;
using CortexBridge.Api.Auth;
using CortexBridge.Api.Common;
using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using CortexBridge.Api.Sessions;
using CortexBridge.Api.Tmux;

namespace CortexBridge.Api.Endpoints;

public static class ReplyEndpoints
{
    public record ReplyRequest(string Text);

    public static void MapReply(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sessions/{projectId}/reply", PostReply);
        app.MapPost("/api/sessions/{projectId}/choice/{digit}", PostChoice);
        app.MapPost("/api/sessions/{projectId}/quick-reply/{action}", PostQuickReply);
        app.MapPost("/api/sessions/{projectId}/cancel-picker", PostCancelPicker);
        app.MapPost("/api/sessions/{projectId}/key/{key}", PostKey);
    }

    // Friendly key token (from the PWA "raw TUI remote" keypad) → tmux key name.
    // Digits are handled separately (SendMenuChoiceAsync). This is the WHOLE
    // vocabulary the remote can send — nothing else reaches send-keys through
    // this path, so a picker can be driven exactly as at the keyboard with no
    // answer composition / pane-scrape guessing (ADR-026 → the raw-remote
    // redesign, 2026-07-18: the scrape+compose card kept mis-submitting and
    // injecting garbage into replies; direct key pass-through removes the
    // inference entirely).
    private static readonly Dictionary<string, string> KeyMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["up"] = "Up", ["down"] = "Down", ["left"] = "Left", ["right"] = "Right",
            ["enter"] = "Enter", ["esc"] = "Escape", ["escape"] = "Escape",
            ["tab"] = "Tab", ["btab"] = "BTab", ["space"] = "Space",
            ["bspace"] = "BSpace", ["home"] = "Home", ["end"] = "End",
            ["pageup"] = "PageUp", ["pagedown"] = "PageDown",
        };

    /// <summary>
    /// Send ONE raw key to the session's tmux pane — the single primitive behind
    /// the PWA's "raw TUI remote" keypad for interactive pickers (permission
    /// prompt / AskUserQuestion). <paramref name="key"/> is a friendly token:
    /// a digit 1-9 (menu accelerator, via SendMenuChoiceAsync) or a named key
    /// in <see cref="KeyMap"/> (arrows / enter / esc / tab / space / …). The
    /// keypad mirrors what a person would press at the keyboard, so there is NO
    /// answer composition and NO submit-key guessing — the user drives CC's real
    /// TUI and watches the live pane preview react.
    ///
    /// Deliberately does NOT mutate needsInput/Processing: a single key may be a
    /// mid-picker toggle or navigation, not an answer. The picker's real state
    /// flows back through the existing pane-preview SSE + /prompt poll; when the
    /// user actually submits/cancels, the pane stops being Blocked and the
    /// watchdog/poll clears needsInput. This removes every optimistic-state race
    /// that made the old card dismiss-then-reappear.
    /// </summary>
    private static async Task<IResult> PostKey(
        string projectId,
        string key,
        HttpContext ctx,
        TmuxClient tmux,
        ProjectReplyMutex mutex,
        TokenRateLimiter limiter,
        SessionScanner scanner,
        BridgeDbContext db,
        CancellationToken ct)
    {
        var isDigit = key.Length == 1 && key[0] is >= '1' and <= '9';
        if (!isDigit && !KeyMap.ContainsKey(key))
            return ResultsHelpers.Error(400, "key.unknown",
                "key must be a digit 1-9 or one of: "
                + string.Join(", ", KeyMap.Keys));

        var bearer = ctx.GetAuthToken();
        if (bearer is not null && !limiter.TryAcquire(bearer.Id))
            return ResultsHelpers.Error(429, "rate_limit.exceeded",
                "Too many keys — limit is 30 per minute per token");

        if (!await tmux.WindowExistsAsync(projectId, ct))
            return ResultsHelpers.Error(404, "tmux.window_missing",
                $"No tmux window named '{projectId}'");

        // ADR-016 Slice 2: optional ?session= must match the live-slot UID so a
        // key can never land on the wrong session.
        var (sErr, active) = await CheckSessionAsync(ctx, scanner, projectId, ct);
        if (sErr is not null) return sErr;

        using var lk = mutex.TryAcquire(projectId);
        if (lk is null)
            return ResultsHelpers.Error(409, "reply.in_flight",
                "Another reply is already being delivered for this project");

        try
        {
            if (isDigit) await tmux.SendMenuChoiceAsync(projectId, key, ct);
            else await tmux.SendKeyAsync(projectId, KeyMap[key], ct);
        }
        catch (TmuxException ex)
        {
            await AuditAsync(db, bearer, projectId, active?.SessionUuid, "key", null, "error", ex.Message, ct);
            return ResultsHelpers.Error(500, "tmux.send_failed", "tmux invocation failed");
        }

        // Key name is safe to log (no transcript content). Lets the audit trail
        // reconstruct a remote-driven picker session.
        await AuditAsync(db, bearer, projectId, active?.SessionUuid, "key", null, "ok",
            isDigit ? "digit:" + key : KeyMap[key], ct);
        return Results.Json(new { acceptedAt = DateTimeOffset.UtcNow.ToString("o") },
            Json.Default, statusCode: 202);
    }

    /// <summary>
    /// Select a numbered option in a CC interactive menu (permission prompt,
    /// AskUserQuestion). Sends the digit as a RAW keystroke (no bracketed paste —
    /// see TmuxClient.SendMenuChoiceAsync). This is what the PWA's 1/2/3 buttons
    /// hit; the old path pasted the digit as free text which a menu read as a
    /// cancel → "[Request interrupted]" → stuck "thinking".
    /// Deliberately does NOT optimistically SetProcessing(true): an approve that
    /// proceeds re-arms Processing authoritatively via the next PreToolUse
    /// activity hook; a deny leaves the session idle (composer must stay usable so
    /// the user can redirect). The watchdog backstops either way.
    /// </summary>
    private static async Task<IResult> PostChoice(
        string projectId,
        string digit,
        HttpContext ctx,
        TmuxClient tmux,
        ProjectReplyMutex mutex,
        TokenRateLimiter limiter,
        SessionScanner scanner,
        SessionStateRegistry state,
        BridgeDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Choice");
        if (digit.Length != 1 || digit[0] is < '1' or > '9')
            return ResultsHelpers.Error(400, "choice.bad_digit",
                "choice must be a single digit 1-9 (CC menu accelerator range)");

        var bearer = ctx.GetAuthToken();
        if (bearer is not null && !limiter.TryAcquire(bearer.Id))
            return ResultsHelpers.Error(429, "rate_limit.exceeded",
                "Too many replies — limit is 30 per minute per token");

        if (!await tmux.WindowExistsAsync(projectId, ct))
            return ResultsHelpers.Error(404, "tmux.window_missing",
                $"No tmux window named '{projectId}'");

        // ADR-016 Slice 2: optional ?session= must match the live-slot UID.
        // Check BEFORE the reply mutex so a 409 mismatch never contends with a
        // parallel real reply (would falsely report reply.in_flight).
        var (sErr, active) = await CheckSessionAsync(ctx, scanner, projectId, ct);
        if (sErr is not null) return sErr;

        using var lk = mutex.TryAcquire(projectId);
        if (lk is null)
            return ResultsHelpers.Error(409, "reply.in_flight",
                "Another reply is already being delivered for this project");

        try
        {
            await tmux.SendMenuChoiceAsync(projectId, digit, ct);
        }
        catch (TmuxException ex)
        {
            log.LogError(ex, "tmux menu-choice failed for {Project}", projectId);
            await AuditAsync(db, bearer, projectId, active?.SessionUuid, "choice", null, "error", ex.Message, ct);
            return ResultsHelpers.Error(500, "tmux.send_failed", "tmux invocation failed");
        }

        state.SetNeedsInput(projectId, false);
        await AuditAsync(db, bearer, projectId, active?.SessionUuid, "choice", null, "ok", digit, ct);
        return Results.Json(new { acceptedAt = DateTimeOffset.UtcNow.ToString("o") },
            Json.Default, statusCode: 202);
    }

    /// <summary>
    /// Send Esc to a tmux window to dismiss CC's interactive AskUserQuestion picker.
    /// PWA can't drive the picker via paste-buffer reliably, so this gives users an
    /// out — abort picker, send free-text answer instead.
    /// </summary>
    private static async Task<IResult> PostCancelPicker(
        string projectId,
        HttpContext ctx,
        TmuxClient tmux,
        SessionScanner scanner,
        SessionStateRegistry state,
        CancellationToken ct)
    {
        if (!await tmux.WindowExistsAsync(projectId, ct))
            return ResultsHelpers.Error(404, "tmux.window_missing",
                $"No tmux window named '{projectId}'");

        // ADR-016 Slice 2: optional ?session= must match the live-slot UID.
        var (sErr, _) = await CheckSessionAsync(ctx, scanner, projectId, ct);
        if (sErr is not null) return sErr;

        try { await tmux.SendKeyAsync(projectId, "Escape", ct); }
        catch (TmuxException ex)
        {
            return ResultsHelpers.Error(500, "tmux.send_failed", ex.Message);
        }
        // Best-effort: clear needsInput too so banner dismisses immediately.
        state.SetNeedsInput(projectId, false);
        return Results.Json(new { acceptedAt = DateTimeOffset.UtcNow.ToString("o") },
            Json.Default, statusCode: 202);
    }

    private static async Task<IResult> PostReply(
        string projectId,
        ReplyRequest body,
        HttpContext ctx,
        TmuxClient tmux,
        ProjectReplyMutex mutex,
        TokenRateLimiter limiter,
        SessionScanner scanner,
        SessionStateRegistry state,
        BridgeDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Reply");
        if (string.IsNullOrEmpty(body.Text))
            return ResultsHelpers.Error(400, "reply.empty", "text is required");

        var bearer = ctx.GetAuthToken();
        if (bearer is not null && !limiter.TryAcquire(bearer.Id))
            return ResultsHelpers.Error(429, "rate_limit.exceeded",
                "Too many replies — limit is 30 per minute per token");

        if (!await tmux.WindowExistsAsync(projectId, ct))
            return ResultsHelpers.Error(404, "tmux.window_missing",
                $"No tmux window named '{projectId}' in session '{tmux.SessionName}'");

        // ADR-016 Slice 2: optional ?session= must match the live-slot UID.
        var (sErr, active) = await CheckSessionAsync(ctx, scanner, projectId, ct);
        if (sErr is not null) return sErr;

        using var lk = mutex.TryAcquire(projectId);
        if (lk is null)
            return ResultsHelpers.Error(409, "reply.in_flight",
                "Another reply is already being delivered for this project");

        var hash = HashText(body.Text);

        // Option C (2026-06-07): SendReplyWithPickerDismissAsync wraps the
        // picker-dismiss dance every paste path must honour. Esc ONLY when
        // the pane is actually Blocked so a running turn is never interrupted.
        // Best-effort — on any tmux hiccup fall through to the paste regardless.
        try
        {
            await tmux.SendReplyWithPickerDismissAsync(projectId, body.Text, ct);
        }
        catch (TmuxException ex)
        {
            log.LogError(ex, "tmux send-reply failed for {Project}", projectId);
            await AuditAsync(db, bearer, projectId, active?.SessionUuid, "reply", hash, "error", ex.Message, ct);
            return ResultsHelpers.Error(500, "tmux.send_failed", "tmux invocation failed");
        }

        // Successful inject — clear needsInput; optimistically mark processing so
        // the composer locks immediately (anti "nhồi lệnh") even before the
        // UserPromptSubmit hook lands. Stop hook will clear it when the turn ends.
        state.SetNeedsInput(projectId, false, sessionUuid: active?.SessionUuid);
        state.SetProcessing(projectId, true, active?.SessionUuid);
        await AuditAsync(db, bearer, projectId, active?.SessionUuid, "reply", hash, "ok", null, ct);

        return Results.Json(new
        {
            acceptedAt = DateTimeOffset.UtcNow.ToString("o"),
        }, Json.Default, statusCode: 202);
    }

    private static async Task<IResult> PostQuickReply(
        string projectId,
        string action,
        HttpContext ctx,
        TmuxClient tmux,
        ProjectReplyMutex mutex,
        TokenRateLimiter limiter,
        SessionScanner scanner,
        SessionStateRegistry state,
        BridgeDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("QuickReply");
        // Affirmative = option 1 (always "Yes" across CC permission/trust menus).
        // Negative ≠ "2": on a Write/Edit/Bash menu option 2 is "Yes, allow all
        // edits" — a deny that approves-everything (security-relevant). Esc is the
        // universal cancel CC shows on every menu, so deny = Escape.
        var act = action.ToLowerInvariant();
        var isAffirmative = act is "yes" or "approve";
        var isNegative = act is "no" or "deny";
        if (!isAffirmative && !isNegative)
            return ResultsHelpers.Error(400, "quick_reply.unknown_action",
                $"action must be one of: yes, no, approve, deny (got '{action}')");

        var bearer = ctx.GetAuthToken();
        if (bearer is not null && !limiter.TryAcquire(bearer.Id))
            return ResultsHelpers.Error(429, "rate_limit.exceeded",
                "Too many replies — limit is 30 per minute per token");

        if (!await tmux.WindowExistsAsync(projectId, ct))
            return ResultsHelpers.Error(404, "tmux.window_missing",
                $"No tmux window named '{projectId}'");

        // ADR-016 Slice 2: optional ?session= must match the live-slot UID.
        var (sErr, active) = await CheckSessionAsync(ctx, scanner, projectId, ct);
        if (sErr is not null) return sErr;

        using var lk = mutex.TryAcquire(projectId);
        if (lk is null)
            return ResultsHelpers.Error(409, "reply.in_flight",
                "Another reply is already being delivered for this project");

        try
        {
            // Raw keystroke, not paste: affirmative = digit "1" accelerator;
            // negative = Escape (universal cancel). Mirrors PostChoice.
            if (isAffirmative)
                await tmux.SendMenuChoiceAsync(projectId, "1", ct);
            else
                await tmux.SendKeyAsync(projectId, "Escape", ct);
        }
        catch (TmuxException ex)
        {
            log.LogError(ex, "tmux quick-reply failed for {Project}", projectId);
            await AuditAsync(db, bearer, projectId, active?.SessionUuid, "quick-reply", null, "error", ex.Message, ct);
            return ResultsHelpers.Error(500, "tmux.send_failed", "tmux invocation failed");
        }

        // No optimistic SetProcessing: see PostChoice — the next activity hook
        // re-arms it if the turn actually proceeds; watchdog backstops.
        state.SetNeedsInput(projectId, false);
        await AuditAsync(db, bearer, projectId, active?.SessionUuid, "quick-reply", null, "ok", act, ct);

        return Results.Json(new { acceptedAt = DateTimeOffset.UtcNow.ToString("o") },
            Json.Default, statusCode: 202);
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

    // ADR-016 Slice 2: shared `?session=` precondition for every write endpoint.
    // Reads the optional query param, validates its shape, resolves the live-slot
    // UID via scanner.ResolveAsync (Slice 1 semantics — NOT mtime), and on
    // mismatch returns a typed 409 carrying `activeSessionUuid` so the PWA can
    // offer an `activate` confirm without a second round-trip. Called BEFORE the
    // reply mutex so a mismatch never falsely contends as `reply.in_flight`.
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
