using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Core.Manifests;
using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.RuntimeBuilder.Abstractions.RuntimeConfigurations;
using ElsaControl.PackageCatalog.Core.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;

internal sealed class PackageSourceConfiguration : IEntityTypeConfiguration<PackageSource>
{
    private static readonly ValueComparer<List<string>> StringListComparer = new(
        (left, right) => ReferenceEquals(left, right) || (left != null && right != null && left.SequenceEqual(right)),
        value => value == null ? 0 : value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode(StringComparison.Ordinal))),
        value => value == null ? new List<string>() : value.ToList());

    public void Configure(EntityTypeBuilder<PackageSource> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Url).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.LastSyncError).HasMaxLength(2048);
        builder.Property(x => x.PollingInterval).HasMaxLength(64);
        builder.Property(x => x.Browseable).HasDefaultValue(true);
        builder.Property(x => x.Visibility).HasDefaultValue(PackageSourceVisibility.Public);
        builder.Property(x => x.VersionDiscoveryPolicy).HasDefaultValue(PackageSourceVersionDiscoveryPolicy.AllVersions);
        builder.Property(x => x.IncludePatterns)
            .HasConversion(value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null), value => JsonSerializer.Deserialize<List<string>>(value, (JsonSerializerOptions?)null) ?? new List<string>())
            .Metadata.SetValueComparer(StringListComparer);
        builder.Property(x => x.ExcludePatterns)
            .HasConversion(value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null), value => JsonSerializer.Deserialize<List<string>>(value, (JsonSerializerOptions?)null) ?? new List<string>())
            .Metadata.SetValueComparer(StringListComparer);
        builder.HasMany(x => x.Packages).WithOne(x => x.Source).HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.OwnerWorkspace).WithMany().HasForeignKey(x => x.OwnerWorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PackageConfiguration : IEntityTypeConfiguration<Package>
{
    public void Configure(EntityTypeBuilder<Package> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PackageId).HasMaxLength(256).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
        builder.HasIndex(x => new { x.SourceId, x.PackageId }).IsUnique();
        builder.HasMany(x => x.Versions).WithOne(x => x.Package).HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PackageVersionConfiguration : IEntityTypeConfiguration<PackageVersion>
{
    public void Configure(EntityTypeBuilder<PackageVersion> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ManifestJson).IsRequired();
        builder.Property(x => x.ManifestHash).HasMaxLength(128).IsRequired();
        builder.HasIndex(x => new { x.PackageId, x.Version }).IsUnique();
        builder.HasMany(x => x.Features).WithOne(x => x.PackageVersion).HasForeignKey(x => x.PackageVersionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class FeatureRecordConfiguration : IEntityTypeConfiguration<FeatureRecord>
{
    public void Configure(EntityTypeBuilder<FeatureRecord> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FeatureId).HasMaxLength(256).IsRequired();
        builder.Property(x => x.TypeName).HasMaxLength(512).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
        builder.HasIndex(x => new { x.PackageVersionId, x.FeatureId }).IsUnique();
        builder.HasMany(x => x.Settings).WithOne(x => x.FeatureRecord).HasForeignKey(x => x.FeatureRecordId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class FeatureSettingRecordConfiguration : IEntityTypeConfiguration<FeatureSettingRecord>
{
    public void Configure(EntityTypeBuilder<FeatureSettingRecord> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(256).IsRequired();
        builder.Property(x => x.JsonType).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => new { x.FeatureRecordId, x.Name }).IsUnique();
    }
}

internal sealed class ManifestValidationResultRecordConfiguration : IEntityTypeConfiguration<ManifestValidationResultRecord>
{
    public void Configure(EntityTypeBuilder<ManifestValidationResultRecord> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasOne(x => x.PackageVersion).WithMany().HasForeignKey(x => x.PackageVersionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ApprovalRecordConfiguration : IEntityTypeConfiguration<ApprovalRecord>
{
    public void Configure(EntityTypeBuilder<ApprovalRecord> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Actor).HasMaxLength(256).IsRequired();
    }
}

internal sealed class SyncRunConfiguration : IEntityTypeConfiguration<SyncRun>
{
    public void Configure(EntityTypeBuilder<SyncRun> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.StartedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.CompletedAt)
            .HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.HasMany(x => x.Items).WithOne(x => x.SyncRun).HasForeignKey(x => x.SyncRunId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SyncRunItemConfiguration : IEntityTypeConfiguration<SyncRunItem>
{
    public void Configure(EntityTypeBuilder<SyncRunItem> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasOne(x => x.PackageVersion).WithMany().HasForeignKey(x => x.PackageVersionId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.DisplayName).HasMaxLength(256);
        builder.Property(x => x.Email).HasMaxLength(320);
        builder.HasMany(x => x.ExternalIdentities).WithOne(x => x.Account).HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.OrganizationMemberships).WithOne(x => x.Account).HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Memberships).WithOne(x => x.Account).HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ExternalIdentityConfiguration : IEntityTypeConfiguration<ExternalIdentity>
{
    public void Configure(EntityTypeBuilder<ExternalIdentity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Issuer).HasMaxLength(512).IsRequired();
        builder.Property(x => x.Subject).HasMaxLength(512).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(256);
        builder.Property(x => x.Email).HasMaxLength(320);
        builder.HasIndex(x => new { x.Issuer, x.Subject }).IsUnique();
    }
}

internal sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.OrganizationId, x.Id });
        builder.Property(x => x.Name).HasMaxLength(256).IsRequired();
        builder.HasIndex(x => new { x.OrganizationId, x.SoftDeletedAt, x.Name });
        builder.HasOne(x => x.Organization).WithMany(x => x.Workspaces).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Memberships).WithOne(x => x.Workspace).HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.EntitlementSnapshots).WithOne(x => x.Workspace).HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.CustomerReference).HasMaxLength(512);
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.ArchivedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.HasMany(x => x.Memberships).WithOne(x => x.Organization).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.EntitlementSnapshots).WithOne(x => x.Organization).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.AuditRecords).WithOne(x => x.Organization).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OrganizationMembershipConfiguration : IEntityTypeConfiguration<OrganizationMembership>
{
    public void Configure(EntityTypeBuilder<OrganizationMembership> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Role).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.DisabledAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.HasIndex(x => new { x.OrganizationId, x.AccountId }).IsUnique();
    }
}

internal sealed class OrganizationEntitlementSnapshotConfiguration : IEntityTypeConfiguration<OrganizationEntitlementSnapshot>
{
    public void Configure(EntityTypeBuilder<OrganizationEntitlementSnapshot> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SyncedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.SubscriptionState).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.ManagedHostingExpiresAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.MaxInstances).IsRequired();
        builder.HasIndex(x => x.OrganizationId).IsUnique();
        builder.HasOne(x => x.Subscription)
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.SubscriptionId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OrganizationAuditRecordConfiguration : IEntityTypeConfiguration<OrganizationAuditRecord>
{
    public void Configure(EntityTypeBuilder<OrganizationAuditRecord> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.OperatorSubject).HasMaxLength(512);
        builder.Property(x => x.Action).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.TargetType).HasMaxLength(128).IsRequired();
        builder.Property(x => x.TargetId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Summary).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
    }
}

