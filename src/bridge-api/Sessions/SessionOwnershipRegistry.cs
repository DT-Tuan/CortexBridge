using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using CortexBridge.Api.Tmux;
using Microsoft.EntityFrameworkCore;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// Per-project session ownership tracker for ADR-015 Mode A / Mode B.
///
/// Semantics:
///   - "tmux"  → claude is running inside our tmux window for this project (Mode A).
///   - "pc"    → user explicitly handed off to the Anthropic native CC extension (Mode B).
///   - "none"  → no claude process is bound to this project right now.
///
/// Ownership is DERIVED (see <see cref="Derive"/>), combining two signals:
///   1. The explicit handoff marker we persist (pc on hand-off, tmux on
///      take-over) — an out-of-band user decision, timestamped.
///   2. The active JSONL's last-record `entrypoint` — the ground truth of who
///      is actually writing the session right now ("claude-vscode" = the PC
///      native extension, anything else = a bridge/tmux `claude`). This catches
///      the case the marker can't: the user started CC directly on the PC
///      (never went through our hand-off) while a stale tmux window lingers.
/// "a tmux window with this name exists" alone is NOT sufficient — a parked
/// tmux claude can coexist with the live PC one on the same session UID.
/// </summary>
public class SessionOwnershipRegistry
{
    public enum Owner { None, Tmux, Pc }

    /// <summary>
    /// Single source of truth for owner derivation. Priority:
    ///  1. explicit pc marker            → Pc  (user handed off to PC)
    ///  2. explicit tmux marker, and no JSONL record written since it was set
    ///                                    → Tmux (recent take-over; PC hasn't
    ///                                      written anything back yet)
    ///  3. last record entrypoint == "claude-vscode"
    ///                                    → Pc  (PC native ext is the live writer)
    ///  4. a tmux window for the project exists
    ///                                    → Tmux
    ///  5. otherwise                      → None
    /// </summary>
    public static Owner Derive(
        SessionOwnership? row, string? lastEntrypoint, DateTimeOffset? lastTs, bool tmuxAlive)
    {
        if (row is not null && string.Equals(row.Owner, "pc", StringComparison.OrdinalIgnoreCase))
            return Owner.Pc;
        if (row is not null && string.Equals(row.Owner, "tmux", StringComparison.OrdinalIgnoreCase)
            && (lastTs is null || row.SinceUtc >= lastTs))
            return Owner.Tmux;
        if (string.Equals(lastEntrypoint, "claude-vscode", StringComparison.OrdinalIgnoreCase))
            return Owner.Pc;
        if (tmuxAlive)
            return Owner.Tmux;
        return Owner.None;
    }

    /// <summary>
    /// Is the PC native CC ("claude-vscode") the LIVE writer right now? True
    /// ONLY when the last JSONL record's entrypoint is "claude-vscode" AND it
    /// was written strictly AFTER the bridge took ownership
    /// (<paramref name="sinceUtc"/> — the persisted marker time). A
    /// claude-vscode record at-or-before the marker is a PRE-TAKEOVER FOSSIL:
    /// just after a (forced) take-over the resumed tmux `claude` sits idle at
    /// the prompt and has not yet written its first "cli" record, so the JSONL
    /// tail is still the last line the PC session wrote *before* hand-back.
    /// This is the SAME staleness discipline <see cref="Derive"/> applies to
    /// the tmux marker (<c>row.SinceUtc &gt;= lastTs</c>); the owner-INDEPENDENT
    /// two-process guard in <c>ModeWatcher</c> MUST use this and not a raw
    /// entrypoint check, otherwise a forced take-over flaps straight back to PC
    /// (live user bug 2026-05-19: PC powered off *before* takeover, 5 reverts).
    /// The genuine two-process hazard (force-takeover, THEN start CC on PC)
    /// has the PC write strictly after the marker, so it still returns true:
    /// zero regression.
    /// </summary>
    public static bool PcIsLiveWriter(
        string? lastEntrypoint, DateTimeOffset? lastTs, DateTimeOffset sinceUtc) =>
        string.Equals(lastEntrypoint, "claude-vscode", StringComparison.OrdinalIgnoreCase)
        && lastTs is { } lt && lt > sinceUtc;

