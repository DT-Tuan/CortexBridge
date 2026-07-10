namespace CortexBridge.Api.Data.Entities;

public class PushSubscription
{
    public long Id { get; set; }
    /// <summary>Browser-supplied push service endpoint (unique per device+browser).</summary>
    public required string Endpoint { get; set; }
    /// <summary>Client public key (P-256, base64url no padding).</summary>
    public required string P256dh { get; set; }
    /// <summary>Auth secret (base64url no padding, 16 bytes).</summary>
    public required string Auth { get; set; }
    public long? BearerTokenId { get; set; }
    public string? DeviceLabel { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
