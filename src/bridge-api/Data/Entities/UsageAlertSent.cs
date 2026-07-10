namespace CortexBridge.Api.Data.Entities;

/// <summary>
/// Dedupe table for usage threshold-crossing Web Push alerts. One row per
/// (window_kind, window_id, threshold_pct) — prevents re-alerting at the same
/// threshold within the same 5h block / 7d week. A new block_id (fresh 5h
/// block) or a new week period naturally resets the dedup, no purge needed.
/// </summary>
public class UsageAlertSent
{
    public long Id { get; set; }
    /// <summary>"block5h" or "week7d".</summary>
    public string WindowKind { get; set; } = "";
    /// <summary>For block5h = block start UTC; for week7d = ISO week's Monday.</summary>
    public string WindowId { get; set; } = "";
    public int ThresholdPct { get; set; }
    public DateTime SentUtc { get; set; }
}
