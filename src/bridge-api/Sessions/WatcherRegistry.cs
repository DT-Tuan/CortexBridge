using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// One JsonlWatcher per project, shared across SSE subscribers.
/// Lazily created on first subscriber; reference-counted; idle watchers
/// linger 5 minutes after last subscriber detaches (per src/bridge-api/CLAUDE.md).
/// </summary>
public class WatcherRegistry : IAsyncDisposable
{
    private readonly SessionScanner _scanner;
    private readonly JsonlReader _reader;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WatcherRegistry> _log;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public WatcherRegistry(SessionScanner scanner, JsonlReader reader, ILoggerFactory loggerFactory, ILogger<WatcherRegistry> log)
    {
        _scanner = scanner;
        _reader = reader;
        _loggerFactory = loggerFactory;
        _log = log;
    }

    private class Entry
    {
        public required JsonlWatcher Watcher;
        public List<Channel<SessionMessage>> Fanout = new();
        public int RefCount;
        public CancellationTokenSource? IdleTimerCts;
        public Task? FanoutTask;
        public CancellationTokenSource FanoutCts = new();
    }

    /// <summary>
    /// Subscribe to a project's stream. Returns a per-subscriber channel reader and
    /// an unsubscribe action. The watcher is created on first subscribe.
    /// </summary>
    public async Task<(ChannelReader<SessionMessage> reader, Func<ValueTask> unsubscribe)> SubscribeAsync(
        string projectId, CancellationToken ct)
    {
        bool created = false;
        var entry = _entries.GetOrAdd(projectId, _ =>
        {
            created = true;
            var w = new JsonlWatcher(projectId, _scanner, _reader, _loggerFactory.CreateLogger<JsonlWatcher>());
            return new Entry { Watcher = w };
        });

        // Cancel any pending idle-shutdown for this entry (we have a new subscriber)
        entry.IdleTimerCts?.Cancel();
        entry.IdleTimerCts = null;

        // Allocate this subscriber's channel
        var subChan = Channel.CreateBounded<SessionMessage>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        // Register subscriber BEFORE starting watcher/fanout so the first LIVE
        // deltas (appended right after StartAsync's EOF snapshot) aren't lost in a
        // race against FanoutLoopAsync starting up. NOTE: the watcher no longer
        // flushes history on start (EOF-start) — history is served by REST and the
        // per-connection ?since= catch-up in StreamEndpoint (docs/specs/01). Fix #1.
        lock (entry.Fanout) { entry.Fanout.Add(subChan); entry.RefCount++; }

        if (created)
        {
            try
            {
                await entry.Watcher.StartAsync(ct);
                entry.FanoutTask = Task.Run(() => FanoutLoopAsync(entry, projectId, entry.FanoutCts.Token));
            }
            catch
            {
                // If watcher startup fails, undo the registration to avoid leaks
                lock (entry.Fanout) { entry.Fanout.Remove(subChan); entry.RefCount--; }
                subChan.Writer.TryComplete();
                _entries.TryRemove(projectId, out _);
                throw;
            }
        }

        async ValueTask Unsub()
        {
            lock (entry.Fanout) { entry.Fanout.Remove(subChan); entry.RefCount--; }
            subChan.Writer.TryComplete();
            if (entry.RefCount == 0) ScheduleIdleShutdown(projectId, entry);
            await ValueTask.CompletedTask;
        }

        return (subChan.Reader, Unsub);
    }

    private async Task FanoutLoopAsync(Entry entry, string projectId, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in entry.Watcher.Reader.ReadAllAsync(ct))
            {
                List<Channel<SessionMessage>> snapshot;
                lock (entry.Fanout) snapshot = entry.Fanout.ToList();
                foreach (var ch in snapshot)
                {
                    try { await ch.Writer.WriteAsync(msg, ct); }
                    catch { /* subscriber gone */ }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Fanout loop terminated for {Project}", projectId);
        }
    }

    private void ScheduleIdleShutdown(string projectId, Entry entry)
    {
        var cts = new CancellationTokenSource();
        entry.IdleTimerCts = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromMinutes(5), cts.Token); }
            catch (OperationCanceledException) { return; }

            if (entry.RefCount == 0 && _entries.TryRemove(projectId, out _))
            {
                _log.LogInformation("Disposing idle watcher for {Project}", projectId);
                try { entry.FanoutCts.Cancel(); } catch { }
                if (entry.FanoutTask is not null)
                    try { await entry.FanoutTask; } catch { }
                await entry.Watcher.DisposeAsync();
                entry.FanoutCts.Dispose();
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, entry) in _entries)
        {
            try { entry.FanoutCts.Cancel(); } catch { }
            if (entry.FanoutTask is not null)
                try { await entry.FanoutTask; } catch { }
            await entry.Watcher.DisposeAsync();
            entry.FanoutCts.Dispose();
        }
        _entries.Clear();
    }
}
