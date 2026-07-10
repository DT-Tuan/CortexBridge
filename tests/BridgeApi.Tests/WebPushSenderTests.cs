using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using CortexBridge.Api.Hooks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexBridge.Api.Tests;

/// <summary>
/// WebPushSender — Phase 2 hardening (#1). Covers the deterministic, no-network
/// contracts that underpin the "global fan-out is bounded" safety story
/// (see memory feedback_webpush_global_fanout): when VAPID is unconfigured the
/// sender is a hard no-op, and an empty subscription table short-circuits
/// before any push client call. Removing either guard now fails a test.
///
/// Backed by a real BridgeDbContext over an in-memory SQLite connection (no new
/// test package — Microsoft.EntityFrameworkCore.Sqlite flows transitively). The
/// network-dependent paths (410 stale-sub removal, LastUsedAt bump, re-prompt
/// dedup) require a WebPushClient seam = a production change, deliberately out
/// of scope for this test-only slice.
/// </summary>
public class WebPushSenderTests
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

    // Empty-string config values force the disabled path deterministically:
    // production does `config[key] ?? Environment.GetEnvironmentVariable(key)`,
    // so an empty (non-null) config value never consults a stray host env var.
    private static WebPushSender Disabled() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VAPID_PUBLIC_KEY"] = "",
            ["VAPID_PRIVATE_KEY"] = "",
        }).Build(),
        NullLogger<WebPushSender>.Instance);

    private static WebPushSender Enabled() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VAPID_PUBLIC_KEY"] = "test-public-key",
            ["VAPID_PRIVATE_KEY"] = "test-private-key",
        }).Build(),
        NullLogger<WebPushSender>.Instance);

    private static PushSubscription Sub(string endpoint = "https://push.example/abc") => new()
    {
        Endpoint = endpoint,
        P256dh = "p256dh-key",
        Auth = "auth-secret",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void IsEnabled_False_WhenVapidKeysAbsent()
    {
        Assert.False(Disabled().IsEnabled);
    }

    [Fact]
    public void IsEnabled_True_WhenBothVapidKeysPresent()
    {
        Assert.True(Enabled().IsEnabled);
    }

    [Fact]
    public async Task SendToAllAsync_Disabled_IsNoOp_AndKeepsSubscriptions()
    {
        using var db = NewDb(out var conn);
        using (conn)
        {
            db.PushSubscriptions.Add(Sub());
            await db.SaveChangesAsync();

            // Must return BEFORE touching subscriptions or any push client.
            await Disabled().SendToAllAsync(db, "proj", "t", "b", "https://x/", default);

            var only = await db.PushSubscriptions.SingleAsync();
            Assert.Null(only.LastUsedAt); // not sent ⇒ not stamped
        }
    }

    [Fact]
    public async Task SendToAllAsync_Enabled_NoSubscriptions_ReturnsCleanly()
    {
        using var db = NewDb(out var conn);
        using (conn)
        {
            // Empty table ⇒ short-circuits at `subs.Count == 0`, no client call.
            await Enabled().SendToAllAsync(db, "proj", "t", "b", null, default);
            Assert.Equal(0, await db.PushSubscriptions.CountAsync());
        }
    }

    [Fact]
    public async Task SendClearAsync_Disabled_IsNoOp()
    {
        using var db = NewDb(out var conn);
        using (conn)
        {
            db.PushSubscriptions.Add(Sub());
            await db.SaveChangesAsync();

            await Disabled().SendClearAsync(db, "proj", default);

            Assert.Equal(1, await db.PushSubscriptions.CountAsync());
        }
    }

    [Fact]
    public async Task SendClearAsync_Enabled_NoSubscriptions_ReturnsCleanly()
    {
        using var db = NewDb(out var conn);
        using (conn)
        {
            await Enabled().SendClearAsync(db, "proj", default);
            Assert.Equal(0, await db.PushSubscriptions.CountAsync());
        }
    }
}
