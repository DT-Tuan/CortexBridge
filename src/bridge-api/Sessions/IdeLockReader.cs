using System.Text.Json;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// Reads Anthropic's VS Code extension IDE lockfiles to learn which projects
/// currently have a PC-side VS Code + Claude-Code window open (ADR-017).
///
/// While a VS Code window with the Anthropic extension is open on a workspace,
/// the extension keeps <c>~/.claude/ide/&lt;port&gt;.lock</c>:
/// <code>{"pid":…,"workspaceFolders":["/home/youruser/workspace/CortexBridge"],…}</code>
/// <c>~/.claude</c> is bind-mounted into the bridge container (ADR-011), so this
/// is a host-visible file signal needing NO host PID namespace (ADR-013). It
/// means "VS Code is open here", i.e. Mode B intent — a deliberately
/// conservative proxy (safety over Mode-A availability, see ADR-017). The lock
/// vanishing = the user closed VS Code / dropped Remote-SSH = the canonical
/// "left the PC" trigger for an automatic, provably-safe B→A switch.
///
/// projectId = basename of a workspaceFolders entry (matches how SessionScanner
/// derives projectId from a record's cwd).
/// </summary>
public sealed class IdeLockReader
{
    private readonly string _ideDir;
    private readonly ILogger _log;

    public IdeLockReader(BridgePaths paths, ILoggerFactory loggerFactory)
    {
        _ideDir = Path.Combine(paths.ClaudeUserDir, "ide");
        _log = loggerFactory.CreateLogger("IdeLockReader");
    }

    /// <summary>
    /// projectIds with a PC-side VS Code window open right now. Empty set when
    /// the ide dir is absent/unreadable (⇒ "no PC attached" — the watcher then
    /// treats B→A as a candidate, still gated by its other safety checks).
    /// Never throws.
    /// </summary>
    public IReadOnlySet<string> PcAttachedProjects()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_ideDir)) return set;
        try
        {
            foreach (var f in Directory.EnumerateFiles(_ideDir, "*.lock"))
            {
                string json;
                try { json = File.ReadAllText(f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue; // locked / mid-write — skip this tick
                }
                foreach (var p in ParseProjects(json)) set.Add(p);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "ide lock dir enumerate failed; treating as no PC attached");
        }
        return set;
    }

    /// <summary>
    /// Pure: lock JSON → projectIds (basename of each workspaceFolders entry).
    /// Tolerates partial/garbled content (returns nothing) — never throws.
    /// </summary>
    public static IEnumerable<string> ParseProjects(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { yield break; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("workspaceFolders", out var wf)
                || wf.ValueKind != JsonValueKind.Array)
                yield break;

            foreach (var el in wf.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) continue;
                var path = el.GetString();
                if (string.IsNullOrEmpty(path)) continue;
                // Basename, tolerant of a trailing slash and either separator.
                var name = Path.GetFileName(path.TrimEnd('/', '\\'));
                if (!string.IsNullOrEmpty(name)) yield return name;
            }
        }
    }
}