    /// <summary>
    /// Is the PC native CC active RIGHT NOW — i.e. the last JSONL record is a
    /// "claude-vscode" record whose timestamp is within <paramref name="grace"/>
    /// of <paramref name="now"/>. This is **recency-relative-to-now**, the
    /// opposite axis from <see cref="PcIsLiveWriter"/> which is
    /// **marker-relative** ("did PC write after we took ownership?").
    ///
    /// They answer different questions and must not be conflated:
    ///  - <see cref="PcIsLiveWriter"/> → the two-process guard / ForcedTmux
    ///    yield: "is PC a competing writer vs. our (possibly just-resumed)
    ///    tmux?". Correctly *sticky* relative to the takeover marker.
    ///  - <see cref="PcRecentlyActive"/> → "is PC still here?" for
    ///    `pcPresent` / the B→A `freshPcRecord` gate. Must DECAY: once PC
    ///    closes and stops writing, the (now stale) claude-vscode tail must
    ///    stop counting as "present" so automatic B→A can proceed.
    ///
    /// Using the sticky marker-relative predicate for "is PC here" was the
    /// bug behind "auto-B→A never fires after a real Mode-B session — the
    /// user must always force": the post-Mode-B claude-vscode tail stays
    /// forever-newer than the OLD pc marker, so pcPresent latched true and
    /// ModeWatcher early-returned at "steady Mode B" every scan, never
    /// reaching the grace/provable-safe B→A logic (live test 2026-05-19).
    /// </summary>
    public static bool PcRecentlyActive(
        string? lastEntrypoint, DateTimeOffset? lastTs, DateTimeOffset now, TimeSpan grace) =>
        string.Equals(lastEntrypoint, "claude-vscode", StringComparison.OrdinalIgnoreCase)
        && lastTs is { } lt && now - lt < grace;

    /// <summary>One project session for <see cref="PickLiveSession"/>.
    /// <paramref name="LastRecordTs"/> is the last *conversation record*'s
    /// timestamp (from <c>ReadLastRecordMetaAsync</c>) — NOT the file's mtime.</summary>
    public readonly record struct SessionCandidate(string Uuid, DateTimeOffset? LastRecordTs);

    /// <summary>
    /// ADR-016 Slice 1, step 0 (the keystone). Pick a project's CANONICAL
    /// (live-slot) session UID — **never file mtime**, which is the shadowing
    /// bug: a parked/test sibling JSONL whose file was merely touched (a
    /// metadata/snapshot write) out-mtimes the session the live `claude` is
    /// actually driving, so <c>SessionScanner</c> (newest-file-mtime) resolves
    /// the wrong session for every write/state/notify surface.
    ///
    /// Priority — consistent with <see cref="Derive"/> (no divergent second
    /// notion of "the session"):
    ///  1. The bridge's explicitly-tracked slot occupant
    ///     (<paramref name="ownershipUuid"/> = <c>session_ownership.SessionUuid</c>,
    ///     set authoritatively by SetTmux/SetPc) — if it is among candidates it
    ///     wins outright. This alone kills the mtime-sibling shadowing for any
    ///     tracked project. Row staleness is ADR-017 Derive's concern, not this
    ///     picker's (it must not second-guess the authoritative row).
    ///  2. Untracked project (no row) → the session being actively written =
    ///     newest *last-record* timestamp (not file mtime).
    ///  3. Nothing decidable → null ⇒ caller may fall back to mtime, the
    ///     pre-ADR-016 behaviour, ONLY for the truly-untracked/empty case.
    /// Pure + deterministic + has no file-mtime input by construction, so it
    /// structurally cannot be fooled by mtime. Unit-tested.
    /// </summary>
    public static string? PickLiveSession(
        IReadOnlyList<SessionCandidate> candidates, string? ownershipUuid)
    {
        if (candidates is null || candidates.Count == 0) return null;

        if (!string.IsNullOrEmpty(ownershipUuid))
            foreach (var c in candidates)
                if (string.Equals(c.Uuid, ownershipUuid, StringComparison.OrdinalIgnoreCase))
                    return c.Uuid;

        string? best = null;
        DateTimeOffset bestTs = DateTimeOffset.MinValue;
        foreach (var c in candidates)
            if (c.LastRecordTs is { } ts && (best is null || ts > bestTs))
            {
                best = c.Uuid;
                bestTs = ts;
            }
        return best;
    }

    public static string ToWire(Owner o) => o switch
    {
        Owner.Tmux => "tmux",
        Owner.Pc => "pc",
        _ => "none",
    };

    /// <summary>
    /// Fires when the bridge changes ownership. StreamEndpoint subscribes per-connection
    /// to push an owner_change frame so the PWA updates live (no reload).
    /// </summary>
    public event Action<string, Owner>? OwnerChanged;

    // ADR-017: per-project "B→A is provably safe right now" flag, maintained by
    // ModeWatcher (PC lock gone ≥ grace, no fresh claude-vscode record, no tmux).
    // Drives the single guarded "Tiếp quản" escape hatch — the PWA only enables
    // it when this is true; it can never force an unsafe switch.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _takeoverSafe = new();

    public bool TakeoverSafe(string projectId) =>
        _takeoverSafe.TryGetValue(projectId, out var v) && v;

