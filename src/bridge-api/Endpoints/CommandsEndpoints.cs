using CortexBridge.Api.Common;
using CortexBridge.Api.Sessions;

namespace CortexBridge.Api.Endpoints;

public static class CommandsEndpoints
{
    public record CommandItem(string Name, string Kind, string? Description);
    public record CommandsResponse(List<CommandItem> Commands);

    public static void MapCommands(this IEndpointRouteBuilder app)
    {
        // GET /api/projects/:projectId/commands
        // Returns commands + skills the user can invoke via "/<name>" in CC.
        // Sources (deduped by name; project-level wins over user-level wins over builtin):
        //   - hardcoded common builtins
        //   - ~/.claude/skills/*/SKILL.md
        //   - <workspace>/<projectId>/.claude/skills/*/SKILL.md
        //   - ~/.claude/commands/*.md
        //   - <workspace>/<projectId>/.claude/commands/*.md
        app.MapGet("/api/projects/{projectId}/commands", Handler);
    }

    private static async Task<IResult> Handler(
        string projectId,
        BridgePaths paths,
        CancellationToken ct)
    {
        if (projectId.Contains('/') || projectId.Contains('\\') || projectId.Contains(".."))
            return ResultsHelpers.Error(400, "project.bad_id", "projectId must be a single directory name");

        // name → item; later sources overwrite earlier so project > workspace > user > builtin precedence works.
        var bag = new Dictionary<string, CommandItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var b in Builtins) bag[b.Name] = b;

        await ScanSkillsAsync(Path.Combine(paths.ClaudeUserDir, "skills"), "user-skill", bag, ct);
        await ScanCommandsAsync(Path.Combine(paths.ClaudeUserDir, "commands"), "user-command", bag, ct);

        // Workspace-shared (between user and project): /workspace/.claude/{skills,commands}/.
        // Bootstrap symlinks these into ~/.claude so CC discovers them; we tag them
        // explicitly here so the picker shows the right kind label.
        var workspaceClaudeDir = Path.Combine(paths.WorkspaceRoot, ".claude");
        await ScanSkillsAsync(Path.Combine(workspaceClaudeDir, "skills"), "workspace-skill", bag, ct);
        await ScanCommandsAsync(Path.Combine(workspaceClaudeDir, "commands"), "workspace-command", bag, ct);

        var projectClaudeDir = Path.Combine(paths.WorkspaceRoot, projectId, ".claude");
        await ScanSkillsAsync(Path.Combine(projectClaudeDir, "skills"), "project-skill", bag, ct);
        await ScanCommandsAsync(Path.Combine(projectClaudeDir, "commands"), "project-command", bag, ct);

        var list = bag.Values.OrderBy(c => KindOrder(c.Kind)).ThenBy(c => c.Name).ToList();
        return Results.Json(new CommandsResponse(list), Json.Default);
    }

    private static int KindOrder(string kind) => kind switch
    {
        "project-skill" => 0,
        "project-command" => 1,
        "workspace-skill" => 2,
        "workspace-command" => 3,
        "user-skill" => 4,
        "user-command" => 5,
        "builtin" => 6,
        _ => 9,
    };

    private static async Task ScanSkillsAsync(string root, string kind, Dictionary<string, CommandItem> bag, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            // SKILL.md is the canonical filename per CC convention; also accept skill.md as fallback.
            var skillFile = new[] { "SKILL.md", "skill.md" }
                .Select(f => Path.Combine(dir, f))
                .FirstOrDefault(File.Exists);
            string? desc = skillFile is not null ? await ReadFrontmatterDescriptionAsync(skillFile, ct) : null;
            bag[name] = new CommandItem(name, kind, desc);
        }
    }

    private static async Task ScanCommandsAsync(string root, string kind, Dictionary<string, CommandItem> bag, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*.md"))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(file);
            var desc = await ReadFrontmatterDescriptionAsync(file, ct);
            bag[name] = new CommandItem(name, kind, desc);
        }
    }

    private static async Task<string?> ReadFrontmatterDescriptionAsync(string file, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            var first = await reader.ReadLineAsync(ct);
            if (first?.Trim() != "---") return null;
            for (var i = 0; i < 30; i++)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Trim() == "---") return null;
                if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                {
                    var v = line["description:".Length..].Trim().Trim('"', '\'');
                    return v.Length > 200 ? v[..200] + "…" : v;
                }
            }
        }
        catch { /* unreadable / locked — skip */ }
        return null;
    }

    // Curated Claude Code built-in slash commands. This picker only PREFIXES the
    // composer (the user still reviews + taps Send), but it drives a REMOTE tmux
    // session the mobile user has no terminal for — so commands that kill the
    // monitored session or are purely local-terminal-interactive with zero remote
    // value are deliberately excluded: /exit /quit, /login /logout, /vim,
    // /terminal-setup, /ide, /bug. Everything here either takes effect headlessly
    // or prints something useful back into the transcript the PWA shows.
    private static readonly CommandItem[] Builtins =
    {
        new("help", "builtin", "Show help and the list of available commands"),
        new("context", "builtin", "Visualize current context-window usage"),
        new("compact", "builtin", "Summarize + compact the conversation to free context"),
        new("clear", "builtin", "Clear conversation history (destructive — wipes context)"),
        new("cost", "builtin", "Show token usage and cost for this session"),
        new("usage", "builtin", "Show plan usage limits and remaining quota"),
        new("status", "builtin", "Show account, model and connectivity status"),
        new("model", "builtin", "Switch the active model"),
        new("output-style", "builtin", "Change the response output style"),
        new("review", "builtin", "Review a pull request or pending changes"),
        new("pr-comments", "builtin", "Fetch and show pull-request comments"),
        new("init", "builtin", "Generate or refresh CLAUDE.md for this project"),
        new("memory", "builtin", "View and edit memory / CLAUDE.md files"),
        new("agents", "builtin", "Manage subagents"),
        new("mcp", "builtin", "Show / manage MCP server connections"),
        new("permissions", "builtin", "View and edit the tool-permission allowlist"),
        new("hooks", "builtin", "View and manage lifecycle hooks"),
        new("config", "builtin", "Open settings"),
        new("doctor", "builtin", "Diagnose the Claude Code installation"),
        new("export", "builtin", "Export the current conversation"),
        new("resume", "builtin", "Resume a previous conversation"),
        new("rewind", "builtin", "Rewind the conversation to an earlier checkpoint"),
        new("add-dir", "builtin", "Add an additional working directory"),
        new("release-notes", "builtin", "Show Claude Code release notes"),
    };
}
