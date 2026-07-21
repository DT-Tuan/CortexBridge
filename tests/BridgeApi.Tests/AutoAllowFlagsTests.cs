using CortexBridge.Api.Endpoints;

namespace CortexBridge.Api.Tests;

/// <summary>
/// Guards the per-project auto-allow flag files (the toggle the host PreToolUse
/// hook gates on). Covers partial updates, path-traversal rejection, and the
/// independence of the four tiers.
/// </summary>
public class AutoAllowFlagsTests : IDisposable
{
    private readonly string _claudeDir;
    private readonly string _flagDir;
    private const string Proj = "proj";

    public AutoAllowFlagsTests()
    {
        _claudeDir = Path.Combine(Path.GetTempPath(), "cb-autoallow-" + Guid.NewGuid().ToString("N"));
        Assert.True(AutoAllowFlags.TryFlagDir(_claudeDir, Proj, out _flagDir));
    }

    public void Dispose()
    {
        if (Directory.Exists(_claudeDir)) Directory.Delete(_claudeDir, recursive: true);
    }

    private AutoAllowFlags.SetRequest Req(bool? on = null, bool? autonomy = null,
        bool? push = null, bool? install = null, bool? roOff = null) => new(on, autonomy, push, install, roOff);

    [Fact]
    public void DefaultState_AllOff()
    {
        var s = AutoAllowFlags.Read(_flagDir, Proj);
        Assert.False(s.Enabled);
        Assert.False(s.Autonomy);
        Assert.False(s.Push);
        Assert.False(s.Install);
        Assert.False(s.RoOff);
    }

    [Fact]
    public void RoOff_OptOutFlag_IndependentAndReversible()
    {
        var (s, changed) = AutoAllowFlags.Apply(_flagDir, Proj, Req(roOff: true));
        Assert.True(s.RoOff);
        Assert.False(s.Enabled);
        Assert.True(File.Exists(Path.Combine(_flagDir, Proj + ".ro-off")));
        Assert.Contains("ro-off=on", changed);

        var (s2, changed2) = AutoAllowFlags.Apply(_flagDir, Proj, Req(roOff: false));
        Assert.False(s2.RoOff);
        Assert.False(File.Exists(Path.Combine(_flagDir, Proj + ".ro-off")));
        Assert.Contains("ro-off=off", changed2);
    }

    [Fact]
    public void Burst_SetReadCancel()
    {
        var (until, opaque) = AutoAllowFlags.SetBurst(_flagDir, Proj, minutes: 30, opaque: true);
        Assert.True(until > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.True(opaque);
        var s = AutoAllowFlags.Read(_flagDir, Proj);
        Assert.Equal(until, s.BurstUntil);
        Assert.True(s.BurstOpaque);

        AutoAllowFlags.SetBurst(_flagDir, Proj, minutes: 0, opaque: false);   // cancel
        var s2 = AutoAllowFlags.Read(_flagDir, Proj);
        Assert.Equal(0, s2.BurstUntil);
        Assert.False(File.Exists(Path.Combine(_flagDir, Proj + ".burst")));
    }

    [Fact]
    public void Burst_ExpiredReadsAsZeroAndDeletes()
    {
        var path = Path.Combine(_flagDir, Proj + ".burst");
        Directory.CreateDirectory(_flagDir);
        File.WriteAllText(path, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10) + "\n");
        Assert.Equal(0, AutoAllowFlags.Read(_flagDir, Proj).BurstUntil);
        Assert.False(File.Exists(path));   // expired -> cleaned on read
    }

    [Fact]
    public void Burst_ClampedToCeiling()
    {
        var (until, _) = AutoAllowFlags.SetBurst(_flagDir, Proj, minutes: 100_000, opaque: false);
        var maxExp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 8 * 60 * 60 + 5;
        Assert.True(until <= maxExp, $"burst {until} exceeded 8h ceiling {maxExp}");
    }

    [Fact]
    public void EnableSafe_OnlyTouchesOnFlag()
    {
        var (s, changed) = AutoAllowFlags.Apply(_flagDir, Proj, Req(on: true));
        Assert.True(s.Enabled);
        Assert.False(s.Autonomy);
        Assert.True(File.Exists(Path.Combine(_flagDir, Proj + ".on")));
        Assert.False(File.Exists(Path.Combine(_flagDir, Proj + ".autonomy")));
        Assert.Contains("on=on", changed);
    }

    [Fact]
    public void PartialUpdate_LeavesOtherFlagsUntouched()
    {
        AutoAllowFlags.Apply(_flagDir, Proj, Req(on: true, autonomy: true));
        // Now flip only push on; on + autonomy must remain.
        var (s, changed) = AutoAllowFlags.Apply(_flagDir, Proj, Req(push: true));
        Assert.True(s.Enabled);
        Assert.True(s.Autonomy);
        Assert.True(s.Push);
        Assert.False(s.Install);
        Assert.Single(changed);
        Assert.Contains("push=on", changed);
    }

    [Fact]
    public void Disable_RemovesFlagFile()
    {
        AutoAllowFlags.Apply(_flagDir, Proj, Req(on: true, autonomy: true, push: true));
        var (s, changed) = AutoAllowFlags.Apply(_flagDir, Proj, Req(autonomy: false));
        Assert.True(s.Enabled);
        Assert.False(s.Autonomy);
        Assert.True(s.Push); // push flag file left as-is (hook ignores it without autonomy)
        Assert.False(File.Exists(Path.Combine(_flagDir, Proj + ".autonomy")));
        Assert.Contains("autonomy=off", changed);
    }

    [Fact]
    public void NoOp_WhenValueUnchanged()
    {
        AutoAllowFlags.Apply(_flagDir, Proj, Req(on: true));
        var (_, changed) = AutoAllowFlags.Apply(_flagDir, Proj, Req(on: true));
        Assert.Empty(changed);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("")]
    public void TryFlagDir_RejectsBadProjectId(string bad)
    {
        Assert.False(AutoAllowFlags.TryFlagDir(_claudeDir, bad, out _));
    }

    [Fact]
    public void TryFlagDir_AcceptsPlainName()
    {
        Assert.True(AutoAllowFlags.TryFlagDir(_claudeDir, "MyProj-1", out var dir));
        Assert.EndsWith(AutoAllowFlags.FlagSubdir, dir);
    }
}
