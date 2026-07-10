using CortexBridge.Api.Data;
using CortexBridge.Api.Data.Entities;

namespace CortexBridge.Api.Cortex;

/// <summary>
/// Drains <see cref="CortexSaveQueue"/> and performs each save_memory off the
/// request path (the MCP call is 50–70 s — embedding on the contended LXC).
/// Each job gets its own DI scope (for <see cref="ICortexPlexusClient"/> +
/// <see cref="BridgeDbContext"/>), audits the outcome (scope + topic only,
/// NEVER the content body), and logs. A failure is swallowed after audit — the
/// cockpit is best-effort; the user re-lists to confirm.
/// </summary>
public class CortexSaveWorker(
    CortexSaveQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<CortexSaveWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            string result = "ok";
            try
            {
                using var scope = scopeFactory.CreateScope();
                var client = scope.ServiceProvider.GetRequiredService<ICortexPlexusClient>();
                var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();

                var saved = await client.SaveMemoryAsync(
                    job.Content, job.Scope, job.Topic, job.Repository, job.Importance, stoppingToken);
                result = saved.Stored ? "ok" : "fail";

                await AuditAsync(db, job, result, stoppingToken);
                log.LogInformation("cortex.save done scope={Scope} topic={Topic} result={Result}",
                    job.Scope, job.Topic, result); // never log content
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "cortex.save failed scope={Scope} topic={Topic}", job.Scope, job.Topic);
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
                    await AuditAsync(db, job, "error", stoppingToken);
                }
                catch { /* best-effort audit */ }
            }
        }
    }

    private static async Task AuditAsync(BridgeDbContext db, CortexSaveJob job, string result, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTimeOffset.UtcNow,
            ProjectId = job.Repository ?? "global",
            Action = "cortex.save",
            TokenId = job.TokenId,
            Result = result,
            Detail = $"scope={job.Scope}, topic={job.Topic}", // never content
        });
        await db.SaveChangesAsync(ct);
    }
}
