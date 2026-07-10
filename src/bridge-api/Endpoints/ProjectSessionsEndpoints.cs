using CortexBridge.Api.Auth;
using CortexBridge.Api.Common;
using CortexBridge.Api.Data;
using CortexBridge.Api.Sessions;
using System.Text.Json.Serialization;

namespace CortexBridge.Api.Endpoints;

public static class ProjectSessionsEndpoints
{
    public record SessionRowDto(
        [property: JsonPropertyName("sessionUuid")] string SessionUuid,
        [property: JsonPropertyName("firstMessageAt")] string? FirstMessageAt,
        [property: JsonPropertyName("lastMessageAt")] string? LastMessageAt,
        [property: JsonPropertyName("messageCount")] int MessageCount,
        [property: JsonPropertyName("firstUserText")] string? FirstUserText,
        [property: JsonPropertyName("cwd")] string? Cwd,
        [property: JsonPropertyName("isActive")] bool IsActive,
        [property: JsonPropertyName("isImported")] bool IsImported,
        [property: JsonPropertyName("canResume")] bool CanResume,
        [property: JsonPropertyName("sizeBytes")] long SizeBytes,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("note")] string? Note);

    public record SetLabelRequest(
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("note")] string? Note);

    public record ProjectSessionsResponse(
        [property: JsonPropertyName("projectId")] string ProjectId,
        [property: JsonPropertyName("activeSessionUuid")] string? ActiveSessionUuid,
        // ADR-015/017: surface owner ('tmux'|'pc'|'none') so the PWA can lock
        // destructive UI for PC-owned projects (the bridge cannot terminate
        // Anthropic ext; activate/new/delete must be hidden, not just refused).
        [property: JsonPropertyName("owner")] string Owner,
        [property: JsonPropertyName("sessions")] List<SessionRowDto> Sessions);

    public static void MapProjectSessions(this IEndpointRouteBuilder app)
    {
        // GET /api/projects/:projectId/sessions — spec 04
        app.MapGet("/api/projects/{projectId}/sessions", async (
            string projectId,
            SessionScanner scanner,
            SessionOwnershipRegistry ownership,
            CortexBridge.Api.Tmux.TmuxClient tmux,
            BridgeDbContext db,
            CancellationToken ct) =>
        {
            var sessions = await scanner.ListAllAsync(projectId, db, ct);
            var active = sessions.FirstOrDefault(s => s.IsActive);
            var (ownerEnum, _, _) = await ownership.ResolveAsync(projectId, db, tmux, scanner, ct);

            var dto = new ProjectSessionsResponse(
                ProjectId: projectId,
                ActiveSessionUuid: active?.SessionUuid,
                Owner: SessionOwnershipRegistry.ToWire(ownerEnum),
                Sessions: sessions.Select(s => new SessionRowDto(
                    SessionUuid: s.SessionUuid,
                    FirstMessageAt: s.FirstMessageAt?.ToString("o"),
                    LastMessageAt: s.LastMessageAt?.ToString("o"),
                    MessageCount: s.MessageCount,
                    FirstUserText: s.FirstUserText,
                    Cwd: s.Cwd,
                    IsActive: s.IsActive,
                    IsImported: s.IsImported,
                    CanResume: s.CanResume,
                    SizeBytes: s.SizeBytes,
                    Label: s.Label,
                    Note: s.Note)).ToList());

            return Results.Json(dto, Json.Default);
        });

        // DELETE /api/projects/:projectId/sessions/:sessionUuid — spec 04 §"Delete"
        // Permanently removes the JSONL file from disk + any label row. Refuses when
        // the target is the project's currently-active session (would orphan the
        // tmux window). Audit logged.
        app.MapDelete("/api/projects/{projectId}/sessions/{sessionUuid}", async (
            string projectId,
            string sessionUuid,
            HttpContext ctx,
            SessionScanner scanner,
            BridgeDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            // Reject path-traversal etc.
            if (string.IsNullOrEmpty(sessionUuid)
                || !System.Text.RegularExpressions.Regex.IsMatch(sessionUuid,
                    "^[A-Za-z0-9._-]{1,128}$"))
                return ResultsHelpers.Error(400, "delete.bad_uuid",
                    "sessionUuid must match [A-Za-z0-9._-]{1,128}");

            var active = await scanner.ResolveAsync(projectId, ct);
            if (active is null)
                return ResultsHelpers.Error(404, "delete.no_project",
                    $"Project '{projectId}' has no sessions");

            if (string.Equals(active.SessionUuid, sessionUuid, StringComparison.OrdinalIgnoreCase))
                return ResultsHelpers.Error(409, "delete.is_active",
                    "Cannot delete the currently-active session. Resume a different session first.");

            var jsonlPath = Path.Combine(active.EncodedCwdDir, $"{sessionUuid}.jsonl");
            // Path-traversal guard: jsonlPath must still resolve inside EncodedCwdDir
            var resolved = Path.GetFullPath(jsonlPath);
            var rootResolved = Path.GetFullPath(active.EncodedCwdDir);
            if (!resolved.StartsWith(rootResolved + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return ResultsHelpers.Error(400, "delete.escape", "Path escapes project folder");

            if (!File.Exists(jsonlPath))
                return ResultsHelpers.Error(404, "delete.not_found",
                    $"No JSONL for session '{sessionUuid}' in project '{projectId}'");

            try { File.Delete(jsonlPath); }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("DeleteSession")
                    .LogError(ex, "delete jsonl failed for {Project}/{Uuid}", projectId, sessionUuid);
                return ResultsHelpers.Error(500, "delete.io_error", ex.Message);
            }

            // Remove label row too (foreign-key-free — just delete by composite key)
            var labelRow = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstOrDefaultAsync(
                    db.SessionLabels.Where(x => x.ProjectId == projectId
                        && x.SessionUuid == sessionUuid), ct);
            if (labelRow is not null) db.SessionLabels.Remove(labelRow);

            var bearer = ctx.GetAuthToken();
            db.AuditLogs.Add(new Data.Entities.AuditLog
            {
                Timestamp = DateTimeOffset.UtcNow,
                ProjectId = projectId,
                SessionUuid = sessionUuid,
                Action = "delete-session",
                TokenId = bearer?.Id,
                Result = "ok",
                Detail = "jsonl removed",
            });
            await db.SaveChangesAsync(ct);

            return Results.Json(new { projectId, sessionUuid, deleted = true }, Json.Default);
        });

        // PUT /api/projects/:projectId/sessions/:sessionUuid/label — spec 04 §"UI labels"
        // Upsert label + note for a (project, session) pair. Bearer-protected.
        // Pass label=null to clear; the row is kept so a re-label preserves the
        // createdAt timestamp.
        app.MapPut("/api/projects/{projectId}/sessions/{sessionUuid}/label", async (
            string projectId,
            string sessionUuid,
            SetLabelRequest body,
            BridgeDbContext db,
            CancellationToken ct) =>
        {
            var label = (body.Label ?? "").Trim().ToLowerInvariant();
            if (label != "" && label != "shell" && label != "task")
                return ResultsHelpers.Error(400, "label.invalid_value",
                    "label must be 'shell', 'task', or null/empty");

            var existing = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstOrDefaultAsync(
                    db.SessionLabels.Where(x => x.ProjectId == projectId
                        && x.SessionUuid == sessionUuid), ct);

            var now = DateTimeOffset.UtcNow;
            if (existing is null)
            {
                db.SessionLabels.Add(new Data.Entities.SessionLabel
                {
                    ProjectId = projectId,
                    SessionUuid = sessionUuid,
                    Label = label == "" ? null : label,
                    Note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim(),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                existing.Label = label == "" ? null : label;
                existing.Note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
                existing.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);

            return Results.Json(new
            {
                projectId,
                sessionUuid,
                label = label == "" ? null : label,
                note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim(),
                updatedAt = now.ToString("o"),
            }, Json.Default);
        });
    }
}
