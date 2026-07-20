using CortexBridge.Api.Auth;
using CortexBridge.Api.Common;
using CortexBridge.Api.Sessions;
using CortexBridge.Api.Streaming;
using CortexBridge.Api.Tmux;

namespace CortexBridge.Api.Endpoints;

public static class StreamEndpoint
{
    public static void MapStream(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sessions/{projectId}/stream", StreamHandler);
    }

    private static async Task StreamHandler(
        HttpContext ctx,
        string projectId,
        StreamTokenStore streamTokens,
        WatcherRegistry registry,
        SessionScanner scanner,
        SessionStateRegistry state,
        SessionOwnershipRegistry ownership,
        CortexBridge.Api.Data.BridgeDbContext db,
        TmuxClient tmux,
        JsonlReader jsonl,
        ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger("Stream");

        // Auth via stream-token query param (EventSource cannot send headers)
        var streamToken = ctx.Request.Query["t"].ToString();
        var bearerId = streamTokens.ConsumeAndValidate(streamToken);
        if (bearerId is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = new { code = "auth.invalid_stream_token", message = "stream-token missing or expired" } });
            return;
        }

        var active = await scanner.ResolveAsync(projectId, ctx.RequestAborted);
        var tmuxRunning = await tmux.WindowExistsAsync(projectId, ctx.RequestAborted);

        // Spec 04: ?session=<uuid> addresses a specific session. If it doesn't match
        // the currently-active session, emit a single status frame marking the view
        // read-only and close the stream — historical JSONLs aren't being appended to.
        var requestedSession = ctx.Request.Query["session"].ToString();
        // Zero-gap SSE handshake (docs/specs/01): the PWA passes the byte offset
        // its REST transcript read consumed up to (?since=) plus the session it
        // belongs to (?sinceSession=). Below, after subscribing to live deltas,
        // we replay only [since, EOF) to THIS connection — the records appended
        // in the gap between the REST read and this connect — so nothing is lost
        // and the full history is never re-streamed.
        long.TryParse(ctx.Request.Query["since"].ToString(), out var sinceOffset);
        var sinceSession = ctx.Request.Query["sinceSession"].ToString();
        if (!string.IsNullOrEmpty(requestedSession)
            && (active is null || !string.Equals(requestedSession, active.SessionUuid,
                StringComparison.OrdinalIgnoreCase)))
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache, no-transform";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var sseRO = new SseChannel(ctx.Response);
            await sseRO.WriteEventAsync("status", new SessionStatus(
                Kind: "status",
                ProjectId: projectId,
                SessionUuid: requestedSession,
                NeedsInput: false,
                Running: false,
                LastEventAt: null,
                NotificationMessage: null,
                Processing: false
            ), ctx.RequestAborted);
            return;
        }

        // Reject only if neither JSONL nor tmux exists — i.e., truly nothing to stream.
        // If tmux is running but no JSONL yet (fresh CC just spawned), accept the
        // connection and poll for JSONL to materialize below.
        if (active is null && !tmuxRunning)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsJsonAsync(new { error = new { code = "session.not_found", message = $"No active session for project '{projectId}'" } });
            return;
        }

        // SSE response headers
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache, no-transform";
        ctx.Response.Headers.Pragma = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no"; // disable proxy buffering

        // Per-connection serialized writer (Fix #2 — keepalive + message loop must not interleave frames)
        var sse = new SseChannel(ctx.Response);

        // Per spec 01 §"Status.running derivation": running = window exists in tmux session 'cc'.
        var running = tmuxRunning;

        // If active is null (fresh tmux session — no JSONL yet), keep connection alive
        // with status + keepalives, polling every 2s until a JSONL appears or the client
        // disconnects. Cap the wait so we don't tie up resources indefinitely on dead sessions.
        if (active is null)
        {
            await sse.WriteEventAsync("status", new SessionStatus(
                Kind: "status",
                ProjectId: projectId,
                SessionUuid: null,
                NeedsInput: state.NeedsInput(projectId),
                Running: running,
                LastEventAt: null,
                NotificationMessage: state.NotificationMessage(projectId),
                Processing: state.Processing(projectId)
            ), ctx.RequestAborted);

            using var pollKeepaliveTimer = new PeriodicTimer(TimeSpan.FromSeconds(25));
            var pollKeepaliveTask = Task.Run(async () =>
            {
                try
                {
                    while (await pollKeepaliveTimer.WaitForNextTickAsync(ctx.RequestAborted))
                        await sse.WriteKeepaliveAsync(ctx.RequestAborted);
                }
                catch (OperationCanceledException) { }
            }, ctx.RequestAborted);

            var pollDeadline = DateTimeOffset.UtcNow.AddSeconds(120);
            try
            {
                while (active is null
                    && DateTimeOffset.UtcNow < pollDeadline
                    && !ctx.RequestAborted.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ctx.RequestAborted);
                    active = await scanner.ResolveAsync(projectId, ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            try { pollKeepaliveTimer.Dispose(); } catch { }
            try { await pollKeepaliveTask; } catch { }

            // Either JSONL appeared, or we hit the deadline / client disconnected.
            if (active is null) return;
        }

        var (reader, unsubscribe) = await registry.SubscribeAsync(projectId, ctx.RequestAborted);

        // Push a fresh status frame whenever needsInput flips for THIS project.
        // Fire-and-forget into SseChannel — its internal lock serializes with the message pump.
        // Hook handler must not block on a slow subscriber, so we don't await here.
        void OnStatusChanged(string p, string? changedUuid)
        {
            if (p != projectId) return;
            // ADR-016: this connection tracks the project's live session
            // (active.SessionUuid). Ignore a state change for a *different*
            // (background) session — its state must not move this frame.
            if (changedUuid is not null && active.SessionUuid is not null
                && !string.Equals(changedUuid, active.SessionUuid, StringComparison.OrdinalIgnoreCase))
                return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await sse.WriteEventAsync("status", new SessionStatus(
                        Kind: "status",
                        ProjectId: projectId,
                        SessionUuid: active.SessionUuid,
                        NeedsInput: state.NeedsInput(projectId, active.SessionUuid),
                        Running: await tmux.WindowExistsAsync(projectId, ctx.RequestAborted),
                        LastEventAt: state.LastEventAt(projectId, active.SessionUuid)?.ToString("o"),
                        NotificationMessage: state.NotificationMessage(projectId, active.SessionUuid),
                        Processing: state.Processing(projectId, active.SessionUuid)
                    ), ctx.RequestAborted);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { log.LogDebug(ex, "Status push failed for {Project}", projectId); }
            });
        }
        state.StatusChanged += OnStatusChanged;

        // ADR-015 / spec 05: push owner_change frames when the bridge updates ownership
        // (handoff endpoint or take-over). Fire-and-forget so the registry's event
        // handler doesn't block.
        void OnOwnerChanged(string p, SessionOwnershipRegistry.Owner newOwner)
        {
            if (p != projectId) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    var (owner, uuid, since) = await ownership.ResolveAsync(
                        projectId, db, tmux, scanner, ctx.RequestAborted);
                    await sse.WriteEventAsync("owner_change", new
                    {
                        owner = owner.ToString().ToLowerInvariant(),
                        sessionUuid = uuid,
                        sinceUtc = since.ToString("o"),
                        // ADR-017: drives the PWA's single guarded "Tiếp quản".
                        takeoverSafe = ownership.TakeoverSafe(projectId),
                    }, ctx.RequestAborted);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { log.LogDebug(ex, "Owner-change push failed for {Project}", projectId); }
            });
        }
        ownership.OwnerChanged += OnOwnerChanged;

        try
        {
            // Connect-time pane reconcile. Processing/NeedsInput are hook-TRANSITION
            // driven (UserPromptSubmit/Pre/PostToolUse set; Stop/Notification clear),
            // so a turn that was ALREADY running before THIS connection opened can
            // leave state.Processing=false here — most acutely after a container
            // restart, since CC lives on the host tmux (ADR-013) and fired its hooks
            // before our in-memory state existed. The initial snapshot would then say
            // "idle" → the PWA shows no busy strip and the pane-preview loop below
            // (gated on the same state.Processing) never emits → the live view only
            // appears after the user sends a reply (which forces UserPromptSubmit).
            // Seed the signal once from the live pane so it shows immediately. ADDITIVE
            // only (Working→Processing, Blocked→needsInput); never clears — dead-latch
            // clearing stays ProcessingWatchdog's job (it has the debounce/timing to
            // do it safely). Mirrors the watchdog's PaneClassifier authority.
            if (running
                && !state.Processing(projectId, active.SessionUuid)
                && !state.NeedsInput(projectId, active.SessionUuid))
            {
                try
                {
                    var pane = await tmux.CapturePaneAsync(projectId, ctx.RequestAborted);
                    switch (PaneClassifier.Classify(pane))
                    {
                        case PaneClassifier.PaneState.Working:
                            state.SetProcessing(projectId, true, active.SessionUuid);
                            break;
                        case PaneClassifier.PaneState.Blocked:
                        {
                            // This seed bypasses the watchdog's consecutive-scan
                            // debounce, so confirm on a second capture: a single
                            // mid-redraw frame at connect time must not re-arm
                            // the banner (same defect class as the 2026-07-18
                            // re-asked-AskUserQuestion incident). A real picker
                            // is static — 400ms later it still classifies Blocked.
                            await Task.Delay(TimeSpan.FromMilliseconds(400), ctx.RequestAborted);
                            var confirm = await tmux.CapturePaneAsync(projectId, ctx.RequestAborted);
                            if (PaneClassifier.Classify(confirm) == PaneClassifier.PaneState.Blocked)
                                state.SetNeedsInput(projectId, true, sessionUuid: active.SessionUuid);
                            break;
                        }
                    }
                }
                catch (TmuxException) { /* transient — snapshot falls back to hook state */ }
            }

            // Initial status snapshot
            await sse.WriteEventAsync("status", new SessionStatus(
                Kind: "status",
                ProjectId: projectId,
                SessionUuid: active.SessionUuid,
                NeedsInput: state.NeedsInput(projectId, active.SessionUuid),
                Running: running,
                LastEventAt: state.LastEventAt(projectId, active.SessionUuid)?.ToString("o"),
                NotificationMessage: state.NotificationMessage(projectId, active.SessionUuid),
                Processing: state.Processing(projectId, active.SessionUuid)
            ), ctx.RequestAborted);

            // Pump messages + keepalive (concurrent writers, both go through SseChannel's lock)
            using var keepaliveTimer = new PeriodicTimer(TimeSpan.FromSeconds(25));
            var keepaliveTask = Task.Run(async () =>
            {
                try
                {
                    while (await keepaliveTimer.WaitForNextTickAsync(ctx.RequestAborted))
                        await sse.WriteKeepaliveAsync(ctx.RequestAborted);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { log.LogDebug(ex, "Keepalive ended for {Project}", projectId); }
            }, ctx.RequestAborted);

            // Ephemeral live-view: CC writes a JSONL record only when a block
            // COMPLETES, so the transcript lags a long generation / tool run.
            // While Processing (and not blocked — the needsInput banner owns
            // that case), poll the tmux pane ~1s and push the work tail so the
            // PWA can show what CC is doing NOW. One empty frame on
            // Processing→false tells the PWA to hide the panel; the canonical
            // JSONL bubble then replaces it. Read-only; tmux faults swallowed;
            // dies with the connection (ctx.RequestAborted).
            var lastPreview = string.Empty;
            var previewCleared = true;
            var panePreviewTask = Task.Run(async () =>
            {
                try
                {
                    while (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        // Two live modes share this loop:
                        //  - Processing (running turn): show the work TAIL (Tail
                        //    strips the input box → the streaming text/tool run).
                        //  - NeedsInput (open picker): show the PICKER itself so
                        //    the PWA "raw TUI remote" reflects each keypress. This
                        //    is the OPPOSITE region — PickerView KEEPS the box that
                        //    Tail removes (cursor/checkbox/tab-bar/footer). Without
                        //    this the remote is blind (livePane empty during a
                        //    Blocked picker) — the whole point of the redesign.
                        var needs = state.NeedsInput(projectId);
                        var live = needs || state.Processing(projectId);
                        if (live)
                        {
                            string[] tail = [];
                            try
                            {
                                if (await tmux.WindowExistsAsync(projectId, ctx.RequestAborted))
                                {
                                    var pane = await tmux.CapturePaneAsync(projectId, ctx.RequestAborted);
                                    tail = needs ? PanePreview.PickerView(pane) : PanePreview.Tail(pane);
                                }
                            }
                            catch (TmuxException) { /* transient — try next tick */ }

                            var joined = string.Join("\n", tail);
                            if (joined != lastPreview)
                            {
                                lastPreview = joined;
                                previewCleared = false;
                                await sse.WriteEventAsync("pane_preview",
                                    new { projectId, lines = tail }, ctx.RequestAborted);
                            }
                            await Task.Delay(TimeSpan.FromSeconds(1), ctx.RequestAborted);
                        }
                        else
                        {
                            if (!previewCleared)
                            {
                                previewCleared = true;
                                lastPreview = string.Empty;
                                await sse.WriteEventAsync("pane_preview",
                                    new { projectId, lines = Array.Empty<string>() },
                                    ctx.RequestAborted);
                            }
                            await Task.Delay(TimeSpan.FromSeconds(2), ctx.RequestAborted);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { log.LogDebug(ex, "pane_preview ended for {Project}", projectId); }
            }, ctx.RequestAborted);

            // Zero-gap catch-up: replay [since, EOF) to THIS connection BEFORE
            // draining the live channel, so the gap records land in order ahead
            // of any live delta. The watcher starts at EOF (no history flush), so
            // the only overlap with the live stream is a few records appended
            // between this read and the watcher start — deduped client-side by
            // uuid (+ null-uuid ts/kind hardening). Skipped when the client gave
            // no offset (full-load fallback) or the active session changed since
            // its REST read (a session_switch frame will drive a fresh re-fetch).
            if (sinceOffset > 0
                && string.Equals(sinceSession, active.SessionUuid, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var (gap, _) = await jsonl.ReadFromOffsetAsync(
                        active.JsonlPath, sinceOffset, projectId, ctx.RequestAborted);
                    foreach (var m in gap)
                        await sse.WriteEventAsync("message", m, ctx.RequestAborted);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "Catch-up read failed for {Project} @ {Since}", projectId, sinceOffset);
                }
            }

            await foreach (var msg in reader.ReadAllAsync(ctx.RequestAborted))
            {
                var evt = msg.Kind switch
                {
                    "session_switch" => "session_switch",
                    "session_reset" => "session_reset",
                    _ => "message"
                };
                await sse.WriteEventAsync(evt, msg, ctx.RequestAborted);
            }

            try { await keepaliveTask; } catch { /* ignore */ }
            try { await panePreviewTask; } catch { /* ignore */ }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal.
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Stream loop failed for {Project}", projectId);
        }
        finally
        {
            state.StatusChanged -= OnStatusChanged;
            ownership.OwnerChanged -= OnOwnerChanged;
            await unsubscribe();
        }
    }
}
