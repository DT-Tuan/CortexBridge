using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using CortexBridge.Api.Sessions;
using CortexBridge.Api.Tmux;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexBridge.Api.Tests;

/// <summary>
/// SessionScanner Tier 0 — the pinned-but-fileless live slot.
///
/// CC creates a session (and fires the SessionStart hook that pins it into
/// session_ownership) minutes BEFORE it writes the session's first JSONL byte.
/// Measured on a real /clear: hook at 05:04:27, file born 05:07:47 — a 3m20s
/// window in which the live session is real but has no file. Every other
/// resolution tier is file-derived, so without Tier 0 the scanner hands back the
/// dead pre-/clear session for that whole window and the PWA sits on a stale
/// transcript until the user sends a message (live user bug 2026-07-17).
///
/// Tier 0 is gated on a live tmux window so a marker whose session never
/// materialises (CC crashed, no exit hook) decays back to normal resolution
/// instead of wedging the project on a permanently empty transcript.
/// </summary>
public class SessionScannerLiveSlotTests : IDisposable
{
    private const string ProjectId = "myproj";
    private const string OldUuid = "11111111-1111-1111-1111-111111111111";
    private const string NewUuid = "22222222-2222-2222-2222-222222222222";

    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<BridgeDbContext> _dbOpts;
    private readonly ServiceProvider _sp;
    private readonly string _root;
    private readonly string _projectDir;

    public SessionScannerLiveSlotTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _dbOpts = new DbContextOptionsBuilder<BridgeDbContext>().UseSqlite(_conn).Options;
        using (var seed = new BridgeDbContext(_dbOpts)) seed.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddSingleton(_dbOpts);
        services.AddScoped(sp => new BridgeDbContext(sp.GetRequiredService<DbContextOptions<BridgeDbContext>>()));
        _sp = services.BuildServiceProvider();

        _root = Path.Combine(Path.GetTempPath(), "cbscan-" + Guid.NewGuid().ToString("N"));
        _projectDir = Path.Combine(_root, "-tmp-" + ProjectId);
        Directory.CreateDirectory(_projectDir);

        // The pre-/clear session: a real file with a timestamped record, which is
        // what out-ranks a fileless newcomer in every file-derived tier.
        File.WriteAllText(Path.Combine(_projectDir, OldUuid + ".jsonl"),
            $$"""
            {"cwd":"/tmp/{{ProjectId}}","type":"user","entrypoint":"cli","timestamp":"2026-07-17T05:00:00.000Z"}
            """ + "\n");
    }

    public void Dispose()
    {
        _sp.Dispose();
        _conn.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>Fake tmux: printing the project id makes WindowExistsAsync true for it.
    /// A non-zero exit (/bin/false) makes it false — the "CC is gone" case.</summary>
    private string FakeTmux(bool windowAlive)
    {
        if (!windowAlive) return "/bin/false";
        var script = Path.Combine(_root, "fake-tmux.sh");
        File.WriteAllText(script, $"#!/bin/sh\necho {ProjectId}\n");
        // bridge-api is Linux-only by module rule (runs identically in Docker); the
        // guard is here to satisfy CA1416, not because Windows is a target.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private SessionScanner Build(bool windowAlive)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["BRIDGE_CC_PROJECTS_ROOT"] = _root,
                ["BRIDGE_TMUX_BIN"] = FakeTmux(windowAlive),
            }).Build();
        return new SessionScanner(
            new BridgePaths(config),
            new JsonlReader(NullLogger<JsonlReader>.Instance),
            new TmuxClient(config, NullLogger<TmuxClient>.Instance),
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SessionScanner>.Instance);
    }

    private void Pin(string uuid)
    {
        using var db = new BridgeDbContext(_dbOpts);
        db.SessionOwnerships.Add(new SessionOwnership
        {
            ProjectId = ProjectId,
            Owner = "tmux",
            SessionUuid = uuid,
            SinceUtc = DateTimeOffset.UtcNow,
            ChangedByClient = "activity-hook:sessionstart",
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task PinnedFilelessSession_TmuxAlive_WinsOverOldSessionWithFile()
    {
        // The post-/clear window: SessionStart pinned the new uuid, CC hasn't
        // written its JSONL yet. The old session is the only one with a file.
        Pin(NewUuid);

        var active = await Build(windowAlive: true).ResolveAsync(ProjectId, default);

        Assert.NotNull(active);
        Assert.Equal(NewUuid, active!.SessionUuid);
        Assert.Equal(Path.Combine(_projectDir, NewUuid + ".jsonl"), active.JsonlPath);
        Assert.False(File.Exists(active.JsonlPath)); // readers treat this as empty
    }

    [Fact]
    public async Task PinnedFilelessSession_TmuxDead_DecaysToOldSession()
    {
        // Marker points at a session that never materialised and CC is gone —
        // must not wedge the project on an empty transcript.
        Pin(NewUuid);

        var active = await Build(windowAlive: false).ResolveAsync(ProjectId, default);

        Assert.NotNull(active);
        Assert.Equal(OldUuid, active!.SessionUuid);
    }

    [Fact]
    public async Task PinnedSessionWithFile_StillResolvesNormally()
    {
        // Tier 1 regression guard: once CC writes the file, nothing changes.
        File.WriteAllText(Path.Combine(_projectDir, NewUuid + ".jsonl"),
            $$"""
            {"cwd":"/tmp/{{ProjectId}}","type":"user","entrypoint":"cli","timestamp":"2026-07-17T05:10:00.000Z"}
            """ + "\n");
        Pin(NewUuid);

        var active = await Build(windowAlive: true).ResolveAsync(ProjectId, default);

        Assert.NotNull(active);
        Assert.Equal(NewUuid, active!.SessionUuid);
        Assert.True(File.Exists(active.JsonlPath));
    }

    [Fact]
    public async Task NoPin_UntrackedProject_ResolvesByLastRecord()
    {
        // No ownership row at all (the state most projects are actually in).
        var active = await Build(windowAlive: true).ResolveAsync(ProjectId, default);

        Assert.NotNull(active);
        Assert.Equal(OldUuid, active!.SessionUuid);
    }
}
