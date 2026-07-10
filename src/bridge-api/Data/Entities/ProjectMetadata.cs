namespace CortexBridge.Api.Data.Entities;

public class ProjectMetadata
{
    public required string ProjectId { get; set; }
    public required string TmuxWindow { get; set; }
    public bool Pinned { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}
