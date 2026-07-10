using System.Collections.Concurrent;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// In-memory single-slot reply buffer per project. The queue endpoint
/// (POST /api/sessions/{projectId}/queue) defers a user message until CC
/// stops processing — parity with the CC CLI's /btw command, which lets the
/// user "tape on" context for the next turn without interrupting the current
/// one.
///
/// Design constraints:
/// - 1 slot per projectId — submitting a 2nd entry REPLACES the 1st (with
///   reason="queue.replaced" audit). Avoids accidental flood of stale msgs.
/// - TTL enforced by SessionQueueFlusher (5 min default); a SSE disconnect
///   between enqueue and processing=false must not strand the buffer forever.
/// - Interrupt (/api/sessions/{projectId}/interrupt) calls Clear() — the
///   user's intent is to STOP, not let stale context paste a minute later.
/// - All access concurrent-safe via ConcurrentDictionary.
/// - Single user only — no per-token isolation; SessionQueue is process-wide.
/// </summary>
public class SessionQueue
{
    public record QueuedReply(
        string ProjectId,
        string Text,
        string PayloadHash,
        long? TokenId,
        string? SessionUuid,
        DateTimeOffset EnqueuedAt);

    private readonly ConcurrentDictionary<string, QueuedReply> _slots =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Set the slot. Returns the prior entry (if any) so the caller can
    /// audit a "queue.replaced" event with the prior payload hash.
    /// </summary>
    public QueuedReply? PutOrReplace(QueuedReply entry)
    {
        QueuedReply? prior = null;
        _slots.AddOrUpdate(entry.ProjectId, entry, (_, existing) =>
        {
            prior = existing;
            return entry;
        });
        return prior;
    }

    /// <summary>Look at the slot without removing it.</summary>
    public QueuedReply? Peek(string projectId) =>
        _slots.TryGetValue(projectId, out var v) ? v : null;

    /// <summary>Atomically take the slot if present (flush path).</summary>
    public QueuedReply? TryDequeue(string projectId) =>
        _slots.TryRemove(projectId, out var v) ? v : null;

    /// <summary>Idempotent clear (interrupt + expiry path).</summary>
    public bool Clear(string projectId) =>
        _slots.TryRemove(projectId, out _);

    /// <summary>Snapshot for the flusher tick — safe to iterate concurrently.</summary>
    public IReadOnlyList<QueuedReply> Snapshot() => _slots.Values.ToArray();
}
