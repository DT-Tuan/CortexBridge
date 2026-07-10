using CortexBridge.Api.Common;
using CortexBridge.Api.Sessions;

namespace CortexBridge.Api.Endpoints;

public static class SyncPullEndpoint
{
    public static void MapSyncPull(this IEndpointRouteBuilder app)
    {
        // Spec 01: POST /api/sessions/:projectId/sync-pull — `git pull` in /workspace/:projectId
        app.MapPost("/api/sessions/{projectId}/sync-pull", async (
            string projectId,
            BridgePaths paths,
            GitInspector git,
            CancellationToken ct) =>
        {
            // Reject path traversal — projectId must be a single dir name, no slashes
            if (projectId.Contains('/') || projectId.Contains('\\') || projectId.Contains(".."))
                return ResultsHelpers.Error(400, "project.bad_id",
                    "projectId must be a single directory name");

            var dir = Path.Combine(paths.WorkspaceRoot, projectId);
            // Verify dir is actually inside WorkspaceRoot after Combine (defense-in-depth)
            var resolved = Path.GetFullPath(dir);
            var rootResolved = Path.GetFullPath(paths.WorkspaceRoot);
            if (!resolved.StartsWith(rootResolved + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && resolved != rootResolved)
            {
                return ResultsHelpers.Error(400, "project.escape",
                    "Resolved path escapes workspace root");
            }
            if (!Directory.Exists(dir))
                return ResultsHelpers.Error(404, "project.not_found",
                    $"Workspace directory '{projectId}' not found");
            if (!Directory.Exists(Path.Combine(dir, ".git")))
                return ResultsHelpers.Error(400, "project.not_git_repo",
                    $"'{projectId}' has no .git directory");

            try
            {
                var result = await git.PullAsync(dir, ct);
                if (result is null)
                    return ResultsHelpers.Error(500, "git.unknown_error", "git pull returned no result");
                var (commit, changedFiles) = result.Value;
                return Results.Json(new { commit, changedFiles }, Json.Default);
            }
            catch (Exception ex)
            {
                return ResultsHelpers.Error(500, "git.pull_failed",
                    ex.Message.Length > 200 ? ex.Message[..200] : ex.Message);
            }
        });
    }
}
