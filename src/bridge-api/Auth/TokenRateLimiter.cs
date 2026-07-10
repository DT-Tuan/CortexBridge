using System.Collections.Concurrent;

namespace CortexBridge.Api.Auth;

/// <summary>
/// Per-token sliding-window rate limit for reply endpoints. Spec 03 §3.6:
/// "Per token: max 30 replies/minute (sliding window). Excess returns 429."
///
/// Cheap in-memory: ring of timestamps per tokenId. No persistence — resets on restart,
/// acceptable for a single-user bridge.
/// </summary>
public class TokenRateLimiter
{
    private const int MaxPerWindow = 30;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<long, RingBuffer> _buckets = new();

    /// <summary>
    /// Returns true if the request is allowed. Atomically records the timestamp on success.
    /// </summary>
    public bool TryAcquire(long tokenId)
    {
        var now = DateTimeOffset.UtcNow;
        var bucket = _buckets.GetOrAdd(tokenId, _ => new RingBuffer(MaxPerWindow));
        return bucket.TryAdd(now, Window);
    }

    /// <summary>
    /// Number of slots remaining for the current window. Useful for headers.
    /// </summary>
    public int Remaining(long tokenId)
    {
        if (!_buckets.TryGetValue(tokenId, out var bucket)) return MaxPerWindow;
        return bucket.Remaining(DateTimeOffset.UtcNow, Window);
    }

    private sealed class RingBuffer
    {
        private readonly DateTimeOffset[] _slots;
        private readonly object _lock = new();
        private readonly int _capacity;

        public RingBuffer(int capacity)
        {
            _capacity = capacity;
            _slots = new DateTimeOffset[capacity];
            // Initial values are DateTimeOffset.MinValue, all considered "outside the window"
        }

        public bool TryAdd(DateTimeOffset now, TimeSpan window)
        {
            lock (_lock)
            {
                var cutoff = now - window;
                int oldestIdx = 0;
                var oldestTs = DateTimeOffset.MaxValue;
                int count = 0;

                for (int i = 0; i < _capacity; i++)
                {
                    if (_slots[i] > cutoff) count++;
                    if (_slots[i] < oldestTs) { oldestTs = _slots[i]; oldestIdx = i; }
                }

                if (count >= _capacity) return false;
                _slots[oldestIdx] = now;
                return true;
            }
        }

        public int Remaining(DateTimeOffset now, TimeSpan window)
        {
            lock (_lock)
            {
                var cutoff = now - window;
                int count = 0;
                for (int i = 0; i < _capacity; i++)
                    if (_slots[i] > cutoff) count++;
                return Math.Max(0, _capacity - count);
            }
        }
    }
}
