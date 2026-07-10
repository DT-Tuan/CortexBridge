using CortexBridge.Api.Data;
using CortexBridge.Api.Tmux;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CortexBridge.Api.Sessions;

/// <summary>
/// Enumerates ~/.claude/projects/ and resolves project_id -> active JSONL.
/// project_id is derived from the embedded `cwd` field of the JSONL records (basename).
/// Spec 03 §1.2 + spec 04 §"Backend" for multi-session listing.
/// </summary>
public class SessionScanner
{
    private readonly BridgePaths _paths;
    private readonly JsonlReader _jsonl;
    private readonly TmuxClient _tmux;
    private readonly ILogger<SessionScanner> _log;
    // Singleton → scoped DbContext via factory (same pattern as ModeWatcher).
    // ADR-016 Slice 1: read session_ownership.SessionUuid (the bridge's tracked
    // live-slot occupant) so the per-project pick is the LIVE session, not the
    // newest file mtime (the shadowing bug).
    private readonly IServiceScopeFactory _scopeFactory;

    public SessionScanner(BridgePaths paths, JsonlReader jsonl, TmuxClient tmux,
        IServiceScopeFactory scopeFactory, ILogger<SessionScanner> log)
    {
        _paths = paths;
        _jsonl = jsonl;
        _tmux = tmux;
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public record ActiveSession(
        string ProjectId,
        string? SessionUuid,
        string JsonlPath,
        DateTimeOffset LastModified,
        string EncodedCwdDir);

    /// <summary>
    /// Scans the projects root, returns one ActiveSession per project (newest .jsonl wins).
    /// </summary>
    public async Task<List<ActiveSession>> ScanAsync(CancellationToken ct)
    {
        var result = new List<ActiveSession>();
        if (!Directory.Exists(_paths.CcProjectsRoot))
        {
            _log.LogInformation("CC projects root {Path} does not exist yet", _paths.CcProjectsRoot);
            return result;
        }

        // ADR-016 Slice 1: one tiny read of the bridge's tracked slot occupants
        // (session_ownership has ~1 row/project). projectId → tracked SessionUuid.
        Dictionary<string, string?> ownerMap;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
            ownerMap = await db.SessionOwnerships
                .Select(x => new { x.ProjectId, x.SessionUuid })
                .ToDictionaryAsync(x => x.ProjectId, x => x.SessionUuid,
                    StringComparer.OrdinalIgnoreCase, ct);
        }
        catch (Exception ex)
        {
            // Never let an ownership-read hiccup break scanning — degrade to the
            // pre-ADR-016 mtime behaviour for this scan.
            _log.LogWarning(ex, "SessionScanner: session_ownership read failed; mtime fallback this scan");
            ownerMap = new(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var dir in Directory.EnumerateDirectories(_paths.CcProjectsRoot))
        {
            ct.ThrowIfCancellationRequested();
            var jsonlFiles = Directory.EnumerateFiles(dir, "*.jsonl").ToList();
            if (jsonlFiles.Count == 0) continue;

            // Newest-by-mtime is kept ONLY as the projectId reference (identical
            // to pre-ADR-016 projectId derivation) and as the rule-3 fallback.
            string? newestPath = null;
            DateTimeOffset newestTs = DateTimeOffset.MinValue;
            foreach (var f in jsonlFiles)
            {
                var ts = new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero);
                if (ts > newestTs) { newestTs = ts; newestPath = f; }
            }
            if (newestPath is null) continue;

            var projectId = await ReadProjectIdAsync(newestPath, ct)
                ?? FallbackProjectId(dir);
            var ownershipUuid = ownerMap.GetValueOrDefault(projectId);

            // Tier 1 (hot path): a tracked slot occupant whose file exists wins
            // outright — NO record reads (PickLiveSession rule 1).
            var uuidOnly = jsonlFiles
                .Select(f => new SessionOwnershipRegistry.SessionCandidate(
                    Path.GetFileNameWithoutExtension(f), null))
                .ToList();
            var pickedUuid = SessionOwnershipRegistry.PickLiveSession(uuidOnly, ownershipUuid);

            // Tier 2 (untracked / tracked-file-gone): the actively-written
            // session = newest LAST-RECORD timestamp (not file mtime). Only
            // here do we tail-read each candidate.
            if (pickedUuid is null)
            {
                var withTs = new List<SessionOwnershipRegistry.SessionCandidate>();
                foreach (var f in jsonlFiles)
                {
                    var (_, lastTs) = await ReadLastRecordMetaAsync(f, ct);
                    withTs.Add(new(Path.GetFileNameWithoutExtension(f), lastTs));
                }
                pickedUuid = SessionOwnershipRegistry.PickLiveSession(withTs, ownershipUuid);
            }

            // Rule 3: nothing decidable → pre-ADR-016 newest-mtime behaviour.
            var chosenPath = pickedUuid is null
                ? newestPath
                : jsonlFiles.FirstOrDefault(f =>
                      string.Equals(Path.GetFileNameWithoutExtension(f), pickedUuid,
                          StringComparison.OrdinalIgnoreCase))
                  ?? newestPath;

            result.Add(new ActiveSession(
                ProjectId: projectId,
                SessionUuid: Path.GetFileNameWithoutExtension(chosenPath),
                JsonlPath: chosenPath,
                LastModified: new DateTimeOffset(File.GetLastWriteTimeUtc(chosenPath), TimeSpan.Zero),
                EncodedCwdDir: dir));
        }

        return result;
    }

