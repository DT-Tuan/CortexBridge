using System.Globalization;
using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using CortexBridge.Api.Hooks;
using Microsoft.EntityFrameworkCore;

namespace CortexBridge.Api.Usage;

/// <summary>
/// Phase 3 of usage tracking. Reads the host-sampled usage JSON via
/// <see cref="UsageService"/>, snapshots it (sparkline) and fires Web Push on
/// quota threshold-crossings using the OFFICIAL Anthropic utilization (ADR-024).
///
/// Notify policy v2 (2026-06-12) — fixes a production spam bug. The dedup key
/// <c>window_id</c> used the RAW official <c>resets_at</c>, which jitters
/// sub-second every sample (12:29:59.x vs 12:30:00.x for the SAME window) →
/// dedup never held → every threshold + the reset push re-fired each tick.
/// Fix: <see cref="StableWindowId"/> rounds resets_at to the nearest MINUTE
/// (round, not truncate) — absorbs the jitter while keeping 5h/7d windows
/// (≥5h apart) distinct.
///
/// Thresholds: 5h block = 95; 7d week = 90 + 100. A one-shot "quota reset"
/// push fires when a PREVIOUSLY-ALERTED window rolls to a new stable window_id
/// (= the server actually reset; never early; silent during the limit).
/// Poller runs every 1 min for snappy reset detection (reads local usage.json,
/// 0 endpoint cost — the OAuth endpoint is rate-limited and sampled host-side
/// every 60 s, decoupled). Snapshots are throttled to ~5 min so the 1-min
/// cadence doesn't bloat usage_snapshots. Official block absent ⇒ skip (ADR-024
/// has no estimated fallback).
/// </summary>
public sealed class UsagePoller(
    UsageService usage,
    IServiceScopeFactory scopeFactory,
    WebPushSender push,
    ILogger<UsagePoller> log) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(5);
    private static readonly int[] Block5hThresholds = [95];
    private static readonly int[] Week7dThresholds = [90, 100];
    private const int ResetThreshold = -1;   // synthetic: "window reset/freed" push
    private const string PushProjectId = "_usage_";

    private DateTime _lastSnapshotUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Initial delay so we don't spam right at startup before the host
        // sampler has run.
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "UsagePoller tick failed");
            }
            try { await Task.Delay(ScanInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        var dto = usage.GetCurrent();
        if (dto is null)
        {
            log.LogDebug("No usage data yet; skipping poller tick");
            return;
        }

        var five = dto.Official?.FiveHour;
        var seven = dto.Official?.SevenDay;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();

        // Snapshot is throttled (sparkline only needs ~5-min resolution) so the
        // 1-min alert cadence doesn't write 1440 rows/day.
        if (DateTime.UtcNow - _lastSnapshotUtc >= SnapshotInterval)
        {
            db.UsageSnapshots.Add(new UsageSnapshot
            {
                TakenUtc = DateTime.UtcNow,
                Block5hId = dto.Block5h?.StartUtc ?? "",
                Block5hCurrentUsd = dto.Block5h?.CurrentCostUsd ?? 0m,
                Block5hProjectedUsd = dto.Block5h?.ProjectedCostUsd ?? 0m,
                Block5hPctCurrent = five?.Utilization ?? 0m,
                Block5hPctProjected = 0m,
                Week7dPeriod = dto.Week7d?.PeriodStart ?? "",
                Week7dCurrentUsd = dto.Week7d?.CurrentCostUsd ?? 0m,
                Week7dPctCurrent = seven?.Utilization ?? 0m,
            });
            await db.SaveChangesAsync(ct);
            _lastSnapshotUtc = DateTime.UtcNow;
        }

        if (five is not null)
        {
            var win = StableWindowId(five.ResetsAt);
            await CheckResetFreed(db, "block5h", win, "5h block", ct);
            await CheckThresholds(db, "block5h", win, five.Utilization, five.ResetsAt,
                Block5hThresholds, "5h block", ct);
        }
        if (seven is not null)
        {
            var win = StableWindowId(seven.ResetsAt);
            await CheckResetFreed(db, "week7d", win, "7d week", ct);
            await CheckThresholds(db, "week7d", win, seven.Utilization, seven.ResetsAt,
                Week7dThresholds, "7d week", ct);
        }
    }

    private async Task CheckThresholds(
        BridgeDbContext db, string kind, string windowId, decimal pct, string resetsAtIso,
        int[] thresholds, string humanLabel, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(windowId)) return;

        foreach (var threshold in thresholds)
        {
            if (pct < threshold) continue;
            var alreadySent = await db.UsageAlertsSent.AnyAsync(a =>
                a.WindowKind == kind && a.WindowId == windowId && a.ThresholdPct == threshold, ct);
            if (alreadySent) continue;

            var title = threshold >= 100
                ? $"Quota limit reached: {humanLabel}"
                : $"Usage {threshold}%+: {humanLabel}";
            var body = $"{pct:F0}% of plan limit · reset in {FormatReset(resetsAtIso)}.";
            await SendAndRecord(db, kind, windowId, threshold, title, body, ct);
        }
    }

    /// <summary>
    /// One-shot "quota reset" push: when the latest window we ALERTED on for this
    /// kind differs from the current (stable) window, the server has rolled the
    /// window over — the quota is freed. Deduped via <see cref="ResetThreshold"/>
    /// on the new window_id. Only fires if the prior window had a real threshold
    /// alert (= we were actually limited), so it stays silent during the limit
    /// and on windows that never tripped a threshold.
    /// </summary>
    private async Task CheckResetFreed(
        BridgeDbContext db, string kind, string windowId, string humanLabel, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(windowId)) return;

        var lastAlertedWindow = await db.UsageAlertsSent
            .Where(a => a.WindowKind == kind && a.ThresholdPct >= 0)
            .OrderByDescending(a => a.SentUtc)
            .Select(a => a.WindowId)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrEmpty(lastAlertedWindow) || lastAlertedWindow == windowId) return;

        var already = await db.UsageAlertsSent.AnyAsync(a =>
            a.WindowKind == kind && a.WindowId == windowId && a.ThresholdPct == ResetThreshold, ct);
        if (already) return;

        await SendAndRecord(db, kind, windowId, ResetThreshold,
            $"Quota reset: {humanLabel}",
            $"{humanLabel} đã reset — có thể chạy tiếp.", ct);
    }

    private async Task SendAndRecord(
        BridgeDbContext db, string kind, string windowId, int threshold,
        string title, string body, CancellationToken ct)
    {
        try
        {
            await push.SendToAllAsync(db, PushProjectId, title, body, "/usage", ct);
            db.UsageAlertsSent.Add(new UsageAlertSent
            {
                WindowKind = kind, WindowId = windowId,
                ThresholdPct = threshold, SentUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            log.LogInformation("Sent usage push: {Kind} {Id} threshold {Threshold}", kind, windowId, threshold);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to send usage push for {Kind} {Id} @ {Threshold}", kind, windowId, threshold);
        }
    }

    /// <summary>
    /// Stable dedup key for a window: the official resets_at ROUNDED to the
    /// nearest minute. The endpoint returns sub-second + ~1s boundary jitter for
    /// the same window (12:29:59.x vs 12:30:00.x); rounding (not truncating)
    /// collapses that to one key, while 5h/7d windows (≥5h apart) stay distinct.
    /// Falls back to the raw string if unparseable.
    /// </summary>
    private static string StableWindowId(string resetsAtIso)
    {
        if (!DateTimeOffset.TryParse(resetsAtIso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return resetsAtIso;
        var ticks = (dto.UtcDateTime.Ticks + TimeSpan.TicksPerMinute / 2)
                    / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute;
        return new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>Human "2h 13m" / "3d 4h" until the official resets_at, or "?" if unparseable.</summary>
    private static string FormatReset(string resetsAtIso)
    {
        if (!DateTimeOffset.TryParse(resetsAtIso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var resetsAt))
            return "?";
        var span = resetsAt - DateTimeOffset.UtcNow;
        if (span <= TimeSpan.Zero) return "soon";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{span.Minutes}m";
    }
}
