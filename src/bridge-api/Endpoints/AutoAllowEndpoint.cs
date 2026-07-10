using System.Text.Json;
using CortexBridge.Api.Auth;
using CortexBridge.Api.Common;
using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;
using CortexBridge.Api.Sessions;

namespace CortexBridge.Api.Endpoints;

/// <summary>
/// Per-project opt-in for the auto-allow tiers (project_autoallow_design.md,
/// Architecture C). The actual allow decision is made by the HOST PreToolUse
/// hook (cc-autoallow-hook.sh) which returns {permissionDecision:"allow"}; this
/// endpoint only owns the per-project FLAG FILES the hook gates on, under
///   {ClaudeUserDir}/cortex-autoallow/{projectId}.{on|autonomy|push|install}
///
/// Two independent tiers + two sub-flags (see <see cref="AutoAllowFlags"/>):
///   on        — SAFE: read-only tools + read-only Bash (incl. read-only chains)
///   autonomy  — TRUST: also build/test/lint/format + local git (add/commit/stash)
///   push      — sub of autonomy: also `git push` (never --force)
///   install   — sub of autonomy: also package installs
///
/// The claude-config docker volume binds to host ~/.claude, so a flag the bridge
/// writes here is read live by the host hook on the next tool call.
/// </summary>
public static class AutoAllowEndpoint
{
    public static void MapAutoAllow(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sessions/{projectId}/autoallow", GetHandler);
        app.MapPost("/api/sessions/{projectId}/autoallow", SetHandler);
        app.MapPost("/api/sessions/{projectId}/autoallow/learn", LearnHandler);
    }

    private static IResult GetHandler(string projectId, BridgePaths paths)
    {
        if (!AutoAllowFlags.TryFlagDir(paths.ClaudeUserDir, projectId, out var dir))
            return ResultsHelpers.Error(400, "autoallow.bad_project_id", "Invalid projectId");
        return Results.Json(AutoAllowFlags.Read(dir, projectId), Json.Default);
    }

    private static async Task<IResult> LearnHandler(
        string projectId,
        LearnedAutoAllow.LearnRequest body,
        HttpContext ctx,
        BridgePaths paths,
        BridgeDbContext db,
        CancellationToken ct)
    {
        if (!AutoAllowFlags.TryFlagDir(paths.ClaudeUserDir, projectId, out var dir))
            return ResultsHelpers.Error(400, "autoallow.bad_project_id", "Invalid projectId");
        if (string.IsNullOrWhiteSpace(body.Tool))
            return ResultsHelpers.Error(400, "autoallow.bad_tool", "Tool is required");

        LearnedAutoAllow.Append(dir, projectId, body.Tool.Trim(), body.Command?.Trim() ?? string.Empty);

        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTimeOffset.UtcNow,
            ProjectId = projectId,
            Action = "session.autoallow.learn",
            TokenId = ctx.GetAuthToken()?.Id,
            Result = "ok",
            Detail = $"tool={body.Tool.Trim()}",  // never log command content
        });
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> SetHandler(
        string projectId,
        AutoAllowFlags.SetRequest body,
        HttpContext ctx,
        BridgePaths paths,
        BridgeDbContext db,
        CancellationToken ct)
    {
        if (!AutoAllowFlags.TryFlagDir(paths.ClaudeUserDir, projectId, out var dir))
            return ResultsHelpers.Error(400, "autoallow.bad_project_id", "Invalid projectId");

        var (state, changed) = AutoAllowFlags.Apply(dir, projectId, body);

        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTimeOffset.UtcNow,
            ProjectId = projectId,
            Action = "session.autoallow",
            TokenId = ctx.GetAuthToken()?.Id,
            Result = "ok",
            // Only tier names + on/off, never command content.
            Detail = changed.Count > 0 ? string.Join(", ", changed) : "no-op",
        });
        await db.SaveChangesAsync(ct);

        return Results.Json(state, Json.Default);
    }
}

/// <summary>
/// Pure flag-file read/write for the auto-allow tiers — no DI, no HTTP, so the
/// path safety + toggle logic is directly unit-testable. A flag is ON iff its
/// file exists; content is irrelevant.
/// </summary>
public static class AutoAllowFlags
{
    public const string FlagSubdir = "cortex-autoallow";

    // Wire names (camelCase JSON) -> filename suffix. `Enabled` keeps its legacy
    // name/.on suffix so the pre-existing PWA toggle (sends {enabled}) still works.
    private static readonly (string Suffix, Func<SetRequest, bool?> Get)[] Map =
    {
        (".on",       r => r.Enabled),
        (".autonomy", r => r.Autonomy),
        (".push",     r => r.Push),
        (".install",  r => r.Install),
    };

