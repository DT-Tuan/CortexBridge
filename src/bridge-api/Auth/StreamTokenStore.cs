using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CortexBridge.Api.Auth;

/// <summary>
/// Short-lived single-use tokens for SSE EventSource auth.
/// EventSource cannot send Authorization headers — see spec 01 §"SSE auth".
/// Tokens live in memory only (60s TTL, single use). Survive restart by re-issuing.
/// </summary>
public class StreamTokenStore
{
    private readonly ConcurrentDictionary<string, Entry> _store = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private record Entry(long BearerTokenId, DateTimeOffset ExpiresAt);

    public (string token, DateTimeOffset expiresAt) Issue(long bearerTokenId)
    {
        var raw = RandomNumberGenerator.GetBytes(24);
        var token = Convert.ToHexStringLower(raw);
        var expiresAt = DateTimeOffset.UtcNow + Ttl;
        _store[token] = new Entry(bearerTokenId, expiresAt);
        Sweep();
        return (token, expiresAt);
    }

    public long? ConsumeAndValidate(string presented)
    {
        if (string.IsNullOrEmpty(presented)) return null;
        if (!_store.TryRemove(presented, out var entry)) return null;
        if (entry.ExpiresAt < DateTimeOffset.UtcNow) return null;
        return entry.BearerTokenId;
    }

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (k, v) in _store)
        {
            if (v.ExpiresAt < now) _store.TryRemove(k, out _);
        }
    }
}
