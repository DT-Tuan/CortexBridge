using CortexBridge.Api.Common;
using CortexBridge.Api.Sessions;

namespace CortexBridge.Api.Endpoints;

/// <summary>
/// ADR-025 Phase 4 Slice 1 — observability for the CortexBridge ⇄ CortexPlexus
/// leg. Reports how much CC leaned on the cortexplexus MCP this session, derived
/// PURELY from the parsed transcript (no call to CortexPlexus). Backs the chat
/// header "🧠" badge. Fail-soft: missing session/project ⇒ an empty tally (200),
/// so the badge degrades quietly and never blocks the chat view.
/// </summary>
public static class CortexUsageEndpoint
{
    // Bounded default so the scan stays cheap on huge transcripts (a 37 MB
    // session is enough history for "is CC using cortexplexus"); ?limit= overrides.
    private const int DefaultLimit = 1000;

    public static void MapCortexUsage(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sessions/{projectId}/cortex-usage", async (
            string projectId,
            HttpContext ctx,
            SessionScanner scanner,
            JsonlReader reader,
            CancellationToken ct) =>
        {
            var requestedSession = ctx.Request.Query["session"].ToString();
            if (!int.TryParse(ctx.Request.Query["limit"].ToString(), out var limit) || limit <= 0)
                limit = DefaultLimit;

            var active = await scanner.ResolveAsync(projectId, ct);
            if (active is null)
                return Results.Json(Empty(requestedSession), Json.Default);

            string jsonlPath;
            string? sessionUuid;
            if (string.IsNullOrEmpty(requestedSession))
            {
                jsonlPath = active.JsonlPath;
                sessionUuid = active.SessionUuid;
            }
            else
            {
                jsonlPath = Path.Combine(active.EncodedCwdDir, $"{requestedSession}.jsonl");
                if (!File.Exists(jsonlPath))
                    return ResultsHelpers.Error(404, "usage.unknown_uuid",
                        $"No JSONL for session '{requestedSession}' in project '{projectId}'");
                sessionUuid = requestedSession;
            }

            var (tail, _, truncated, _) = await reader.ReadTailAsync(jsonlPath, projectId, limit, ct);
            return Results.Json(
                CortexUsage.Tally(tail, sessionUuid, scanned: tail.Count, truncated), Json.Default);
        });
    }

    private static CortexUsageResult Empty(string? sessionUuid) =>
        new(string.IsNullOrEmpty(sessionUuid) ? null : sessionUuid,
            0, new Dictionary<string, int>(), null, 0, false);
}
