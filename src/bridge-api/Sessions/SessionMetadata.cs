namespace CortexBridge.Api.Sessions;

/// <summary>
/// Metadata row for a single JSONL in a project folder (spec 04).
/// Cheaper than parsing the full transcript — meant for session list views.
/// </summary>
public record SessionMetadata(
    string SessionUuid,
    string ProjectId,
    DateTimeOffset? FirstMessageAt,
    DateTimeOffset? LastMessageAt,
    int MessageCount,
    string? FirstUserText,
    string? Cwd,
    bool IsActive,
    bool IsImported,
    bool CanResume,
    long SizeBytes,
    /// <summary>"shell" | "task" | null (unlabeled). User-assigned per session via PUT label endpoint.</summary>
    string? Label = null,
    string? Note = null);
