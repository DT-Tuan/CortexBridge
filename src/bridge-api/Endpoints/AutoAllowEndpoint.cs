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
///   ro-off    — opt a workspace project OUT of default-on read-only (ADR-028 A):
///               the hook turns the read tier ON by default under ~/workspace, and
///               this flag is the per-project opt-out the PWA shield writes.
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
        app.MapPost("/api/sessions/{projectId}/autoallow/burst", BurstHandler);
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

    private static async Task<IResult> BurstHandler(
        string projectId,
        AutoAllowFlags.BurstRequest body,
        HttpContext ctx,
        BridgePaths paths,
        BridgeDbContext db,
        CancellationToken ct)
    {
        if (!AutoAllowFlags.TryFlagDir(paths.ClaudeUserDir, projectId, out var dir))
            return ResultsHelpers.Error(400, "autoallow.bad_project_id", "Invalid projectId");

        var (until, opaque) = AutoAllowFlags.SetBurst(dir, projectId, body.Minutes, body.Opaque);

        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTimeOffset.UtcNow,
            ProjectId = projectId,
            Action = "session.autoallow.burst",
            TokenId = ctx.GetAuthToken()?.Id,
            Result = "ok",
            // tier/time only, never command content
            Detail = until > 0 ? $"until={until}{(opaque ? " opaque" : string.Empty)}" : "cancelled",
        });
        await db.SaveChangesAsync(ct);

        return Results.Json(AutoAllowFlags.Read(dir, projectId), Json.Default);
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
        (".ro-off",   r => r.RoOff),   // ADR-028 A: opt-out of default-on read-only
    };

    public record State(bool Enabled, bool Autonomy, bool Push, bool Install, bool RoOff,
        long BurstUntil, bool BurstOpaque);
    public record SetRequest(bool? Enabled, bool? Autonomy, bool? Push, bool? Install, bool? RoOff);
    public record BurstRequest(int Minutes, bool Opaque);

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

    public static State Read(string flagDir, string projectId)
    {
        var burstUntil = ReadBurst(flagDir, projectId, out var burstOpaque);
        return new(
            Enabled: File.Exists(Path.Combine(flagDir, projectId + ".on")),
            Autonomy: File.Exists(Path.Combine(flagDir, projectId + ".autonomy")),
            Push: File.Exists(Path.Combine(flagDir, projectId + ".push")),
            Install: File.Exists(Path.Combine(flagDir, projectId + ".install")),
            RoOff: File.Exists(Path.Combine(flagDir, projectId + ".ro-off")),
            BurstUntil: burstUntil,
            BurstOpaque: burstOpaque);
    }

    /// <summary>
    /// ADR-028 B — read the time-boxed burst. Returns the expiry epoch (seconds) if a
    /// .burst flag exists and is unexpired, else 0 (and deletes an expired file). Hook
    /// format is "&lt;epoch&gt; [opaque]" on the first line.
    /// </summary>
    public static long ReadBurst(string flagDir, string projectId, out bool opaque)
    {
        opaque = false;
        var path = Path.Combine(flagDir, projectId + ".burst");
        if (!File.Exists(path)) return 0;
        try
        {
            var first = File.ReadAllText(path).Split('\n', 2)[0].TrimEnd('\r');
            var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !long.TryParse(parts[0], out var exp)) return 0;
            if (exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                try { File.Delete(path); } catch { /* best-effort cleanup */ }
                return 0;
            }
            opaque = parts.Length > 1 && parts[1] == "opaque";
            return exp;
        }
        catch { return 0; }
    }

    /// <summary>
    /// ADR-028 B — start (minutes &gt; 0, clamped to 8h) or cancel (minutes &lt;= 0) a
    /// burst, writing the hook's "&lt;epoch&gt; [opaque]" format. Returns (until, opaque).
    /// </summary>
    public static (long Until, bool Opaque) SetBurst(string flagDir, string projectId, int minutes, bool opaque)
    {
        var path = Path.Combine(flagDir, projectId + ".burst");
        if (minutes <= 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return (0, false);
        }
        Directory.CreateDirectory(flagDir);
        minutes = Math.Min(minutes, 8 * 60);   // ceiling: a bad client can't grant an unbounded window
        var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + minutes * 60L;
        File.WriteAllText(path, opaque ? $"{exp} opaque\n" : $"{exp}\n");
        return (exp, opaque);
    }

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
/// Schema: { "bashCommands": [], "tools": ["Read", ...] }
///   bashCommands — RETIRED (ADR-028 D): kept in the schema for back-compat but never
///                  written or read. Exact-string Bash learning never re-matched and
///                  captured plaintext secrets in a world-readable file.
///   tools        — non-Bash tool names whose ANY call is auto-allowed (no command content)
///
/// The hook reads .tools after the tier-flag check; bridge owns writes.
/// </summary>
public static class LearnedAutoAllow
{
    private const string FileSuffix = ".learned.json";
    private static readonly Lock _lock = new();

    /// <summary>
    /// Tools that must NEVER be learned/auto-allowed, whatever the PWA sends. These
    /// are INTERACTIVE, user-facing tools whose entire purpose is to elicit a user
    /// decision (AskUserQuestion) or gate a plan (ExitPlanMode). Auto-allowing one is
    /// a category error: it does not "run a safe action", it silently bypasses the
    /// interaction the tool exists for — and empirically corrupts the AskUserQuestion
    /// → PWA surfacing flow (a learned AskUserQuestion got a PreToolUse allow on every
    /// call). The digit-answer path used to record whatever tool was pending, so an
    /// answered single-select AskUserQuestion taught the system to allow it; this set
    /// is the server-side backstop. The hook carries the same denylist independently.
    /// </summary>
    public static readonly IReadOnlySet<string> NeverLearnTools =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AskUserQuestion", "ExitPlanMode" };

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
        // Interactive/meta tools are never learnable — see NeverLearnTools.
        if (NeverLearnTools.Contains(tool)) return;

        // ADR-028 D: exact-string Bash learning is RETIRED. It never re-matched (≈98% of the
        // historical store was write-once) and captured plaintext secrets (tokens, ssh key
        // paths, .env greps) into a world-readable file. A (default-on reads) + B (autonomy/
        // burst) cover the safe cases; the unsafe ones must keep prompting. Only tool-NAME
        // learning survives (Read/Write/Edit — no command content, so no secret capture).
        // `command` is intentionally ignored.
        _ = command;
        if (string.Equals(tool, "Bash", StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrEmpty(tool)) return;

        var path = FilePath(flagDir, projectId);
        lock (_lock)
        {
            Directory.CreateDirectory(flagDir);
            var data = Load(path);
            if (!data.Tools.Contains(tool))
            {
                data.Tools.Add(tool);
                File.WriteAllText(path, JsonSerializer.Serialize(data, Json.Default));
            }
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
