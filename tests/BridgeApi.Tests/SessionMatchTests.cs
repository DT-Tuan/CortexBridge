using CortexBridge.Api.Sessions;

namespace CortexBridge.Api.Tests;

/// <summary>
/// ADR-016 Slice 2 Step 0 — <see cref="SessionMatch.Check"/> is the keystone:
/// every write endpoint consults it before the reply mutex, so a mismatch
/// never contends with a real reply.
/// </summary>
public class SessionMatchTests
{
    [Theory]
    [InlineData(null, "live")]
    [InlineData("", "live")]
    [InlineData(null, null)]
    public void NoRequested_IsOk_BackwardCompat(string? requested, string? active)
    {
        Assert.Equal(SessionMatch.Result.Ok, SessionMatch.Check(requested, active));
    }

    [Fact]
    public void Matching_IsOk_CaseInsensitive()
    {
        Assert.Equal(SessionMatch.Result.Ok, SessionMatch.Check("ABCD", "abcd"));
        Assert.Equal(SessionMatch.Result.Ok, SessionMatch.Check("abcd-1234", "abcd-1234"));
    }

    [Fact]
    public void Mismatch_IsMismatch()
    {
        Assert.Equal(SessionMatch.Result.Mismatch, SessionMatch.Check("bg", "live"));
    }

    [Fact]
    public void RequestedButNoActive_IsMismatch()
    {
        Assert.Equal(SessionMatch.Result.Mismatch, SessionMatch.Check("bg", null));
        Assert.Equal(SessionMatch.Result.Mismatch, SessionMatch.Check("bg", ""));
    }

    [Theory]
    [InlineData("11111111-aaaa-bbbb-cccc-222222222222", true)]   // Slice-1 acceptance UID
    [InlineData("session-BBBB", true)]
    [InlineData("a", true)]
    [InlineData("../etc/passwd", false)]                          // path traversal
    [InlineData("with space", false)]
    [InlineData("", false)]
    public void IsValidUuidShape_RejectsInjections(string candidate, bool expected)
    {
        Assert.Equal(expected, SessionMatch.IsValidUuidShape(candidate));
    }
}
