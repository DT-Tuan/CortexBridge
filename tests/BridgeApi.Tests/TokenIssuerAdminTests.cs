using CortexBridge.Api.Auth;
using CortexBridge.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CortexBridge.Api.Tests;

/// <summary>
/// ADR-020 — TokenIssuer self-service admin (list / revoke / rotate). Backed by
/// a real BridgeDbContext over in-memory SQLite (the WebPushSenderTests pattern;
/// no new test package, no WebApplicationFactory). These pin the security
/// contracts: revoke is terminal + idempotent and immediately stops
/// ValidateAsync; rotate atomically swaps a working token for the old one.
/// </summary>
public class TokenIssuerAdminTests
{
    private static BridgeDbContext NewDb(out SqliteConnection conn)
    {
        conn = new SqliteConnection("DataSource=:memory:");
        conn.Open(); // keep open — closing drops the in-memory schema
        var opts = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new BridgeDbContext(opts);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task RevokeAsync_StopsValidation_AndIsTerminalIdempotent()
    {
        using var db = NewDb(out var conn);
        using (conn)
        {
            var issuer = new TokenIssuer(db);
            var (token, row) = await issuer.IssueAsync("phone", default);
            Assert.NotNull(await issuer.ValidateAsync(token, default));

            Assert.True(await issuer.RevokeAsync(row.Id, default));     // revoked now
            Assert.Null(await issuer.ValidateAsync(token, default));    // immediately dead
            Assert.False(await issuer.RevokeAsync(row.Id, default));    // idempotent no-op
            Assert.Null(await issuer.RevokeAsync(999_999, default));    // unknown id
        }
    }

    [Fact]
    public async Task RotateAsync_NewTokenWorks_OldTokenIsRevoked_Atomically()
    {
        using var db = NewDb(out var conn);
        using (conn)
        {
            var issuer = new TokenIssuer(db);
            var (oldToken, oldRow) = await issuer.IssueAsync("phone", default);

            var (newToken, newRow) = await issuer.RotateAsync(oldRow, null, default);

            Assert.NotEqual(oldToken, newToken);
            Assert.Null(await issuer.ValidateAsync(oldToken, default));   // old dead
            Assert.NotNull(await issuer.ValidateAsync(newToken, default)); // new live
            var reloadedOld = await db.BearerTokens.FindAsync(oldRow.Id);
            Assert.NotNull(reloadedOld!.RevokedAt);                       // persisted
            Assert.NotEqual(oldRow.Id, newRow.Id);
        }
    }

    [Fact]
    public async Task RotateAsync_CarriesDeviceName_UnlessOverridden()
    {
        using var db = NewDb(out var conn);
        using (conn)
        {
            var issuer = new TokenIssuer(db);
            var (_, a) = await issuer.IssueAsync("old-name", default);
            var (_, kept) = await issuer.RotateAsync(a, null, default);
            Assert.Equal("old-name", kept.DeviceName);

            var (_, renamed) = await issuer.RotateAsync(kept, "new-name", default);
            Assert.Equal("new-name", renamed.DeviceName);
        }
    }

    [Fact]
    public async Task ListAsync_NewestFirst_IncludesRevoked()
    {
        using var db = NewDb(out var conn);
        using (conn)
        {
            var issuer = new TokenIssuer(db);
            var (_, first) = await issuer.IssueAsync("first", default);
            var (_, second) = await issuer.IssueAsync("second", default);
            await issuer.RevokeAsync(first.Id, default);

            var list = await issuer.ListAsync(default);

            Assert.Equal(2, list.Count);
            Assert.Equal(second.Id, list[0].Id);          // newest first
            Assert.Contains(list, t => t.Id == first.Id && t.RevokedAt != null);
        }
    }
}