internal sealed class OrganizationSubscriptionConfiguration : IEntityTypeConfiguration<OrganizationSubscription>
{
    public void Configure(EntityTypeBuilder<OrganizationSubscription> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Provider).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ProviderCustomerReference).HasMaxLength(OrganizationBillingLimits.ProviderReferenceMaxLength);
        builder.Property(x => x.ProviderSubscriptionReference).HasMaxLength(OrganizationBillingLimits.ProviderReferenceMaxLength);
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.LastProviderEventId).HasMaxLength(256);
        builder.Property(x => x.TrialStartedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.TrialEndsAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.ActivatedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.PastDueAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.GraceEndsAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.ConstrainedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.SuspendedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.RetentionEndsAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.RetainedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.DeletedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.EarlyDeletionRequestedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.LastProviderEventOccurredAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => x.OrganizationId).IsUnique();
        builder.HasAlternateKey(x => new { x.OrganizationId, x.Id });
        builder.HasIndex(x => new { x.Provider, x.ProviderCustomerReference })
            .IsUnique()
            .HasFilter("ProviderCustomerReference IS NOT NULL");
        builder.HasIndex(x => new { x.Provider, x.ProviderSubscriptionReference })
            .IsUnique()
            .HasFilter("ProviderSubscriptionReference IS NOT NULL");
        builder.HasOne(x => x.Organization).WithMany(x => x.Subscriptions).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OrganizationBillingLifecycleNoticeConfiguration : IEntityTypeConfiguration<OrganizationBillingLifecycleNotice>
{
    public void Configure(EntityTypeBuilder<OrganizationBillingLifecycleNotice> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.DeliveryStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.DeliveredAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.LastFailureCode).HasMaxLength(128);
        builder.HasIndex(x => new { x.OrganizationId, x.SubscriptionId, x.Kind }).IsUnique();
        builder.HasOne(x => x.Organization).WithMany(x => x.BillingLifecycleNotices).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.Subscription).WithMany().HasForeignKey(x => new { x.OrganizationId, x.SubscriptionId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OrganizationBillingCleanupConfiguration : IEntityTypeConfiguration<OrganizationBillingCleanup>
{
    public void Configure(EntityTypeBuilder<OrganizationBillingCleanup> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CleanupKey).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Provider).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ProviderCustomerReference).HasMaxLength(OrganizationBillingLimits.ProviderReferenceMaxLength);
        builder.Property(x => x.ProviderSubscriptionReference).HasMaxLength(OrganizationBillingLimits.ProviderReferenceMaxLength);
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.LastFailureCode).HasMaxLength(128);
        builder.Property(x => x.RequestedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.NotBeforeAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.LastAttemptAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.CompletedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.LeaseExpiresAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.LeaseOwner).HasMaxLength(128);
        builder.Property(x => x.LeaseToken).HasMaxLength(128);
        builder.HasIndex(x => x.SubscriptionId).IsUnique();
        builder.HasIndex(x => new { x.State, x.NotBeforeAt, x.LeaseExpiresAt });
        builder.HasOne(x => x.Organization).WithMany(x => x.BillingCleanups).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.Subscription).WithMany().HasForeignKey(x => new { x.OrganizationId, x.SubscriptionId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class BillingProviderEventInboxEntryConfiguration : IEntityTypeConfiguration<BillingProviderEventInboxEntry>
{
    public void Configure(EntityTypeBuilder<BillingProviderEventInboxEntry> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Provider).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ProviderEventId).HasMaxLength(256).IsRequired();
        builder.Property(x => x.EventType).HasMaxLength(128).IsRequired();
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.EventHash).HasMaxLength(71).IsRequired();
        builder.Property(x => x.ProviderCustomerReference).HasMaxLength(OrganizationBillingLimits.ProviderReferenceMaxLength);
        builder.Property(x => x.ProviderSubscriptionReference).HasMaxLength(OrganizationBillingLimits.ProviderReferenceMaxLength);
        builder.Property(x => x.ProcessingStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.RejectionCode).HasMaxLength(128);
        builder.Property(x => x.OccurredAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.ReceivedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.ProcessedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.HasIndex(x => new { x.Provider, x.ProviderEventId }).IsUnique();
        builder.HasIndex(x => new { x.OrganizationId, x.OccurredAt });
        builder.HasOne(x => x.Organization).WithMany(x => x.BillingProviderEvents).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WorkspaceMembershipConfiguration : IEntityTypeConfiguration<WorkspaceMembership>
{
    public void Configure(EntityTypeBuilder<WorkspaceMembership> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.WorkspaceId, x.AccountId }).IsUnique();
    }
}

internal sealed class WorkspaceEntitlementSnapshotConfiguration : IEntityTypeConfiguration<WorkspaceEntitlementSnapshot>
{
    public void Configure(EntityTypeBuilder<WorkspaceEntitlementSnapshot> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SyncedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.CreatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => x.WorkspaceId).IsUnique();
    }
}

internal sealed class RuntimeConfigurationConfiguration : IEntityTypeConfiguration<RuntimeConfiguration>
{
    public void Configure(EntityTypeBuilder<RuntimeConfiguration> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1000);
        builder.Property(x => x.IntentJson).IsRequired();
        builder.Property(x => x.CreatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.SoftDeletedAt)
            .HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.HasIndex(x => new { x.WorkspaceId, x.SoftDeletedAt });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Versions).WithOne(x => x.RuntimeConfiguration).HasForeignKey(x => x.RuntimeConfigurationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RuntimeConfigurationVersionConfiguration : IEntityTypeConfiguration<RuntimeConfigurationVersion>
{
    public void Configure(EntityTypeBuilder<RuntimeConfigurationVersion> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1000);
        builder.Property(x => x.IntentJson).IsRequired();
        builder.Property(x => x.CreatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.RuntimeConfigurationId, x.VersionNumber }).IsUnique();
    }
}

