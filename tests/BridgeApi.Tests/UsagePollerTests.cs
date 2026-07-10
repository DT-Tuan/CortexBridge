using CortexBridge.Api.Data;
using CortexBridge.Api.Hooks;
using CortexBridge.Api.Usage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexBridge.Api.Tests;

/// <summary>
/// UsagePoller v2 (2026-06-12): thresholds 5h=95, 7d=90+100; window_id is the
/// official resets_at ROUNDED to the nearest minute (fixes the sub-second-jitter
/// spam where the raw resets_at changed every tick → dedup never held); a
/// one-shot reset push fires when a previously-alerted window rolls over; the
/// snapshot is throttled to ~5 min. WebPushSender has no VAPID so SendToAllAsync
/// no-ops — we assert the persisted alert/snapshot rows (the real contract).
/// </summary>
public class UsagePollerTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _jsonPath;
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<BridgeDbContext> _dbOpts;
    private readonly ServiceProvider _sp;

    public UsagePollerTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "usagepoller-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _jsonPath = Path.Combine(_tmpDir, "usage.json");

        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _dbOpts = new DbContextOptionsBuilder<BridgeDbContext>().UseSqlite(_conn).Options;
        using (var seed = new BridgeDbContext(_dbOpts)) seed.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddSingleton(_dbOpts);
        services.AddScoped(sp => new BridgeDbContext(sp.GetRequiredService<DbContextOptions<BridgeDbContext>>()));
        _sp = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _sp.Dispose();
        _conn.Dispose();
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    // Each test gets its own poller instance (its own throttle clock).
    private (UsagePoller poller, BridgeDbContext checkDb) Build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["USAGE_JSON_PATH"] = _jsonPath,
        }).Build();

        var usageSvc = new UsageService(new UsagePaths(config), NullLogger<UsageService>.Instance);
        var pushSender = new WebPushSender(config, NullLogger<WebPushSender>.Instance);
        var scopeFactory = _sp.GetRequiredService<IServiceScopeFactory>();
        var poller = new UsagePoller(usageSvc, scopeFactory, pushSender, NullLogger<UsagePoller>.Instance);
        return (poller, new BridgeDbContext(_dbOpts));
    }

    // resets_at are real ISO instants so StableWindowId can round them.
    private void WriteUsage(string fiveResetsAt, decimal fivePct, string weekResetsAt, decimal weekPct,
        bool includeOfficial = true)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var official = includeOfficial
            ? $$"""
              ,"official": {
                "fiveHour": { "utilization": {{fivePct.ToString(inv)}}, "resetsAt": "{{fiveResetsAt}}" },
                "sevenDay": { "utilization": {{weekPct.ToString(inv)}}, "resetsAt": "{{weekResetsAt}}" },
                "takenAtUtc": "2026-06-12T08:00:00Z"
              }
              """
            : "";
        var json = $$"""
        {
          "takenAtUtc": "2026-06-12T08:00:00Z",
          "block5h": {
            "startTime": "2026-06-12T07:00:00.000Z",
            "endTime": "2026-06-12T12:00:00.000Z",
            "isActive": true, "entries": 10, "models": ["claude-opus-4-7"],
            "costUSD": 4.2, "totalTokens": 1000,
            "burnRate": { "costPerHour": 1.0, "tokensPerMinute": 100.0 },
            "projection": { "remainingMinutes": 120, "totalCost": 6.3, "totalTokens": 5000 }
          },
          "week7d": { "period": "2026-06-08", "totalCost": 33.0, "totalTokens": 5000, "modelBreakdowns": [] }{{official}}
        }
        """;
        File.WriteAllText(_jsonPath, json);
    }

    private const string W5_A = "2026-06-12T12:30:00+00:00";   // 5h window A (rounds 12:30)
    private const string W5_B = "2026-06-12T17:30:00+00:00";   // 5h window B (rounds 17:30)
    private const string W7   = "2026-06-17T17:00:00+00:00";   // 7d window

    [Fact]
    public async Task FirstTick_NoCrossing_OneSnapshot_NoAlerts()
    {
        WriteUsage(W5_A, fivePct: 50, weekResetsAt: W7, weekPct: 20);
        var (poller, db) = Build();
        await poller.TickAsync(default);

        Assert.Equal(1, await db.UsageSnapshots.CountAsync());
        Assert.Equal(0, await db.UsageAlertsSent.CountAsync());
    }

    [Fact]
    public async Task Block5h_Below95_DoesNotAlert()
    {
        WriteUsage(W5_A, fivePct: 92, weekResetsAt: W7, weekPct: 20);   // old 90 floor would trip
        var (poller, db) = Build();
        await poller.TickAsync(default);
        Assert.Equal(0, await db.UsageAlertsSent.CountAsync(a => a.WindowKind == "block5h"));
    }

    [Fact]
    public async Task Block5h_Crosses95_FiresOnce()
    {
        WriteUsage(W5_A, fivePct: 96, weekResetsAt: W7, weekPct: 20);
        var (poller, db) = Build();
        await poller.TickAsync(default);

        var alerts = await db.UsageAlertsSent.Where(a => a.WindowKind == "block5h").ToListAsync();
        Assert.Single(alerts);
        Assert.Equal(95, alerts[0].ThresholdPct);
        Assert.Equal("2026-06-12T12:30Z", alerts[0].WindowId);   // rounded
    }

    [Fact]
    public async Task Week7d_Crosses90Only_Then100()
    {
        WriteUsage(W5_A, fivePct: 10, weekResetsAt: W7, weekPct: 92);
        var (poller, db) = Build();
        await poller.TickAsync(default);
        Assert.Equal(new[] { 90 },
            (await db.UsageAlertsSent.Where(a => a.WindowKind == "week7d").Select(a => a.ThresholdPct).ToListAsync()));

        WriteUsage(W5_A, fivePct: 10, weekResetsAt: W7, weekPct: 100);
        await poller.TickAsync(default);
        Assert.Equal(new[] { 90, 100 },
            (await db.UsageAlertsSent.Where(a => a.WindowKind == "week7d").OrderBy(a => a.ThresholdPct).Select(a => a.ThresholdPct).ToListAsync()));
    }

    [Fact]
    public async Task Jitter_SameWindow_SubSecondResetsAt_DedupsToOneAlert()
    {
        // THE bug: the official endpoint returns jittered resets_at for the SAME
        // window. Two ticks, util high both, resets_at differs sub-second but
        // rounds to the same minute → exactly ONE alert (no per-tick re-fire).
        var (poller, db) = Build();
        WriteUsage("2026-06-12T12:29:59.078385+00:00", 96, W7, 20);
        await poller.TickAsync(default);
        WriteUsage("2026-06-12T12:30:00.229906+00:00", 97, W7, 20);
        await poller.TickAsync(default);
        WriteUsage("2026-06-12T12:29:59.835341+00:00", 98, W7, 20);
        await poller.TickAsync(default);

        Assert.Equal(1, await db.UsageAlertsSent.CountAsync(a => a.WindowKind == "block5h" && a.ThresholdPct == 95));
        Assert.Equal(0, await db.UsageAlertsSent.CountAsync(a => a.ThresholdPct == -1));   // no spurious reset
    }

    [Fact]
    public async Task ResetFreed_AfterAlert_NewWindow_FiresOnce()
    {
        var (poller, db) = Build();
        WriteUsage(W5_A, 96, W7, 20);          // window A trips 95
        await poller.TickAsync(default);

        WriteUsage(W5_B, 10, W7, 20);          // window B: server rolled, util freed
        await poller.TickAsync(default);
        await poller.TickAsync(default);       // dedup: still one reset

        var resets = await db.UsageAlertsSent
            .Where(a => a.WindowKind == "block5h" && a.ThresholdPct == -1).ToListAsync();
        Assert.Single(resets);
        Assert.Equal("2026-06-12T17:30Z", resets[0].WindowId);
    }

    [Fact]
    public async Task ResetFreed_NoPriorAlert_DoesNotFire()
    {
        var (poller, db) = Build();
        WriteUsage(W5_A, 50, W7, 20);          // never alerted
        await poller.TickAsync(default);
        WriteUsage(W5_B, 50, W7, 20);
        await poller.TickAsync(default);

        Assert.Equal(0, await db.UsageAlertsSent.CountAsync());
    }

    [Fact]
    public async Task DuringLimit_SameWindow_NoExtraPush()
    {
        var (poller, db) = Build();
        WriteUsage(W5_A, 96, W7, 20);
        await poller.TickAsync(default);       // 95 fires
        WriteUsage(W5_A, 99, W7, 20);
        await poller.TickAsync(default);       // still limited, same window → silent

        Assert.Equal(1, await db.UsageAlertsSent.CountAsync());
    }

    [Fact]
    public async Task Snapshot_Throttled_RapidTicks_OneRow()
    {
        var (poller, db) = Build();
        WriteUsage(W5_A, 50, W7, 20);
        await poller.TickAsync(default);
        await poller.TickAsync(default);       // <5 min later (same instant) → throttled
        await poller.TickAsync(default);

        Assert.Equal(1, await db.UsageSnapshots.CountAsync());
    }

    [Fact]
    public async Task NoOfficialBlock_NoAlerts_SnapshotZeroPct()
    {
        WriteUsage("x", 0, "x", 0, includeOfficial: false);
        var (poller, db) = Build();
        await poller.TickAsync(default);

        Assert.Equal(1, await db.UsageSnapshots.CountAsync());
        Assert.Equal(0, await db.UsageAlertsSent.CountAsync());
        var snap = await db.UsageSnapshots.SingleAsync();
        Assert.Equal(0m, snap.Block5hPctCurrent);
        Assert.Equal(4.2m, snap.Block5hCurrentUsd);
    }
}
