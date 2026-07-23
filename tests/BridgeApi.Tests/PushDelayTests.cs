using CortexBridge.Api.Endpoints;
using Microsoft.Extensions.Configuration;

namespace CortexBridge.Api.Tests;

/// <summary>
/// ADR-028 F — unit-tests the delay-parse+clamp helper used by NotificationHandler
/// for permission-prompt push debounce. Full suppression path (re-check NeedsInput
/// after delay) is verified by the re-check logic + logs in InternalHooksEndpoints.
/// </summary>
public class PushDelayTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs)
    {
        var dict = pairs
            .Where(p => p.Value is not null)
            .ToDictionary(p => p.Key, p => p.Value!);
        return new ConfigurationBuilder().AddInMemoryCollection(dict!).Build();
    }

    [Fact]
    public void Default_IsFourSeconds()
    {
        // Clear env so a host BRIDGE_PUSH_DELAY_SECONDS does not leak into the test.
        var prev = Environment.GetEnvironmentVariable("BRIDGE_PUSH_DELAY_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("BRIDGE_PUSH_DELAY_SECONDS", null);
            Assert.Equal(4, InternalHooksEndpoints.ResolvePushDelaySeconds(Config()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRIDGE_PUSH_DELAY_SECONDS", prev);
        }
    }

    [Fact]
    public void ConfigValue_Wins()
    {
        var prev = Environment.GetEnvironmentVariable("BRIDGE_PUSH_DELAY_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("BRIDGE_PUSH_DELAY_SECONDS", null);
            Assert.Equal(8, InternalHooksEndpoints.ResolvePushDelaySeconds(
                Config(("BRIDGE_PUSH_DELAY_SECONDS", "8"))));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRIDGE_PUSH_DELAY_SECONDS", prev);
        }
    }

    [Fact]
    public void EnvVar_HonoredWhenConfigMissing()
    {
        var prev = Environment.GetEnvironmentVariable("BRIDGE_PUSH_DELAY_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("BRIDGE_PUSH_DELAY_SECONDS", "12");
            Assert.Equal(12, InternalHooksEndpoints.ResolvePushDelaySeconds(Config()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRIDGE_PUSH_DELAY_SECONDS", prev);
        }
    }

    [Theory]
    [InlineData("-5", 0)]
    [InlineData("0", 0)]
    [InlineData("30", 30)]
    [InlineData("99", 30)]
    public void Clamp_ToZeroThroughThirty(string raw, int expected)
    {
        var prev = Environment.GetEnvironmentVariable("BRIDGE_PUSH_DELAY_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("BRIDGE_PUSH_DELAY_SECONDS", null);
            Assert.Equal(expected, InternalHooksEndpoints.ResolvePushDelaySeconds(
                Config(("BRIDGE_PUSH_DELAY_SECONDS", raw))));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRIDGE_PUSH_DELAY_SECONDS", prev);
        }
    }
}