internal sealed class DeploymentApplicationConfiguration : IEntityTypeConfiguration<DeploymentApplicationEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentApplicationEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1000);
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.WorkspaceId, x.Name }).IsUnique();
        // The alternate key is the principal for the environment ownership
        // relationship; an index alone would not let the database enforce it.
        builder.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DeploymentEnvironmentConfiguration : IEntityTypeConfiguration<DeploymentEnvironmentEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentEnvironmentEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Tier).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.TierRequiresReview).HasDefaultValue(false);
        builder.Property(x => x.DeploymentStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.DriftStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.WorkspaceId, x.ApplicationId, x.Name }).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.TierId });
        builder.HasIndex(x => x.ElsaInstanceId)
            .IsUnique()
            .HasFilter("ElsaInstanceId IS NOT NULL");
        builder.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        builder.HasOne(x => x.TierDefinition).WithMany(x => x.Environments).HasForeignKey(x => x.TierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Application)
            .WithMany(x => x.Environments)
            .HasForeignKey(x => new { x.WorkspaceId, x.ApplicationId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.ElsaInstance)
            .WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.ElsaInstanceId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id })
            // The workspace key is non-null, so SQL Server cannot implement a
            // composite SET NULL action. Unbinding is an explicit, authorized
            // operation; deletion never silently detaches a managed target.
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Engines).WithOne(x => x.Environment).HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Revisions).WithOne(x => x.Environment).HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.ObservabilityBindings).WithOne(x => x.Environment).HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.DriftReports).WithOne(x => x.Environment).HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DeploymentTierDefinitionConfiguration : IEntityTypeConfiguration<DeploymentTierDefinitionEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentTierDefinitionEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1000);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.ArchivedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.HasIndex(x => new { x.WorkspaceId, x.Status, x.Name }).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.SortOrder });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Capabilities).WithOne(x => x.Tier).HasForeignKey(x => x.TierId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Changes).WithOne(x => x.Tier).HasForeignKey(x => x.TierId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DeploymentTierCapabilityAssignmentConfiguration : IEntityTypeConfiguration<DeploymentTierCapabilityAssignmentEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentTierCapabilityAssignmentEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CapabilityId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.TierId, x.CapabilityId }).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.CapabilityId });
    }
}

internal sealed class DeploymentTierChangeRecordConfiguration : IEntityTypeConfiguration<DeploymentTierChangeRecordEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentTierChangeRecordEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ChangeType).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Summary).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.ChangedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.WorkspaceId, x.TierId, x.ChangedAt });
    }
}

internal sealed class WorkflowEngineConfiguration : IEntityTypeConfiguration<WorkflowEngineEntity>
{
    public void Configure(EntityTypeBuilder<WorkflowEngineEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.BaseUrl).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.Region).HasMaxLength(128);
        builder.Property(x => x.Version).HasMaxLength(128);
        builder.Property(x => x.CertificateStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.CredentialProvider).HasMaxLength(200).IsRequired();
        builder.Property(x => x.CredentialReference).HasMaxLength(1024).IsRequired();
        builder.Property(x => x.CredentialAssignmentStatus).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.CredentialVerificationStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.Health).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.VerificationMessage).HasMaxLength(1024).IsRequired();
        builder.Property(x => x.HostingProvider).HasMaxLength(200);
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.WorkspaceId, x.EnvironmentId, x.Name }).IsUnique();
        builder.HasMany(x => x.Capabilities).WithOne(x => x.Engine).HasForeignKey(x => x.EngineId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Controls).WithOne(x => x.Engine).HasForeignKey(x => x.EngineId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.CredentialReferenceMetadata).WithMany(x => x.Engines).HasForeignKey(x => x.CredentialReferenceId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => new { x.WorkspaceId, x.CredentialReferenceId });
    }
}

internal sealed class DeploymentSecretStoreConfiguration : IEntityTypeConfiguration<DeploymentSecretStoreEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentSecretStoreEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Provider).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(64).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1000);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.ArchivedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.HasIndex(x => new { x.WorkspaceId, x.Status, x.Name });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.CredentialReferences).WithOne(x => x.SecretStore).HasForeignKey(x => x.SecretStoreId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DeploymentCredentialReferenceConfiguration : IEntityTypeConfiguration<DeploymentCredentialReferenceEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentCredentialReferenceEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Reference).HasMaxLength(1024).IsRequired();
        builder.Property(x => x.ProtectedSecret).HasMaxLength(4096);
        builder.Property(x => x.ProtectedSecretUpdatedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.Description).HasMaxLength(1000);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.VerificationStatus).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.LastVerifiedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.ArchivedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.HasIndex(x => new { x.SecretStoreId, x.Status, x.Name });
        builder.HasIndex(x => new { x.WorkspaceId, x.Status });
    }
}

internal sealed class WorkspaceDeploymentArtifactConfiguration : IEntityTypeConfiguration<WorkspaceDeploymentArtifactEntity>
{
    public void Configure(EntityTypeBuilder<WorkspaceDeploymentArtifactEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ArtifactId).HasMaxLength(256).IsRequired();
        builder.Property(x => x.LayoutVersion).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ContentDigestAlgorithm).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ContentDigest).HasMaxLength(256).IsRequired();
        builder.Property(x => x.EnvelopeVersion).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ArtifactTypeId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ArtifactSchemaVersion).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ManifestDigestAlgorithm).HasMaxLength(32);
        builder.Property(x => x.ManifestDigest).HasMaxLength(256);
        builder.Property(x => x.PayloadReferenceJson).IsRequired();
        builder.Property(x => x.ProducerJson).IsRequired();
        builder.Property(x => x.DisplayMetadataJson).IsRequired();
        builder.Property(x => x.CompatibilityHintsJson).IsRequired();
        builder.Property(x => x.Format).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.ReferenceProvider).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Reference).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.ManifestName).HasMaxLength(256);
        builder.Property(x => x.ManifestVersion).HasMaxLength(128);
        builder.Property(x => x.ManifestEnvironment).HasMaxLength(128);
        builder.Property(x => x.ResourceSummaryJson).IsRequired();
        builder.Property(x => x.ChecksumStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.InspectionStatus).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.DiagnosticsJson).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.RegisteredAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.ArchivedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.WorkspaceId, x.ArtifactId }).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.Status, x.RegisteredAt });
        builder.HasIndex(x => new { x.WorkspaceId, x.RegisteredAt });
    }
}

internal sealed class WorkspaceArtifactUploadSessionConfiguration : IEntityTypeConfiguration<WorkspaceArtifactUploadSessionEntity>
{
    public void Configure(EntityTypeBuilder<WorkspaceArtifactUploadSessionEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.FileName).HasMaxLength(512);
        builder.Property(x => x.ContentType).HasMaxLength(128);
        builder.Property(x => x.StagedFilePath).HasMaxLength(2048);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(256);
        builder.Property(x => x.DiagnosticsJson).IsRequired();
        builder.Property(x => x.ExpiresAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.WorkspaceId, x.IdempotencyKey });
        builder.HasIndex(x => new { x.WorkspaceId, x.Status, x.ExpiresAt });
    }
}

internal sealed class EngineCapabilityConfiguration : IEntityTypeConfiguration<EngineCapabilityEntity>
{
    public void Configure(EntityTypeBuilder<EngineCapabilityEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CapabilityId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Label).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Boundary).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(x => new { x.EngineId, x.CapabilityId }).IsUnique();
    }
}

internal sealed class RuntimeControlConfiguration : IEntityTypeConfiguration<RuntimeControlEntity>
{
    public void Configure(EntityTypeBuilder<RuntimeControlEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ControlId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Label).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Boundary).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.RequiredCapabilityId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1000).IsRequired();
        builder.HasIndex(x => new { x.EngineId, x.ControlId }).IsUnique();
    }
}

