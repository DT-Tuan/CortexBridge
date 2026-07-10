namespace CortexBridge.Api.Data.Entities;

public class SessionOwnership
{
    public required string ProjectId { get; set; }
    public required string Owner { get; set; }
    public string? SessionUuid { get; set; }
    public DateTimeOffset SinceUtc { get; set; }
    public string? ChangedByClient { get; set; }
}
