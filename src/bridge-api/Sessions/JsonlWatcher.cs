using System.Threading.Channels;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// One JsonlWatcher per active project. FileSystemWatcher with 100ms debounce on the
/// active .jsonl file. Reads from byte offset → emits parsed messages onto an output channel.
/// Detects: file growth (normal), file truncation (session_reset), new .jsonl appearing
/// in the same dir (session_switch).
///
/// Spec 03 §1.7.
/// </summary>
public class JsonlWatcher : IAsyncDisposable
{
    private readonly string _projectId;
    private readonly SessionScanner _scanner;
    private readonly JsonlReader _reader;
    private readonly ILogger<JsonlWatcher> _log;
    private readonly Channel<SessionMessage> _channel;
    private readonly CancellationTokenSource _cts = new();
    private FileSystemWatcher? _fsw;
    private string? _currentJsonlPath;
    private string? _currentSessionUuid;
    private long _offset;
    private DateTimeOffset _lastEvent = DateTimeOffset.MinValue;
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(100);
    private Task? _processTask;

    public ChannelReader<SessionMessage> Reader => _channel.Reader;
    public string ProjectId => _projectId;

    public JsonlWatcher(string projectId, SessionScanner scanner, JsonlReader reader, ILogger<JsonlWatcher> log)
    {
        _projectId = projectId;
        _scanner = scanner;
        _reader = reader;
        _log = log;
        _channel = Channel.CreateBounded<SessionMessage>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
        });
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var active = await _scanner.ResolveAsync(_projectId, ct);
        if (active is null)
        {
            _log.LogWarning("JsonlWatcher: project {Project} has no active session yet", _projectId);
            return;
        }

        _currentJsonlPath = active.JsonlPath;
        _currentSessionUuid = active.SessionUuid;
        // Start at EOF, NOT 0. SSE is a LIVE-DELTA stream: history is served by
        // the REST transcript endpoint, and the per-connection ?since= catch-up
        // in StreamEndpoint replays only the gap. Flushing the whole JSONL here
        // (the old behavior) scrambled a tail-loaded PWA — it received ~13k
        // historical records over SSE and appended them after the tail. The
        // watcher only ever needs to stream what's appended AFTER it starts.
        // See docs/specs/01 "SSE = live delta thuần; lịch sử qua REST".
        try { _offset = new FileInfo(active.JsonlPath).Length; }
        catch { _offset = 0; }

        // Initial pump: with _offset at EOF this reads nothing (it only sets up
        // FileSystemWatcher liveness below); kept so session_switch/reset logic
        // and offset bookkeeping run through the one code path.
        await PumpAsync(_cts.Token);

        // Watcher on the project directory (for session_switch) and the file itself
        _fsw = new FileSystemWatcher(active.EncodedCwdDir, "*.jsonl")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                         | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _fsw.Changed += OnChanged;
        _fsw.Created += OnChanged;
        _fsw.Renamed += OnChanged;

        _processTask = Task.Run(() => DebounceLoopAsync(_cts.Token), _cts.Token);
    }

    private void OnChanged(object _, FileSystemEventArgs __) =>
        _lastEvent = DateTimeOffset.UtcNow;

    private async Task DebounceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(50, ct);
            }
            catch (OperationCanceledException) { return; }

            if (_lastEvent == DateTimeOffset.MinValue) continue;
            var elapsed = DateTimeOffset.UtcNow - _lastEvent;
            if (elapsed < DebounceWindow) continue;

            _lastEvent = DateTimeOffset.MinValue;
            try { await PumpAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "JsonlWatcher pump failed for {Project}", _projectId);
            }
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        // Re-resolve to detect session_switch
        var active = await _scanner.ResolveAsync(_projectId, ct);
        if (active is null) return;

        if (active.JsonlPath != _currentJsonlPath)
        {
            _log.LogInformation("Project {Project} session switch: {Old} -> {New}",
                _projectId, _currentSessionUuid, active.SessionUuid);

            await _channel.Writer.WriteAsync(new SessionMessage(
                Kind: "session_switch",
                Uuid: null,
                ParentUuid: null,
                SessionUuid: active.SessionUuid,
                ProjectId: _projectId,
                Timestamp: DateTimeOffset.UtcNow.ToString("o"),
                Role: null,
                UserType: null,
                IsSidechain: false,
                Content: null,
                Text: _currentSessionUuid, // old uuid for client convenience
                Raw: null), ct);

            _currentJsonlPath = active.JsonlPath;
            _currentSessionUuid = active.SessionUuid;
            // EOF-start on the NEW session too (same reason as StartAsync): the
            // PWA re-fetches the switched-to session's history via REST on the
            // session_switch event, so the watcher must not flush it over SSE.
            try { _offset = new FileInfo(active.JsonlPath).Length; }
            catch { _offset = 0; }
        }

        var fileLen = new FileInfo(_currentJsonlPath!).Length;
        if (fileLen < _offset)
        {
            _log.LogInformation("Project {Project} JSONL truncated, sending session_reset", _projectId);
            await _channel.Writer.WriteAsync(new SessionMessage(
                Kind: "session_reset",
                Uuid: null,
                ParentUuid: null,
                SessionUuid: _currentSessionUuid,
                ProjectId: _projectId,
                Timestamp: DateTimeOffset.UtcNow.ToString("o"),
                Role: null,
                UserType: null,
                IsSidechain: false,
                Content: null,
                Text: null,
                Raw: null), ct);
            _offset = 0;
        }

        var (msgs, newOffset) = await _reader.ReadFromOffsetAsync(_currentJsonlPath!, _offset, _projectId, ct);
        _offset = newOffset;
        foreach (var m in msgs) await _channel.Writer.WriteAsync(m, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_fsw is not null)
        {
            _fsw.EnableRaisingEvents = false;
            _fsw.Dispose();
        }
        if (_processTask is not null)
        {
            try { await _processTask; } catch { /* ignore */ }
        }
        _channel.Writer.TryComplete();
        _cts.Dispose();
    }
}