internal sealed class RuntimeControlExecutionConfiguration : IEntityTypeConfiguration<RuntimeControlExecutionEntity>
{
    public void Configure(EntityTypeBuilder<RuntimeControlExecutionEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ControlId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ControlLabel).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Boundary).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.RequiredCapabilityId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.Message).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.WorkspaceId, x.EngineId, x.ControlId, x.CreatedAt });
        builder.HasOne(x => x.Engine).WithMany().HasForeignKey(x => x.EngineId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DesiredStateRevisionConfiguration : IEntityTypeConfiguration<DesiredStateRevisionEntity>
{
    public void Configure(EntityTypeBuilder<DesiredStateRevisionEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Label).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Commit).HasMaxLength(128);
        builder.Property(x => x.ContentHash).HasMaxLength(128).IsRequired();
        builder.Property(x => x.DesiredStateJson).IsRequired();
        builder.Property(x => x.AuthoredAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.EnvironmentId, x.RevisionNumber }).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.ContentHash });
        builder.HasMany(x => x.Records).WithOne(x => x.Revision).HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StructuredDesiredStateRecordConfiguration : IEntityTypeConfiguration<StructuredDesiredStateRecordEntity>
{
    public void Configure(EntityTypeBuilder<StructuredDesiredStateRecordEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.Name).HasMaxLength(256).IsRequired();
        builder.Property(x => x.PayloadJson).IsRequired();
        builder.Property(x => x.ContentHash).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ArtifactId).HasMaxLength(256);
        builder.Property(x => x.ArtifactTypeId).HasMaxLength(128);
        builder.Property(x => x.ArtifactDigestAlgorithm).HasMaxLength(32);
        builder.Property(x => x.ArtifactDigest).HasMaxLength(256);
        builder.HasIndex(x => new { x.RevisionId, x.Kind, x.Name }).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.ContentHash });
        builder.HasIndex(x => new { x.WorkspaceId, x.ArtifactRecordId });
        builder.HasIndex(x => new { x.WorkspaceId, x.ArtifactId });
    }
}

internal sealed class WorkspacePermissionGrantConfiguration : IEntityTypeConfiguration<WorkspacePermissionGrantEntity>
{
    public void Configure(EntityTypeBuilder<WorkspacePermissionGrantEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Permission).HasMaxLength(200).IsRequired();
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.RevokedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.HasIndex(x => new { x.WorkspaceId, x.AccountId, x.Permission, x.RevokedAt });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WorkspacePermissionAuditRecordConfiguration : IEntityTypeConfiguration<WorkspacePermissionAuditRecordEntity>
{
    public void Configure(EntityTypeBuilder<WorkspacePermissionAuditRecordEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Permission).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Action).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.OccurredAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.GrantId, x.Action }).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.OccurredAt, x.Id });
        builder.HasIndex(x => new { x.WorkspaceId, x.AccountId, x.OccurredAt });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ActionConfirmationConfiguration : IEntityTypeConfiguration<ActionConfirmationEntity>
{
    public void Configure(EntityTypeBuilder<ActionConfirmationEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ActionType).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.TargetId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ConfirmedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.ExpiresAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UsedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.HasIndex(x => new { x.WorkspaceId, x.ActionType, x.TargetId, x.ConfirmedByAccountId });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DeploymentRunConfiguration : IEntityTypeConfiguration<DeploymentRunEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentRunEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ElsaInstanceId);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.ValidationOutcome).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.QueuedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.StartedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.CompletedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.WorkerId).HasMaxLength(200);
        builder.Property(x => x.WorkerHeartbeatAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.RecoveryReason).HasMaxLength(1000);
        builder.Property(x => x.FailureMessage).HasMaxLength(2000);
        builder.HasIndex(x => new { x.WorkspaceId, x.EnvironmentId, x.Status });
        builder.HasIndex(x => new { x.WorkspaceId, x.EnvironmentId })
            .IsUnique()
            .HasFilter("Status IN ('Queued', 'Running', 'RecoveryRequired')");
        builder.HasOne(x => x.Environment).WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.EnvironmentId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.History).WithOne(x => x.Run).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DeploymentRunHistoryEventConfiguration : IEntityTypeConfiguration<DeploymentRunHistoryEventEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentRunHistoryEventEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.Message).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.WorkspaceId, x.RunId, x.CreatedAt });
    }
}

internal sealed class DeploymentCommandConfiguration : IEntityTypeConfiguration<DeploymentCommandEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentCommandEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.ArtifactJson).IsRequired();
        builder.Property(x => x.IdempotencyKey).HasMaxLength(512).IsRequired();
        builder.Property(x => x.WorkerId).HasMaxLength(256);
        builder.Property(x => x.LeaseToken).HasMaxLength(256);
        builder.Property(x => x.ProgressMessage).HasMaxLength(1000);
        builder.Property(x => x.ObservedArtifactDigestAlgorithm).HasMaxLength(32);
        builder.Property(x => x.ObservedArtifactDigest).HasMaxLength(256);
        builder.Property(x => x.RuntimeReference).HasMaxLength(1024);
        builder.Property(x => x.DiagnosticsJson).IsRequired();
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.AvailableAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.ExpiresAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.ClaimedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.LeaseExpiresAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.HeartbeatAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.CompletedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.HasIndex(x => new { x.WorkspaceId, x.EngineId, x.Status, x.AvailableAt });
        builder.HasIndex(x => new { x.WorkspaceId, x.IdempotencyKey }).IsUnique();
        builder.HasMany(x => x.Events).WithOne(x => x.Command).HasForeignKey(x => x.CommandId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Run).WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DeploymentCommandEventConfiguration : IEntityTypeConfiguration<DeploymentCommandEventEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentCommandEventEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.Message).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.WorkspaceId, x.CommandId, x.CreatedAt });
        builder.HasIndex(x => new { x.WorkspaceId, x.RunId, x.CreatedAt });
    }
}

