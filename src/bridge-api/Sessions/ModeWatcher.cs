using System.Collections.Concurrent;
using CortexBridge.Api.Data;
using CortexBridge.Api.Tmux;
using Microsoft.Extensions.DependencyInjection;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// ADR-017 automatic Mode A/B reconciler. Same shape as
/// <see cref="ProcessingWatchdog"/>: periodic, conservative, log-verified,
/// NEVER acts on ambiguity in the dangerous direction. The dangerous failure
/// is two `claude` on one session UID (interleaved JSONL, the
/// "[Request interrupted]" loop), so the two directions are asymmetric:
///
///   A→B (PC took the desk) — SAFE to automate. PC VS Code window is open for
///     the project (ide lockfile present) OR the last JSONL record's
///     entrypoint is "claude-vscode". The bridge kills its OWN tmux window
///     (the robust direction it controls: Esc → /exit → poll ≤3s →
///     kill-window) and records owner=pc. Zero two-process risk.
///
///   B→A (left the desk) — automate ONLY when provably safe. No ide lock for
///     the project for ≥ <see cref="Grace"/>, no fresh "claude-vscode" record,
///     no tmux window ⇒ the PC side is provably gone ⇒ resume in tmux + record
///     owner=tmux. Anything ambiguous (lock gone &lt; grace, fresh PC record,
///     ide dir unreadable) ⇒ DO NOTHING, keep pc. The same predicate is
///     published as <see cref="SessionOwnershipRegistry.TakeoverSafe"/> so the
///     PWA's single guarded "Tiếp quản" escape hatch is enabled only here.
/// </summary>
public sealed class ModeWatcher(
    IServiceScopeFactory scopeFactory,
    IdeLockReader ideLocks,
    SessionScanner scanner,
    TmuxClient tmux,
    SessionOwnershipRegistry ownership,
    SessionStateRegistry state,
    ProjectResumeMutex resumeMutex,
    BridgePaths paths,
    IConfiguration config,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(12);

    // Acceptance safety gate. ModeWatcher AUTO-KILLS a project's tmux window on
    // A→B — applied to every project it sees. During sign-off we must NOT touch
    // the live CortexBridge/project-zeta/vm-master sessions (cf. scratch-project-only test rule), so
    // BRIDGE_MODEWATCHER_PROJECTS=scratch-project scopes it to a CSV allowlist. Unset /
    // empty = act on ALL projects (the intended production behaviour). Disable
    // entirely with BRIDGE_MODEWATCHER_PROJECTS=-.
    private readonly HashSet<string>? _scope = ParseScope(
        config["BRIDGE_MODEWATCHER_PROJECTS"]
        ?? Environment.GetEnvironmentVariable("BRIDGE_MODEWATCHER_PROJECTS"));

    private static HashSet<string>? ParseScope(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null; // null ⇒ all projects
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private bool InScope(string projectId) =>
        _scope is null || _scope.Contains(projectId);
    // The PC side must be gone this long (lock absent) before B→A auto-fires.
    // Absorbs a VS Code reload / brief Remote-SSH blip without bouncing modes.
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(45);

    private readonly ILogger _log = loggerFactory.CreateLogger("ModeWatcher");
    // projectId → first tick we saw the PC lock absent while owner==pc.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _absentSince = new();

    // projectId → last time we auto-resumed it (B→A). A flapping PC ide-lock
    // (VS Code Remote-SSH whose lock read intermittently fails mid-write, or
    // whose heartbeat lapses) makes owner oscillate pc↔tmux every Grace window;
    // each B→A is a `claude --resume` that auto-compacts a large session. This
    // cooldown caps auto-resume to once per window so a flap can't trigger a
    // resume/compact storm. Live 2026-05-22: CortexBridge + project-beta each
    // compacted 5-6× from flapping. A genuine "user walked away" B→A is not
    // time-critical, so a few-minute delay is an acceptable price for immunity.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastResumeAt = new();
    private static readonly TimeSpan ResumeCooldown = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("ModeWatcher started — scope: {Scope}",
            _scope is null ? "ALL projects" : string.Join(",", _scope));
        using var timer = new PeriodicTimer(ScanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "ModeWatcher scan failed; continuing"); }
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        // Honor a PC-attach signal ONLY for projectIds that map to a real
        // workspace dir. Projects live UNDER the workspace root; a VS Code
        // window opened at /home/youruser (the home itself, not a project)
        // yields basename "youruser" via ParseProjects, and /workspace/youruser
        // doesn't exist — so ignore it. Without this, ModeWatcher "watches" a
        // phantom project: it stamps a bogus owner=pc ownership row and
        // reconciles something that can never run (live 2026-05-22: phantom
        // youruser + pc-bridge rows). Same workspace-dir reality check the
        // bootstrap uses. IdeLockReader stays pure (yields every basename per
        // its documented contract); the project-reality filter lives here.
        var pcProjects = ideLocks.PcAttachedProjects()
            .Where(p => Directory.Exists(Path.Combine(paths.WorkspaceRoot, p)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();

        // Candidate set: active sessions ∪ rows we already track ∪ PC-attached.
        var active = await scanner.ScanAsync(ct);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in active) candidates.Add(s.ProjectId);
        foreach (var p in pcProjects) candidates.Add(p);
        foreach (var r in db.SessionOwnerships.Select(x => x.ProjectId)) candidates.Add(r);

        foreach (var projectId in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!InScope(projectId)) continue; // acceptance safety gate
            try { await ReconcileProjectAsync(projectId, pcProjects.Contains(projectId), db, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ModeWatcher: {Project} reconcile failed (continuing)", projectId);
            }
        }
    }

    private async Task ReconcileProjectAsync(
        string projectId, bool pcAttached, BridgeDbContext db, CancellationToken ct)
    {
        var (owner, uuid, sinceUtc) = await ownership.ResolveAsync(projectId, db, tmux, scanner, ct);
        var tmuxAlive = await tmux.WindowExistsAsync(projectId, ct);

        var act = await scanner.ResolveAsync(projectId, ct);
        var (entrypoint, lastTs) = act?.JsonlPath is { } p
            ? await SessionScanner.ReadLastRecordMetaAsync(p, ct)
            : (null, null);
        var now = DateTimeOffset.UtcNow;

        // TWO distinct PC signals — must not be conflated (conflating them was
        // the "auto-B→A never fires after a real Mode-B session" bug):
        //
        //  - pcWriting (MARKER-relative, sticky): claude-vscode tail newer than
        //    the ownership marker sinceUtc. Answers "is PC a competing writer
        //    vs. our (possibly just-resumed) tmux?" — for the two-process guard
        //    and the ForcedTmux yield ONLY. Correctly stays true relative to a
        //    takeover marker (a pre-takeover fossil is older ⇒ false).
        //  - pcRecentlyActive (NOW-relative, decays): claude-vscode record
        //    within Grace of now. Answers "is PC still here?" — for pcPresent
        //    and the B→A freshness gate. MUST decay: once PC closes and stops
        //    writing, the stale claude-vscode tail must stop counting as
        //    "present" so automatic B→A can proceed. (Using sticky pcWriting
        //    here latched pcPresent true forever after Mode-B → ModeWatcher
        //    early-returned "steady Mode B" every scan, never reaching the
        //    grace/provable-safe B→A logic — live 2026-05-19.)
        var pcWriting = SessionOwnershipRegistry.PcIsLiveWriter(entrypoint, lastTs, sinceUtc);
        var pcRecentlyActive =
            SessionOwnershipRegistry.PcRecentlyActive(entrypoint, lastTs, now, Grace);
        var pcPresent = pcAttached || pcRecentlyActive;

        // ---- Two-process guard (highest priority, owner-INDEPENDENT) ----
        // SessionOwnershipRegistry.Derive() flips owner→Pc the instant the last
        // JSONL record's entrypoint == "claude-vscode". That makes the
        // owner-gated A→B branch below (and the ForcedTmux yield nested in it)
        // UNREACHABLE while a bridge tmux window is still alive — stranding two
        // `claude` on one session UID (the exact ADR-015/017 hazard). It is hit
        // when the user force-takes-over (tmux marker + ForcedTmux, tmux claude
        // resumed) and THEN starts CC on PC: Derive→Pc, A→B skipped, the stale
        // tmux claude is never killed. Caught live 2026-05-19 (user test).
        // Resolution: kill our OWN tmux window — the direction the bridge fully
        // controls — regardless of how owner derived. Fires ONLY when PC is
        // *actively writing POST-takeover* (claude-vscode record newer than
        // sinceUtc — see pcWriting above; a pre-takeover fossil does NOT count)
        // AND a tmux window lives: it never touches the legitimate ForcedTmux
        // hold (which is !pcWriting by definition), normal Mode A (no live
        // claude-vscode), normal Mode B (no tmux), or a just-resumed idle tmux
        // whose JSONL tail is still the stale pre-takeover PC line. Strictly
        // safer; no regression to the real two-process case.
        if (tmuxAlive && pcWriting)
        {
            _log.LogWarning(
                "ModeWatcher {Project}: TWO-PROCESS GUARD — claude-vscode is "
                + "writing while a bridge tmux window is alive; killing own "
                + "tmux to keep one claude per UID", projectId);
            ownership.SetForcedTmux(projectId, false);
            await KillOwnTmuxAsync(projectId, ct);
            await ownership.SetPcAsync(projectId, uuid ?? act?.SessionUuid, "mode-watcher", db, ct);
            _absentSince.TryRemove(projectId, out _);
            ownership.SetTakeoverSafe(projectId, false);
            return;
        }

        // ---- A → B : PC took the desk. Safe, immediate. ----
        if (pcPresent && owner != SessionOwnershipRegistry.Owner.Pc)
        {
            // Forced-takeover override (ADR-017 §3). The user explicitly took
            // the session back to Bridge while the PC ide-lock was still
            // present (VS Code Remote-SSH open, no CC on PC — the by-design
            // §2 case auto B→A can't resolve). Honor it: do NOT auto-revert
            // on the ide-lock ALONE. The ONLY thing that overrides the
            // override is pcWriting (entrypoint == "claude-vscode"): the PC
            // native CC is actually writing the JSONL — the real two-process
            // hazard — so we yield to PC (clear the flag, fall through to A→B).
            if (ownership.ForcedTmux(projectId))
            {
                if (!pcWriting)
                {
                    _log.LogDebug(
                        "ModeWatcher {Project}: forced-tmux override active, PC ide-lock "
                        + "present but not writing — holding tmux (no auto A→B revert)",
                        projectId);
                    _absentSince.TryRemove(projectId, out _);
                    ownership.SetTakeoverSafe(projectId, false);
                    return;
                }
                _log.LogInformation(
                    "ModeWatcher {Project}: forced-tmux override YIELDING — PC native CC "
                    + "is writing (entrypoint=claude-vscode); clearing override → A→B",
                    projectId);
                ownership.SetForcedTmux(projectId, false);
            }
            if (tmuxAlive)
            {
                _log.LogInformation(
                    "ModeWatcher A→B {Project}: PC attached ({Why}) — killing own tmux window",
                    projectId, pcAttached ? "ide-lock" : "entrypoint=claude-vscode");
                await KillOwnTmuxAsync(projectId, ct);
            }
            // Clear any stale needsInput from the Bridge-mode session. The tmux
            // window (and its Blocked pane) no longer exist; PC-mode sessions are
            // managed by VS Code's own UI. Bug fix 2026-07-06: needsInput stuck
            // after A→B handoff (docs/bugs/2026-07-06-needsinput-stuck-on-mode-ab.md).
            // Unconditional: even if tmuxAlive=false (window already gone), we still
            // need to clear needsInput when transitioning to PC mode.
            var hadNeedsInput = state.NeedsInput(projectId);
            if (hadNeedsInput)
                _log.LogDebug("ModeWatcher A→B {Project}: clearing stale needsInput", projectId);
            state.SetNeedsInput(projectId, false, null, uuid ?? act?.SessionUuid);
            await ownership.SetPcAsync(projectId, uuid ?? act?.SessionUuid, "mode-watcher", db, ct);
            _absentSince.TryRemove(projectId, out _);
            ownership.SetTakeoverSafe(projectId, false);
            return;
        }

        if (pcPresent)
        {
            // Already pc and PC still here — steady Mode B.
            _absentSince.TryRemove(projectId, out _);
            ownership.SetTakeoverSafe(projectId, false);
            return;
        }

        // ---- owner == pc, PC NOT present : candidate B → A ----
        if (owner == SessionOwnershipRegistry.Owner.Pc)
        {
            var firstAbsent = _absentSince.GetOrAdd(projectId, _ => now);
            var goneLongEnough = now - firstAbsent >= Grace;
            // "Fresh PC write within grace" = PC only just closed / might still
            // be flushing → not provably gone yet. This MUST be the decaying,
            // now-relative signal — NOT the sticky marker-relative pcWriting
            // (which, post-Mode-B, never decays and made provableSafe forever
            // false, so auto-B→A could never fire — live 2026-05-19).
            var freshPcRecord = pcRecentlyActive;
            var provableSafe = goneLongEnough && !freshPcRecord && !tmuxAlive
                               && !string.IsNullOrEmpty(uuid);

            ownership.SetTakeoverSafe(projectId, provableSafe);

            if (!provableSafe)
            {
                _log.LogDebug(
                    "ModeWatcher {Project}: pc, PC-lock absent but not provable-safe "
                    + "(goneLongEnough={Gone} freshPcRecord={Fresh} tmuxAlive={Tmux}) — holding",
                    projectId, goneLongEnough, freshPcRecord, tmuxAlive);
                return;
            }

            // Cooldown: a flapping PC lock can satisfy provableSafe every few
            // ticks; cap auto-resume frequency so it can't storm `claude
            // --resume` (each one compacts a large session). Stay pc until the
            // window elapses; a genuine return resumes on the next eligible
            // tick. The guarded UI "Tiếp quản" button is NOT subject to this —
            // it's an explicit user action on a separate path.
            if (_lastResumeAt.TryGetValue(projectId, out var lastResume)
                && now - lastResume < ResumeCooldown)
            {
                _log.LogInformation(
                    "ModeWatcher {Project}: provably-safe B→A SUPPRESSED — auto-resumed "
                    + "{Ago}s ago (< {Cooldown}s cooldown). PC ide-lock is flapping; "
                    + "holding to avoid a resume/compact storm.",
                    projectId, (int)(now - lastResume).TotalSeconds,
                    (int)ResumeCooldown.TotalSeconds);
                return;
            }

            // Provably safe → auto B→A. The guarded UI button hits the same
            // mutex-protected resume path, so a user tap can't double-spawn.
            using var lease = resumeMutex.TryAcquire(projectId);
            if (lease is null)
            {
                _log.LogDebug("ModeWatcher {Project}: resume already in flight — skip", projectId);
                return;
            }
            await tmux.KillWindowAsync(projectId, ct); // belt-and-braces; should be none
            var cwd = Path.Combine(paths.WorkspaceRoot, projectId);
            try
            {
                await tmux.NewWindowAsync(projectId, cwd, $"claude --resume {uuid}", ct);
            }
            catch (TmuxException ex)
            {
                _log.LogWarning(ex, "ModeWatcher B→A {Project}: tmux resume failed — staying pc", projectId);
                return;
            }
            await ownership.SetTmuxAsync(projectId, uuid, "mode-watcher", db, ct);
            _absentSince.TryRemove(projectId, out _);
            _lastResumeAt[projectId] = now; // arm the cooldown against flapping
            ownership.SetTakeoverSafe(projectId, false);
            _log.LogInformation(
                "ModeWatcher B→A {Project}: PC provably gone ≥{Grace}s — resumed tmux claude --resume <uid>",
                projectId, (int)Grace.TotalSeconds);
            return;
        }

        // owner is tmux/none and no PC — steady Mode A; clear transient state.
        // The PC lock is also gone now, so a prior forced-tmux override has
        // served its purpose (the want-tmux-while-lock-present state it guarded
        // no longer holds) — retire it so it can't linger stale.
        _absentSince.TryRemove(projectId, out _);
        if (ownership.ForcedTmux(projectId))
            ownership.SetForcedTmux(projectId, false);
        ownership.SetTakeoverSafe(projectId, false);
    }

    /// <summary>
    /// Robust own-side stop (mirrors HandoffEndpoint.HandoffToPc): Esc dismisses
    /// any open menu so /exit isn't eaten as a paste-cancel; /exit + Enter;
    /// poll ≤3s; unconditional kill-window backstop. The bridge owns this tmux
    /// window and tmux does not respawn claude, so this always terminates it.
    /// </summary>
    private async Task KillOwnTmuxAsync(string projectId, CancellationToken ct)
    {
        try
        {
            try { await tmux.SendKeyAsync(projectId, "Escape", ct); }
            catch (TmuxException) { /* nothing to dismiss */ }
            await tmux.SendKeysAsync(projectId, "/exit", ct);
            await tmux.SendKeyAsync(projectId, "Enter", ct);
        }
        catch (TmuxException ex)
        {
            _log.LogWarning(ex, "ModeWatcher {Project}: /exit injection failed — forcing kill", projectId);
        }

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && await tmux.WindowExistsAsync(projectId, ct))
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);

        if (await tmux.WindowExistsAsync(projectId, ct))
            await tmux.KillWindowAsync(projectId, ct);
    }
}
