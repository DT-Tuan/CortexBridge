using System.Text.Json;
using System.Text.Json.Serialization;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// Per-session tally of how much CC leaned on the cortexplexus MCP, derived
/// PURELY from the transcript (ADR-025 Phase 4 Slice 1). MCP tool calls land in
/// the JSONL as assistant <c>tool_use</c> blocks named
/// <c>mcp__cortexplexus__&lt;tool&gt;</c>; we count them locally — no call to
/// CortexPlexus is made here. Pure + I/O-free so it is unit-testable over JSONL
/// goldens. Never surfaces tool input/output (CLAUDE.md: no content logging).
/// </summary>
public static class CortexUsage
{
    /// <summary>Prefix CC writes for every cortexplexus MCP tool, regardless of repo.</summary>
    public const string Prefix = "mcp__cortexplexus__";

    public static CortexUsageResult Tally(
        IEnumerable<SessionMessage> messages, string? sessionUuid, int scanned, bool truncated)
    {
        var byTool = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        string? lastUsedAt = null;

        foreach (var m in messages)
        {
            // tool_use blocks live in an assistant message's content ARRAY.
            if (m.Content is not { ValueKind: JsonValueKind.Array } content) continue;

            var matchedHere = false;
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (!block.TryGetProperty("type", out var typeEl)
                    || typeEl.ValueKind != JsonValueKind.String
                    || typeEl.GetString() != "tool_use") continue;
                if (!block.TryGetProperty("name", out var nameEl)
                    || nameEl.ValueKind != JsonValueKind.String) continue;

                var name = nameEl.GetString();
                if (name is null || !name.StartsWith(Prefix, StringComparison.Ordinal)) continue;

                var tool = name[Prefix.Length..];
                byTool[tool] = byTool.TryGetValue(tool, out var n) ? n + 1 : 1;
                total++;
                matchedHere = true;
            }

            // Messages arrive in file (chronological) order → last match wins.
            if (matchedHere && m.Timestamp is not null) lastUsedAt = m.Timestamp;
        }

        return new CortexUsageResult(sessionUuid, total, byTool, lastUsedAt, scanned, truncated);
    }
}

/// <summary>
/// Wire shape for <c>GET /api/sessions/{projectId}/cortex-usage</c>. Tool NAMES +
/// counts only — never any tool argument or result content.
/// </summary>
public record CortexUsageResult(
    [property: JsonPropertyName("sessionUuid")] string? SessionUuid,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("byTool")] Dictionary<string, int> ByTool,
    [property: JsonPropertyName("lastUsedAt")] string? LastUsedAt,
    [property: JsonPropertyName("scanned")] int Scanned,
    [property: JsonPropertyName("truncated")] bool Truncated
);