internal sealed class AzureProviderOperationConfiguration : IEntityTypeConfiguration<AzureProviderOperationEntity>
{
    public void Configure(EntityTypeBuilder<AzureProviderOperationEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.Phase).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.AttemptedStep).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.Health).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.TargetKey).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OrganizationId);
        builder.Property(x => x.InstanceId);
        builder.Property(x => x.ProviderAssignmentId);
        builder.Property(x => x.LifecycleAction).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(512).IsRequired();
        builder.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.OperationIdentity).HasMaxLength(64).IsRequired();
        builder.Property(x => x.PlanFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TemplateFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ProviderScopeFingerprint).HasMaxLength(64);
        builder.Property(x => x.SqlWorkflowPackageVersion).HasMaxLength(128);
        builder.Property(x => x.SqlQuartzPackageVersion).HasMaxLength(128);
        builder.Property(x => x.ElsaVersion).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ReleaseLine).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Topology).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Isolation).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Location).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ImageRepository).HasMaxLength(512).IsRequired();
        builder.Property(x => x.ImageDigest).HasMaxLength(71).IsRequired();
        builder.Property(x => x.ReleaseManifestDigest).HasMaxLength(71);
        builder.Property(x => x.ReleaseManifestSignatureDigest).HasMaxLength(71);
        builder.Property(x => x.ReleaseManifestReference).HasMaxLength(2048);
        builder.Property(x => x.ReleaseManifestSignatureReference).HasMaxLength(2048);
        builder.Property(x => x.SecretReferencesJson).HasMaxLength(10000).IsRequired();
        builder.Property(x => x.ResourceGroupName).HasMaxLength(90);
        builder.Property(x => x.FoundationDeploymentId).HasMaxLength(512);
        builder.Property(x => x.WorkloadDeploymentId).HasMaxLength(512);
        builder.Property(x => x.WorkloadResourceId).HasMaxLength(1024);
        builder.Property(x => x.WorkloadRevisionName).HasMaxLength(128);
        builder.Property(x => x.StableTrafficRevisionName).HasMaxLength(128);
        builder.Property(x => x.WorkloadIdentityResourceId).HasMaxLength(1024);
        builder.Property(x => x.WorkloadIdentityClientId).HasMaxLength(128);
        builder.Property(x => x.WorkloadIdentityPrincipalId).HasMaxLength(128);
        builder.Property(x => x.KeyVaultResourceId).HasMaxLength(1024);
        builder.Property(x => x.KeyVaultUri).HasMaxLength(2048);
        builder.Property(x => x.SqlServerResourceId).HasMaxLength(1024);
        builder.Property(x => x.SqlServerFqdn).HasMaxLength(512);
        builder.Property(x => x.ContainerAppsEnvironmentResourceId).HasMaxLength(1024);
        builder.Property(x => x.RegistryResourceId).HasMaxLength(1024);
        builder.Property(x => x.AcrPullDeploymentId).HasMaxLength(512);
        builder.Property(x => x.AcrPullRoleAssignmentId).HasMaxLength(1024);
        builder.Property(x => x.Endpoint).HasMaxLength(2048);
        builder.Property(x => x.DiagnosticsJson).HasMaxLength(10000).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().ValueGeneratedNever();
        builder.Property(x => x.WorkerId).HasMaxLength(128);
        builder.Property(x => x.LeaseTokenHash).HasMaxLength(64);
        builder.Property(x => x.CompletionLeaseTokenHash).HasMaxLength(64);
        builder.Property(x => x.CompletionFingerprint).HasMaxLength(64);
        ConfigureDateTime(builder.Property(x => x.CreatedAt));
        ConfigureDateTime(builder.Property(x => x.UpdatedAt));
        ConfigureNullableDateTime(builder.Property(x => x.CompletedAt));
        ConfigureNullableDateTime(builder.Property(x => x.LeaseExpiresAt));
        ConfigureNullableDateTime(builder.Property(x => x.HeartbeatAt));
        builder.HasIndex(x => new { x.WorkspaceId, x.TargetKey, x.IdempotencyKey }).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.TargetKey, x.OperationIdentity })
            .IsUnique().HasFilter("Status IN ('Accepted', 'Queued', 'EntitlementHeld', 'Running', 'RecoveryRequired')");
        builder.HasIndex(x => new { x.WorkspaceId, x.TargetKey })
            .IsUnique().HasFilter("Status IN ('Accepted', 'Queued', 'EntitlementHeld', 'Running', 'RecoveryRequired')");
        builder.HasIndex(x => new { x.Status, x.LeaseExpiresAt, x.UpdatedAt, x.Id });
        builder.HasIndex(x => new { x.WorkspaceId, x.TargetKey, x.CreatedAt });
        builder.HasOne(x => x.Workspace).WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ProviderAssignment).WithMany(x => x.Operations).HasForeignKey(x => x.ProviderAssignmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Transitions).WithOne(x => x.Operation).HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureDateTime(PropertyBuilder<DateTimeOffset> property) =>
        property.HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));

    private static void ConfigureNullableDateTime(PropertyBuilder<DateTimeOffset?> property) =>
        property.HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null,
            value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
}

internal sealed class AzureProviderResourceAssignmentConfiguration : IEntityTypeConfiguration<AzureProviderResourceAssignmentEntity>
{
    public void Configure(EntityTypeBuilder<AzureProviderResourceAssignmentEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProviderScopeFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.NamingVersion).IsRequired();
        builder.Property(x => x.SubscriptionId).HasMaxLength(36).IsRequired();
        builder.Property(x => x.ResourceGroupName).HasMaxLength(90).IsRequired();
        builder.Property(x => x.WorkloadName).HasMaxLength(16).IsRequired();
        builder.Property(x => x.OwnershipKey).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Location).HasMaxLength(64).IsRequired();
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken().ValueGeneratedNever();
        builder.Property(x => x.FoundationDeploymentId).HasMaxLength(512);
        builder.Property(x => x.WorkloadDeploymentId).HasMaxLength(512);
        builder.Property(x => x.WorkloadResourceId).HasMaxLength(1024);
        builder.Property(x => x.WorkloadRevisionName).HasMaxLength(128);
        builder.Property(x => x.StableTrafficRevisionName).HasMaxLength(128);
        builder.Property(x => x.WorkloadIdentityResourceId).HasMaxLength(1024);
        builder.Property(x => x.WorkloadIdentityClientId).HasMaxLength(128);
        builder.Property(x => x.WorkloadIdentityPrincipalId).HasMaxLength(128);
        builder.Property(x => x.KeyVaultResourceId).HasMaxLength(1024);
        builder.Property(x => x.KeyVaultUri).HasMaxLength(2048);
        builder.Property(x => x.SqlServerResourceId).HasMaxLength(1024);
        builder.Property(x => x.SqlServerFqdn).HasMaxLength(512);
        builder.Property(x => x.ContainerAppsEnvironmentResourceId).HasMaxLength(1024);
        builder.Property(x => x.RegistryResourceId).HasMaxLength(1024);
        builder.Property(x => x.AcrPullDeploymentId).HasMaxLength(512);
        builder.Property(x => x.AcrPullRoleAssignmentId).HasMaxLength(1024);
        ConfigureDateTime(builder.Property(x => x.CreatedAt));
        ConfigureDateTime(builder.Property(x => x.UpdatedAt));
        ConfigureNullableDateTime(builder.Property(x => x.DeletedAt));
        builder.HasIndex(x => new { x.WorkspaceId, x.InstanceId, x.ProviderScopeFingerprint }).IsUnique();
        builder.HasIndex(x => new { x.State, x.UpdatedAt, x.Id });
        builder.HasOne(x => x.Workspace).WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Rebinds).WithOne(x => x.Assignment).HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureDateTime(PropertyBuilder<DateTimeOffset> property) =>
        property.HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));

    private static void ConfigureNullableDateTime(PropertyBuilder<DateTimeOffset?> property) =>
        property.HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null,
            value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
}

