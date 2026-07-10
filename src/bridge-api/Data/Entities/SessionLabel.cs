namespace CortexBridge.Api.Data.Entities;

/// <summary>
/// User-assigned label for a (project, session) pair. Spec 04 / ADR-014 — supports
/// the Hybrid Disciplined workflow by letting users tag a session as "shell"
/// (long-lived, owns daily evolution) or "task" (short-lived, single focus).
/// CC itself doesn't track this — bridge owns it in SQLite.
/// </summary>
public class SessionLabel
{
    public long Id { get; set; }
    public required string ProjectId { get; set; }
    public required string SessionUuid { get; set; }
    /// <summary>"shell" | "task" | null (unlabeled).</summary>
    public string? Label { get; set; }
    /// <summary>Free-text annotation, e.g. "Refactor: SessionScanner".</summary>
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