    public record State(bool Enabled, bool Autonomy, bool Push, bool Install);
    public record SetRequest(bool? Enabled, bool? Autonomy, bool? Push, bool? Install);

    /// <summary>
    /// Resolve + validate the flag directory for a project, rejecting any
    /// projectId that could escape cortex-autoallow (separators, traversal, bad
    /// filename chars). Defense in depth: re-checks the resolved dir is contained.
    /// </summary>
    public static bool TryFlagDir(string claudeUserDir, string projectId, out string dir)
    {
        dir = string.Empty;
        if (string.IsNullOrWhiteSpace(projectId)
            || projectId.Contains('/') || projectId.Contains('\\')
            || projectId.Contains("..")
            || projectId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        var resolved = Path.GetFullPath(Path.Combine(claudeUserDir, FlagSubdir));
        // The flag file is <resolved>/<projectId><suffix> — confirm projectId alone
        // can't push the path outside the dir.
        var probe = Path.GetFullPath(Path.Combine(resolved, projectId + ".on"));
        if (!probe.StartsWith(resolved + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;

        dir = resolved;
        return true;
    }

    public static State Read(string flagDir, string projectId) => new(
        Enabled: File.Exists(Path.Combine(flagDir, projectId + ".on")),
        Autonomy: File.Exists(Path.Combine(flagDir, projectId + ".autonomy")),
        Push: File.Exists(Path.Combine(flagDir, projectId + ".push")),
        Install: File.Exists(Path.Combine(flagDir, projectId + ".install")));

    /// <summary>
    /// Apply a partial request (only non-null fields change). Returns the new
    /// state and the list of human-readable changes for the audit log.
    /// </summary>
    public static (State State, List<string> Changed) Apply(
        string flagDir, string projectId, SetRequest req)
    {
        var changed = new List<string>();
        foreach (var (suffix, get) in Map)
        {
            var want = get(req);
            if (want is null) continue;
            var path = Path.Combine(flagDir, projectId + suffix);
            var exists = File.Exists(path);
            if (want.Value && !exists)
            {
                Directory.CreateDirectory(flagDir);
                File.WriteAllText(path, "on\n");
                changed.Add($"{suffix[1..]}=on");
            }
            else if (!want.Value && exists)
            {
                File.Delete(path);
                changed.Add($"{suffix[1..]}=off");
            }
        }
        return (Read(flagDir, projectId), changed);
    }
}

/// <summary>
/// Per-project learned-command store: commands/tools approved by the user from
/// the PWA are persisted to {flagDir}/{projectId}.learned.json so the hook can
/// auto-allow them on future calls without a prompt.
///
/// Schema: { "bashCommands": ["exact cmd", ...], "tools": ["Read", ...] }
///   bashCommands — exact Bash command strings (exact match in the hook)
///   tools        — non-Bash tool names whose ANY call is auto-allowed
///
/// The hook reads this file after the tier-flag check; bridge owns writes.
/// </summary>
public static class LearnedAutoAllow
{
    private const string FileSuffix = ".learned.json";
    private static readonly Lock _lock = new();

    public record LearnRequest(string? Tool, string? Command);

    public record LearnedData
    {
        public List<string> BashCommands { get; set; } = [];
        public List<string> Tools { get; set; } = [];
    }

    /// <summary>
    /// Append a learned entry idempotently (no-op if already present).
    /// Thread-safe via a process-level lock (single-process bridge).
    /// </summary>
    public static void Append(string flagDir, string projectId, string tool, string command)
    {
        var path = FilePath(flagDir, projectId);
        lock (_lock)
        {
            Directory.CreateDirectory(flagDir);
            var data = Load(path);
            var changed = false;

            if (string.Equals(tool, "Bash", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(command) && !data.BashCommands.Contains(command))
                {
                    data.BashCommands.Add(command);
                    changed = true;
                }
            }
            else if (!string.IsNullOrEmpty(tool) && !data.Tools.Contains(tool))
            {
                data.Tools.Add(tool);
                changed = true;
            }

            if (changed)
                File.WriteAllText(path, JsonSerializer.Serialize(data, Json.Default));
        }
    }

    public static LearnedData ReadData(string flagDir, string projectId)
        => Load(FilePath(flagDir, projectId));

    private static string FilePath(string flagDir, string projectId)
        => Path.Combine(flagDir, projectId + FileSuffix);

    private static LearnedData Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            return JsonSerializer.Deserialize<LearnedData>(File.ReadAllText(path), Json.Default)
                   ?? new();
        }
        catch { return new(); }
    }
}
