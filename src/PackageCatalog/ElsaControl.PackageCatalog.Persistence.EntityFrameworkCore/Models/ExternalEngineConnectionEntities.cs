using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;

internal sealed class ExternalEngineConnectionEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string DisplayName { get; set; } = "";
    public string OwnershipMode { get; set; } = "";
    public string Status { get; set; } = "";
    public string RuntimeHealth { get; set; } = "";
    public string ConnectorReachability { get; set; } = "";
    public DateTimeOffset? LastAuthenticatedAt { get; set; }
    public string? ConnectorProtocol { get; set; }
    public string? ConnectorVersion { get; set; }
    public string? ObservedDistribution { get; set; }
    public string? ObservedVersion { get; set; }
    public string ReleaseEvidenceLevel { get; set; } = "";
    public string? StudioDestination { get; set; }
    public DateTimeOffset? CapabilitiesObservedAt { get; set; }
    public Guid? ActiveIdentityId { get; set; }
    public Guid? LastChallengeId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string CreateRequestDigest { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public int Version { get; set; }
    public List<ExternalEngineConnectionCapabilityEntity> Capabilities { get; set; } = [];
}

internal sealed class ExternalEngineConnectionCapabilityEntity
{
    public Guid ConnectionId { get; set; }
    public string Capability { get; set; } = "";
    public ExternalEngineConnectionEntity Connection { get; set; } = null!;
}

/// <summary>Value-free connection lifecycle metadata.</summary>
internal sealed class ExternalEngineConnectionAuditEventEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ConnectionId { get; set; }
    public string Action { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class ExternalEngineConnectionConfiguration : IEntityTypeConfiguration<ExternalEngineConnectionEntity>
{
    public void Configure(EntityTypeBuilder<ExternalEngineConnectionEntity> builder)
    {
        builder.ToTable("ExternalEngineConnections", table =>
        {
            table.HasCheckConstraint("CK_ExternalEngineConnections_OwnershipMode", "OwnershipMode = 'CustomerOperated'");
            table.HasCheckConstraint("CK_ExternalEngineConnections_Status", "Status IN ('Pending', 'Connected', 'Degraded', 'Revoked')");
            table.HasCheckConstraint("CK_ExternalEngineConnections_RuntimeHealth", "RuntimeHealth IN ('Unknown', 'Healthy', 'Unhealthy')");
            table.HasCheckConstraint("CK_ExternalEngineConnections_Reachability", "ConnectorReachability IN ('Unknown', 'Reachable', 'Unreachable')");
            table.HasCheckConstraint("CK_ExternalEngineConnections_Evidence", "ReleaseEvidenceLevel IN ('None', 'SelfReported', 'VerifiedManifest')");
            table.HasCheckConstraint("CK_ExternalEngineConnections_Version", "Version > 0");
            table.HasCheckConstraint("CK_ExternalEngineConnections_Timestamps", "UpdatedAt >= CreatedAt AND (RevokedAt IS NULL OR RevokedAt >= CreatedAt)");
            table.HasCheckConstraint("CK_ExternalEngineConnections_RevokedState", "(Status = 'Revoked' AND RevokedAt IS NOT NULL) OR (Status <> 'Revoked' AND RevokedAt IS NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.OwnershipMode).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.Property(x => x.RuntimeHealth).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ConnectorReachability).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ConnectorProtocol).HasMaxLength(64);
        builder.Property(x => x.ConnectorVersion).HasMaxLength(64);
        builder.Property(x => x.ObservedDistribution).HasMaxLength(128);
        builder.Property(x => x.ObservedVersion).HasMaxLength(128);
        builder.Property(x => x.ReleaseEvidenceLevel).HasMaxLength(32).IsRequired();
        builder.Property(x => x.StudioDestination).HasMaxLength(2048);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
        builder.Property(x => x.CreateRequestDigest).HasMaxLength(64).IsRequired();
        builder.Property(x => x.LastAuthenticatedAt).HasNullableUtcTicksConversion();
        builder.Property(x => x.CapabilitiesObservedAt).HasNullableUtcTicksConversion();
        builder.Property(x => x.CreatedAt).HasUtcTicksConversion();
        builder.Property(x => x.UpdatedAt).HasUtcTicksConversion();
        builder.Property(x => x.RevokedAt).HasNullableUtcTicksConversion();
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasAlternateKey(x => new { x.OrganizationId, x.WorkspaceId, x.Id });
        builder.HasIndex(x => new { x.OrganizationId, x.WorkspaceId, x.Id }).IsUnique();
        builder.HasIndex(x => new { x.OrganizationId, x.WorkspaceId, x.IdempotencyKey }).IsUnique();
        builder.HasOne<Organization>().WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Workspace>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ExternalEngineConnectionCapabilityConfiguration : IEntityTypeConfiguration<ExternalEngineConnectionCapabilityEntity>
{
    public void Configure(EntityTypeBuilder<ExternalEngineConnectionCapabilityEntity> builder)
    {
        builder.ToTable("ExternalEngineConnectionCapabilities");
        builder.HasKey(x => new { x.ConnectionId, x.Capability });
        builder.Property(x => x.Capability).HasMaxLength(128).IsRequired();
        builder.HasOne(x => x.Connection).WithMany(x => x.Capabilities)
            .HasForeignKey(x => x.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ExternalEngineConnectionAuditEventConfiguration : IEntityTypeConfiguration<ExternalEngineConnectionAuditEventEntity>
{
    public void Configure(EntityTypeBuilder<ExternalEngineConnectionAuditEventEntity> builder)
    {
        builder.ToTable("ExternalEngineConnectionAuditEvents", table =>
            table.HasCheckConstraint("CK_ExternalEngineConnectionAuditEvents_Action", "Action IN ('Created', 'PairingIssued', 'RepairStarted', 'IdentityEnrolled', 'Disconnected')"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasMaxLength(32).IsRequired();
        builder.Property(x => x.OccurredAt).HasUtcTicksConversion();
        builder.HasIndex(x => new { x.OrganizationId, x.WorkspaceId, x.ConnectionId, x.OccurredAt });
        builder.HasOne<ExternalEngineConnectionEntity>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId, x.ConnectionId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.WorkspaceId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
