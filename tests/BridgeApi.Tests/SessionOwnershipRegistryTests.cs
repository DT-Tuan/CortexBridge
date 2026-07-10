using CortexBridge.Api.Data.Entities;
using CortexBridge.Api.Sessions;

namespace CortexBridge.Api.Tests;

/// <summary>
/// ADR-017 ownership staleness discipline. The owner-INDEPENDENT two-process
/// guard in ModeWatcher must treat a "claude-vscode" JSONL tail as a LIVE PC
/// writer only when that record post-dates the ownership marker (sinceUtc).
/// A claude-vscode record at-or-before the marker is a PRE-TAKEOVER FOSSIL —
/// counting it flapped a forced take-over straight back to PC (live user bug
/// 2026-05-19: PC powered off *before* takeover, 5 reverts). PcIsLiveWriter
/// centralises that rule; Derive() must agree on the boundary.
/// </summary>
public class SessionOwnershipRegistryTests
{
    private static readonly DateTimeOffset Marker =
        new(2026, 5, 19, 4, 0, 0, TimeSpan.Zero); // the take-over instant

    [Fact]
    public void Fossil_StaleVscodeBeforeMarker_NotLiveWriter()
    {
        // The bug: last line is the PC's final pre-handback record; the
        // resumed tmux claude is idle and hasn't written "cli" yet.
        var lastTs = Marker.AddSeconds(-90);
        Assert.False(SessionOwnershipRegistry.PcIsLiveWriter("claude-vscode", lastTs, Marker));
    }

    [Fact]
    public void GenuineHazard_FreshVscodeAfterMarker_IsLiveWriter()
    {
        // Real two-process case (force-takeover, THEN start CC on PC): the PC
        // writes claude-vscode strictly after the marker — guard must trip.
        var lastTs = Marker.AddSeconds(30);
        Assert.True(SessionOwnershipRegistry.PcIsLiveWriter("claude-vscode", lastTs, Marker));
    }

    [Fact]
    public void Boundary_EqualTimestamp_TreatedAsFossil()
    {
        // Strict ">" — equal ts counts as fossil, matching Derive()'s
        // `row.SinceUtc >= lastTs` (tmux marker stays valid at equality).
        Assert.False(SessionOwnershipRegistry.PcIsLiveWriter("claude-vscode", Marker, Marker));
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("claude")]
    [InlineData("")]
    [InlineData(null)]
    public void NonVscodeEntrypoint_NeverLiveWriter_EvenIfFresh(string? entrypoint)
        => Assert.False(
            SessionOwnershipRegistry.PcIsLiveWriter(entrypoint, Marker.AddSeconds(60), Marker));

    [Fact]
    public void NullTimestamp_CannotProvePostTakeover_NotLiveWriter()
        => Assert.False(SessionOwnershipRegistry.PcIsLiveWriter("claude-vscode", null, Marker));

    [Fact]
    public void VscodeCaseInsensitive_FreshStillLive()
        => Assert.True(
            SessionOwnershipRegistry.PcIsLiveWriter("Claude-VSCode", Marker.AddSeconds(5), Marker));

    // --- Derive() must agree with PcIsLiveWriter on the fossil boundary ---

    [Fact]
    public void Derive_TmuxMarker_PreTakeoverVscodeFossil_StaysTmux()
    {
        // Same scenario as the flap: tmux take-over marker at `Marker`, last
        // JSONL record is a stale claude-vscode 90s earlier. Derive already
        // keeps Tmux here (row.SinceUtc >= lastTs); the guard now agrees
        // (PcIsLiveWriter == false) so it no longer reverts behind Derive.
        var row = new SessionOwnership
        {
            ProjectId = "CortexBridge",
            Owner = "tmux",
            SessionUuid = "8ca034cb-aaaa-bbbb-cccc-ddddeeeeffff",
            SinceUtc = Marker,
            ChangedByClient = "pwa",
        };
        var lastTs = Marker.AddSeconds(-90);

        Assert.Equal(
            SessionOwnershipRegistry.Owner.Tmux,
            SessionOwnershipRegistry.Derive(row, "claude-vscode", lastTs, tmuxAlive: true));
        Assert.False(SessionOwnershipRegistry.PcIsLiveWriter("claude-vscode", lastTs, Marker));
    }

    [Fact]
    public void Derive_TmuxMarker_FreshVscodeAfterMarker_FlipsToPc()
    {
        // Genuine hand-back: PC writes claude-vscode after the tmux marker →
        // Derive flips to Pc AND PcIsLiveWriter is true (guard trips). Both
        // agree the PC is the live writer.
        var row = new SessionOwnership
        {
            ProjectId = "CortexBridge",
            Owner = "tmux",
            SessionUuid = "8ca034cb-aaaa-bbbb-cccc-ddddeeeeffff",
            SinceUtc = Marker,
            ChangedByClient = "pwa",
        };
        var lastTs = Marker.AddSeconds(30);

        Assert.Equal(
            SessionOwnershipRegistry.Owner.Pc,
            SessionOwnershipRegistry.Derive(row, "claude-vscode", lastTs, tmuxAlive: true));
        Assert.True(SessionOwnershipRegistry.PcIsLiveWriter("claude-vscode", lastTs, Marker));
    }

    // --- PcRecentlyActive: NOW-relative "is PC still here?" (decays) ---

