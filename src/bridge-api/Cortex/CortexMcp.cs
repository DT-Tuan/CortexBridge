using System.Text.Json;

namespace CortexBridge.Api.Cortex;

/// <summary>
/// Pure helpers for speaking MCP Streamable HTTP to CortexPlexus /mcp — no I/O,
/// so the wire framing is unit-testable. Contract verified 2026-06-20 (memory
/// eaab3a44): responses are text/event-stream (`event: …\n data: {json}` per
/// event); a tools/call result carries its real payload as a JSON STRING inside
/// <c>result.content[0].text</c> (two parse layers).
/// </summary>
public static class CortexMcp
{
    /// <summary>
    /// Extract the tool-result text from an SSE-framed JSON-RPC response body.
    /// Throws <see cref="CortexPlexusUnavailableException"/> on a JSON-RPC error
    /// or a shape we don't recognise.
    /// </summary>
    public static string ExtractToolResultText(string sseBody)
    {
        if (string.IsNullOrWhiteSpace(sseBody))
            throw new CortexPlexusUnavailableException("empty MCP response");

        // Walk the SSE lines; the JSON-RPC response is the `data:` payload that
        // parses to an object carrying `result` or `error`. (SSE may interleave
        // other events; pick the response one.)
        foreach (var raw in sseBody.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0 || payload[0] != '{') continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(payload); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err))
                    throw new CortexPlexusUnavailableException(
                        "MCP error: " + (err.TryGetProperty("message", out var m) ? m.GetString() : "unknown"));

                if (!root.TryGetProperty("result", out var result)) continue;
                if (!result.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array) continue;

                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.Object
                        && block.TryGetProperty("type", out var t) && t.GetString() == "text"
                        && block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                        return txt.GetString() ?? string.Empty;
                }
            }
        }
        throw new CortexPlexusUnavailableException("no tool-result text in MCP response");
    }

    /// <summary>
    /// Deserialize the inner {count, memories[]} JSON of list_memories /
    /// recall_memory. A non-JSON or unexpected body ⇒ unavailable.
    /// </summary>
    public static CortexMemoryList ParseMemoryList(string innerJson)
    {
        try
        {
            var list = JsonSerializer.Deserialize<CortexMemoryList>(innerJson, Web);
            return list ?? new CortexMemoryList(0, new());
        }
        catch (JsonException ex)
        {
            throw new CortexPlexusUnavailableException("unexpected memory payload", ex);
        }
    }

    /// <summary>Deserialize the inner {id, scope, topic, …, stored} JSON of save_memory.</summary>
    public static CortexSaveResult ParseSaveResult(string innerJson)
    {
        try
        {
            return JsonSerializer.Deserialize<CortexSaveResult>(innerJson, Web)
                ?? throw new CortexPlexusUnavailableException("empty save_memory payload");
        }
        catch (JsonException ex)
        {
            throw new CortexPlexusUnavailableException("unexpected save_memory payload", ex);
        }
    }

    /// <summary>Read the {forgotten:bool} flag of forget_memory's inner JSON.</summary>
    public static bool ParseForget(string innerJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(innerJson);
            return doc.RootElement.TryGetProperty("forgotten", out var f)
                && f.ValueKind == JsonValueKind.True;
        }
        catch (JsonException ex)
        {
            throw new CortexPlexusUnavailableException("unexpected forget_memory payload", ex);
        }
    }

    /// <summary>
    /// list_repositories returns human-readable PROSE (not JSON) in its text;
    /// scrape the "Name: &lt;x&gt;" lines. Best-effort — format drift just yields
    /// fewer names, never an error.
    /// </summary>
    public static List<string> ParseRepositoryNames(string proseText)
    {
        var names = new List<string>();
        foreach (var raw in proseText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Name:", StringComparison.Ordinal))
            {
                var name = line["Name:".Length..].Trim();
                if (name.Length > 0) names.Add(name);
            }
        }
        return names;
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
