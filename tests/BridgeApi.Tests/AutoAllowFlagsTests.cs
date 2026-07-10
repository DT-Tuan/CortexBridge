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
        bool? push = null, bool? install = null) => new(on, autonomy, push, install);

    [Fact]
    public void DefaultState_AllOff()
    {
        var s = AutoAllowFlags.Read(_flagDir, Proj);
        Assert.False(s.Enabled);
        Assert.False(s.Autonomy);
        Assert.False(s.Push);
        Assert.False(s.Install);
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
