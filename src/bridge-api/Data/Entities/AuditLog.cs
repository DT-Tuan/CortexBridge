namespace CortexBridge.Api.Data.Entities;

public class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? ProjectId { get; set; }
    public string? SessionUuid { get; set; }
    public required string Action { get; set; }
    public long? TokenId { get; set; }
    public string? PayloadHash { get; set; }
    public required string Result { get; set; }
    public string? Detail { get; set; }
}