    public async Task<ActiveSession?> ResolveAsync(string projectId, CancellationToken ct)
    {
        var all = await ScanAsync(ct);
        return all.FirstOrDefault(s =>
            string.Equals(s.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Cheap tail-read of the active JSONL to learn WHO is driving the session:
    /// CC stamps every record with `entrypoint` ("cli" for a bridge/tmux
    /// `claude --resume`, "claude-vscode" for Anthropic's native VS Code
    /// extension on the PC) and a `timestamp`. The last record's values are the
    /// ground truth for ownership — far more accurate than "a tmux window with
    /// this name exists" (a stale/parked tmux claude can coexist with the PC one).
    /// Reads only the file tail (≤ 256 KB) so it stays O(1) on huge transcripts.
    /// </summary>
    public static async Task<(string? entrypoint, DateTimeOffset? ts)>
        ReadLastRecordMetaAsync(string jsonlPath, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(jsonlPath) || !File.Exists(jsonlPath))
                return (null, null);
            await using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite);
            var len = fs.Length;
            if (len == 0) return (null, null);
            const int Window = 256 * 1024;
            var take = (int)Math.Min(len, Window);
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            var read = await fs.ReadAsync(buf.AsMemory(0, take), ct);
            var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
            var lines = text.Split('\n');
            // Walk from the end; first line that parses with an `entrypoint`.
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string? ep = root.TryGetProperty("entrypoint", out var e)
                        && e.ValueKind == System.Text.Json.JsonValueKind.String
                        ? e.GetString() : null;
                    DateTimeOffset? ts = root.TryGetProperty("timestamp", out var t)
                        && t.ValueKind == System.Text.Json.JsonValueKind.String
                        && DateTimeOffset.TryParse(t.GetString(), out var parsed)
                        ? parsed : null;
                    if (ep is not null || ts is not null) return (ep, ts);
                }
                catch (System.Text.Json.JsonException) { /* partial/edge line — keep walking */ }
            }
            return (null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Spec 04: list every JSONL in the project's folder with cheap metadata.
    /// Determines isActive by matching against the tmux window + latest-by-mtime.
    /// Determines isImported by checking the JSONL's cwd field — if it doesn't look
    /// like a POSIX path under the workspace root (e.g. Windows path from PC migration),
    /// the session is marked imported and not resumable.
    /// </summary>
    public async Task<List<SessionMetadata>> ListAllAsync(
        string projectId,
        BridgeDbContext db,
        CancellationToken ct)
    {
        var active = await ResolveAsync(projectId, ct);
        if (active is null)
            return new List<SessionMetadata>();

        var projectFolder = active.EncodedCwdDir;
        var tmuxAlive = await _tmux.WindowExistsAsync(projectId, ct);

        // Pre-fetch labels for this project — single SQL round-trip
        var labels = await db.SessionLabels
            .Where(x => x.ProjectId == projectId)
            .ToDictionaryAsync(x => x.SessionUuid, x => x, ct);

        var results = new List<SessionMetadata>();
        foreach (var path in Directory.EnumerateFiles(projectFolder, "*.jsonl"))
        {
            ct.ThrowIfCancellationRequested();
            var uuid = Path.GetFileNameWithoutExtension(path);
            var (firstAt, lastAt, count, firstUserText, cwd) =
                await _jsonl.ExtractMetadataAsync(path, ct);

            // POSIX-style cwd on the host. Anything that isn't POSIX (no leading '/')
            // is considered imported from a foreign host (e.g. e:\\projects\\CortexBridge).
            var isImported = string.IsNullOrEmpty(cwd) || !cwd.StartsWith('/');
            var isActive = tmuxAlive
                && string.Equals(uuid, active.SessionUuid, StringComparison.OrdinalIgnoreCase);
            // canResume: stopped + native cwd + has at least one message
            var canResume = !isImported && !isActive && count > 0;

            labels.TryGetValue(uuid, out var lbl);

            var fi = new FileInfo(path);
            results.Add(new SessionMetadata(
                SessionUuid: uuid,
                ProjectId: projectId,
                FirstMessageAt: firstAt,
                LastMessageAt: lastAt,
                MessageCount: count,
                FirstUserText: firstUserText,
                Cwd: cwd,
                IsActive: isActive,
                IsImported: isImported,
                CanResume: canResume,
                SizeBytes: fi.Length,
                Label: lbl?.Label,
                Note: lbl?.Note));
        }

        return results
            .OrderByDescending(s => s.IsActive)
            .ThenByDescending(s => s.LastMessageAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    private static string FallbackProjectId(string encodedDir)
    {
        var name = Path.GetFileName(encodedDir);
        // Strip a leading hyphen that CC's encoding adds for absolute-path roots
        if (name.StartsWith('-')) name = name[1..];
        var parts = name.Split('-');
        return parts.Length > 0 ? parts[^1] : name;
    }

    private async Task<string?> ReadProjectIdAsync(string jsonlPath, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            for (var i = 0; i < 50; i++)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Length == 0) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("cwd", out var cwd) &&
                        cwd.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var path = cwd.GetString();
                        if (!string.IsNullOrEmpty(path)) return Path.GetFileName(path);
                    }
                }
                catch { /* skip bad line */ }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read projectId from {Path}", jsonlPath);
        }
        return null;
    }
}