internal sealed class AzureProviderAssignmentRebindConfiguration : IEntityTypeConfiguration<AzureProviderAssignmentRebindEntity>
{
    public void Configure(EntityTypeBuilder<AzureProviderAssignmentRebindEntity> builder)
    {
        builder.ToTable("AzureProviderAssignmentRebinds");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FromProviderScopeFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ToProviderScopeFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TriggeredBy).HasMaxLength(64).IsRequired();
        builder.Property(x => x.OccurredAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.WorkspaceId, x.AssignmentId, x.OccurredAt });
        builder.HasIndex(x => new { x.AssignmentId, x.FromProviderScopeFingerprint, x.ToProviderScopeFingerprint });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AzureProviderOperationTransitionConfiguration : IEntityTypeConfiguration<AzureProviderOperationTransitionEntity>
{
    public void Configure(EntityTypeBuilder<AzureProviderOperationTransitionEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.Phase).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.Code).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Message).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.OccurredAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.OperationId, x.Sequence }).IsUnique();
        builder.HasIndex(x => new { x.OperationId, x.OccurredAt });
    }
}

internal sealed class AzureProviderRecoveryObservationConfiguration : IEntityTypeConfiguration<AzureProviderRecoveryObservationEntity>
{
    public void Configure(EntityTypeBuilder<AzureProviderRecoveryObservationEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.LifecycleAction).HasConversion<string>().HasMaxLength(64).IsRequired();
        builder.Property(x => x.ProviderOperationIdentity).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ProviderRequestHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TargetKey).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ProviderScopeFingerprint).HasMaxLength(64);
        builder.Property(x => x.ResolvedPlanId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ResolvedPlanUri).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.ResolvedPlanContentHash).HasMaxLength(71).IsRequired();
        builder.Property(x => x.ProviderPlanFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ProviderTemplateFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.CompletedStep).HasConversion<string>().HasMaxLength(64).IsRequired();
        builder.Property(x => x.ObservedPhase).HasConversion<string>().HasMaxLength(64).IsRequired();
        builder.Property(x => x.ObservedHealth).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.ResourceFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.PostconditionFingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.NaturalKey).HasMaxLength(64).IsRequired();
        builder.Property(x => x.RecordDigest).HasMaxLength(71).IsRequired();
        ConfigureDateTime(builder.Property(x => x.ObservedAt));
        ConfigureDateTime(builder.Property(x => x.CreatedAt));
        builder.HasIndex(x => new { x.WorkspaceId, x.NaturalKey }).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.LifecycleOperationId, x.ObservedAt });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AzureProviderOperationEntity>().WithMany().HasForeignKey(x => x.ProviderOperationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AzureProviderResourceAssignmentEntity>().WithMany().HasForeignKey(x => x.ProviderAssignmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ElsaInstanceOperationEntity>().WithMany().HasForeignKey(x => x.LifecycleOperationId).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureDateTime(PropertyBuilder<DateTimeOffset> property) =>
        property.HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
}

internal sealed class DeploymentCommandWebhookNotificationConfiguration : IEntityTypeConfiguration<DeploymentCommandWebhookNotificationEntity>
{
    public void Configure(EntityTypeBuilder<DeploymentCommandWebhookNotificationEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.SafePayloadJson).IsRequired();
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.SentAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.HasIndex(x => new { x.WorkspaceId, x.EngineId, x.CreatedAt });
        builder.HasIndex(x => x.CommandId);
    }
}

internal sealed class ObservabilityBindingConfiguration : IEntityTypeConfiguration<ObservabilityBindingEntity>
{
    public void Configure(EntityTypeBuilder<ObservabilityBindingEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.Provider).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.Scope).HasMaxLength(1024).IsRequired();
        builder.Property(x => x.Sample).HasMaxLength(2000);
        builder.HasIndex(x => new { x.WorkspaceId, x.EnvironmentId, x.Kind });
    }
}

internal sealed class DriftReportItemConfiguration : IEntityTypeConfiguration<DriftReportItemEntity>
{
    public void Configure(EntityTypeBuilder<DriftReportItemEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Area).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Desired).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.Observed).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.Action).HasConversion<string>().HasMaxLength(64);
        builder.Property(x => x.DetectedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.WorkspaceId, x.EnvironmentId, x.EngineId, x.DetectedAt });
    }
}

