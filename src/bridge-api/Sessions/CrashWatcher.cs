using System.Collections.Concurrent;
using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using CortexBridge.Api.Hooks;
using CortexBridge.Api.Tmux;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// ADR-022 Option Β — detection-only crash recovery. Every ~60 s scan every
/// project we recorded as `owner=tmux`; if the tmux window is gone (and no IDE
/// lock is in-flight, ruling out an A↔B handoff), fire ONE Web Push asking the
/// user to tap to restart. Deliberately not auto-spawning: `tmux remain-on-exit
/// =off` makes crash-vs-`/exit` indistinguishable, so an auto-resurrect would
/// regularly revive intentional `/exit`s (ADR-022 §"load-bearing discovery").
///
/// Coexistence with [ADR-017](017) <c>ModeWatcher</c>: we skip <c>owner=pc</c>
/// (the bridge cannot resurrect a PC-owned session), and the IDE-lock skip
/// catches the brief window where ModeWatcher is mid-flip. Dedup avoids
/// spamming pushes for the same dead state.
/// </summary>
public sealed class CrashWatcher(
    IServiceScopeFactory scopeFactory,
    TmuxClient tmux,
    IdeLockReader ideLockReader,
    WebPushSender webPush,
    IConfiguration config,
    ILogger<CrashWatcher> log) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    // Per-project last-notified timestamp. Set when we fire a push; cleared
    // when ownership transitions OUT of the crashed state (tmux window
    // reappears, or owner flips to pc/none). Avoids spamming pushes for the
    // same dead state.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastNotified = new();

    /// <summary>
    /// Pure decision: is this project in a crashed state right now? Unit-tested
    /// in isolation so the watcher is just plumbing.
    ///
    /// Crashed ⇔ ALL of:
    ///   * <paramref name="ownerWire"/> is "tmux" (we recorded ourselves as the
    ///     owner — owner=pc means PC owns it; owner=none means nothing to
    ///     resurrect);
    ///   * <paramref name="sessionUuid"/> is non-empty (no UID = nothing to
    ///     `--resume`, and the push deep-link would be useless);
    ///   * <paramref name="tmuxWindowAlive"/> is false (the live signal —
    ///     `tmux remain-on-exit=off` means a dead claude tears down the
    ///     window);
    ///   * <paramref name="ideLockPresent"/> is false (an IDE lock means a
    ///     Mode-A↔B handoff is in flight — leave that to ModeWatcher).
    /// </summary>
    public static bool IsCrashedState(
        string? ownerWire, string? sessionUuid, bool tmuxWindowAlive, bool ideLockPresent)
    {
        if (!string.Equals(ownerWire, "tmux", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrEmpty(sessionUuid)) return false;
        if (tmuxWindowAlive) return false;
        if (ideLockPresent) return false;
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup grace so the bridge can finish booting + ModeWatcher can do
        // its first reconcile before we read any state.
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogWarning(ex, "CrashWatcher reconcile failed"); }

            try { await Task.Delay(ScanInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        // Read all rows once per scan (small table — one row per project).
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var rows = await db.SessionOwnerships.ToListAsync(ct);
        var pcAttached = ideLockReader.PcAttachedProjects();

        foreach (var row in rows)
        {
            var windowAlive = await tmux.WindowExistsAsync(row.ProjectId, ct);
            var lockPresent = pcAttached.Contains(row.ProjectId);
            var crashed = IsCrashedState(row.Owner, row.SessionUuid, windowAlive, lockPresent);

            if (!crashed)
            {
                // Healthy (or owner != tmux): clear dedup so a future crash
                // re-notifies. This is what makes ownership transitions OUT of
                // the dead state re-arm the watcher.
                _lastNotified.TryRemove(row.ProjectId, out _);
                continue;
            }

            if (_lastNotified.ContainsKey(row.ProjectId)) continue; // already notified
            _lastNotified[row.ProjectId] = DateTimeOffset.UtcNow;

            await NotifyAsync(db, row.ProjectId, row.SessionUuid!, ct);
        }
    }

    private async Task NotifyAsync(BridgeDbContext db, string projectId, string sessionUuid, CancellationToken ct)
    {
        log.LogWarning(
            "CrashWatcher: project={Project} session={Session} window missing — notifying",
            projectId, sessionUuid);

        var publicBase = config["BRIDGE_PUBLIC_URL"]
            ?? Environment.GetEnvironmentVariable("BRIDGE_PUBLIC_URL");
        var clickUrl = !string.IsNullOrEmpty(publicBase)
            ? $"{publicBase.TrimEnd('/')}/sessions/{projectId}?session={Uri.EscapeDataString(sessionUuid)}"
            : null;

        try
        {
            await webPush.SendToAllAsync(
                db,
                projectId,
                title: $"Phiên {projectId} kết thúc bất ngờ",
                body: "Chạm để khởi động lại",
                clickUrl: clickUrl,
                ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "CrashWatcher: Web Push fanout failed for {Project}", projectId);
            // Still audit — the detection itself is the observable event.
        }

        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTimeOffset.UtcNow,
            ProjectId = projectId,
            SessionUuid = sessionUuid,
            Action = "crash_notified",
            Result = "ok",
            Detail = "tmux window missing; owner=tmux marker still present",
        });
        await db.SaveChangesAsync(ct);
    }
}
