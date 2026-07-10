using CortexBridge.Api.Data;
using CortexBridge.Api.Sessions;
using CortexBridge.Api.Tmux;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexBridge.Api.Tests;

/// <summary>
/// SessionQueueFlusher unit tests — only the paths that don't touch tmux:
/// processing=true (early return) + TTL expiry (drop + audit). The flush-
/// into-tmux path is live-verified via scratch-project per project convention.
/// </summary>
public class SessionQueueFlusherTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<BridgeDbContext> _dbOpts;
    private readonly ServiceProvider _sp;

    public SessionQueueFlusherTests()
    {
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
    }

    private (SessionQueueFlusher flusher, SessionQueue queue, SessionStateRegistry state, BridgeDbContext checkDb) Build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["BRIDGE_TMUX_BIN"] = "/bin/false" }).Build();
        var tmux = new TmuxClient(config, NullLogger<TmuxClient>.Instance);
        var queue = new SessionQueue();
        var state = new SessionStateRegistry();
        var mutex = new ProjectReplyMutex();
        var scopeFactory = _sp.GetRequiredService<IServiceScopeFactory>();
        var flusher = new SessionQueueFlusher(queue, state, tmux, mutex, scopeFactory,
            NullLogger<SessionQueueFlusher>.Instance);
        var checkDb = new BridgeDbContext(_dbOpts);
        return (flusher, queue, state, checkDb);
    }

    [Fact]
    public async Task FirstTick_NoEntries_NoOp()
    {
        var (flusher, queue, state, db) = Build();
        await flusher.TickAsync(default);
        Assert.Empty(queue.Snapshot());
        Assert.Equal(0, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Tick_ProcessingTrue_DoesNotFlushNorAudit()
    {
        var (flusher, queue, state, db) = Build();
        state.SetProcessing("proj-A", true, "sess-uuid");
        queue.PutOrReplace(new SessionQueue.QueuedReply(
            "proj-A", "deferred", "hash-deferred", 1L, "sess-uuid", DateTimeOffset.UtcNow));

        await flusher.TickAsync(default);

        // Entry still in slot; no audit row (no decision made yet).
        Assert.NotNull(queue.Peek("proj-A"));
        Assert.Equal(0, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Tick_TtlExpired_DropsAndAuditsEvenWhileProcessing()
    {
        var (flusher, queue, state, db) = Build();
        // Processing latched true — but the entry is STALE so we drop anyway.
        state.SetProcessing("proj-A", true, "sess-uuid");
        var stale = new SessionQueue.QueuedReply(
            "proj-A", "old-text", "hash-old",
            1L, "sess-uuid",
            DateTimeOffset.UtcNow - SessionQueueFlusher.EntryTtl - TimeSpan.FromSeconds(1));
        queue.PutOrReplace(stale);

        await flusher.TickAsync(default);

        Assert.Null(queue.Peek("proj-A"));   // dropped
        var audits = await db.AuditLogs.ToListAsync();
        Assert.Single(audits);
        Assert.Equal("queue.expired", audits[0].Action);
        Assert.Equal("hash-old", audits[0].PayloadHash);
    }

    [Fact]
    public async Task Tick_TtlNotExpired_ProcessingFalse_WindowMissing_DropsWithAudit()
    {
        // TmuxClient configured with /bin/false → WindowExistsAsync returns
        // false (catches TmuxException). The flusher then drops with
        // queue.window_missing audit instead of attempting to paste.
        var (flusher, queue, state, db) = Build();
        // Processing=false by default for new project; entry is fresh.
        queue.PutOrReplace(new SessionQueue.QueuedReply(
            "proj-A", "now-text", "hash-now", 1L, "sess-uuid", DateTimeOffset.UtcNow));

        await flusher.TickAsync(default);

        Assert.Null(queue.Peek("proj-A"));
        var audits = await db.AuditLogs.ToListAsync();
        Assert.Single(audits);
        Assert.Equal("queue.window_missing", audits[0].Action);
        Assert.Equal("error", audits[0].Result);
    }
}
