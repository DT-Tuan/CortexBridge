using CortexBridge.Api.Common;
using CortexBridge.Api.Sessions;
using CortexBridge.Api.Tmux;

namespace CortexBridge.Api.Endpoints;

public static class ProjectsEndpoints
{
    public record ProjectListItem(
        string Name,
        string Path,
        string? Branch,
        bool Dirty,
        int Ahead,
        int Behind,
        bool HasGit,
        int SessionCount,
        string? ActiveSessionUuid,
        string? LastActivityAt);

    public record CreateProjectRequest(string Name, bool StartNow = true);

    public record CreateProjectResponse(
        string AcceptedAt,
        string ProjectId,
        string Path,
        bool Started,
        string? Window);

    public static void MapProjects(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects", async (
            BridgePaths paths,
            GitInspector git,
            SessionScanner scanner,
            CancellationToken ct) =>
        {
            if (!Directory.Exists(paths.WorkspaceRoot))
                return Results.Json(Array.Empty<ProjectListItem>(), Json.Default);

            // Pre-resolve active session map by scanning once (spec 04 — dashboard cards
            // show sessionCount + activeSessionUuid). Counting JSONLs per project is a
            // filesystem op done lazily per project below.
            var actives = await scanner.ScanAsync(ct);
            var activeByProject = actives.ToDictionary(
                a => a.ProjectId, a => a, StringComparer.OrdinalIgnoreCase);

            var items = new List<ProjectListItem>();
            foreach (var dir in Directory.EnumerateDirectories(paths.WorkspaceRoot))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;

                var status = await git.InspectAsync(dir, ct);

                int sessionCount = 0;
                string? activeUuid = null;
                string? lastActivity = null;
                if (activeByProject.TryGetValue(name, out var act))
                {
                    sessionCount = Directory.EnumerateFiles(act.EncodedCwdDir, "*.jsonl").Count();
                    activeUuid = act.SessionUuid;
                    lastActivity = act.LastModified.ToString("o");
                }

                items.Add(new ProjectListItem(
                    Name: name,
                    Path: dir,
                    Branch: status?.Branch,
                    Dirty: status?.Dirty ?? false,
                    Ahead: status?.Ahead ?? 0,
                    Behind: status?.Behind ?? 0,
                    HasGit: status is not null,
                    SessionCount: sessionCount,
                    ActiveSessionUuid: activeUuid,
                    LastActivityAt: lastActivity));
            }

            return Results.Json(items.OrderBy(i => i.Name).ToList(), Json.Default);
        });

        // POST /api/projects — create a new workspace dir and (optionally) spawn CC.
        // Body: { name: "claude-global", startNow: true }
        app.MapPost("/api/projects", async (
            CreateProjectRequest body,
            BridgePaths paths,
            TmuxClient tmux,
            CancellationToken ct) =>
        {
            var name = (body.Name ?? string.Empty).Trim();

            // Reject path traversal / awkward chars. Allow letters/digits/dot/dash/underscore.
            if (string.IsNullOrEmpty(name)
                || name.Contains('/') || name.Contains('\\') || name.Contains("..")
                || name.StartsWith('.')
                || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9._-]{1,64}$"))
            {
                return ResultsHelpers.Error(400, "project.bad_name",
                    "name must match [A-Za-z0-9._-]{1,64} and not start with '.'");
            }

            var workspaceDir = Path.Combine(paths.WorkspaceRoot, name);
            var resolved = Path.GetFullPath(workspaceDir);
            var rootResolved = Path.GetFullPath(paths.WorkspaceRoot);
            if (!resolved.StartsWith(rootResolved + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return ResultsHelpers.Error(400, "project.escape", "Path escapes workspace root");

            Directory.CreateDirectory(workspaceDir);

            bool started = false;
            if (body.StartNow)
            {
                if (!await tmux.WindowExistsAsync(name, ct))
                {
                    try
                    {
                        await tmux.NewWindowAsync(name, workspaceDir, "claude", ct);
                        started = true;
                    }
                    catch (TmuxException ex)
                    {
                        return ResultsHelpers.Error(500, "tmux.new_window_failed", ex.Message);
                    }
                }
                else
                {
                    started = true; // already running
                }
            }

            return Results.Json(new CreateProjectResponse(
                AcceptedAt: DateTimeOffset.UtcNow.ToString("o"),
                ProjectId: name,
                Path: workspaceDir,
                Started: started,
                Window: started ? name : null
            ), Json.Default, statusCode: 201);
        });
    }
}
