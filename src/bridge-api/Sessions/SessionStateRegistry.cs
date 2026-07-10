using System.Collections.Concurrent;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// Per-(project, session) ephemeral state. Two orthogonal signals, both
/// authoritative (driven by CC hooks, not guessed from JSONL shape):
///   - NeedsInput  — Notification hook says claude is blocked on the user
///                   (permission / AskUserQuestion). Stop + reply clear it.
///   - Processing  — claude is actively working a turn. UserPromptSubmit /
///                   PreToolUse / PostToolUse set it; Stop ends it; Notification
///                   pauses it (claude is waiting, not working).
/// LastEventAt is refreshed on every hook so the UI/watchdog can tell a long
/// turn (6-min doc write) apart from a genuinely dead session.
///
/// ADR-016 Slice 1: keyed by <c>(projectId, sessionUuid)</c> so two sessions
/// under one project no longer clobber each other's state. **Backward-compat
/// facade:** every method takes an optional <paramref name="sessionUuid"/>;
/// when omitted/empty it resolves — for BOTH read and write, consistently — to
/// the project's most-recently-active entry (the live session is the one
/// firing hooks, so it has the newest LastEventAt). Callers that know the UID
/// (hooks via <c>payload.SessionId</c>; reply/SSE via the resolved active
/// session) pass it explicitly for exact attribution.
/// </summary>
public class SessionStateRegistry
{
    private readonly ConcurrentDictionary<(string ProjectId, string SessionUuid), State> _state = new();

    private record State(
        bool NeedsInput, bool Processing, DateTimeOffset? LastEventAt, string? NotificationMessage);

    /// <summary>
    /// Fires with (projectId, sessionUuid) whenever NeedsInput or Processing
    /// changes. sessionUuid is null when the entry is the project-level
    /// fallback bucket. StreamEndpoint subscribes per-connection and re-pushes
    /// the full status frame so PWA + ext update live without a reload.
    /// </summary>
    public event Action<string, string?>? StatusChanged;

    /// <summary>
    /// Resolve the concrete dictionary key. Explicit uuid ⇒ exact entry.
    /// Empty/null ⇒ the project's newest-LastEventAt entry (de-facto the live
    /// session, which fires the most recent hooks); none ⇒ the ("") bucket.
    /// Same rule for read and write so they never diverge.
    /// </summary>
    private (string, string) Key(string projectId, string? sessionUuid)
    {
        if (!string.IsNullOrEmpty(sessionUuid))
            return (projectId, sessionUuid);

        (string, string)? best = null;
        DateTimeOffset bestTs = DateTimeOffset.MinValue;
        foreach (var kv in _state)
            if (string.Equals(kv.Key.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)
                && kv.Value.LastEventAt is { } ts && (best is null || ts > bestTs))
            {
                best = kv.Key;
                bestTs = ts;
            }
        return best ?? (projectId, string.Empty);
    }

    private static string? WireUuid(string sessionUuid) =>
        sessionUuid.Length == 0 ? null : sessionUuid;

    public bool NeedsInput(string projectId, string? sessionUuid = null) =>
        _state.TryGetValue(Key(projectId, sessionUuid), out var s) && s.NeedsInput;

    public bool Processing(string projectId, string? sessionUuid = null) =>
        _state.TryGetValue(Key(projectId, sessionUuid), out var s) && s.Processing;

    public DateTimeOffset? LastEventAt(string projectId, string? sessionUuid = null) =>
        _state.TryGetValue(Key(projectId, sessionUuid), out var s) ? s.LastEventAt : null;

    /// <summary>
    /// Latest Notification hook message text (e.g. "Claude needs your permission to use Bash").
    /// Cleared when needsInput flips back to false. Used by PWA to surface what CC is asking.
    /// </summary>
    public string? NotificationMessage(string projectId, string? sessionUuid = null) =>
        _state.TryGetValue(Key(projectId, sessionUuid), out var s) ? s.NotificationMessage : null;

    public void SetNeedsInput(string projectId, bool needs, string? message = null,
        string? sessionUuid = null)
    {
        var key = Key(projectId, sessionUuid);
        var prev = _state.TryGetValue(key, out var s0) && s0.NeedsInput;
        _state.AddOrUpdate(
            key,
            _ => new State(needs, false, DateTimeOffset.UtcNow, needs ? message : null),
            (_, prevState) => prevState with
            {
                NeedsInput = needs,
                // Waiting on the user ⇒ not actively processing.
                Processing = needs ? false : prevState.Processing,
                LastEventAt = DateTimeOffset.UtcNow,
                NotificationMessage = needs ? (message ?? prevState.NotificationMessage) : null
            });
        if (prev != needs) StatusChanged?.Invoke(projectId, WireUuid(key.Item2));
    }

    /// <summary>
    /// Set the authoritative processing flag. Always refreshes LastEventAt
    /// (proves liveness during long turns even when the flag value is unchanged).
    /// </summary>
    public void SetProcessing(string projectId, bool processing, string? sessionUuid = null)
    {
        var key = Key(projectId, sessionUuid);
        var prev = _state.TryGetValue(key, out var s0) && s0.Processing;
        _state.AddOrUpdate(
            key,
            _ => new State(false, processing, DateTimeOffset.UtcNow, null),
            (_, prevState) => prevState with
            {
                Processing = processing,
                // Claude doing work ⇒ it is no longer blocked on the user.
                NeedsInput = processing ? false : prevState.NeedsInput,
                NotificationMessage = processing ? null : prevState.NotificationMessage,
                LastEventAt = DateTimeOffset.UtcNow
            });
        if (prev != processing) StatusChanged?.Invoke(projectId, WireUuid(key.Item2));
    }

    public void TouchLastEvent(string projectId, string? sessionUuid = null) =>
        _state.AddOrUpdate(
            Key(projectId, sessionUuid),
            _ => new State(false, false, DateTimeOffset.UtcNow, null),
            (_, prev) => prev with { LastEventAt = DateTimeOffset.UtcNow });

    /// <summary>
    /// Watchdog candidate query (READ-ONLY — does not mutate state). Returns
    /// every (project, session) still flagged Processing whose last hook is
    /// older than <paramref name="idleFor"/>. Built-in slash commands
    /// (/clear, /compact, /exit) and some skill paths fire an activity hook
    /// (Processing=true) but never the terminal Stop hook, so the flag can
    /// latch true forever.
    ///
    /// "Stale" is necessary-but-NOT-sufficient for a dead latch: a single long
    /// tool-call/think (S2) and a stuck AskUserQuestion (S1) also go &gt;120s
    /// with no hook yet are NOT dead. The clear decision is therefore taken by
    /// <see cref="ProcessingWatchdog"/>, which reads the tmux pane per
    /// candidate and only then calls SetProcessing(false) / SetNeedsInput(true).
    /// Split out so the pane I/O lives in the BackgroundService, keeping this
    /// registry pure. Returns ids only — never message content.
    /// </summary>
    public IReadOnlyList<(string ProjectId, string SessionUuid)> FindStaleProcessing(TimeSpan idleFor)
    {
        var cutoff = DateTimeOffset.UtcNow - idleFor;
        var stale = new List<(string, string)>();
        foreach (var (key, s) in _state)
        {
            if (!s.Processing) continue;
            if (s.LastEventAt is { } last && last > cutoff) continue; // still live
            stale.Add(key);
        }
        return stale;
    }
}
