using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CortexBridge.Api.Cortex;

/// <summary>
/// Minimal MCP Streamable-HTTP client for CortexPlexus /mcp — the read side of
/// the Phase-4 memory cockpit (ADR-025). Each logical call does the full
/// stateless handshake (initialize → notifications/initialized → tools/call) so
/// there is no session-expiry state to manage; the cockpit is not a hot path and
/// the round-trip is a few small POSTs on the LAN. Any failure surfaces as
/// <see cref="CortexPlexusUnavailableException"/> (fail-soft at the endpoint).
/// No auth — the endpoint is LAN-only (verified: CC wires it tokenless).
///
/// NOTE (verified 2026-06-20): only the FAST tools are exposed. recall_memory
/// (semantic) measured 30–50 s on this LXC (embedding + vector search, I/O-bound
/// per ADR-023) — unusable on a synchronous mobile request, so the cockpit uses
/// list_memories + client-side filtering. A future async "deep search" slice can
/// add recall back behind a long-running/poll UX.
/// </summary>
public interface ICortexPlexusClient
{
    Task<CortexMemoryList> ListMemoriesAsync(string scope, string? repository, int limit, CancellationToken ct);
    Task<List<string>> ListRepositoriesAsync(CancellationToken ct);
    // Write side (Slice 3). save_memory embeds the content (~10 s on this LXC) —
    // the 30 s client timeout covers it; forget is instant.
    Task<CortexSaveResult> SaveMemoryAsync(string content, string scope, string topic, string? repository, double? importance, CancellationToken ct);
    Task<bool> ForgetMemoryAsync(string id, CancellationToken ct);
}

public sealed class CortexPlexusClient(HttpClient http) : ICortexPlexusClient
{
    public async Task<CortexMemoryList> ListMemoriesAsync(
        string scope, string? repository, int limit, CancellationToken ct)
    {
        var args = new Dictionary<string, object> { ["scope"] = scope, ["limit"] = limit };
        if (!string.IsNullOrEmpty(repository)) args["repository"] = repository;
        return CortexMcp.ParseMemoryList(await CallToolAsync("list_memories", args, ct));
    }

    public async Task<List<string>> ListRepositoriesAsync(CancellationToken ct) =>
        CortexMcp.ParseRepositoryNames(
            await CallToolAsync("list_repositories", new Dictionary<string, object>(), ct));

    public async Task<CortexSaveResult> SaveMemoryAsync(
        string content, string scope, string topic, string? repository, double? importance, CancellationToken ct)
    {
        var args = new Dictionary<string, object>
        {
            ["content"] = content, ["scope"] = scope, ["topic"] = topic
        };
        if (!string.IsNullOrEmpty(repository)) args["repository"] = repository;
        if (importance is not null) args["importance"] = importance.Value;
        return CortexMcp.ParseSaveResult(await CallToolAsync("save_memory", args, ct));
    }

    public async Task<bool> ForgetMemoryAsync(string id, CancellationToken ct) =>
        CortexMcp.ParseForget(
            await CallToolAsync("forget_memory", new Dictionary<string, object> { ["id"] = id }, ct));

    private async Task<string> CallToolAsync(string tool, Dictionary<string, object> args, CancellationToken ct)
    {
        try
        {
            var sid = await InitializeAsync(ct);
            await PostAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", sid, ct);
            var toolCall = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new { name = tool, arguments = args }
            });
            var body = await PostAsync(toolCall, sid, ct);
            return CortexMcp.ExtractToolResultText(body);
        }
        catch (CortexPlexusUnavailableException) { throw; }
        catch (Exception ex)
        {
            throw new CortexPlexusUnavailableException($"cortexplexus {tool} call failed", ex);
        }
    }

    private const string InitBody =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{" +
        "\"protocolVersion\":\"2025-06-18\",\"capabilities\":{}," +
        "\"clientInfo\":{\"name\":\"cortexbridge\",\"version\":\"1.0\"}}}";

    private async Task<string> InitializeAsync(CancellationToken ct)
    {
        using var req = Build(InitBody);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        if (!resp.Headers.TryGetValues("Mcp-Session-Id", out var v))
            throw new CortexPlexusUnavailableException("MCP initialize returned no session id");
        return v.First();
    }

    private async Task<string> PostAsync(string json, string sessionId, CancellationToken ct)
    {
        using var req = Build(json);
        req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private HttpRequestMessage Build(string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, http.BaseAddress)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return req;
    }
}
