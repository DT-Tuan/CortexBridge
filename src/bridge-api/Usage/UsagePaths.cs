namespace CortexBridge.Api.Usage;

/// <summary>
/// Resolves the location of the host-sampled usage JSON. The bridge container
/// does NOT run ccusage itself (no Node, per the slim-image rule in
/// <c>docker/Dockerfile</c>); a host-side systemd-user timer writes
/// <c>/var/cortex-bridge/usage.json</c> every ~60s and we read it.
///
/// Quota caps are gone (ADR-024): the sampler now carries Anthropic's own
/// 5h/7d utilization in the <c>official</c> block, so there is nothing to
/// configure or tune here anymore.
/// </summary>
public class UsagePaths
{
    public string UsageJsonPath { get; }

    public UsagePaths(IConfiguration config)
    {
        UsageJsonPath = config["USAGE_JSON_PATH"]
            ?? Environment.GetEnvironmentVariable("USAGE_JSON_PATH")
            ?? "/var/cortex-bridge/usage.json";
    }
}
