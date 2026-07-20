using CortexBridge.Api.Data;
using CortexBridge.Api.Tmux;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// Self-healing safety net for the per-project Processing flag.
///
/// Processing is set true by the activity hook (UserPromptSubmit/PreToolUse/
/// PostToolUse) and cleared by the Stop hook. But built-in slash commands
/// (/clear, /compact, /exit) and some skill paths emit a start-signal hook
/// with NO terminal Stop, so the flag latches true and every PWA/companion
/// client shows "thinking" with the composer locked forever.
///
/// The OLD watchdog cleared purely on "no hook for 120s". That wrongly
/// unlocked the composer mid-turn for two silent-but-busy states (S1 stuck
/// AskUserQuestion, S2 one long tool-call/think) — the chronic "composer
/// unlocks early" bug. So before clearing a stale candidate we now READ the
/// session's tmux pane (reusing the same capture-pane infra as
/// <see cref="Endpoints.PromptEndpoint"/>) and classify it:
///   - Working  ("esc to interrupt")           → keep Processing (still working)
///   - Blocked  (menu/permission/AskUserQuestion open) → SetNeedsInput(true):
///       the bridge is otherwise BLIND to AskUserQuestion (it fires no hook).
///   - Idle     (clean prompt / unrecognised)  → genuine dead latch: clear it.
/// If the pane can't be read (window gone / tmux error) we fall back to the
/// active JSONL mtime: fresh ⇒ still alive (keep); stale ⇒ crashed (clear).
///
/// Clearing fires StatusChanged → SSE pushes the corrected status frame →
/// clients un-stick live with no reload.
/// </summary>
public sealed class ProcessingWatchdog(
    SessionStateRegistry state,
    TmuxClient tmux,
    SessionScanner scanner,
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(15);

    // Post-spawn grace: skip Blocked-sweep for sessions whose ownership row
    // was set within this window. A freshly-spawned `claude --resume <uid>`
    // sits at a Blocked pane (composer ready for first user prompt) with no
    // hook fired yet — `state.LastEventAt` is null so the 10s
    // RecentActivityWindow doesn't cover it. Without this grace, the
    // Blocked-sweep falsely surfaces "needsInput" right after every activate/
    // restart of a migrated/parked session. Live failure 2026-05-21 on
    // CortexPlexus + project-alpha.
    private static readonly TimeSpan PostSpawnGrace = TimeSpan.FromSeconds(60);
    // No PreToolUse/PostToolUse for 2 min while still flagged busy makes a
    // project a CANDIDATE — not proof it's dead. The pane check below is what
    // actually decides; the threshold just bounds how long a real dead latch
    // can linger before we look.
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(120);

    private readonly ILogger _log = loggerFactory.CreateLogger("ProcessingWatchdog");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ScanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a bad scan kill the loop — the watchdog must outlive
                // every transient fault to keep clients un-stickable.
                _log.LogError(ex, "Watchdog scan failed; continuing");
            }
        }
    }

    // A hook/reply just touched this project — let it settle one tick before
    // the Blocked sweep can (re)surface needsInput, so an answer-in-flight
    // doesn't flicker the banner back on. The picker, if truly still open,
    // persists and is caught next tick.
    private static readonly TimeSpan RecentActivityWindow = TimeSpan.FromSeconds(10);

    // Debounce for surfacing needsInput from a pane read: require the SAME
    // Blocked classification on this many CONSECUTIVE scans before believing
    // it. A real stuck picker persists for minutes, so the added latency is
    // one scan interval; a transient misread (mid-redraw frame, adversarial
    // transcript content — live failure 2026-07-18 re-asked an answered
    // AskUserQuestion every sweep) never survives two. Keyed per project;
    // reset by any non-Blocked classification. Only the watchdog loop touches
    // it — no locking needed.
    private const int BlockedStreakToSurface = 2;
    // Two observations only count as CONSECUTIVE when they are at most this far
    // apart — a residual count must not pair with a transient minutes later
    // (finder finding 2026-07-18: a streak bumped once, left un-scanned for a
    // while, then confirmed by an unrelated transient = debounce bypassed).
    private static readonly TimeSpan BlockedStreakMaxGap = ScanInterval * 2 + TimeSpan.FromSeconds(5);
    private readonly Dictionary<string, (int Count, DateTimeOffset At)> _blockedStreak =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bump the project's consecutive-Blocked counter; true once it
    /// reaches <see cref="BlockedStreakToSurface"/>. A stale prior observation
    /// (older than <see cref="BlockedStreakMaxGap"/>) restarts the streak. The
    /// caller resets after surfacing so the NEXT arming needs a fresh streak
    /// (finder finding 2026-07-18: without that, one confirm disabled the
    /// debounce for every later re-arm).</summary>
    private bool BlockedConfirmed(string projectId)
    {
        var now = DateTimeOffset.UtcNow;
        var count = _blockedStreak.TryGetValue(projectId, out var prev)
            && now - prev.At <= BlockedStreakMaxGap
            ? prev.Count + 1 : 1;
        _blockedStreak[projectId] = (count, now);
        return count >= BlockedStreakToSurface;
    }

    private void ResetBlockedStreak(string projectId) => _blockedStreak.Remove(projectId);

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var candidates = state.FindStaleProcessing(IdleThreshold);

        var cleared = new List<string>();   // genuine dead latch — composer unlocks
        var surfaced = new List<string>();  // silent menu — promoted to needsInput

        foreach (var (projectId, sessionUuid) in candidates)
        {
            ct.ThrowIfCancellationRequested();

            string? pane = await TryCapturePaneAsync(projectId, ct);

            if (pane is null)
            {
                // Fallback C: pane unreadable (window gone / tmux error). Use the
                // active JSONL mtime — a still-living turn keeps appending output
                // even when it fires no per-tool hook; a crash/exit goes quiet.
                var sess = await scanner.ResolveAsync(projectId, ct);
                var jsonlFresh = sess is not null
                    && sess.LastModified > DateTimeOffset.UtcNow - IdleThreshold;
                if (jsonlFresh)
                {
                    _log.LogDebug(
                        "Watchdog: {Project} pane unreadable but JSONL fresh — keeping Processing",
                        projectId);
                    continue;
                }
                state.SetProcessing(projectId, false, sessionUuid);
                cleared.Add(projectId);
                continue;
            }

            switch (PaneClassifier.Classify(pane))
            {
                case PaneClassifier.PaneState.Working:
                    // S2: a single long tool-call/think. Real work — leave it.
                    // Re-checked every scan until the pane changes or Stop fires.
                    ResetBlockedStreak(projectId);
                    _log.LogDebug(
                        "Watchdog: {Project} pane shows a running turn — keeping Processing",
                        projectId);
                    break;

                case PaneClassifier.PaneState.Blocked:
                    // S1: a permission / AskUserQuestion menu is open. The bridge
                    // got NO hook for it, so promote the silent latch to the
                    // authoritative needsInput state — banner shows, composer
                    // stays gated, PWA's /prompt fetch renders the real options.
                    // Debounced: a real picker survives consecutive scans.
                    if (BlockedConfirmed(projectId))
                    {
                        state.SetNeedsInput(projectId, true, "Claude needs your input", sessionUuid);
                        surfaced.Add(projectId);
                        ResetBlockedStreak(projectId); // next arming needs a fresh streak
                        _log.LogInformation(
                            "Watchdog: {Project} stale-pass Blocked confirmed (marker={Marker})",
                            projectId, PaneClassifier.BlockedMarker(pane) ?? "?");
                    }
                    else
                    {
                        _log.LogDebug(
                            "Watchdog: {Project} pane Blocked once (marker={Marker}) — awaiting confirmation",
                            projectId, PaneClassifier.BlockedMarker(pane) ?? "?");
                    }
                    break;

                default: // Idle
                    ResetBlockedStreak(projectId);
                    var confident = PaneClassifier.IsConfidentIdle(pane);
                    state.SetProcessing(projectId, false, sessionUuid);
                    cleared.Add(projectId);
                    _log.LogDebug(
                        "Watchdog: {Project} cleared ({Kind} idle prompt)",
                        projectId, confident ? "confirmed" : "unrecognised");
                    break;
            }
        }

        if (surfaced.Count > 0)
            _log.LogInformation(
                "Watchdog surfaced needsInput for {Count} project(s): {Projects} "
                + "(pane shows an open menu/permission prompt that fired no hook — S1)",
                surfaced.Count, string.Join(", ", surfaced));

        if (cleared.Count > 0)
            _log.LogWarning(
                "Watchdog cleared stale Processing for {Count} project(s): {Projects} "
                + "(pane is a clean idle prompt — dead latch from a slash command/"
                + "skill/crash with no Stop)",
                cleared.Count, string.Join(", ", cleared));

        // ADR-017 follow-up: the stale-Processing pass above NEVER sees a
        // project whose Processing is false — but a pending AskUserQuestion
        // fires NO Notification hook AND Processing is also lost on a bridge
        // restart, so such a picker stayed invisible to the PWA (no needsInput
        // → banner never rendered → "CC stuck waiting"). Independently sweep
        // every running tmux window's pane for a Blocked picker, regardless of
        // Processing, and surface it.
        var handledProjects = new List<string>(candidates.Count);
        foreach (var c in candidates) handledProjects.Add(c.ProjectId);
        await SweepBlockedPanesAsync(handledProjects, ct);
    }

    /// <summary>
    /// SET-only Blocked sweep (conservative). For every running tmux window
    /// not already handled by the stale pass and not already needsInput,
    /// classify the live pane; an open picker (PaneClassifier.Blocked) ⇒
    /// SetNeedsInput(true) even though Processing is false / no hook fired.
    /// Clearing stays with the existing Stop/reply/SetProcessing paths (over-
    /// clearing is the dangerous direction; a lingering banner is the safe
    /// failure). Recent-activity guard prevents an answer-in-flight flicker.
    /// </summary>
    private async Task SweepBlockedPanesAsync(IReadOnlyList<string> alreadyHandled, CancellationToken ct)
    {
        List<string> windows;
        try { windows = await tmux.ListWindowsAsync(ct); }
        catch (TmuxException) { return; }

        // Read all "tmux"-owner rows whose spawn timestamp falls inside the
        // post-spawn grace window — skip Blocked-sweep for these projects.
        // One scoped DB read per scan; cheap (one-row-per-project table).
        var recentlySpawned = await RecentlySpawnedProjectsAsync(ct);

        var surfaced = new List<string>();
        foreach (var projectId in windows)
        {
            ct.ThrowIfCancellationRequested();
            if (alreadyHandled.Contains(projectId)) continue;     // stale pass classified it
            if (state.NeedsInput(projectId)) continue;            // already surfaced (saves a capture)
            // Post-spawn grace: the project's ownership row was just (re)set
            // by an activate/restart/new path — pane is naturally Blocked at
            // the "ready for first prompt" composer with no hook fired yet.
            // Don't false-positive the banner on it.
            if (recentlySpawned.Contains(projectId)) continue;
            // Something just happened (hook/reply) — settle one tick so an
            // answer-in-flight doesn't bounce the banner back on.
            if (state.LastEventAt(projectId) is { } t
                && DateTimeOffset.UtcNow - t < RecentActivityWindow) continue;

            var pane = await TryCapturePaneAsync(projectId, ct);
            if (pane is null) continue;
            if (PaneClassifier.Classify(pane) != PaneClassifier.PaneState.Blocked)
            {
                ResetBlockedStreak(projectId);
                continue;
            }
            // Debounce: only a picker that survives consecutive sweeps is real
            // (transient misreads re-asked an answered AskUserQuestion — live
            // failure 2026-07-18).
            if (!BlockedConfirmed(projectId))
            {
                _log.LogDebug(
                    "Watchdog Blocked-sweep: {Project} Blocked once (marker={Marker}) — awaiting confirmation",
                    projectId, PaneClassifier.BlockedMarker(pane) ?? "?");
                continue;
            }
            _log.LogInformation(
                "Watchdog Blocked-sweep: {Project} confirmed (marker={Marker})",
                projectId, PaneClassifier.BlockedMarker(pane) ?? "?");

            // Resolve the active VM sessionUuid so SetNeedsInput keys onto
            // the same (projectId, sessionUuid) entry that the PWA queries.
            // Without this the state writes to SessionStateRegistry's
            // newest-LastEventAt fallback (or worse, the project-wide ""
            // bucket if no entries exist yet), and the PWA — which opens
            // with `?session=<uuid>` — reads a different entry and sees
            // needsInput=false. Live failure 2026-05-22 on CortexPlexus:
            // Blocked-sweep "surfaced" the latch but PWA banner never
            // rendered. Same as the resolver added in InternalHooksEndpoints
            // NotificationHandler.
            var resolved = await scanner.ResolveAsync(projectId, ct);
            state.SetNeedsInput(projectId, true, "Claude needs your input", resolved?.SessionUuid);
            surfaced.Add(projectId);
            ResetBlockedStreak(projectId); // next arming needs a fresh streak
        }

        if (surfaced.Count > 0)
            _log.LogInformation(
                "Watchdog Blocked-sweep surfaced needsInput for {Count} project(s): {Projects} "
                + "(open picker, no Notification hook / Processing not latched — e.g. "
                + "pending AskUserQuestion or post-restart)",
                surfaced.Count, string.Join(", ", surfaced));
    }

    /// <summary>
    /// Read-only capture of the session's tmux pane. Returns null when the
    /// window doesn't exist or tmux errors — the caller falls back to JSONL
    /// mtime. Never throws (the watchdog loop must not die on a tmux hiccup).
    /// </summary>
    private async Task<string?> TryCapturePaneAsync(string projectId, CancellationToken ct)
    {
        try
        {
            if (!await tmux.WindowExistsAsync(projectId, ct)) return null;
            return await tmux.CapturePaneAsync(projectId, ct);
        }
        catch (TmuxException)
        {
            return null;
        }
    }

    /// <summary>
    /// Projects whose <c>session_ownership.SinceUtc</c> is inside the
    /// post-spawn grace window — i.e. the bridge just spawned a tmux
    /// `claude --resume` for them and the pane is naturally Blocked
    /// (composer ready for the first user prompt). DB failure ⇒ empty set
    /// (conservative: Blocked-sweep can still fire — the worse failure mode
    /// is the original false-positive, not under-firing).
    /// </summary>
    private async Task<HashSet<string>> RecentlySpawnedProjectsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
            var cutoff = DateTimeOffset.UtcNow - PostSpawnGrace;
            // EF Core SQLite cannot ORDER BY a DateTimeOffset (hardening #3
            // caught this) but a Where comparison on DateTimeOffset IS
            // supported. Single-table small scan.
            var rows = await db.SessionOwnerships
                .Where(x => x.Owner == "tmux" && x.SinceUtc > cutoff)
                .Select(x => x.ProjectId)
                .ToListAsync(ct);
            return new HashSet<string>(rows, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "RecentlySpawnedProjectsAsync failed; treating as empty (Blocked-sweep allowed)");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
