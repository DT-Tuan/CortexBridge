using CortexBridge.Api.Usage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexBridge.Api.Tests;

public class UsageServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _jsonPath;

    public UsageServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "usage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _jsonPath = Path.Combine(_tmpDir, "usage.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    private UsageService Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["USAGE_JSON_PATH"] = _jsonPath,
            }).Build();
        return new UsageService(new UsagePaths(config), NullLogger<UsageService>.Instance);
    }

    private const string SampleJson = """
    {
      "takenAtUtc": "2026-06-05T15:43:14Z",
      "block5h": {
        "id": "2026-06-05T14:00:00.000Z",
        "startTime": "2026-06-05T14:00:00.000Z",
        "endTime": "2026-06-05T19:00:00.000Z",
        "isActive": true,
        "entries": 168,
        "models": ["claude-sonnet-4-6", "claude-opus-4-7"],
        "costUSD": 12.24,
        "totalTokens": 19139486,
        "burnRate": { "costPerHour": 11.78, "tokensPerMinute": 307125.0 },
        "projection": { "remainingMinutes": 197, "totalCost": 50.94, "totalTokens": 79643187 }
      },
      "week7d": {
        "period": "2026-06-01",
        "totalCost": 21.82,
        "totalTokens": 29745559,
        "modelBreakdowns": [
          { "modelName": "claude-opus-4-7",   "cost": 15.86, "inputTokens": 129, "outputTokens": 111477, "cacheCreationTokens": 901112, "cacheReadTokens": 14883651 },
          { "modelName": "claude-sonnet-4-6", "cost": 5.96,  "inputTokens": 198, "outputTokens": 63037,  "cacheCreationTokens": 254999, "cacheReadTokens": 13530956 }
        ]
      },
      "official": {
        "fiveHour": { "utilization": 35.0, "resetsAt": "2026-06-05T19:00:00+00:00" },
        "sevenDay": { "utilization": 23.0, "resetsAt": "2026-06-10T17:00:00+00:00" },
        "takenAtUtc": "2026-06-05T15:43:10Z"
      }
    }
    """;

    [Fact]
    public void GetCurrent_ParsesBlock5hWithProjection()
    {
        File.WriteAllText(_jsonPath, SampleJson);
        var svc = Build();

        var dto = svc.GetCurrent();

        Assert.NotNull(dto);
        Assert.NotNull(dto!.Block5h);
        var b = dto.Block5h!;
        Assert.Equal("2026-06-05T14:00:00.000Z", b.StartUtc);
        Assert.Equal("2026-06-05T19:00:00.000Z", b.EndUtc);
        Assert.Equal(197, b.RemainingMinutes);
        Assert.Equal(12.24m, b.CurrentCostUsd);
        Assert.Equal(50.94m, b.ProjectedCostUsd);
        Assert.Equal(19139486L, b.CurrentTokens);
        Assert.Equal(79643187L, b.ProjectedTokens);
        Assert.Equal(168, b.Entries);
        Assert.Equal(2, b.Models.Length);
    }

    [Fact]
    public void GetCurrent_ParsesWeek7dWithModelBreakdown()
    {
        File.WriteAllText(_jsonPath, SampleJson);
        var svc = Build();

        var dto = svc.GetCurrent();
        var w = dto!.Week7d!;

        Assert.Equal("2026-06-01", w.PeriodStart);
        Assert.Equal(21.82m, w.CurrentCostUsd);
        Assert.Equal(2, w.ModelBreakdown.Length);
        Assert.Equal("claude-opus-4-7", w.ModelBreakdown[0].Model);
        Assert.Equal(15.86m, w.ModelBreakdown[0].CostUsd);
    }

    [Fact]
    public void GetCurrent_ParsesOfficialBlock()
    {
        File.WriteAllText(_jsonPath, SampleJson);
        var svc = Build();

        var dto = svc.GetCurrent();
        var o = dto!.Official;

        Assert.NotNull(o);
        Assert.Equal(35.0m, o!.FiveHour!.Utilization);
        Assert.Equal("2026-06-05T19:00:00+00:00", o.FiveHour.ResetsAt);
        Assert.Equal(23.0m, o.SevenDay!.Utilization);
        Assert.Equal("2026-06-10T17:00:00+00:00", o.SevenDay.ResetsAt);
        Assert.Equal("2026-06-05T15:43:10Z", o.TakenAtUtc);
    }

    [Fact]
    public void GetCurrent_MissingOfficial_ReturnsNullOfficial()
    {
        // Pre-ADR-024 sampler output (or endpoint never reachable since boot):
        // no `official` key at all — service must surface null, not throw.
        File.WriteAllText(_jsonPath, """{ "takenAtUtc": "2026-06-05T15:43:14Z" }""");
        var svc = Build();

        var dto = svc.GetCurrent();

        Assert.NotNull(dto);
        Assert.Null(dto!.Official);
    }

    [Fact]
    public void GetCurrent_OfficialNullWindows_ParsesAsNull()
    {
        // Sampler writes "official": null on total failure with no last-known;
        // also a window can individually be null.
        File.WriteAllText(_jsonPath, """
        {
          "takenAtUtc": "2026-06-05T15:43:14Z",
          "official": {
            "fiveHour": null,
            "sevenDay": { "utilization": 23.0, "resetsAt": "2026-06-10T17:00:00+00:00" },
            "takenAtUtc": "2026-06-05T15:43:10Z"
          }
        }
        """);
        var svc = Build();

        var dto = svc.GetCurrent();

        Assert.NotNull(dto!.Official);
        Assert.Null(dto.Official!.FiveHour);
        Assert.Equal(23.0m, dto.Official.SevenDay!.Utilization);
    }

    [Fact]
    public void GetCurrent_NullBurnRateAndProjection_ParsesWithFallbacks()
    {
        // ccusage emits "burnRate": null / "projection": null in the first
        // minutes of a fresh 5h block — live caught 2026-06-11 (one 503).
        File.WriteAllText(_jsonPath, """
        {
          "takenAtUtc": "2026-06-11T13:06:00Z",
          "block5h": {
            "startTime": "2026-06-11T13:00:00.000Z",
            "endTime": "2026-06-11T18:00:00.000Z",
            "isActive": true,
            "entries": 1,
            "models": [],
            "costUSD": 0.42,
            "totalTokens": 1000,
            "burnRate": null,
            "projection": null
          }
        }
        """);
        var svc = Build();

        var dto = svc.GetCurrent();

        Assert.NotNull(dto?.Block5h);
        var b = dto!.Block5h!;
        Assert.Equal(0.42m, b.CurrentCostUsd);
        Assert.Equal(0.42m, b.ProjectedCostUsd);   // falls back to current
        Assert.Equal(1000L, b.ProjectedTokens);    // falls back to current
        Assert.Equal(0, b.RemainingMinutes);
        Assert.Equal(0m, b.CostPerHour);
        Assert.Equal(0m, b.TokensPerMinute);
    }

    [Fact]
    public void GetCurrent_MissingFile_ReturnsNullThenLastGood()
    {
        var svc = Build();
        // First call: no file
        Assert.Null(svc.GetCurrent());

        // Write file, get a snapshot
        File.WriteAllText(_jsonPath, SampleJson);
        var good = svc.GetCurrent();
        Assert.NotNull(good);

        // Delete file: should still return the last-good cached snapshot
        File.Delete(_jsonPath);
        var stale = svc.GetCurrent();
        Assert.NotNull(stale);
        Assert.Equal(good!.TakenAtUtc, stale!.TakenAtUtc);
    }

    [Fact]
    public void GetCurrent_CorruptJson_ReturnsLastGoodOrNull()
    {
        File.WriteAllText(_jsonPath, SampleJson);
        var svc = Build();
        var good = svc.GetCurrent();

        File.WriteAllText(_jsonPath, "not a json {");
        var fallback = svc.GetCurrent();
        Assert.Equal(good!.TakenAtUtc, fallback!.TakenAtUtc);
    }

    [Fact]
    public void GetCurrent_NoProjectsField_ReturnsEmptyArray()
    {
        // Existing SampleJson has no `projects` field — verifies backwards-compat
        // for the brief window before the host sampler ships the new schema.
        File.WriteAllText(_jsonPath, SampleJson);
        var svc = Build();
        var dto = svc.GetCurrent();
        Assert.NotNull(dto!.Projects);
        Assert.Empty(dto.Projects);
    }

    private const string ProjectsJson = """
    {
      "takenAtUtc": "2026-06-06T08:00:00Z",
      "projects": [
        {
          "encodedPath": "-home-youruser-workspace-CortexBridge",
          "name": "CortexBridge",
          "totalCostUsd": 1292.6,
          "totalTokens": 1832378879,
          "inputTokens": 1234,
          "outputTokens": 5678,
          "cacheCreationTokens": 9000,
          "cacheReadTokens": 11111,
          "sessionCount": 5,
          "lastActivity": "2026-06-06",
          "models": ["claude-opus-4-7", "claude-sonnet-4-6"]
        },
        {
          "encodedPath": "-home-youruser-workspace-project-epsilon",
          "name": "project-epsilon",
          "totalCostUsd": 12.5,
          "totalTokens": 200000,
          "inputTokens": 10, "outputTokens": 20,
          "cacheCreationTokens": 30, "cacheReadTokens": 40,
          "sessionCount": 1,
          "lastActivity": "",
          "models": []
        }
      ]
    }
    """;

    [Fact]
    public void GetCurrent_ParsesProjectsArray_PreservesSamplerOrder()
    {
        File.WriteAllText(_jsonPath, ProjectsJson);
        var svc = Build();
        var dto = svc.GetCurrent();

        Assert.Equal(2, dto!.Projects.Length);
        // Sampler sorts desc by cost; service preserves that order.
        Assert.Equal("CortexBridge", dto.Projects[0].Name);
        Assert.Equal("-home-youruser-workspace-CortexBridge", dto.Projects[0].EncodedPath);
        Assert.Equal(1292.6m, dto.Projects[0].TotalCostUsd);
        Assert.Equal(1832378879L, dto.Projects[0].TotalTokens);
        Assert.Equal(5, dto.Projects[0].SessionCount);
        Assert.Equal("2026-06-06", dto.Projects[0].LastActivity);
        Assert.Equal(new[] { "claude-opus-4-7", "claude-sonnet-4-6" }, dto.Projects[0].Models);

        // Second project — empty lastActivity coerced to null, no models.
        Assert.Equal("project-epsilon", dto.Projects[1].Name);
        Assert.Null(dto.Projects[1].LastActivity);
        Assert.Empty(dto.Projects[1].Models);
    }
}
