using CortexBridge.Api.Auth;
using CortexBridge.Api.Common;
using CortexBridge.Api.Cortex;
using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;

namespace CortexBridge.Api.Endpoints;

/// <summary>
/// ADR-025 Phase 4 Slice 2 — the read side of the memory cockpit. Proxies
/// CortexPlexus's MCP memory tools (list_memories / recall_memory /
/// list_repositories) so the PWA can browse + search the cross-project store.
/// Bearer-gated (global middleware). Fail-soft: a CortexPlexus outage ⇒ typed
/// 503, never a 500, and /api/health is untouched. Never logs memory content.
/// </summary>
public static class CortexMemoryEndpoint
{
    public static void MapCortexMemory(this IEndpointRouteBuilder app)
    {
        // GET /api/cortex/memories?scope=project|all&repo=<dir>&limit=
        //   list_memories only (fast). The PWA filters the returned set
        //   client-side — semantic recall_memory is 30–50 s on this LXC, far too
        //   slow for a synchronous request (see CortexPlexusClient note).
        //   scope=project requires repo (mapped via CortexRepoMap).
        app.MapGet("/api/cortex/memories", async (
            HttpContext ctx, ICortexPlexusClient client, CancellationToken ct) =>
        {
            var scope = ctx.Request.Query["scope"].ToString();
            if (scope is not ("project" or "all")) scope = "all";
            if (!int.TryParse(ctx.Request.Query["limit"].ToString(), out var limit) || limit <= 0)
                limit = 200;

            string? repository = null;
            if (scope == "project")
            {
                var repo = ctx.Request.Query["repo"].ToString();
                if (string.IsNullOrWhiteSpace(repo))
                    return ResultsHelpers.Error(400, "cortex.repo_required",
                        "scope=project requires repo");
                repository = CortexRepoMap.Resolve(repo);
            }

            try
            {
                return Results.Json(
                    await client.ListMemoriesAsync(scope, repository, limit, ct), Json.Default);
            }
            catch (CortexPlexusUnavailableException)
            {
                return ResultsHelpers.Error(503, "cortexplexus.unavailable",
                    "CortexPlexus không phản hồi");
            }
        });

        // GET /api/cortex/repositories → names for the scope picker.
        app.MapGet("/api/cortex/repositories", async (
            ICortexPlexusClient client, CancellationToken ct) =>
        {
            try { return Results.Json(await client.ListRepositoriesAsync(ct), Json.Default); }
            catch (CortexPlexusUnavailableException)
            {
                return ResultsHelpers.Error(503, "cortexplexus.unavailable",
                    "CortexPlexus không phản hồi");
            }
        });

        // POST /api/cortex/memories — save a new memory (Slice 3, ASYNC). save_memory
        // embeds the content on the LXC (50–70 s under load) → too slow to await on a
        // mobile request, so we validate, ENQUEUE, and return 202; CortexSaveWorker
        // performs the MCP call off the request path and audits (scope+topic only,
        // NEVER the content body). The PWA refetches the list to confirm.
        app.MapPost("/api/cortex/memories", (
            SaveMemoryRequest body, HttpContext ctx, CortexSaveQueue queue) =>
        {
            if (string.IsNullOrWhiteSpace(body.Content))
                return ResultsHelpers.Error(400, "cortex.content_required", "content is required");
            var scope = body.Scope;
            if (scope is not ("project" or "global"))
                return ResultsHelpers.Error(400, "cortex.bad_scope", "scope must be project|global");
            if (!ValidTopics.Contains(body.Topic))
                return ResultsHelpers.Error(400, "cortex.bad_topic",
                    "topic must be one of " + string.Join('|', ValidTopics));

            string? repository = null;
            if (scope == "project")
            {
                if (string.IsNullOrWhiteSpace(body.Repo))
                    return ResultsHelpers.Error(400, "cortex.repo_required", "scope=project requires repo");
                repository = CortexRepoMap.Resolve(body.Repo);
            }

            queue.Enqueue(new CortexSaveJob(
                body.Content, scope, body.Topic, repository, body.Importance, ctx.GetAuthToken()?.Id));
            return Results.Json(new { accepted = true }, Json.Default, statusCode: 202);
        });

        // DELETE /api/cortex/memories/{id} — forget a memory (instant).
        app.MapDelete("/api/cortex/memories/{id}", async (
            string id, HttpContext ctx, ICortexPlexusClient client,
            BridgeDbContext db, CancellationToken ct) =>
        {
            try
            {
                var forgotten = await client.ForgetMemoryAsync(id, ct);
                db.AuditLogs.Add(new AuditLog
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    ProjectId = "cortex",
                    Action = "cortex.forget",
                    TokenId = ctx.GetAuthToken()?.Id,
                    Result = forgotten ? "ok" : "not_found",
                    Detail = $"id={id}",
                });
                await db.SaveChangesAsync(ct);
                return Results.Json(new { forgotten, id }, Json.Default);
            }
            catch (CortexPlexusUnavailableException)
            {
                return ResultsHelpers.Error(503, "cortexplexus.unavailable", "CortexPlexus không phản hồi");
            }
        });
    }

    private static readonly HashSet<string> ValidTopics =
        new(StringComparer.Ordinal) { "preference", "pattern", "decision", "bug", "todo", "note" };

    public record SaveMemoryRequest(string Content, string Scope, string Topic, string? Repo, double? Importance);
}