internal sealed class ElsaInstanceConfiguration : IEntityTypeConfiguration<ElsaInstanceEntity>
{
    public void Configure(EntityTypeBuilder<ElsaInstanceEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.OrganizationId, x.WorkspaceId, x.Id });
        builder.Property(x => x.Name).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Slug).HasMaxLength(128).IsRequired();
        builder.HasIndex(x => new { x.WorkspaceId, x.Slug })
            .IsUnique()
            .HasFilter("DeletedAt IS NULL");

        builder.Property(x => x.DistributionId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ReleaseLine).HasMaxLength(128).IsRequired();
        builder.Property(x => x.RequestedVersion).HasMaxLength(128);
        builder.Property(x => x.Channel).HasMaxLength(128).IsRequired();
        builder.Property(x => x.PatchUpdates).HasMaxLength(128).IsRequired();
        builder.Property(x => x.MinorUpdates).HasMaxLength(128).IsRequired();
        builder.Property(x => x.MajorMigrations).HasMaxLength(128).IsRequired();
        builder.Property(x => x.PreviewManifestDigest).HasMaxLength(71);
        builder.Property(x => x.TopologyId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.FeaturePresetId).HasMaxLength(128);
        builder.Property(x => x.FeatureOverridesJson).HasMaxLength(32768).IsRequired();
        builder.Property(x => x.PackagePolicy).HasMaxLength(128);
        builder.Property(x => x.ConfigurationShapeRevisionId).HasMaxLength(128);
        builder.Property(x => x.TargetMode).HasMaxLength(128).IsRequired();
        builder.Property(x => x.RegionCode).HasMaxLength(128).IsRequired();
        builder.Property(x => x.IsolationProfile).HasMaxLength(128).IsRequired();
        builder.Property(x => x.CapacityProfile).HasMaxLength(128).IsRequired();
        builder.Property(x => x.NetworkOutcome).HasMaxLength(128).IsRequired();
        builder.Property(x => x.DomainOutcome).HasMaxLength(128).IsRequired();
        builder.Property(x => x.DesiredLifecycle).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.ObservedLifecycle).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.Health).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.DesiredStateRevisionId).HasMaxLength(128);
        builder.Property(x => x.ResolvedPlanId).HasMaxLength(128);
        builder.Property(x => x.ResolvedPlanContentHash).HasMaxLength(71);
        builder.Property(x => x.ResolvedPlanUri).HasMaxLength(2048);
        builder.Property(x => x.CurrentReleaseDistributionId).HasMaxLength(128);
        builder.Property(x => x.CurrentReleaseLine).HasMaxLength(128);
        builder.Property(x => x.CurrentReleaseVersion).HasMaxLength(128);
        builder.Property(x => x.CurrentReleaseManifestDigest).HasMaxLength(71);
        builder.Property(x => x.CurrentReleaseComponentDigestsJson).HasMaxLength(65536);
        builder.Property(x => x.CurrentDeploymentId).HasMaxLength(128);
        builder.Property(x => x.CurrentDeploymentRevisionId).HasMaxLength(128);
        builder.Property(x => x.CurrentDeploymentEndpointUri).HasMaxLength(2048);
        builder.Property(x => x.PlacementAssignmentId).HasMaxLength(128);
        builder.Property(x => x.ElsaTenantId).HasMaxLength(128);
        builder.Property(x => x.ElsaTenantAudience).HasMaxLength(256);
        builder.Property(x => x.LastOperationId).HasMaxLength(128);
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.DeletedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);

        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.IdentityBinding).WithOne(x => x.Instance).HasForeignKey<ElsaInstanceIdentityBindingEntity>(x => x.InstanceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Operations).WithOne(x => x.Instance)
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId, x.InstanceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.WorkspaceId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.AuditEvents).WithOne(x => x.Instance)
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId, x.InstanceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.WorkspaceId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Migrations).WithOne(x => x.Instance)
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId, x.InstanceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.WorkspaceId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ElsaInstanceOperationConfiguration : IEntityTypeConfiguration<ElsaInstanceOperationEntity>
{
    public void Configure(EntityTypeBuilder<ElsaInstanceOperationEntity> builder)
    {
        builder.ToTable("ElsaInstanceOperations", t =>
        {
            t.HasCheckConstraint("CK_ElsaInstanceOperations_NullInstanceOnlyCreate", "InstanceId IS NOT NULL OR Action = 'Create'");
            t.HasCheckConstraint("CK_ElsaInstanceOperations_LeaseVersion_Range", "LeaseVersion >= 0 AND LeaseVersion < 2147483647");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasConversion<string>().HasMaxLength(64).IsRequired();
        builder.Property(x => x.IdempotencyScope).HasMaxLength(256).IsRequired();
        builder.Property(x => x.RecoveryIdempotencyScope).HasMaxLength(256);
        builder.Property(x => x.RecoveryIdempotencyKey).HasMaxLength(128);
        builder.Property(x => x.RecoveryRequestHash).HasMaxLength(64);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
        builder.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(64).IsRequired();
        builder.Property(x => x.AcceptedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.StartedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.CompletedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.WorkerId).HasMaxLength(256);
        builder.Property(x => x.LeaseTokenHash).HasMaxLength(64);
        builder.Property(x => x.LeaseExpiresAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.HeartbeatAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.DesiredStateRevisionId).HasMaxLength(128);
        builder.Property(x => x.ResolvedPlanId).HasMaxLength(128);
        builder.Property(x => x.FailureCode).HasMaxLength(128);
        // Kept as a compatibility-shaped projection for the future worker seam,
        // but CatalogDbContext collapses it to a stable safe code at write time.
        builder.Property(x => x.FailureSummary).HasMaxLength(128);
        builder.Property(x => x.ReconciliationEvidenceFingerprint).HasMaxLength(64);
        builder.Property(x => x.ReconciliationDiagnosticCode).HasMaxLength(128);
        builder.Property(x => x.ReconciliationRetryEvidenceReference).HasMaxLength(2048);
        builder.Property(x => x.ReconciliationRetryEvidenceDigest).HasMaxLength(71);
        builder.Property(x => x.ReconciledObservedLifecycle).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.ReconciledHealth).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.ReconciledAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.DeletionEvidenceFingerprint).HasMaxLength(64);
        builder.Property(x => x.DeletionEvidenceReference).HasMaxLength(2048);
        builder.Property(x => x.DeletionEvidenceDigest).HasMaxLength(71);
        builder.Property(x => x.DeletionDiagnosticCode).HasMaxLength(128);
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.WorkspaceId, x.IdempotencyScope, x.IdempotencyKey }).IsUnique();
        builder.HasIndex(x => x.InstanceId)
            .HasDatabaseName("IX_ElsaInstanceOperations_ActiveInstanceId")
            .IsUnique()
            .HasFilter("InstanceId IS NOT NULL AND State IN ('Accepted', 'Queued', 'Running', 'RecoveryRequired')");
        // EF Core merges two indexes with the same property list. Including the
        // state column gives the waiting successor its own model index while the
        // filtered uniqueness still enforces one waiting successor per instance.
        builder.HasIndex(x => new { x.InstanceId, x.State })
            .HasDatabaseName("IX_ElsaInstanceOperations_WaitingInstanceId")
            .IsUnique()
            .HasFilter("InstanceId IS NOT NULL AND State = 'WaitingForPriorOperation'");
        builder.HasIndex(x => new { x.WorkspaceId, x.State, x.AcceptedAt });
        builder.HasOne<Workspace>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ElsaInstanceRecoveryRequestConfiguration : IEntityTypeConfiguration<ElsaInstanceRecoveryRequestEntity>
{
    public void Configure(EntityTypeBuilder<ElsaInstanceRecoveryRequestEntity> builder)
    {
        builder.ToTable("ElsaInstanceRecoveryRequests");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.IdempotencyScope).HasMaxLength(256).IsRequired();
        builder.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
        builder.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.RecoveryObservationReference).HasMaxLength(2048);
        builder.Property(x => x.RecoveryObservationDigest).HasMaxLength(71);
        builder.Property(x => x.AzureDeleteRecoveryAuthority).HasMaxLength(4096);
        builder.Property(x => x.AcceptedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.WorkspaceId, x.IdempotencyScope, x.IdempotencyKey }).IsUnique();
        builder.HasIndex(x => new { x.OperationId, x.AttemptNumber }).IsUnique();
        builder.HasOne(x => x.Operation).WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Workspace>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ElsaInstanceIntentRevisionConfiguration : IEntityTypeConfiguration<ElsaInstanceIntentRevisionEntity>
{
    public void Configure(EntityTypeBuilder<ElsaInstanceIntentRevisionEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.RevisionNumber).IsRequired();
        builder.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();

        builder.Property(x => x.DistributionId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ReleaseLine).HasMaxLength(128).IsRequired();
        builder.Property(x => x.RequestedVersion).HasMaxLength(128);
        builder.Property(x => x.Channel).HasMaxLength(128).IsRequired();
        builder.Property(x => x.PatchUpdates).HasMaxLength(128).IsRequired();
        builder.Property(x => x.MinorUpdates).HasMaxLength(128).IsRequired();
        builder.Property(x => x.MajorMigrations).HasMaxLength(128).IsRequired();
        builder.Property(x => x.PreviewManifestDigest).HasMaxLength(71);
        builder.Property(x => x.TopologyId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.FeaturePresetId).HasMaxLength(128);
        builder.Property(x => x.FeatureOverridesJson).HasMaxLength(32768).IsRequired();
        builder.Property(x => x.PackagePolicy).HasMaxLength(128);
        builder.Property(x => x.ConfigurationShapeRevisionId).HasMaxLength(128);
        builder.Property(x => x.TargetMode).HasMaxLength(128).IsRequired();
        builder.Property(x => x.RegionCode).HasMaxLength(128).IsRequired();
        builder.Property(x => x.IsolationProfile).HasMaxLength(128).IsRequired();
        builder.Property(x => x.CapacityProfile).HasMaxLength(128).IsRequired();
        builder.Property(x => x.NetworkOutcome).HasMaxLength(128).IsRequired();
        builder.Property(x => x.DomainOutcome).HasMaxLength(128).IsRequired();
        builder.Property(x => x.DesiredLifecycle).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.AuthoredAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));

        builder.HasIndex(x => new { x.InstanceId, x.RevisionNumber }).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.ContentHash });
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Workspace>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Instance).WithMany(x => x.IntentRevisions)
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId, x.InstanceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.WorkspaceId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ElsaInstanceResolvedPlanConfiguration : IEntityTypeConfiguration<ElsaInstanceResolvedPlanEntity>
{
    public void Configure(EntityTypeBuilder<ElsaInstanceResolvedPlanEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PlanId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.SchemaVersion).IsRequired();
        builder.Property(x => x.ContentHash).HasMaxLength(71).IsRequired();
        builder.Property(x => x.PlanUri).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.SerializedPlan).HasMaxLength(1_048_576).IsRequired();
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.WorkspaceId, x.InstanceId, x.PlanId }).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.ContentHash });
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Workspace>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Instance).WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId, x.InstanceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.WorkspaceId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ElsaInstanceLifecycleOutboxConfiguration : IEntityTypeConfiguration<ElsaInstanceLifecycleOutboxEntity>
{
    public void Configure(EntityTypeBuilder<ElsaInstanceLifecycleOutboxEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasConversion<string>().HasMaxLength(64).IsRequired();
        builder.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.QuarantinedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.QuarantineCode).HasMaxLength(64);

        builder.HasIndex(x => x.OperationId).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
        builder.HasIndex(x => new { x.WorkspaceId, x.InstanceId, x.CreatedAt });
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Workspace>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Instance).WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId, x.InstanceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.WorkspaceId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Operation).WithMany()
            .HasForeignKey(x => x.OperationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ElsaInstanceAuditEventConfiguration : IEntityTypeConfiguration<ElsaInstanceAuditEventEntity>
{
    public void Configure(EntityTypeBuilder<ElsaInstanceAuditEventEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.EventType).HasMaxLength(128).IsRequired();
        builder.Property(x => x.OperatorSubject).HasMaxLength(71);
        builder.Property(x => x.PriorState).HasMaxLength(64);
        builder.Property(x => x.NewState).HasMaxLength(64);
        builder.Property(x => x.DesiredStateRevisionId).HasMaxLength(128);
        builder.Property(x => x.PlanReference).HasMaxLength(2048);
        builder.Property(x => x.DiagnosticCode).HasMaxLength(128);
        builder.Property(x => x.Summary).HasMaxLength(128);
        builder.Property(x => x.RequestKeyHash).HasMaxLength(64);
        builder.HasIndex(x => x.MigrationId);
        builder.Property(x => x.OccurredAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.InstanceId, x.Sequence }).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.OccurredAt });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ElsaInstanceIdentityBindingConfiguration : IEntityTypeConfiguration<ElsaInstanceIdentityBindingEntity>
{
    public void Configure(EntityTypeBuilder<ElsaInstanceIdentityBindingEntity> builder)
    {
        builder.HasKey(x => x.InstanceId);
        builder.Property(x => x.Audience).HasMaxLength(256).IsRequired();
        builder.Property(x => x.CanonicalCallbackUri).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.VerifiedEndpointOrigin).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.BindingVersion).IsRequired();
        builder.Property(x => x.BindingVersion).IsConcurrencyToken();
        builder.Property(x => x.ChangedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => x.Audience).IsUnique();
        builder.HasIndex(x => x.CanonicalCallbackUri).IsUnique();
    }
}