    private static readonly DateTimeOffset Now =
        new(2026, 5, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(45);

    [Fact]
    public void RecentlyActive_VscodeWroteWithinGrace_True()
        => Assert.True(SessionOwnershipRegistry.PcRecentlyActive(
            "claude-vscode", Now.AddSeconds(-20), Now, Grace));

    [Fact]
    public void RecentlyActive_StaleVscodeOlderThanGrace_False()
        => Assert.False(SessionOwnershipRegistry.PcRecentlyActive(
            "claude-vscode", Now.AddSeconds(-300), Now, Grace));

    [Theory]
    [InlineData("cli")]
    [InlineData(null)]
    public void RecentlyActive_NonVscode_FalseEvenIfRecent(string? ep)
        => Assert.False(SessionOwnershipRegistry.PcRecentlyActive(ep, Now.AddSeconds(-1), Now, Grace));

    [Fact]
    public void RecentlyActive_NullTs_False()
        => Assert.False(SessionOwnershipRegistry.PcRecentlyActive("claude-vscode", null, Now, Grace));

    [Fact]
    public void RecentlyActive_BoundaryAtGrace_False()
        => Assert.False(SessionOwnershipRegistry.PcRecentlyActive(
            "claude-vscode", Now - Grace, Now, Grace)); // strict "< grace"

    /// <summary>
    /// THE auto-B→A bug, deterministically: a real Mode-B session left a
    /// claude-vscode tail NEWER than the OLD pc-ownership marker; PC then
    /// closed and nothing else wrote, so "now" is long after that tail.
    /// `PcIsLiveWriter` (marker-relative) stays sticky-TRUE forever — that is
    /// what latched pcPresent/freshPcRecord and made provable-safe permanently
    /// false, so automatic B→A could never fire. `PcRecentlyActive`
    /// (now-relative) correctly DECAYS to false, letting B→A proceed. The two
    /// predicates MUST disagree here — that disagreement IS the fix.
    /// </summary>
    [Fact]
    public void AfterRealModeB_PcClosed_LiveWriterStaysSticky_ButRecentlyActiveDecays()
    {
        var oldPcMarker = Now.AddMinutes(-40);   // owner=pc set long ago
        var lastVscodeWrite = Now.AddMinutes(-3); // PC's final write, > marker, but stale vs now

        // marker-relative: still TRUE (this was the latch behind the bug)
        Assert.True(SessionOwnershipRegistry.PcIsLiveWriter(
            "claude-vscode", lastVscodeWrite, oldPcMarker));

        // now-relative: FALSE — PC is no longer present ⇒ B→A may proceed
        Assert.False(SessionOwnershipRegistry.PcRecentlyActive(
            "claude-vscode", lastVscodeWrite, Now, Grace));
    }

    // --- ADR-016 Slice 1 step 0: PickLiveSession (the shadowing fix) ---

    private static SessionOwnershipRegistry.SessionCandidate Cand(
        string uuid, DateTimeOffset? ts = null) => new(uuid, ts);

    /// <summary>
    /// THE shadowing bug, deterministically: the bridge tracks slot occupant A
    /// (session_ownership.SessionUuid), but a parked/test sibling B has the
    /// newest activity. Newest-file-mtime resolution returned B (wrong);
    /// PickLiveSession returns the tracked A — even when B's last record is
    /// also newer. The picker has NO mtime input by construction.
    /// </summary>
    [Fact]
    public void PickLiveSession_TrackedSlotWins_OverNewerSibling()
    {
        var cands = new[]
        {
            Cand("A", Now.AddMinutes(-10)), // tracked slot, older last record
            Cand("B", Now.AddMinutes(-1)),  // sibling, newer — would win on mtime/ts
        };
        Assert.Equal("A", SessionOwnershipRegistry.PickLiveSession(cands, ownershipUuid: "A"));
    }

    [Fact]
    public void PickLiveSession_TrackedUuid_CaseInsensitive()
        => Assert.Equal("8CA0", SessionOwnershipRegistry.PickLiveSession(
            new[] { Cand("8CA0", Now.AddMinutes(-5)), Cand("zzzz", Now) }, ownershipUuid: "8ca0"));

    [Fact]
    public void PickLiveSession_TrackedUuidGone_FallsBackToNewestRecordTs()
    {
        var cands = new[] { Cand("A", Now.AddMinutes(-9)), Cand("B", Now.AddMinutes(-2)) };
        // ownership points at C (file deleted) ⇒ newest last-record ts wins (B)
        Assert.Equal("B", SessionOwnershipRegistry.PickLiveSession(cands, ownershipUuid: "C"));
    }

    [Fact]
    public void PickLiveSession_Untracked_NewestRecordTs_NotMtime()
    {
        // No ownership row. Picker only sees last-RECORD ts (no mtime input):
        // A's conversation is newer than B's even if B's file was touched later.
        var cands = new[] { Cand("A", Now.AddMinutes(-1)), Cand("B", Now.AddMinutes(-30)) };
        Assert.Equal("A", SessionOwnershipRegistry.PickLiveSession(cands, ownershipUuid: null));
    }

    [Fact]
    public void PickLiveSession_NoRecordTsAnywhere_ReturnsNull_ForCallerMtimeFallback()
        => Assert.Null(SessionOwnershipRegistry.PickLiveSession(
            new[] { Cand("A"), Cand("B") }, ownershipUuid: null));

    [Fact]
    public void PickLiveSession_Empty_Null()
        => Assert.Null(SessionOwnershipRegistry.PickLiveSession(
            System.Array.Empty<SessionOwnershipRegistry.SessionCandidate>(), "A"));

    [Fact]
    public void PickLiveSession_Single_ReturnsIt()
        => Assert.Equal("only", SessionOwnershipRegistry.PickLiveSession(
            new[] { Cand("only", Now) }, ownershipUuid: null));
}
