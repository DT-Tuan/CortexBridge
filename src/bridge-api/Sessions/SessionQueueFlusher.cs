using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using CortexBridge.Api.Tmux;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// Background service that flushes queued replies into tmux when CC becomes
/// idle (SessionStateRegistry.Processing transitions from true → false).
/// Polls every 1s — flushing is cheap (1 entry per project at most) and
/// SSE handler isn't reliable for trigger (the PWA could be disconnected).
///
/// Drops entries older than <see cref="EntryTtl"/>. TTL must stay short
/// (5 min default) — a stale queued message that pastes 30 minutes after
/// the user typed it would be surprising and dangerous.
/// </summary>
public class SessionQueueFlusher : BackgroundService
{
    private readonly SessionQueue _queue;
    private readonly SessionStateRegistry _state;
    private readonly TmuxClient _tmux;
    private readonly ProjectReplyMutex _mutex;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionQueueFlusher> _log;
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(5);

    public SessionQueueFlusher(
        SessionQueue queue,
        SessionStateRegistry state,
        TmuxClient tmux,
        ProjectReplyMutex mutex,
        IServiceScopeFactory scopeFactory,
        ILogger<SessionQueueFlusher> log)
    {
        _queue = queue;
        _state = state;
        _tmux = tmux;
        _mutex = mutex;
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogError(ex, "SessionQueueFlusher tick failed"); }
            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Single pass over the queue snapshot. Exposed for unit tests so the
    /// flush behavior can be exercised without spinning the BackgroundService.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        foreach (var entry in _queue.Snapshot())
        {
            // TTL expiry first — even if processing=true forever, we drop.
            if (DateTimeOffset.UtcNow - entry.EnqueuedAt > EntryTtl)
            {
                if (_queue.Clear(entry.ProjectId))
                {
                    await AuditAsync(entry, "queue.expired", "expired", ct);
                    _log.LogInformation("Queue entry expired for {Project} (age > {Ttl})",
                        entry.ProjectId, EntryTtl);
                }
                continue;
            }

            // Wait while CC is still working — the whole point of the queue.
            if (_state.Processing(entry.ProjectId, entry.SessionUuid)) continue;

            // Idle path. Verify the window still exists; mode could've swapped
            // to PC since enqueue (no tmux window) — drop with audit.
            if (!await _tmux.WindowExistsAsync(entry.ProjectId, ct))
            {
                _queue.Clear(entry.ProjectId);
                await AuditAsync(entry, "queue.window_missing", "error", ct);
                continue;
            }

            using var lk = _mutex.TryAcquire(entry.ProjectId);
            if (lk is null) continue;   // a /reply is mid-flight; retry next tick

            // Atomically pop. Guard against another tick (or PutOrReplace) that
            // swapped the slot between Snapshot() and now — paste only the
            // ENTRY WE SAW.
            var popped = _queue.TryDequeue(entry.ProjectId);
            if (popped is null || popped.PayloadHash != entry.PayloadHash) continue;

            try
            {
                // Option C: SendReplyWithPickerDismissAsync wraps the picker
                // dance (Esc + 250ms if pane is Blocked, then paste). CRITICAL:
                // when CC ends a turn by opening a picker, Processing flips
                // false (gate that released us) but pane is Blocked → bracketed
                // paste eaten as cancel → message lost. The extension handles
                // it in one place so future paste paths can't forget.
                var dismissed = await _tmux.SendReplyWithPickerDismissAsync(
                    popped.ProjectId, popped.Text, ct);
                if (dismissed)
                    _log.LogInformation(
                        "Flush pre-dismissed picker for {Project} (PaneClassifier=Blocked)",
                        popped.ProjectId);

                // Optimistic Processing(true) so the UI locks immediately even
                // before UserPromptSubmit hook lands. Stop hook will clear.
                _state.SetProcessing(popped.ProjectId, true, popped.SessionUuid);
                await AuditAsync(popped, "queue.flushed", "ok", ct);
                _log.LogInformation("Flushed queued reply for {Project} (len {Len})",
                    popped.ProjectId, popped.Text.Length);
            }
            catch (TmuxException ex)
            {
                _log.LogError(ex, "Flush failed for {Project}", popped.ProjectId);
                await AuditAsync(popped, "queue.flushed", "error", ct);
            }
        }
    }

    private async Task AuditAsync(
        SessionQueue.QueuedReply entry, string action, string result, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTimeOffset.UtcNow,
            ProjectId = entry.ProjectId,
            SessionUuid = entry.SessionUuid,
            Action = action,
            TokenId = entry.TokenId,
            PayloadHash = entry.PayloadHash,
            Result = result,
            Detail = null,
        });
        await db.SaveChangesAsync(ct);
    }
}
