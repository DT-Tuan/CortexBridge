namespace CortexBridge.Api.Sessions;

/// <summary>
/// Resolves filesystem paths the bridge cares about. Configurable via env or appsettings;
/// defaults match the production container layout.
/// </summary>
public class BridgePaths
{
    public string CcProjectsRoot { get; }
    public string WorkspaceRoot { get; }
    public string DataDir { get; }
    /// <summary>~/.claude (the user-level CC config dir). Holds skills/, commands/, etc.</summary>
    public string ClaudeUserDir { get; }

    public BridgePaths(IConfiguration config)
    {
        CcProjectsRoot = config["BRIDGE_CC_PROJECTS_ROOT"]
            ?? Environment.GetEnvironmentVariable("BRIDGE_CC_PROJECTS_ROOT")
            ?? DefaultCcProjectsRoot();

        WorkspaceRoot = config["BRIDGE_WORKSPACE_ROOT"]
            ?? Environment.GetEnvironmentVariable("BRIDGE_WORKSPACE_ROOT")
            ?? "/workspace";

        DataDir = config["BRIDGE_DATA_DIR"]
            ?? Environment.GetEnvironmentVariable("BRIDGE_DATA_DIR")
            ?? "/data";

        ClaudeUserDir = config["BRIDGE_CLAUDE_USER_DIR"]
            ?? Environment.GetEnvironmentVariable("BRIDGE_CLAUDE_USER_DIR")
            ?? Path.GetDirectoryName(CcProjectsRoot)
            ?? Path.Combine(HomeDir(), ".claude");
    }

    private static string HomeDir() =>
        Environment.GetEnvironmentVariable("HOME")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string DefaultCcProjectsRoot() =>
        Path.Combine(HomeDir(), ".claude", "projects");
}
