using System.Security.Cryptography;
using System.Text;
using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CortexBridge.Api.Auth;

public class TokenIssuer
{
    private const string Prefix = "cb_";
    private readonly BridgeDbContext _db;

    public TokenIssuer(BridgeDbContext db) => _db = db;

    public async Task<(string token, BearerToken row)> IssueAsync(string? deviceName, CancellationToken ct)
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Prefix + Convert.ToHexStringLower(raw);
        var row = new BearerToken
        {
            TokenHash = Hash(token),
            DeviceName = deviceName,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.BearerTokens.Add(row);
        await _db.SaveChangesAsync(ct);
        return (token, row);
    }

    public async Task<BearerToken?> ValidateAsync(string presented, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(presented) || !presented.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        var hash = Hash(presented);
        var row = await _db.BearerTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null, ct);
        if (row is null) return null;

        row.LastUsedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public static string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(bytes);
    }

    // ----- ADR-020: self-service token admin (list / revoke / rotate) -----
    // Logic lives here so the endpoints stay thin and this — the project's one
    // unit-tested auth class — carries the behaviour. Secrets are never
    // returned by list; revocation is terminal (no un-revoke).

    /// <summary>All tokens, newest first. Callers MUST project to a safe DTO
    /// (never expose <see cref="BearerToken.TokenHash"/>).</summary>
    // Order by Id, not CreatedAt: SQLite (the production DB) cannot ORDER BY a
    // DateTimeOffset. Id is the autoincrement insertion order, so descending Id
    // is newest-first and semantically equivalent.
    public async Task<IReadOnlyList<BearerToken>> ListAsync(CancellationToken ct) =>
        await _db.BearerTokens
            .OrderByDescending(t => t.Id)
            .ToListAsync(ct);

    /// <summary>Revoke by id. null = not found, false = already revoked
    /// (idempotent no-op), true = revoked just now.</summary>
    public async Task<bool?> RevokeAsync(long id, CancellationToken ct)
    {
        var row = await _db.BearerTokens.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (row is null) return null;
        if (row.RevokedAt is not null) return false;
        row.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Atomically mint a new token and revoke <paramref name="current"/>
    /// (the authenticating token) in a single save. The new secret is returned
    /// once, like <see cref="IssueAsync"/>.</summary>
    public async Task<(string token, BearerToken row)> RotateAsync(
        BearerToken current, string? newDeviceName, CancellationToken ct)
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Prefix + Convert.ToHexStringLower(raw);
        var row = new BearerToken
        {
            TokenHash = Hash(token),
            DeviceName = newDeviceName ?? current.DeviceName,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.BearerTokens.Add(row);
        // Revoke the old token in the SAME unit of work so a crash can't leave
        // both live. Re-fetch by id so this works whether `current` is the
        // request-scoped tracked instance or a detached copy.
        var liveCurrent = await _db.BearerTokens
            .FirstOrDefaultAsync(t => t.Id == current.Id, ct) ?? current;
        liveCurrent.RevokedAt ??= DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return (token, row);
    }
}
