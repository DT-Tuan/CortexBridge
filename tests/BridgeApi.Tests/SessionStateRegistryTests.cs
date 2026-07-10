using CortexBridge.Api.Sessions;

namespace CortexBridge.Api.Tests;

/// <summary>
/// SessionStateRegistry — ADR-016 Slice 1: keyed by (projectId, sessionUuid).
/// FindStaleProcessing stays a pure READ (no mutate, no StatusChanged) so the
/// watchdog can veto a clear after reading the pane (the "composer unlocks
/// early" fix). The projectId-only calls below exercise the backward-compat
/// facade (null uuid ⇒ the project's live/newest entry).
/// </summary>
public class SessionStateRegistryTests
{
    [Fact]
    public void StaleProcessing_IsAcandidate()
    {
        var s = new SessionStateRegistry();
        s.SetProcessing("p1", true);
        var stale = s.FindStaleProcessing(TimeSpan.Zero);
        Assert.Contains(stale, t => t.ProjectId == "p1");
    }

    [Fact]
    public void RecentProcessing_IsNotStale()
    {
        var s = new SessionStateRegistry();
        s.SetProcessing("p1", true);
        var stale = s.FindStaleProcessing(TimeSpan.FromHours(1));
        Assert.DoesNotContain(stale, t => t.ProjectId == "p1");
    }

    [Fact]
    public void NotProcessing_IsNotStale()
    {
        var s = new SessionStateRegistry();
        s.SetProcessing("p1", true);
        s.SetProcessing("p1", false);
        Assert.DoesNotContain(s.FindStaleProcessing(TimeSpan.Zero), t => t.ProjectId == "p1");
    }

    [Fact]
    public void NeedsInput_IsNotStale()
    {
        var s = new SessionStateRegistry();
        s.SetProcessing("p1", true);
        s.SetNeedsInput("p1", true);
        Assert.DoesNotContain(s.FindStaleProcessing(TimeSpan.Zero), t => t.ProjectId == "p1");
    }

    [Fact]
    public void FindStaleProcessing_DoesNotMutateOrFireEvents()
    {
        var s = new SessionStateRegistry();
        var events = 0;
        s.SetProcessing("p1", true);
        s.StatusChanged += (_, _) => events++;

        var stale = s.FindStaleProcessing(TimeSpan.Zero);

        Assert.Contains(stale, t => t.ProjectId == "p1");
        Assert.True(s.Processing("p1"));   // still latched — only the watchdog clears it
        Assert.Equal(0, events);            // pure read — no SSE frame pushed
    }

    // --- ADR-016 Slice 1: per-(projectId,sessionUuid) isolation ---

    /// <summary>
    /// THE Slice-1 property: two sessions under ONE project no longer clobber
    /// each other. A background session's hook must not move the live one's
    /// Processing/NeedsInput.
    /// </summary>
    [Fact]
    public void PerSession_StateIsIsolated()
    {
        var s = new SessionStateRegistry();
        s.SetProcessing("proj", true, "live");
        s.SetProcessing("proj", false, "bg");
        s.SetNeedsInput("proj", true, "perm?", "bg");

        Assert.True(s.Processing("proj", "live"));
        Assert.False(s.Processing("proj", "bg"));
        Assert.False(s.NeedsInput("proj", "live"));
        Assert.True(s.NeedsInput("proj", "bg"));
        Assert.Equal("perm?", s.NotificationMessage("proj", "bg"));
        Assert.Null(s.NotificationMessage("proj", "live"));
    }

    [Fact]
    public void Facade_NullReadsTheSingleEntry()
    {
        var s = new SessionStateRegistry();
        s.SetProcessing("proj", true, "only");
        // No uuid given ⇒ facade resolves to the project's live entry.
        Assert.True(s.Processing("proj"));
        Assert.True(s.Processing("proj", "only"));
    }

    [Fact]
    public void Facade_NoEntry_FalseAndNull()
    {
        var s = new SessionStateRegistry();
        Assert.False(s.Processing("nope"));
        Assert.False(s.NeedsInput("nope", "x"));
        Assert.Null(s.LastEventAt("nope"));
    }

    [Fact]
    public void StatusChanged_CarriesProjectAndSessionUuid()
    {
        var s = new SessionStateRegistry();
        (string p, string? u)? got = null;
        s.StatusChanged += (p, u) => got = (p, u);

        s.SetNeedsInput("proj", true, "msg", "S1");

        Assert.NotNull(got);
        Assert.Equal("proj", got!.Value.p);
        Assert.Equal("S1", got!.Value.u);
    }

    [Fact]
    public void FindStaleProcessing_ReturnsProjectAndSessionUuid()
    {
        var s = new SessionStateRegistry();
        s.SetProcessing("proj", true, "S1");
        var stale = s.FindStaleProcessing(TimeSpan.Zero);
        Assert.Contains(stale, t => t.ProjectId == "proj" && t.SessionUuid == "S1");
    }
}
