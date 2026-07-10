using CortexBridge.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CortexBridge.Api.Data;

public class BridgeDbContext : DbContext
{
    public BridgeDbContext(DbContextOptions<BridgeDbContext> options) : base(options) { }

    public DbSet<BearerToken> BearerTokens => Set<BearerToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ProjectMetadata> ProjectMetadata => Set<ProjectMetadata>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<SessionLabel> SessionLabels => Set<SessionLabel>();
    public DbSet<SessionOwnership> SessionOwnerships => Set<SessionOwnership>();
    public DbSet<UsageSnapshot> UsageSnapshots => Set<UsageSnapshot>();
    public DbSet<UsageAlertSent> UsageAlertsSent => Set<UsageAlertSent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BearerToken>(e =>
        {
            e.ToTable("bearer_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TokenHash).HasColumnName("token_hash").IsRequired();
            e.Property(x => x.DeviceName).HasColumnName("device_name");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.HasIndex(x => x.TokenHash).IsUnique();
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Timestamp).HasColumnName("ts").IsRequired();
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.SessionUuid).HasColumnName("session_uuid");
            e.Property(x => x.Action).HasColumnName("action").IsRequired();
            e.Property(x => x.TokenId).HasColumnName("token_id");
            e.Property(x => x.PayloadHash).HasColumnName("payload_hash");
            e.Property(x => x.Result).HasColumnName("result").IsRequired();
            e.Property(x => x.Detail).HasColumnName("detail");
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.ProjectId);
        });

        b.Entity<ProjectMetadata>(e =>
        {
            e.ToTable("project_metadata");
            e.HasKey(x => x.ProjectId);
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.TmuxWindow).HasColumnName("tmux_window").IsRequired();
            e.Property(x => x.Pinned).HasColumnName("pinned");
            e.Property(x => x.ArchivedAt).HasColumnName("archived_at");
        });

        b.Entity<PushSubscription>(e =>
        {
            e.ToTable("push_subscriptions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Endpoint).HasColumnName("endpoint").IsRequired();
            e.Property(x => x.P256dh).HasColumnName("p256dh").IsRequired();
            e.Property(x => x.Auth).HasColumnName("auth").IsRequired();
            e.Property(x => x.BearerTokenId).HasColumnName("bearer_token_id");
            e.Property(x => x.DeviceLabel).HasColumnName("device_label");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.HasIndex(x => x.Endpoint).IsUnique();
        });

        b.Entity<SessionLabel>(e =>
        {
            e.ToTable("session_labels");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
            e.Property(x => x.SessionUuid).HasColumnName("session_uuid").IsRequired();
            e.Property(x => x.Label).HasColumnName("label");
            e.Property(x => x.Note).HasColumnName("note");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
            e.HasIndex(x => new { x.ProjectId, x.SessionUuid }).IsUnique();
        });

        b.Entity<SessionOwnership>(e =>
        {
            e.ToTable("session_ownership");
            e.HasKey(x => x.ProjectId);
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.Owner).HasColumnName("owner").IsRequired();
            e.Property(x => x.SessionUuid).HasColumnName("session_uuid");
            e.Property(x => x.SinceUtc).HasColumnName("since_utc").IsRequired();
            e.Property(x => x.ChangedByClient).HasColumnName("changed_by_client");
        });

        b.Entity<UsageSnapshot>(e =>
        {
            e.ToTable("usage_snapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TakenUtc).HasColumnName("taken_utc").IsRequired();
            e.Property(x => x.Block5hId).HasColumnName("block5h_id");
            e.Property(x => x.Block5hCurrentUsd).HasColumnName("block5h_current_usd");
            e.Property(x => x.Block5hProjectedUsd).HasColumnName("block5h_projected_usd");
            e.Property(x => x.Block5hPctCurrent).HasColumnName("block5h_pct_current");
            e.Property(x => x.Block5hPctProjected).HasColumnName("block5h_pct_projected");
            e.Property(x => x.Week7dPeriod).HasColumnName("week7d_period");
            e.Property(x => x.Week7dCurrentUsd).HasColumnName("week7d_current_usd");
            e.Property(x => x.Week7dPctCurrent).HasColumnName("week7d_pct_current");
            e.HasIndex(x => x.TakenUtc);
        });

        b.Entity<UsageAlertSent>(e =>
        {
            e.ToTable("usage_alerts_sent");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.WindowKind).HasColumnName("window_kind").IsRequired();
            e.Property(x => x.WindowId).HasColumnName("window_id").IsRequired();
            e.Property(x => x.ThresholdPct).HasColumnName("threshold_pct").IsRequired();
            e.Property(x => x.SentUtc).HasColumnName("sent_utc").IsRequired();
            e.HasIndex(x => new { x.WindowKind, x.WindowId, x.ThresholdPct }).IsUnique();
        });
    }
}
