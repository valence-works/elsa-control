using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;

/// <summary>
/// Durable one-time handoff consumption marker. The primary key is the signed
/// handoff token's JTI; a unique insert is the cross-process consume boundary.
/// </summary>
internal sealed class ManagedElsaHandoffReplayEntity
{
    public string Jti { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset ConsumedAt { get; set; }
}

/// <summary>
/// Safe metadata emitted by the managed handoff protocol. This intentionally has
/// no token, verifier, credential, claim payload or other bearer material.
/// </summary>
internal sealed class ManagedElsaHandoffAuditEventEntity
{
    public Guid Id { get; set; }
    public string Action { get; set; } = "";
    public string Jti { get; set; } = "";
    public Guid? AccountId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? InstanceId { get; set; }
    public string? Audience { get; set; }
    public int? BindingVersion { get; set; }
    public string? CorrelationId { get; set; }
    public Guid? KeyRingSnapshotId { get; set; }
    public Guid? KeyRingPreviousSnapshotId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class ManagedElsaHandoffReplayConfiguration : IEntityTypeConfiguration<ManagedElsaHandoffReplayEntity>
{
    public void Configure(EntityTypeBuilder<ManagedElsaHandoffReplayEntity> builder)
    {
        builder.ToTable("ManagedElsaHandoffReplayConsumptions");
        builder.HasKey(x => x.Jti);
        builder.Property(x => x.Jti).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ExpiresAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero))
            .IsRequired();
        builder.Property(x => x.ConsumedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero))
            .IsRequired();
        builder.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class ManagedElsaHandoffAuditEventConfiguration : IEntityTypeConfiguration<ManagedElsaHandoffAuditEventEntity>
{
    public void Configure(EntityTypeBuilder<ManagedElsaHandoffAuditEventEntity> builder)
    {
        builder.ToTable("ManagedElsaHandoffAuditEvents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Jti).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Audience).HasMaxLength(256);
        builder.Property(x => x.BindingVersion);
        builder.Property(x => x.CorrelationId).HasMaxLength(128);
        builder.Property(x => x.OccurredAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero))
            .IsRequired();
        builder.HasIndex(x => new { x.Jti, x.OccurredAt });
        builder.HasIndex(x => x.OccurredAt);
        builder.HasIndex(x => x.KeyRingSnapshotId);
        builder.HasIndex(x => x.KeyRingPreviousSnapshotId)
            .IsUnique()
            .HasFilter("KeyRingPreviousSnapshotId IS NOT NULL AND Action LIKE 'key_ring.active:%'")
            .HasDatabaseName("IX_ManagedElsaHandoffAuditEvents_KeyRingPreviousSnapshot");
    }
}
