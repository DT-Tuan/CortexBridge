using CortexBridge.Api.Auth;

namespace CortexBridge.Api.Tests;

public class TokenRateLimiterTests
{
    [Fact]
    public void TryAcquire_AllowsUpTo30PerMinute()
    {
        var limiter = new TokenRateLimiter();
        for (var i = 0; i < 30; i++)
            Assert.True(limiter.TryAcquire(tokenId: 1));

        Assert.False(limiter.TryAcquire(tokenId: 1)); // 31st rejected
    }

    [Fact]
    public void TryAcquire_PerTokenIsolation()
    {
        var limiter = new TokenRateLimiter();
        for (var i = 0; i < 30; i++) Assert.True(limiter.TryAcquire(1));
        Assert.False(limiter.TryAcquire(1));

        // Token 2 has its own bucket
        Assert.True(limiter.TryAcquire(2));
    }

    [Fact]
    public void Remaining_ReportsAvailability()
    {
        var limiter = new TokenRateLimiter();
        Assert.Equal(30, limiter.Remaining(42));
        for (var i = 0; i < 5; i++) limiter.TryAcquire(42);
        Assert.Equal(25, limiter.Remaining(42));
    }
}