internal sealed class ElsaInstanceMigrationConfiguration : IEntityTypeConfiguration<ElsaInstanceMigrationEntity>
{
    public void Configure(EntityTypeBuilder<ElsaInstanceMigrationEntity> builder)
    {
        builder.HasKey(x => x.MigrationId);
        foreach (var property in new[]
        {
            nameof(ElsaInstanceMigrationEntity.SourcePlanId), nameof(ElsaInstanceMigrationEntity.SourcePlanUri),
            nameof(ElsaInstanceMigrationEntity.SourceReleaseLine), nameof(ElsaInstanceMigrationEntity.SourceVersion),
            nameof(ElsaInstanceMigrationEntity.SourceManifestDigest), nameof(ElsaInstanceMigrationEntity.SourceDeploymentId),
            nameof(ElsaInstanceMigrationEntity.TargetPlanId), nameof(ElsaInstanceMigrationEntity.TargetPlanUri),
            nameof(ElsaInstanceMigrationEntity.TargetReleaseLine), nameof(ElsaInstanceMigrationEntity.TargetVersion),
            nameof(ElsaInstanceMigrationEntity.TargetManifestDigest), nameof(ElsaInstanceMigrationEntity.TargetDeploymentId)
        })
            builder.Property<string?>(property).HasMaxLength(
                property.EndsWith("Uri", StringComparison.Ordinal) ? 2048 :
                property.EndsWith("ManifestDigest", StringComparison.Ordinal) ? 71 : 128);
        builder.Property(x => x.Phase).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SourceAccessMode).HasMaxLength(64).IsRequired();
        builder.Property(x => x.StartRequestHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.LastRequestHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SourceReleaseDiagnosticCode).HasMaxLength(128);
        builder.Property(x => x.SourceReleaseProviderCorrelationId).HasMaxLength(128);
        builder.Property(x => x.SourceReleaseEvidenceReference).HasMaxLength(2048);
        builder.Property(x => x.SourceReleaseEvidenceDigest).HasMaxLength(71);
        builder.Property(x => x.CutoverAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.SourceRetainUntil).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.EarlyReleaseApprovedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.SourceReleasedAt).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.SourceReleaseClaimedUntil).HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null, value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        builder.Property(x => x.CreatedAt).HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(x => x.UpdatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero))
            .IsConcurrencyToken();
        builder.HasIndex(x => new { x.InstanceId, x.Phase });
        builder.HasIndex(x => new { x.InstanceId, x.SourceRetainUntil });
        builder.HasIndex(x => new { x.Phase, x.SourceRetainUntil, x.SourceReleaseClaimedUntil });
        builder.HasIndex(x => new { x.InstanceId, x.StartRequestHash }).IsUnique();
        builder.HasIndex(x => x.OperationId).IsUnique();
        builder.HasIndex(x => x.InstanceId).IsUnique()
            .HasFilter("Phase <> 'RolledBack' AND Phase <> 'Released' AND Phase <> 'Failed'");
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Workspace>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ElsaInstanceOperationEntity>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
    }
}
