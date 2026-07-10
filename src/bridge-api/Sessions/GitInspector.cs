using System.Diagnostics;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// Reads basic git status from a workspace directory by spawning `git`. argv-only,
/// stdout parsed minimally — best-effort, never throws on dirty trees or detached HEADs.
/// </summary>
public class GitInspector
{
    private readonly ILogger<GitInspector> _log;

    public GitInspector(ILogger<GitInspector> log) => _log = log;

    public record GitStatus(
        string? Branch,
        bool Dirty,
        int Ahead,
        int Behind);

    public async Task<GitStatus?> InspectAsync(string projectDir, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(projectDir, ".git"))) return null;

        try
        {
            var branch = (await RunGitAsync(projectDir, new[] { "rev-parse", "--abbrev-ref", "HEAD" }, ct))?.Trim();
            if (string.IsNullOrEmpty(branch)) branch = null;

            var statusOutput = await RunGitAsync(projectDir, new[] { "status", "--porcelain" }, ct);
            var dirty = !string.IsNullOrWhiteSpace(statusOutput);

            var (ahead, behind) = await ReadAheadBehindAsync(projectDir, ct);

            return new GitStatus(branch, dirty, ahead, behind);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "git inspect failed for {Dir}", projectDir);
            return new GitStatus(Branch: null, Dirty: false, Ahead: 0, Behind: 0);
        }
    }

    private async Task<(int ahead, int behind)> ReadAheadBehindAsync(string projectDir, CancellationToken ct)
    {
        // git rev-list --left-right --count HEAD...@{upstream}
        // Returns "<ahead>\t<behind>" or fails if no upstream
        var output = await RunGitAsync(projectDir,
            new[] { "rev-list", "--left-right", "--count", "HEAD...@{upstream}" },
            ct, ignoreExit: true);
        if (string.IsNullOrWhiteSpace(output)) return (0, 0);
        var parts = output.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return (0, 0);
        _ = int.TryParse(parts[0], out var ahead);
        _ = int.TryParse(parts[1], out var behind);
        return (ahead, behind);
    }

    public async Task<(string commit, int filesChanged)?> PullAsync(string projectDir, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(projectDir, ".git"))) return null;
        try
        {
            var beforeHead = (await RunGitAsync(projectDir, new[] { "rev-parse", "HEAD" }, ct))?.Trim();
            await RunGitAsync(projectDir, new[] { "pull", "--ff-only" }, ct);
            var afterHead = (await RunGitAsync(projectDir, new[] { "rev-parse", "HEAD" }, ct))?.Trim() ?? "";
            int changed = 0;
            if (!string.IsNullOrEmpty(beforeHead) && beforeHead != afterHead)
            {
                var diff = await RunGitAsync(projectDir,
                    new[] { "diff", "--name-only", $"{beforeHead}..{afterHead}" }, ct);
                if (!string.IsNullOrWhiteSpace(diff))
                    changed = diff.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            }
            return (afterHead, changed);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "git pull failed in {Dir}", projectDir);
            throw;
        }
    }

    private async Task<string?> RunGitAsync(string cwd, string[] args, CancellationToken ct, bool ignoreExit = false)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log.LogWarning(ex, "git binary not found");
            return null;
        }
        if (p is null) return null;
        using var _ = p;

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stdout = await stdoutTask;

        if (p.ExitCode != 0 && !ignoreExit)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} exit {p.ExitCode}: {await p.StandardError.ReadToEndAsync(ct)}");

        return stdout;
    }
}