    // ADR-017 §3 (forced-takeover override). Set true when the user explicitly
    // took the session back to Bridge via the guarded "Tiếp quản (ép)" button
    // WHILE the PC ide-lock was still present — i.e. the VS Code Remote-SSH
    // window is open but no CC is running on PC. That is the by-design ADR-017
    // §2 case automatic B→A *cannot* resolve (lock present ⇏ CC present), so
    // without this flag ModeWatcher would A→B-revert the takeover on the next
    // 12 s scan. It tells ModeWatcher: do NOT auto-revert on the ide-lock
    // ALONE. It is overridden only by the real two-process hazard
    // (entrypoint == "claude-vscode": PC native CC actually writing the JSONL)
    // and is cleared on any transition back to PC (see SetPcAsync).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _forcedTmux = new();

    public bool ForcedTmux(string projectId) =>
        _forcedTmux.TryGetValue(projectId, out var v) && v;

    public void SetForcedTmux(string projectId, bool forced) =>
        _forcedTmux[projectId] = forced;

    /// <summary>Set by ModeWatcher each scan. Fires OwnerChanged on transition so
    /// the PWA re-renders the guarded button without a reload.</summary>
    public void SetTakeoverSafe(string projectId, bool safe)
    {
        var prev = _takeoverSafe.TryGetValue(projectId, out var v) && v;
        _takeoverSafe[projectId] = safe;
        // Owner itself is unchanged; the StreamEndpoint handler ignores this arg
        // and re-resolves, so the arg value is irrelevant — the event is just
        // the "re-push owner_change (now carrying takeoverSafe)" trigger.
        if (prev != safe) OwnerChanged?.Invoke(projectId, Owner.None);
    }

    public async Task<(Owner owner, string? sessionUuid, DateTimeOffset sinceUtc)>
        ResolveAsync(string projectId, BridgeDbContext db, TmuxClient tmux,
                     SessionScanner scanner, CancellationToken ct)
    {
        var row = await db.SessionOwnerships
            .FirstOrDefaultAsync(x => x.ProjectId == projectId, ct);

        var active = await scanner.ResolveAsync(projectId, ct);
        var (entrypoint, lastTs) = active?.JsonlPath is { } p
            ? await SessionScanner.ReadLastRecordMetaAsync(p, ct)
            : (null, null);
        var tmuxAlive = await tmux.WindowExistsAsync(projectId, ct);

        var owner = Derive(row, entrypoint, lastTs, tmuxAlive);
        return (owner, active?.SessionUuid ?? row?.SessionUuid,
                row?.SinceUtc ?? DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Persist an explicit "pc" handoff. Caller has already stopped the tmux claude
    /// (via SendReplyAsync("/exit") etc.) — this only records the decision.
    /// </summary>
    public async Task SetPcAsync(string projectId, string? sessionUuid, string client,
                                 BridgeDbContext db, CancellationToken ct)
    {
        var row = await db.SessionOwnerships
            .FirstOrDefaultAsync(x => x.ProjectId == projectId, ct);
        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            db.SessionOwnerships.Add(new SessionOwnership
            {
                ProjectId = projectId,
                Owner = "pc",
                SessionUuid = sessionUuid,
                SinceUtc = now,
                ChangedByClient = client,
            });
        }
        else
        {
            row.Owner = "pc";
            row.SessionUuid = sessionUuid;
            row.SinceUtc = now;
            row.ChangedByClient = client;
        }
        await db.SaveChangesAsync(ct);
        // Any move back to PC retires a prior forced-tmux override — the
        // situation it guarded (want tmux while lock present) no longer holds.
        _forcedTmux.TryRemove(projectId, out _);
        OwnerChanged?.Invoke(projectId, Owner.Pc);
    }

    /// <summary>
    /// Persist an explicit "tmux" take-over marker (timestamped). Honored by
    /// <see cref="Derive"/> only until a newer JSONL record appears, so a real
    /// take-over wins over the stale pre-takeover PC entrypoint, then naturally
    /// yields to the live entrypoint signal.
    /// </summary>
    public async Task SetTmuxAsync(string projectId, string? sessionUuid, string client,
                                   BridgeDbContext db, CancellationToken ct)
    {
        var row = await db.SessionOwnerships
            .FirstOrDefaultAsync(x => x.ProjectId == projectId, ct);
        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            db.SessionOwnerships.Add(new SessionOwnership
            {
                ProjectId = projectId,
                Owner = "tmux",
                SessionUuid = sessionUuid,
                SinceUtc = now,
                ChangedByClient = client,
            });
        }
        else
        {
            row.Owner = "tmux";
            row.SessionUuid = sessionUuid;
            row.SinceUtc = now;
            row.ChangedByClient = client;
        }
        await db.SaveChangesAsync(ct);
        OwnerChanged?.Invoke(projectId, Owner.Tmux);
    }
}
