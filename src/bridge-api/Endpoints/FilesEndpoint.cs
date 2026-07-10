using CortexBridge.Api.Common;
using CortexBridge.Api.Sessions;

namespace CortexBridge.Api.Endpoints;

/// <summary>
/// GET /api/projects/{projectId}/files?q=&lt;query&gt;&amp;limit=&lt;n&gt;
///
/// Backs the PWA composer's "@" file-mention picker. Walks the project's
/// workspace dir, prunes the usual build/dep/cache dirs, filters by a
/// case-insensitive query, ranks filename matches above path matches, and
/// returns up to {limit} forward-slash relative paths. CC resolves an
/// "@&lt;relative-path&gt;" token in the submitted prompt natively (verified:
/// a pasted @mention triggers a file Read), so the picker only needs to
/// surface paths — no special injection.
///
/// Security: projectId must be a single dir name; the resolved path is
/// confined under WorkspaceRoot (same guard as NewSession/Projects). Files
/// only (no dirs). Hidden/build dirs pruned. Scan is capped so a pathological
/// tree can't stall the request.
/// </summary>
public static class FilesEndpoint
{
    // Heavy / regenerable dirs never worth surfacing in the picker. Mirrors the
    // source-migration exclude set. Matched by directory NAME at any depth.
    private static readonly HashSet<string> PrunedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "dist", "build", "out", "target",
        ".svelte-kit", ".next", ".nuxt", ".turbo", ".parcel-cache", ".angular",
        "__pycache__", ".vite", ".idea", ".vs", ".gradle", ".venv", "venv",
        ".cache", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox",
        ".terraform", ".pnpm-store",
    };

    private const int MaxScan = 20000;     // hard cap on entries visited
    private const int DefaultLimit = 40;
    private const int MaxLimit = 100;

    public static void MapFiles(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId}/files", (
            string projectId,
            string? q,
            int? limit,
            BridgePaths paths,
            CancellationToken ct) =>
        {
            if (projectId.Contains('/') || projectId.Contains('\\') || projectId.Contains(".."))
                return ResultsHelpers.Error(400, "project.bad_id",
                    "projectId must be a single directory name");

            var workspaceDir = Path.Combine(paths.WorkspaceRoot, projectId);
            var resolved = Path.GetFullPath(workspaceDir);
            var rootResolved = Path.GetFullPath(paths.WorkspaceRoot);
            if (!resolved.StartsWith(rootResolved + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && resolved != rootResolved)
                return ResultsHelpers.Error(400, "project.escape", "Path escapes workspace root");
            if (!Directory.Exists(workspaceDir))
                return ResultsHelpers.Error(404, "project.not_found",
                    $"Workspace directory '{projectId}' not found");

            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var query = (q ?? string.Empty).Trim();

            var files = WalkRelativeFiles(workspaceDir, ct);
            var ranked = Rank(files, query, take);
            return Results.Json(ranked, Json.Default);
        });
    }

    /// <summary>Stack-based walk that prunes <see cref="PrunedDirs"/> and caps total entries.</summary>
    public static List<string> WalkRelativeFiles(string root, CancellationToken ct)
    {
        var rels = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        var scanned = 0;

        while (stack.Count > 0 && scanned < MaxScan)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                if (scanned++ >= MaxScan) break;
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (PrunedDirs.Contains(name)) continue;
                    stack.Push(entry);
                }
                else
                {
                    rels.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
                }
            }
        }
        return rels;
    }

    /// <summary>
    /// Rank: filename starts-with query (0) &gt; filename contains (1) &gt; path
    /// contains (2). Empty query keeps original order. Ties broken by shorter
    /// path then ordinal. Non-matches dropped. Returns at most {take}.
    /// </summary>
    public static List<string> Rank(List<string> files, string query, int take)
    {
        if (query.Length == 0)
            return files
                .OrderBy(f => f.Count(c => c == '/'))   // shallow files first
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(take)
                .ToList();

        var scored = new List<(int score, string path)>();
        foreach (var f in files)
        {
            var fileName = f.AsSpan(f.LastIndexOf('/') + 1);
            int score;
            if (fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase)) score = 0;
            else if (fileName.Contains(query, StringComparison.OrdinalIgnoreCase)) score = 1;
            else if (f.Contains(query, StringComparison.OrdinalIgnoreCase)) score = 2;
            else continue;
            scored.Add((score, f));
        }

        return scored
            .OrderBy(s => s.score)
            .ThenBy(s => s.path.Length)
            .ThenBy(s => s.path, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(s => s.path)
            .ToList();
    }
}
