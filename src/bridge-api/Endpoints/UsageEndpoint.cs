using CortexBridge.Api.Common;
using CortexBridge.Api.Data;
using CortexBridge.Api.Usage;
using Microsoft.EntityFrameworkCore;

namespace CortexBridge.Api.Endpoints;

public static class UsageEndpoint
{
    public static void MapUsage(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/usage", (UsageService svc) =>
        {
            var dto = svc.GetCurrent();
            return dto is null
                ? ResultsHelpers.Error(503, "usage_unavailable",
                    "Usage data not yet sampled. Wait for the host timer to write /var/cortex-bridge/usage.json.")
                : Results.Json(dto, Json.Default);
        });

        // Sparkline source. Range = "24h" (default) / "7d". Returns up to N
        // recent UsagePoller snapshots in ascending taken_utc order.
        app.MapGet("/api/usage/history", async (
            string? range,
            BridgeDbContext db,
            CancellationToken ct) =>
        {
            var hours = range switch
            {
                "7d" => 24 * 7,
                "24h" or null or "" => 24,
                _ => 24
            };
            var since = DateTime.UtcNow.AddHours(-hours);
            var rows = await db.UsageSnapshots
                .Where(s => s.TakenUtc >= since)
                .OrderBy(s => s.TakenUtc)
                .ToListAsync(ct);

            var points = rows.Select(r => new UsageHistoryPoint(
                TakenUtc: r.TakenUtc.ToString("o"),
                Block5hCurrentUsd: r.Block5hCurrentUsd,
                Block5hProjectedUsd: r.Block5hProjectedUsd,
                Block5hPctCurrent: r.Block5hPctCurrent,
                Block5hPctProjected: r.Block5hPctProjected,
                Week7dCurrentUsd: r.Week7dCurrentUsd,
                Week7dPctCurrent: r.Week7dPctCurrent)).ToArray();
            return Results.Json(new UsageHistoryResponse(points), Json.Default);
        });
    }
}
