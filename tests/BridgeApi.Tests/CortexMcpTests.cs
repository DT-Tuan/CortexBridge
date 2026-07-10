using System.Text.Json;
using CortexBridge.Api.Cortex;
using Microsoft.Extensions.Configuration;

namespace CortexBridge.Api.Tests;

/// <summary>
/// ADR-025 Phase 4 Slice 2 — the unit-testable core of the MCP memory client:
/// the SSE/JSON-RPC framing parse + the inner-payload mapping + the repo-name
/// map. Live HTTP isn't unit-tested (no WebApplicationFactory per project rule);
/// these cover the bits that actually carry the contract risk.
/// </summary>
public class CortexMcpTests
{
    private static string Sse(object rpc) =>
        $"event: message\ndata: {JsonSerializer.Serialize(rpc)}\n\n";

    private const string InnerMemoriesJson = """
        {"count":2,"memories":[
          {"id":"m1","content":"hello","scope":"project","repository":"CortexBridge",
           "topic":"decision","importance":0.5,"score":0.91,
           "createdAt":"2026-06-20T00:00:00Z","lastAccessedAt":"2026-06-20T01:00:00Z","accessCount":3},
          {"id":"m2","content":"world","scope":"global","topic":"pattern"}]}
        """;

    [Fact]
    public void ExtractToolResultText_ReturnsInnerTextFromSseFrame()
    {
        var sse = Sse(new { jsonrpc = "2.0", id = 2, result = new { content = new[] { new { type = "text", text = InnerMemoriesJson } } } });
        Assert.Equal(InnerMemoriesJson, CortexMcp.ExtractToolResultText(sse));
    }

    [Fact]
    public void ParseMemoryList_MapsFields()
    {
        var list = CortexMcp.ParseMemoryList(InnerMemoriesJson);
        Assert.Equal(2, list.Count);
        Assert.Equal(2, list.Memories.Count);

        var m1 = list.Memories[0];
        Assert.Equal("m1", m1.Id);
        Assert.Equal("hello", m1.Content);
        Assert.Equal("CortexBridge", m1.Repository);
        Assert.Equal("decision", m1.Topic);
        Assert.Equal(0.91, m1.Score);
        Assert.Equal(3, m1.AccessCount);

        // Sparse row (no repository/score/importance) must not crash.
        Assert.Equal("m2", list.Memories[1].Id);
        Assert.Null(list.Memories[1].Repository);
    }

    [Fact]
    public void EndToEnd_SseToMemoryList()
    {
        var sse = Sse(new { jsonrpc = "2.0", id = 2, result = new { content = new[] { new { type = "text", text = InnerMemoriesJson } } } });
        var list = CortexMcp.ParseMemoryList(CortexMcp.ExtractToolResultText(sse));
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void ExtractToolResultText_JsonRpcError_Throws()
    {
        var sse = Sse(new { jsonrpc = "2.0", id = 2, error = new { code = -32601, message = "boom" } });
        var ex = Assert.Throws<CortexPlexusUnavailableException>(() => CortexMcp.ExtractToolResultText(sse));
        Assert.Contains("boom", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("event: message\ndata: not json\n")]
    [InlineData("event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n")] // no content
    public void ExtractToolResultText_BadOrShapeless_Throws(string body)
    {
        Assert.Throws<CortexPlexusUnavailableException>(() => CortexMcp.ExtractToolResultText(body));
    }

    [Fact]
    public void ParseMemoryList_GarbageInner_Throws()
    {
        Assert.Throws<CortexPlexusUnavailableException>(() => CortexMcp.ParseMemoryList("not json at all"));
    }

    [Fact]
    public void ParseSaveResult_MapsFields()
    {
        const string inner = """
            {"id":"a0647f0c","scope":"project","topic":"note","importance":0.5,
             "savedAt":"2026-06-20T14:55:53Z","stored":true}
            """;
        var r = CortexMcp.ParseSaveResult(inner);
        Assert.Equal("a0647f0c", r.Id);
        Assert.Equal("project", r.Scope);
        Assert.Equal("note", r.Topic);
        Assert.True(r.Stored);
    }

    [Fact]
    public void ParseSaveResult_Garbage_Throws()
    {
        Assert.Throws<CortexPlexusUnavailableException>(() => CortexMcp.ParseSaveResult("not json"));
    }

    [Theory]
    [InlineData("{\"forgotten\":true,\"id\":\"x\"}", true)]
    [InlineData("{\"forgotten\":false,\"id\":\"x\"}", false)]
    [InlineData("{\"id\":\"x\"}", false)]
    public void ParseForget_ReadsFlag(string inner, bool expected)
    {
        Assert.Equal(expected, CortexMcp.ParseForget(inner));
    }

    [Fact]
    public void ParseForget_Garbage_Throws()
    {
        Assert.Throws<CortexPlexusUnavailableException>(() => CortexMcp.ParseForget("nope"));
    }

    [Fact]
    public void ParseRepositoryNames_ScrapesNameLines()
    {
        const string prose =
            "Indexed repositories:\n\n" +
            "  Name: app\n  Path: /x\n  Health: OK\n\n" +
            "  Name: CortexBridge\n  Path: _agent/CortexBridge\n  Health: OK\n";
        Assert.Equal(new[] { "app", "CortexBridge" }, CortexMcp.ParseRepositoryNames(prose));
    }

    [Theory]
    [InlineData("legacy-dir", "IndexedName")]   // configured divergent pair
    [InlineData("some-repo", "some-repo")]      // identity default
    [InlineData("", "")]
    public void CortexRepoMap_Resolve(string projectId, string expected)
    {
        CortexRepoMap.Configure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CortexPlexus:RepoNameMap:legacy-dir"] = "IndexedName",
            })
            .Build());

        Assert.Equal(expected, CortexRepoMap.Resolve(projectId));
    }

    [Fact]
    public void CortexRepoMap_Resolve_IsIdentity_WhenMapEmpty()
    {
        CortexRepoMap.Configure(new ConfigurationBuilder().Build());

        Assert.Equal("anything", CortexRepoMap.Resolve("anything"));
    }
}
