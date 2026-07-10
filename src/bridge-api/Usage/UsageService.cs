using System.Text.Json;

namespace CortexBridge.Api.Usage;

/// <summary>
/// Reads + parses the host-sampled <c>/var/cortex-bridge/usage.json</c>
/// (produced by <c>~/.local/bin/cortex-usage-sample.sh</c> on the VPS host).
/// Stateless; the file itself is the cache (refreshed every ~60s by the host
/// timer). On missing file or parse error the service returns the most recent
/// good snapshot it had, or null on first call — callers translate null to 503.
///
/// Quota %: the <c>official</c> block (ADR-024) carries Anthropic's own
/// 5h/7d utilization — passed through verbatim, no cap math. The former
/// cap subsystem (usage_caps + extrapolation) is gone.
/// </summary>
public class UsageService
{
    private readonly UsagePaths _paths;
    private readonly ILogger<UsageService> _log;
    private UsageResponse? _lastGood;

    public UsageService(UsagePaths paths, ILogger<UsageService> log)
    {
        _paths = paths;
        _log = log;
    }

    public UsageResponse? GetCurrent()
    {
        try
        {
            if (!File.Exists(_paths.UsageJsonPath))
            {
                _log.LogWarning("Usage JSON missing at {Path}", _paths.UsageJsonPath);
                return _lastGood;
            }
            using var stream = File.OpenRead(_paths.UsageJsonPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var dto = Build(root);
            _lastGood = dto;
            return dto;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to read/parse usage JSON at {Path}", _paths.UsageJsonPath);
            return _lastGood;
        }
    }

    private UsageResponse Build(JsonElement root)
    {
        var takenAt = root.TryGetProperty("takenAtUtc", out var t) ? t.GetString() ?? "" : "";
        Block5hDto? block = null;
        Week7dDto? week = null;
        OfficialUsageDto? official = null;

        if (root.TryGetProperty("block5h", out var b) && b.ValueKind == JsonValueKind.Object)
            block = BuildBlock(b);
        if (root.TryGetProperty("week7d", out var w) && w.ValueKind == JsonValueKind.Object)
            week = BuildWeek(w);
        if (root.TryGetProperty("official", out var o) && o.ValueKind == JsonValueKind.Object)
            official = BuildOfficial(o);
        var projects = root.TryGetProperty("projects", out var ps) && ps.ValueKind == JsonValueKind.Array
            ? BuildProjects(ps)
            : Array.Empty<ProjectUsageDto>();

        return new UsageResponse(takenAt, block, week, projects, official);
    }

    private static OfficialUsageDto BuildOfficial(JsonElement o) =>
        new(
            BuildOfficialWindow(o, "fiveHour"),
            BuildOfficialWindow(o, "sevenDay"),
            Str(o, "takenAtUtc"));

    private static OfficialWindowDto? BuildOfficialWindow(JsonElement o, string name) =>
        o.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.Object
            ? new OfficialWindowDto(Dec(w, "utilization"), Str(w, "resetsAt"))
            : null;

    private static ProjectUsageDto[] BuildProjects(JsonElement arr) =>
        arr.EnumerateArray().Select(p => new ProjectUsageDto(
            Str(p, "name"),
            Str(p, "encodedPath"),
            Dec(p, "totalCostUsd"),
            Long(p, "totalTokens"),
            Long(p, "inputTokens"),
            Long(p, "outputTokens"),
            Long(p, "cacheCreationTokens"),
            Long(p, "cacheReadTokens"),
            p.TryGetProperty("sessionCount", out var sc) ? sc.GetInt32() : 0,
            p.TryGetProperty("lastActivity", out var la) && la.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(la.GetString())
                ? la.GetString() : null,
            p.TryGetProperty("models", out var mm) && mm.ValueKind == JsonValueKind.Array
                ? mm.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                : Array.Empty<string>()
        )).ToArray();

    private static Block5hDto BuildBlock(JsonElement b)
    {
        // ccusage emits "burnRate": null / "projection": null in the first
        // minutes of a fresh 5h block (no burn data yet). TryGetProperty on a
        // Null element THROWS — gate every nested access on ValueKind=Object.
        // Live caught 2026-06-11 right after a block rollover (one 503).
        var startUtc = Str(b, "startTime");
        var endUtc   = Str(b, "endTime");
        var hasProjection = b.TryGetProperty("projection", out var pj) && pj.ValueKind == JsonValueKind.Object;
        var hasBurnRate   = b.TryGetProperty("burnRate", out var br) && br.ValueKind == JsonValueKind.Object;
        var remainingMin = hasProjection && pj.TryGetProperty("remainingMinutes", out var rm)
            ? rm.GetInt32() : 0;
        var currentCost = Dec(b, "costUSD");
        var projectedCost = hasProjection && pj.TryGetProperty("totalCost", out var tc)
            ? tc.GetDecimal() : currentCost;
        var totalTokens = b.TryGetProperty("totalTokens", out var tt) ? tt.GetInt64() : 0L;
        var projectedTokens = hasProjection && pj.TryGetProperty("totalTokens", out var pt)
            ? pt.GetInt64() : totalTokens;
        var costPerHour = hasBurnRate && br.TryGetProperty("costPerHour", out var cph)
            ? cph.GetDecimal() : 0m;
        var tpm = hasBurnRate && br.TryGetProperty("tokensPerMinute", out var tpm2)
            ? tpm2.GetDecimal() : 0m;
        var models = b.TryGetProperty("models", out var mm) && mm.ValueKind == JsonValueKind.Array
            ? mm.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>();
        var entries = b.TryGetProperty("entries", out var en) ? en.GetInt32() : 0;

        return new Block5hDto(startUtc, endUtc, remainingMin, currentCost, projectedCost,
            totalTokens, projectedTokens, costPerHour, tpm, models, entries);
    }

    private static Week7dDto BuildWeek(JsonElement w)
    {
        var period = Str(w, "period");
        var cost = Dec(w, "totalCost");
        var tokens = w.TryGetProperty("totalTokens", out var tt) ? tt.GetInt64() : 0L;

        var breakdown = w.TryGetProperty("modelBreakdowns", out var mb) && mb.ValueKind == JsonValueKind.Array
            ? mb.EnumerateArray().Select(e => new ModelUsageDto(
                Str(e, "modelName"),
                Dec(e, "cost"),
                e.TryGetProperty("inputTokens", out var i) ? i.GetInt64() : 0L,
                e.TryGetProperty("outputTokens", out var o) ? o.GetInt64() : 0L,
                e.TryGetProperty("cacheCreationTokens", out var cc) ? cc.GetInt64() : 0L,
                e.TryGetProperty("cacheReadTokens", out var cr) ? cr.GetInt64() : 0L)).ToArray()
            : Array.Empty<ModelUsageDto>();

        return new Week7dDto(period, cost, tokens, breakdown);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";

    private static decimal Dec(JsonElement e, string name) =>
        e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDecimal() : 0m;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt64() : 0L;
}
