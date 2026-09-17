using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;

internal sealed class ExternalEngineEnrollmentChallengeEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ConnectionId { get; set; }
    public string Purpose { get; set; } = "";
    public string Audience { get; set; } = "";
    public string ChallengeHash { get; set; } = "";
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
}

internal sealed class ExternalEngineConnectorIdentityEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ConnectionId { get; set; }
    public string Audience { get; set; } = "";
    public string KeyAlgorithm { get; set; } = "";
    public int KeyVersion { get; set; }
    public string PublicKey { get; set; } = "";
    public string PublicKeyThumbprint { get; set; } = "";
    public DateTimeOffset EnrolledAt { get; set; }
}

internal sealed class ExternalEngineConnectorProofNonceEntity
{
    public Guid Id { get; set; }
    public Guid IdentityId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ConnectionId { get; set; }
    public int KeyVersion { get; set; }
    public string NonceHash { get; set; } = "";
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset ConsumedAt { get; set; }
    public ExternalEngineConnectorIdentityEntity Identity { get; set; } = null!;
}

/// <summary>
/// Value-free audit metadata. This entity intentionally has no challenge, nonce,
/// signature, authorization header, endpoint, request body, or exception field.
/// </summary>
internal sealed class ExternalEngineEnrollmentAuditEventEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid? ChallengeId { get; set; }
    public Guid? IdentityId { get; set; }
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class ExternalEngineEnrollmentChallengeConfiguration : IEntityTypeConfiguration<ExternalEngineEnrollmentChallengeEntity>
{
    public void Configure(EntityTypeBuilder<ExternalEngineEnrollmentChallengeEntity> builder)
    {
        builder.ToTable("ExternalEngineEnrollmentChallenges", table =>
            table.HasCheckConstraint(
                "CK_ExternalEngineEnrollmentChallenges_Lifetime",
                "ExpiresAt > IssuedAt"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Purpose).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Audience).HasMaxLength(512).IsRequired();
        builder.Property(x => x.ChallengeHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.IssuedAt).HasUtcTicksConversion();
        builder.Property(x => x.ExpiresAt).HasUtcTicksConversion();
        builder.Property(x => x.RedeemedAt).HasNullableUtcTicksConversion().IsConcurrencyToken();
        builder.HasIndex(x => new { x.OrganizationId, x.WorkspaceId, x.ConnectionId, x.Id }).IsUnique();
        builder.HasIndex(x => x.ExpiresAt);
        builder.HasOne<Organization>().WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Workspace>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ExternalEngineConnectorIdentityConfiguration : IEntityTypeConfiguration<ExternalEngineConnectorIdentityEntity>
{
    public void Configure(EntityTypeBuilder<ExternalEngineConnectorIdentityEntity> builder)
    {
        builder.ToTable("ExternalEngineConnectorIdentities", table =>
            table.HasCheckConstraint(
                "CK_ExternalEngineConnectorIdentities_KeyVersion",
                "KeyVersion > 0"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Audience).HasMaxLength(512).IsRequired();
        builder.Property(x => x.KeyAlgorithm).HasMaxLength(64).IsRequired();
        builder.Property(x => x.KeyVersion).IsRequired();
        builder.Property(x => x.PublicKey).HasMaxLength(128).IsRequired();
        builder.Property(x => x.PublicKeyThumbprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.EnrolledAt).HasUtcTicksConversion();
        builder.HasAlternateKey(x => new { x.OrganizationId, x.WorkspaceId, x.ConnectionId, x.Id });
        builder.HasIndex(x => new { x.OrganizationId, x.WorkspaceId, x.ConnectionId }).IsUnique();
        builder.HasIndex(x => new { x.OrganizationId, x.WorkspaceId, x.PublicKeyThumbprint }).IsUnique();
        builder.HasOne<Organization>().WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Workspace>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ExternalEngineConnectorProofNonceConfiguration : IEntityTypeConfiguration<ExternalEngineConnectorProofNonceEntity>
{
    public void Configure(EntityTypeBuilder<ExternalEngineConnectorProofNonceEntity> builder)
    {
        builder.ToTable("ExternalEngineConnectorProofNonces", table =>
        {
            table.HasCheckConstraint(
                "CK_ExternalEngineConnectorProofNonces_KeyVersion",
                "KeyVersion > 0");
            table.HasCheckConstraint(
                "CK_ExternalEngineConnectorProofNonces_Lifetime",
                "ExpiresAt > IssuedAt AND ConsumedAt >= IssuedAt AND ConsumedAt < ExpiresAt");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.NonceHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.IssuedAt).HasUtcTicksConversion();
        builder.Property(x => x.ExpiresAt).HasUtcTicksConversion();
        builder.Property(x => x.ConsumedAt).HasUtcTicksConversion();
        builder.HasIndex(x => new { x.OrganizationId, x.WorkspaceId, x.ConnectionId, x.IdentityId, x.KeyVersion, x.NonceHash }).IsUnique();
        builder.HasIndex(x => x.ExpiresAt);
        builder.HasOne(x => x.Identity).WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId, x.ConnectionId, x.IdentityId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.WorkspaceId, x.ConnectionId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ExternalEngineEnrollmentAuditEventConfiguration : IEntityTypeConfiguration<ExternalEngineEnrollmentAuditEventEntity>
{
    public void Configure(EntityTypeBuilder<ExternalEngineEnrollmentAuditEventEntity> builder)
    {
        builder.ToTable("ExternalEngineEnrollmentAuditEvents", table =>
        {
            table.HasCheckConstraint(
                "CK_ExternalEngineEnrollmentAuditEvents_Action",
                "Action IN ('ChallengeIssued', 'RedemptionSucceeded', 'RedemptionRejected', 'ProofNonceConsumed')");
            table.HasCheckConstraint(
                "CK_ExternalEngineEnrollmentAuditEvents_Reason",
                "Reason IN ('None', 'InvalidRequest', 'InvalidProof', 'Expired', 'Replay', 'AlreadyEnrolled')");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(64).IsRequired();
        builder.Property(x => x.OccurredAt).HasUtcTicksConversion();
        builder.HasIndex(x => new { x.OrganizationId, x.WorkspaceId, x.ConnectionId, x.OccurredAt });
        builder.HasIndex(x => x.ChallengeId);
        builder.HasIndex(x => x.IdentityId);
    }
}

internal static class ExternalEngineEnrollmentPropertyBuilderExtensions
{
    public static PropertyBuilder<DateTimeOffset> HasUtcTicksConversion(this PropertyBuilder<DateTimeOffset> builder) =>
        builder.HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero)).IsRequired();

    public static PropertyBuilder<DateTimeOffset?> HasNullableUtcTicksConversion(this PropertyBuilder<DateTimeOffset?> builder) =>
        builder.HasConversion(
            value => value.HasValue ? value.Value.UtcTicks : (long?)null,
            value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
}
