using Microsoft.Extensions.Configuration;

namespace CortexBridge.Api.Cortex;

/// <summary>
/// Maps a workspace project dir (what the PWA/tmux know) to the name CortexPlexus
/// indexed it under. Most repos match (dir == name), so the default is identity.
/// The divergent pairs are deployment-specific — a folder someone renamed years
/// ago — so they come from config, not from source:
/// <code>
/// "CortexPlexus": { "RepoNameMap": { "&lt;workspace-dir&gt;": "&lt;indexed-name&gt;" } }
/// </code>
/// Same map as the ADR-023 watch runbook.
/// </summary>
public static class CortexRepoMap
{
    private static IReadOnlyDictionary<string, string> _divergent =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Load the map from configuration. Call once at startup, before first Resolve.</summary>
    public static void Configure(IConfiguration configuration) =>
        _divergent = configuration.GetSection("CortexPlexus:RepoNameMap")
            .GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .ToDictionary(c => c.Key, c => c.Value!, StringComparer.Ordinal);

    public static string Resolve(string projectId) =>
        _divergent.TryGetValue(projectId, out var name) ? name : projectId;
}
