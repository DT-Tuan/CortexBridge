using CortexBridge.Api.Auth;
using CortexBridge.Api.Common;
using CortexBridge.Api.Data;
using CortexBridge.Api.Sessions;
using CortexBridge.Api.Tmux;
using System.Text.Json.Serialization;

namespace CortexBridge.Api.Endpoints;

public static class ResumeEndpoint
{
    public record ResumeRequest(
        [property: JsonPropertyName("sessionUuid")] string? SessionUuid);

    public record ResumeResponse(
        [property: JsonPropertyName("acceptedAt")] string AcceptedAt,
        [property: JsonPropertyName("activeSessionUuid")] string ActiveSessionUuid);

    public static void MapResume(this IEndpointRouteBuilder app)
    {
        // POST /api/sessions/:projectId/resume — spec 04 §"Endpoints"
        // Body: { sessionUuid: "<uuid>" }
        // Kills the current tmux window for the project (if any) and starts a new one
        // with `claude --resume <uuid>` on the host.
        app.MapPost("/api/sessions/{projectId}/resume", async (
            string projectId,
            HttpContext ctx,
            ResumeRequest body,
            SessionScanner scanner,
            TmuxClient tmux,
            ProjectResumeMutex mutex,
            BridgePaths paths,
            BridgeDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("Resume");
            var target = (body.SessionUuid ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(target)
                || !System.Text.RegularExpressions.Regex.IsMatch(target,
                    "^[A-Za-z0-9._-]{1,128}$"))
            {
                return ResultsHelpers.Error(400, "resume.bad_uuid",
                    "sessionUuid must match [A-Za-z0-9._-]{1,128}");
            }

            // Acquire mutex BEFORE any state changes. Returns 409 if another resume in flight.
            using var lease = mutex.TryAcquire(projectId);
            if (lease is null)
                return ResultsHelpers.Error(409, "resume.in_flight",
                    "Another resume is already running for this project");

            // Find the session metadata to validate isImported / canResume / messageCount.
            var sessions = await scanner.ListAllAsync(projectId, db, ct);
            var session = sessions.FirstOrDefault(s => string.Equals(s.SessionUuid, target,
                StringComparison.OrdinalIgnoreCase));
            if (session is null)
                return ResultsHelpers.Error(404, "resume.unknown_session",
                    $"No JSONL for session '{target}' in project '{projectId}'");

            if (session.IsImported)
                return ResultsHelpers.Error(409, "resume.imported_session",
                    "Imported (foreign cwd) sessions cannot be resumed — read-only by design");
            if (session.IsActive)
                return ResultsHelpers.Error(409, "resume.already_active",
                    "Selected session is already the active session");
            if (session.MessageCount == 0)
                return ResultsHelpers.Error(409, "resume.empty_session",
                    "Session has no records to resume from — start a new session instead");

            // Kill the existing tmux window for this project (best-effort) and start a new one
            // with claude --resume on the host. The bridge container's tmux CLIENT sends the
            // command through the bind-mounted socket (per ADR-013).
            await tmux.KillWindowAsync(projectId, ct);

            var workspaceDir = Path.Combine(paths.WorkspaceRoot, projectId);
            try
            {
                await tmux.NewWindowAsync(projectId, workspaceDir, $"claude --resume {target}", ct);
            }
            catch (TmuxException ex)
            {
                log.LogError(ex, "Resume tmux launch failed for {Project}/{Uuid}", projectId, target);
                return ResultsHelpers.Error(500, "tmux.send_failed",
                    $"Failed to start claude --resume: {ex.Message}");
            }

            // Audit
            var bearer = ctx.GetAuthToken();
            db.AuditLogs.Add(new Data.Entities.AuditLog
            {
                Timestamp = DateTimeOffset.UtcNow,
                ProjectId = projectId,
                SessionUuid = target,
                Action = "resume",
                TokenId = bearer?.Id,
                Result = "ok",
                Detail = target,
            });
            await db.SaveChangesAsync(ct);

            return Results.Json(new ResumeResponse(
                AcceptedAt: DateTimeOffset.UtcNow.ToString("o"),
                ActiveSessionUuid: target), Json.Default, statusCode: 202);
        });
    }
}
