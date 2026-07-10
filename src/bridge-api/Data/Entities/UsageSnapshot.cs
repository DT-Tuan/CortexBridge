namespace CortexBridge.Api.Data.Entities;

/// <summary>
/// One row per UsagePoller tick (5 min). Backs the /usage sparkline. Raw enough
/// to recompute % later if caps change. Body deliberately columnar, not JSON
/// blob — sparkline queries should not need a parse step.
/// </summary>
public class UsageSnapshot
{
    public long Id { get; set; }
    public DateTime TakenUtc { get; set; }

    /// <summary>
    /// ID of the 5h block (= startTime). Same id across consecutive snapshots
    /// of the same block. Empty when sampler couldn't read ccusage.
    /// </summary>
    public string Block5hId { get; set; } = "";
    public decimal Block5hCurrentUsd { get; set; }
    public decimal Block5hProjectedUsd { get; set; }
    public decimal Block5hPctCurrent { get; set; }
    public decimal Block5hPctProjected { get; set; }

    /// <summary>ISO week start (Monday, e.g. "2026-06-01"). Empty if missing.</summary>
    public string Week7dPeriod { get; set; } = "";
    public decimal Week7dCurrentUsd { get; set; }
    public decimal Week7dPctCurrent { get; set; }
}
