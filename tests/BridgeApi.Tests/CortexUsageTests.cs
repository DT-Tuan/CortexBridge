using CortexBridge.Api.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexBridge.Api.Tests;

/// <summary>
/// ADR-025 Phase 4 Slice 1 — the transcript-derived cortexplexus MCP-usage tally.
/// Parses JSONL goldens via <see cref="JsonlReader"/> then asserts the counts the
/// chat-header badge renders. Pure logic; no network, no CortexPlexus call.
/// </summary>
public class CortexUsageTests
{
    private static string FixturePath(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "jsonl", name);
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new FileNotFoundException($"Cannot locate fixture {name}");
    }

    private static async Task<List<SessionMessage>> Parse(string fixture)
    {
        var reader = new JsonlReader(NullLogger<JsonlReader>.Instance);
        var (msgs, _) = await reader.ReadFromOffsetAsync(
            FixturePath(fixture), 0, "p", CancellationToken.None);
        return msgs;
    }

    [Fact]
    public async Task Tally_CountsCortexplexusToolUse_ByToolAndTotal()
    {
        var msgs = await Parse("cortex-usage.jsonl");
        var r = CortexUsage.Tally(msgs, "cu", msgs.Count, truncated: false);

        Assert.Equal(3, r.Total);                 // 2 recall + 1 get_callers
        Assert.Equal(2, r.ByTool["recall_memory"]);
        Assert.Equal(1, r.ByTool["get_callers"]);
        Assert.Equal("cu", r.SessionUuid);
    }

    [Fact]
    public async Task Tally_IgnoresNonCortexplexusTools()
    {
        var msgs = await Parse("cortex-usage.jsonl");
        var r = CortexUsage.Tally(msgs, "cu", msgs.Count, truncated: false);

        // Edit + Bash tool_use exist in the fixture but must NOT be counted.
        Assert.False(r.ByTool.ContainsKey("Edit"));
        Assert.False(r.ByTool.ContainsKey("Bash"));
    }

    [Fact]
    public async Task Tally_LastUsedAt_IsLatestMatchingRecord_NotLatestRecord()
    {
        var msgs = await Parse("cortex-usage.jsonl");
        var r = CortexUsage.Tally(msgs, "cu", msgs.Count, truncated: false);

        // Latest cortexplexus call is in cu-004; the later cu-005 (Edit/Bash) and
        // cu-006 (text only) must not advance lastUsedAt.
        Assert.Equal("2026-06-19T00:00:03Z", r.LastUsedAt);
    }

    [Fact]
    public async Task Tally_NoCortexplexusUse_IsZero_NullLastUsed()
    {
        // sample-session-001 has tool_use (e.g. a normal tool) but no mcp__cortexplexus__*.
        var msgs = await Parse("sample-session-001.jsonl");
        var r = CortexUsage.Tally(msgs, "s", msgs.Count, truncated: false);

        Assert.Equal(0, r.Total);
        Assert.Empty(r.ByTool);
        Assert.Null(r.LastUsedAt);
    }

    [Fact]
    public void Tally_EmptyInput_IsEmptyTally()
    {
        var r = CortexUsage.Tally(Array.Empty<SessionMessage>(), null, 0, truncated: true);
        Assert.Equal(0, r.Total);
        Assert.Empty(r.ByTool);
        Assert.Null(r.LastUsedAt);
        Assert.True(r.Truncated);
        Assert.Null(r.SessionUuid);
    }
}
