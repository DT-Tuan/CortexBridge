using CortexBridge.Api.Data;
using CortexBridge.Api.Hooks;
using CortexBridge.Api.Sessions;
using CortexBridge.Api.Tmux;
using Microsoft.EntityFrameworkCore;

namespace CortexBridge.Api.Endpoints;

/// <summary>
/// Phase 3.A — health subchecks enumerated per <c>src/bridge-api/CLAUDE.md</c>:
/// "tmux process alive, ~/.claude/projects/ readable, SQLite writable, disk
/// > 100 MB free." The endpoint is public (skip-listed by
/// <c>BearerTokenMiddleware</c>), so subcheck output is operational signal only
/// — no secrets, no project ids, no transcripts. Push reachability deliberately
/// stays a config flag (configured/missing) rather than an actual outbound
/// probe: hitting an external push service on every health poll is an
/// anti-pattern (slow, flaky, rate-limit risk).
/// </summary>
public static class HealthEndpoints
{
    public static void MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", async (
            BridgePaths paths,
            TmuxClient tmux,
            BridgeDbContext db,
            WebPushSender push,
            CancellationToken ct) =>
        {
            var projectsReadable = Directory.Exists(paths.CcProjectsRoot);
            var dataDirOk = Directory.Exists(paths.DataDir);

            long? freeBytes = null;
            try
            {
                if (dataDirOk)
                {
                    var drive = new DriveInfo(
                        Path.GetPathRoot(Path.GetFullPath(paths.DataDir)) ?? "/");
                    freeBytes = drive.AvailableFreeSpace;
                }
            }
            catch { /* best-effort */ }
            // 100 MB minimum per CLAUDE.md backend rule.
            var freeBytesOk = freeBytes is { } b && b >= 100L * 1024 * 1024;

            // tmux ping — ListWindowsAsync throws (or returns empty) when the
            // tmux server socket is dead. Cheap (one process spawn) and the
            // standard liveness check (`tmux list-windows`).
            var tmuxOk = false;
            try
            {
                await tmux.ListWindowsAsync(ct);
                tmuxOk = true;
            }
            catch { /* server down — leave tmuxOk=false */ }

            // SQLite writable — EF Core's CanConnectAsync opens the file (we
            // use WAL mode) so a corrupt/locked DB surfaces here. Cheap.
            var sqliteOk = false;
            try { sqliteOk = await db.Database.CanConnectAsync(ct); }
            catch { /* DB down */ }

            // VAPID configured? Doesn't degrade status (Web Push being off is a
            // deployment choice, not a failure) — surfaced for operator triage.
            var pushConfigured = push.IsEnabled;

            var ok = projectsReadable && dataDirOk && freeBytesOk && tmuxOk && sqliteOk;
            return Results.Ok(new
            {
                status = ok ? "ok" : "degraded",
                projects = projectsReadable ? "ok" : "missing",
                data = dataDirOk ? "ok" : "missing",
                sqlite = sqliteOk ? "ok" : "down",
                tmux = tmuxOk ? "ok" : "down",
                push = pushConfigured ? "configured" : "missing",
                freeBytes,
                freeBytesOk,
                ts = DateTimeOffset.UtcNow,
            });
        });
    }
}
