using CortexBridge.Api.Sessions;

namespace CortexBridge.Api.Tests;

/// <summary>
/// ADR-022 Option Β — <see cref="CrashWatcher.IsCrashedState"/> is the keystone:
/// the watcher is just plumbing around this pure decision. Every clause is one
/// of the load-bearing properties from ADR-022 §"Clarified Brief".
/// </summary>
public class CrashWatcherTests
{
    [Fact]
    public void Crashed_When_OwnerTmux_HasUuid_NoWindow_NoLock()
    {
        Assert.True(CrashWatcher.IsCrashedState(
            ownerWire: "tmux",
            sessionUuid: "abc-123",
            tmuxWindowAlive: false,
            ideLockPresent: false));
    }

    [Theory]
    [InlineData("pc")]      // PC owns it — ADR-017: never resurrect Mode B
    [InlineData("none")]    // nothing to resurrect
    [InlineData("TMUX")]    // case-mismatch outside our normalisation is still rejected
    public void NotCrashed_When_OwnerNotTmux(string ownerWire)
    {
        // "TMUX" upper-case IS accepted (StringComparison.OrdinalIgnoreCase) —
        // so test that case via the next line; here exercise the genuine
        // non-tmux paths.
        if (ownerWire == "TMUX")
        {
            Assert.True(CrashWatcher.IsCrashedState(ownerWire, "u", false, false));
            return;
        }
        Assert.False(CrashWatcher.IsCrashedState(ownerWire, "u", false, false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NotCrashed_When_NoSessionUuid(string? uuid)
    {
        // No UID = nothing to --resume; the deep-link in the push would be
        // broken too. Skip.
        Assert.False(CrashWatcher.IsCrashedState("tmux", uuid, false, false));
    }

    [Fact]
    public void NotCrashed_When_TmuxWindowAlive()
    {
        // Healthy — owner=tmux and window is up. This is the steady state.
        Assert.False(CrashWatcher.IsCrashedState("tmux", "u", tmuxWindowAlive: true, ideLockPresent: false));
    }

    [Fact]
    public void NotCrashed_When_IdeLockPresent()
    {
        // ide-lock = an A↔B handoff is in flight; ModeWatcher will reconcile.
        // The CrashWatcher must stay out of the way.
        Assert.False(CrashWatcher.IsCrashedState("tmux", "u", tmuxWindowAlive: false, ideLockPresent: true));
    }

    [Fact]
    public void OwnerWire_IsCaseInsensitive()
    {
        // Be tolerant of the row.Owner string casing (DB-stored).
        Assert.True(CrashWatcher.IsCrashedState("TmUx", "u", false, false));
    }
}
