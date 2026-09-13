using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Manifests;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.RuntimeBuilder.Abstractions.RuntimeConfigurations;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.PackageCatalog.Core.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<PackageSource> PackageSources => Set<PackageSource>();
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<PackageVersion> PackageVersions => Set<PackageVersion>();
    public DbSet<FeatureRecord> Features => Set<FeatureRecord>();
    public DbSet<FeatureSettingRecord> FeatureSettings => Set<FeatureSettingRecord>();
    public DbSet<ManifestValidationResultRecord> ManifestValidationResults => Set<ManifestValidationResultRecord>();
    public DbSet<ApprovalRecord> ApprovalRecords => Set<ApprovalRecord>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<SyncRunItem> SyncRunItems => Set<SyncRunItem>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();
    public DbSet<OrganizationEntitlementSnapshot> OrganizationEntitlementSnapshots => Set<OrganizationEntitlementSnapshot>();
    public DbSet<OrganizationAuditRecord> OrganizationAuditRecords => Set<OrganizationAuditRecord>();
    public DbSet<OrganizationSubscription> OrganizationSubscriptions => Set<OrganizationSubscription>();
    public DbSet<BillingProviderEventInboxEntry> BillingProviderEvents => Set<BillingProviderEventInboxEntry>();
    public DbSet<OrganizationBillingLifecycleNotice> OrganizationBillingLifecycleNotices => Set<OrganizationBillingLifecycleNotice>();
    public DbSet<OrganizationBillingCleanup> OrganizationBillingCleanups => Set<OrganizationBillingCleanup>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMembership> WorkspaceMemberships => Set<WorkspaceMembership>();
    public DbSet<WorkspaceEntitlementSnapshot> WorkspaceEntitlementSnapshots => Set<WorkspaceEntitlementSnapshot>();
    public DbSet<RuntimeConfiguration> RuntimeConfigurations => Set<RuntimeConfiguration>();
    public DbSet<RuntimeConfigurationVersion> RuntimeConfigurationVersions => Set<RuntimeConfigurationVersion>();
    internal DbSet<Models.DeploymentApplicationEntity> DeploymentApplications => Set<Models.DeploymentApplicationEntity>();
    internal DbSet<Models.DeploymentEnvironmentEntity> DeploymentEnvironments => Set<Models.DeploymentEnvironmentEntity>();
    internal DbSet<Models.ElsaInstanceEntity> ElsaInstances => Set<Models.ElsaInstanceEntity>();
    internal DbSet<Models.ElsaInstanceIntentRevisionEntity> ElsaInstanceIntentRevisions => Set<Models.ElsaInstanceIntentRevisionEntity>();
    internal DbSet<Models.ElsaInstanceLifecycleOutboxEntity> ElsaInstanceLifecycleOutbox => Set<Models.ElsaInstanceLifecycleOutboxEntity>();
    internal DbSet<Models.ElsaInstanceOperationEntity> ElsaInstanceOperations => Set<Models.ElsaInstanceOperationEntity>();
    internal DbSet<Models.ElsaInstanceRecoveryRequestEntity> ElsaInstanceRecoveryRequests => Set<Models.ElsaInstanceRecoveryRequestEntity>();
    internal DbSet<Models.ElsaInstanceResolvedPlanEntity> ElsaInstanceResolvedPlans => Set<Models.ElsaInstanceResolvedPlanEntity>();
    internal DbSet<Models.ElsaInstanceAuditEventEntity> ElsaInstanceAuditEvents => Set<Models.ElsaInstanceAuditEventEntity>();
    internal DbSet<Models.ElsaInstanceIdentityBindingEntity> ElsaInstanceIdentityBindings => Set<Models.ElsaInstanceIdentityBindingEntity>();
    internal DbSet<Models.ElsaInstanceMigrationEntity> ElsaInstanceMigrations => Set<Models.ElsaInstanceMigrationEntity>();
    internal DbSet<Models.DeploymentTierDefinitionEntity> DeploymentTierDefinitions => Set<Models.DeploymentTierDefinitionEntity>();
    internal DbSet<Models.DeploymentTierCapabilityAssignmentEntity> DeploymentTierCapabilityAssignments => Set<Models.DeploymentTierCapabilityAssignmentEntity>();
    internal DbSet<Models.DeploymentTierChangeRecordEntity> DeploymentTierChangeRecords => Set<Models.DeploymentTierChangeRecordEntity>();
    internal DbSet<Models.WorkflowEngineEntity> WorkflowEngines => Set<Models.WorkflowEngineEntity>();
    internal DbSet<Models.DeploymentSecretStoreEntity> DeploymentSecretStores => Set<Models.DeploymentSecretStoreEntity>();
    internal DbSet<Models.DeploymentCredentialReferenceEntity> DeploymentCredentialReferences => Set<Models.DeploymentCredentialReferenceEntity>();
    internal DbSet<Models.WorkspaceDeploymentArtifactEntity> WorkspaceDeploymentArtifacts => Set<Models.WorkspaceDeploymentArtifactEntity>();
    internal DbSet<Models.WorkspaceArtifactUploadSessionEntity> WorkspaceArtifactUploadSessions => Set<Models.WorkspaceArtifactUploadSessionEntity>();
    internal DbSet<Models.EngineCapabilityEntity> EngineCapabilities => Set<Models.EngineCapabilityEntity>();
    internal DbSet<Models.RuntimeControlEntity> RuntimeControls => Set<Models.RuntimeControlEntity>();
    internal DbSet<Models.RuntimeControlExecutionEntity> RuntimeControlExecutions => Set<Models.RuntimeControlExecutionEntity>();
    internal DbSet<Models.DesiredStateRevisionEntity> DesiredStateRevisions => Set<Models.DesiredStateRevisionEntity>();
    internal DbSet<Models.StructuredDesiredStateRecordEntity> StructuredDesiredStateRecords => Set<Models.StructuredDesiredStateRecordEntity>();
    internal DbSet<Models.WorkspacePermissionGrantEntity> WorkspacePermissionGrants => Set<Models.WorkspacePermissionGrantEntity>();
    internal DbSet<Models.WorkspacePermissionAuditRecordEntity> WorkspacePermissionAuditRecords => Set<Models.WorkspacePermissionAuditRecordEntity>();
    internal DbSet<Models.ActionConfirmationEntity> ActionConfirmations => Set<Models.ActionConfirmationEntity>();
    internal DbSet<Models.DeploymentRunEntity> DeploymentRuns => Set<Models.DeploymentRunEntity>();
    internal DbSet<Models.DeploymentRunHistoryEventEntity> DeploymentRunHistoryEvents => Set<Models.DeploymentRunHistoryEventEntity>();
    internal DbSet<Models.DeploymentCommandEntity> DeploymentCommands => Set<Models.DeploymentCommandEntity>();
    internal DbSet<Models.DeploymentCommandEventEntity> DeploymentCommandEvents => Set<Models.DeploymentCommandEventEntity>();
    internal DbSet<Models.AzureProviderResourceAssignmentEntity> AzureProviderResourceAssignments => Set<Models.AzureProviderResourceAssignmentEntity>();
    internal DbSet<Models.AzureProviderAssignmentRebindEntity> AzureProviderAssignmentRebinds => Set<Models.AzureProviderAssignmentRebindEntity>();
    internal DbSet<Models.AzureProviderOperationEntity> AzureProviderOperations => Set<Models.AzureProviderOperationEntity>();
    internal DbSet<Models.AzureProviderOperationTransitionEntity> AzureProviderOperationTransitions => Set<Models.AzureProviderOperationTransitionEntity>();
    internal DbSet<Models.AzureProviderRecoveryObservationEntity> AzureProviderRecoveryObservations => Set<Models.AzureProviderRecoveryObservationEntity>();
    internal DbSet<Models.DeploymentCommandWebhookNotificationEntity> DeploymentCommandWebhookNotifications => Set<Models.DeploymentCommandWebhookNotificationEntity>();
    internal DbSet<Models.ObservabilityBindingEntity> ObservabilityBindings => Set<Models.ObservabilityBindingEntity>();
    internal DbSet<Models.DriftReportItemEntity> DriftReportItems => Set<Models.DriftReportItemEntity>();
    internal DbSet<Models.WeaverSessionEntity> WeaverSessions => Set<Models.WeaverSessionEntity>();
    internal DbSet<Models.WeaverMessageEntity> WeaverMessages => Set<Models.WeaverMessageEntity>();
    internal DbSet<Models.WeaverToolCallEntity> WeaverToolCalls => Set<Models.WeaverToolCallEntity>();
    internal DbSet<Models.WeaverPlanEntity> WeaverPlans => Set<Models.WeaverPlanEntity>();
    internal DbSet<Models.WeaverPlanApprovalEntity> WeaverPlanApprovals => Set<Models.WeaverPlanApprovalEntity>();
    internal DbSet<Models.WeaverPlanExecutionEntity> WeaverPlanExecutions => Set<Models.WeaverPlanExecutionEntity>();
    internal DbSet<Models.ManagedElsaHandoffReplayEntity> ManagedElsaHandoffReplays => Set<Models.ManagedElsaHandoffReplayEntity>();
    internal DbSet<Models.ManagedElsaHandoffAuditEventEntity> ManagedElsaHandoffAuditEvents => Set<Models.ManagedElsaHandoffAuditEventEntity>();
    internal DbSet<Models.GovernedReleaseCatalogEntity> GovernedReleaseCatalog => Set<Models.GovernedReleaseCatalogEntity>();
    internal DbSet<Models.GovernedReleaseCatalogPackageDeclarationEntity> GovernedReleaseCatalogPackageDeclarations => Set<Models.GovernedReleaseCatalogPackageDeclarationEntity>();
    internal DbSet<Models.GovernedReleaseCatalogTopologyEntity> GovernedReleaseCatalogTopologies => Set<Models.GovernedReleaseCatalogTopologyEntity>();
    internal DbSet<Models.GovernedReleaseCatalogRuntimeKindEntity> GovernedReleaseCatalogRuntimeKinds => Set<Models.GovernedReleaseCatalogRuntimeKindEntity>();
    internal DbSet<Models.GovernedReleaseCatalogCapabilityEntity> GovernedReleaseCatalogCapabilities => Set<Models.GovernedReleaseCatalogCapabilityEntity>();
    internal DbSet<Models.GovernedReleaseCatalogComponentVersionEntity> GovernedReleaseCatalogComponentVersions => Set<Models.GovernedReleaseCatalogComponentVersionEntity>();
    internal DbSet<Models.GovernedReleaseCatalogComponentEntity> GovernedReleaseCatalogComponents => Set<Models.GovernedReleaseCatalogComponentEntity>();
    internal DbSet<Models.GovernedReleaseCatalogPlatformDigestEntity> GovernedReleaseCatalogPlatformDigests => Set<Models.GovernedReleaseCatalogPlatformDigestEntity>();
    internal DbSet<Models.GovernedReleaseCatalogRoleEntity> GovernedReleaseCatalogRoles => Set<Models.GovernedReleaseCatalogRoleEntity>();
    internal DbSet<Models.GovernedReleaseCatalogComponentCapabilityEntity> GovernedReleaseCatalogComponentCapabilities => Set<Models.GovernedReleaseCatalogComponentCapabilityEntity>();
    internal DbSet<Models.GovernedReleaseCatalogEndpointEntity> GovernedReleaseCatalogEndpoints => Set<Models.GovernedReleaseCatalogEndpointEntity>();
    internal DbSet<Models.GovernedReleaseCatalogEvidenceEntity> GovernedReleaseCatalogEvidence => Set<Models.GovernedReleaseCatalogEvidenceEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new Models.PackageSourceConfiguration());
        modelBuilder.ApplyConfiguration(new Models.PackageConfiguration());
        modelBuilder.ApplyConfiguration(new Models.PackageVersionConfiguration());
        modelBuilder.ApplyConfiguration(new Models.FeatureRecordConfiguration());
        modelBuilder.ApplyConfiguration(new Models.FeatureSettingRecordConfiguration());
        modelBuilder.ApplyConfiguration(new Models.ManifestValidationResultRecordConfiguration());
        modelBuilder.ApplyConfiguration(new Models.ApprovalRecordConfiguration());
        modelBuilder.ApplyConfiguration(new Models.SyncRunConfiguration());
        modelBuilder.ApplyConfiguration(new Models.SyncRunItemConfiguration());
        modelBuilder.ApplyConfiguration(new Models.AccountConfiguration());
        modelBuilder.ApplyConfiguration(new Models.ExternalIdentityConfiguration());
        modelBuilder.ApplyConfiguration(new Models.OrganizationConfiguration());
        modelBuilder.ApplyConfiguration(new Models.OrganizationMembershipConfiguration());
        modelBuilder.ApplyConfiguration(new Models.OrganizationEntitlementSnapshotConfiguration());
        modelBuilder.ApplyConfiguration(new Models.OrganizationAuditRecordConfiguration());
        modelBuilder.ApplyConfiguration(new Models.OrganizationSubscriptionConfiguration());
        modelBuilder.ApplyConfiguration(new Models.BillingProviderEventInboxEntryConfiguration());
        modelBuilder.ApplyConfiguration(new Models.OrganizationBillingLifecycleNoticeConfiguration());
        modelBuilder.ApplyConfiguration(new Models.OrganizationBillingCleanupConfiguration());
        modelBuilder.ApplyConfiguration(new Models.WorkspaceConfiguration());
        modelBuilder.ApplyConfiguration(new Models.WorkspaceMembershipConfiguration());
        modelBuilder.ApplyConfiguration(new Models.WorkspaceEntitlementSnapshotConfiguration());
        modelBuilder.ApplyConfiguration(new Models.RuntimeConfigurationConfiguration());
        modelBuilder.ApplyConfiguration(new Models.RuntimeConfigurationVersionConfiguration());
        modelBuilder.ApplyConfiguration(new Models.DeploymentApplicationConfiguration());
        modelBuilder.ApplyConfiguration(new Models.DeploymentEnvironmentConfiguration());
        modelBuilder.ApplyConfiguration(new Models.ElsaInstanceConfiguration());
        modelBuilder.ApplyConfiguration(new Models.ElsaInstanceIntentRevisionConfiguration());
        modelBuilder.ApplyConfiguration(new Models.ElsaInstanceLifecycleOutboxConfiguration());
        modelBuilder.ApplyConfiguration(new Models.ElsaInstanceOperationConfiguration());
        modelBuilder.ApplyConfiguration(new Models.ElsaInstanceRecoveryRequestConfiguration());
        modelBuilder.ApplyConfiguration(new Models.ElsaInstanceResolvedPlanConfiguration());
        modelBuilder.ApplyConfiguration(new Models.ElsaInstanceAuditEventConfiguration());
        modelBuilder.ApplyConfiguration(new Models.ElsaInstanceIdentityBindingConfiguration());
        modelBuilder.ApplyConfiguration(new Models.ElsaInstanceMigrationConfiguration());
        modelBuilder.ApplyConfiguration(new Models.DeploymentTierDefinitionConfiguration());
        modelBuilder.ApplyConfiguration(new Models.DeploymentTierCapabilityAssignmentConfiguration());
        modelBuilder.ApplyConfiguration(new Models.DeploymentTierChangeRecordConfiguration());
        modelBuilder.ApplyConfiguration(new Models.WorkflowEngineConfiguration());
        modelBuilder.ApplyConfiguration(new Models.DeploymentSecretStoreConfiguration());
        modelBuilder.ApplyConfiguration(new Models.DeploymentCredentialReferenceConfiguration());
        modelBuilder.ApplyConfiguration(new Models.WorkspaceDeploymentArtifactConfiguration());
        modelBuilder.ApplyConfiguration(new Models.WorkspaceArtifactUploadSessionConfiguration());
        modelBuilder.ApplyConfiguration(new Models.EngineCapabilityConfiguration());
        modelBuilder.ApplyConfiguration(new Models.RuntimeControlConfiguration());
        modelBuilder.ApplyConfiguration(new Models.RuntimeControlExecutionConfiguration());
        modelBuilder.ApplyConfiguration(new Models.DesiredStateRevisionConfiguration());
        modelBuilder.ApplyConfiguration(new Models.StructuredDesiredStateRecordConfiguration());
        modelBuilder.ApplyConfiguration(new Models.WorkspacePermissionGrantConfiguration());
        modelBuilder.ApplyConfiguration(new Models.WorkspacePermissionAuditRecordConfiguration());
        modelBuilder.ApplyConfiguration(new Models.ActionConfirmationConfiguration());
        modelBuilder.ApplyConfiguration(new Models.DeploymentRunConfiguration());
        modelBuilder.ApplyConfiguration(new Models.DeploymentRunHistoryEventConfiguration());
        modelBuilder.ApplyConfiguration(new Models.DeploymentCommandConfiguration());
        modelBuilder.ApplyConfiguration(new Models.DeploymentCommandEventConfiguration());
        modelBuilder.ApplyConfiguration(new Models.AzureProviderResourceAssignmentConfiguration());
        modelBuilder.ApplyConfiguration(new Models.AzureProviderAssignmentRebindConfiguration());
        modelBuilder.ApplyConfiguration(new Models.AzureProviderOperationConfiguration());
        modelBuilder.ApplyConfiguration(new Models.AzureProviderOperationTransitionConfiguration());
        modelBuilder.ApplyConfiguration(new Models.AzureProviderRecoveryObservationConfiguration());
        modelBuilder.ApplyConfiguration(new Models.DeploymentCommandWebhookNotificationConfiguration());
        modelBuilder.ApplyConfiguration(new Models.ObservabilityBindingConfiguration());
        modelBuilder.ApplyConfiguration(new Models.DriftReportItemConfiguration());
        modelBuilder.ApplyConfiguration(new Models.WeaverSessionConfiguration());
        modelBuilder.ApplyConfiguration(new Models.WeaverMessageConfiguration());
        modelBuilder.ApplyConfiguration(new Models.WeaverToolCallConfiguration());
        modelBuilder.ApplyConfiguration(new Models.WeaverPlanConfiguration());
        modelBuilder.ApplyConfiguration(new Models.WeaverPlanApprovalConfiguration());
        modelBuilder.ApplyConfiguration(new Models.WeaverPlanExecutionConfiguration());
        modelBuilder.ApplyConfiguration(new Models.ManagedElsaHandoffReplayConfiguration());
        modelBuilder.ApplyConfiguration(new Models.ManagedElsaHandoffAuditEventConfiguration());
        modelBuilder.ApplyConfiguration(new Models.GovernedReleaseCatalogConfiguration());
        modelBuilder.ApplyConfiguration(new Models.GovernedReleaseCatalogPackageDeclarationConfiguration());
        modelBuilder.ApplyConfiguration(new Models.GovernedReleaseCatalogTopologyConfiguration());
        modelBuilder.ApplyConfiguration(new Models.GovernedReleaseCatalogRuntimeKindConfiguration());
        modelBuilder.ApplyConfiguration(new Models.GovernedReleaseCatalogCapabilityConfiguration());
        modelBuilder.ApplyConfiguration(new Models.GovernedReleaseCatalogComponentVersionConfiguration());
        modelBuilder.ApplyConfiguration(new Models.GovernedReleaseCatalogComponentConfiguration());
        modelBuilder.ApplyConfiguration(new Models.GovernedReleaseCatalogPlatformDigestConfiguration());
        modelBuilder.ApplyConfiguration(new Models.GovernedReleaseCatalogRoleConfiguration());
        modelBuilder.ApplyConfiguration(new Models.GovernedReleaseCatalogComponentCapabilityConfiguration());
        modelBuilder.ApplyConfiguration(new Models.GovernedReleaseCatalogEndpointConfiguration());
        modelBuilder.ApplyConfiguration(new Models.GovernedReleaseCatalogEvidenceConfiguration());
        Models.CatalogTableTriggers.Declare(modelBuilder, Database.ProviderName);

        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer")
        {
            const string binaryCollation = "Latin1_General_100_BIN2";
            modelBuilder.Entity<OrganizationSubscription>().Property(x => x.Provider).UseCollation(binaryCollation);
            modelBuilder.Entity<OrganizationSubscription>().Property(x => x.ProviderCustomerReference).UseCollation(binaryCollation);
            modelBuilder.Entity<OrganizationSubscription>().Property(x => x.ProviderSubscriptionReference).UseCollation(binaryCollation);
            modelBuilder.Entity<OrganizationSubscription>().Property(x => x.LastProviderEventId).UseCollation(binaryCollation);
            modelBuilder.Entity<BillingProviderEventInboxEntry>().Property(x => x.Provider).UseCollation(binaryCollation);
            modelBuilder.Entity<BillingProviderEventInboxEntry>().Property(x => x.ProviderEventId).UseCollation(binaryCollation);
            modelBuilder.Entity<BillingProviderEventInboxEntry>().Property(x => x.ProviderCustomerReference).UseCollation(binaryCollation);
            modelBuilder.Entity<BillingProviderEventInboxEntry>().Property(x => x.ProviderSubscriptionReference).UseCollation(binaryCollation);
            modelBuilder.Entity<OrganizationBillingCleanup>().Property(x => x.Provider).UseCollation(binaryCollation);
            modelBuilder.Entity<OrganizationBillingCleanup>().Property(x => x.ProviderCustomerReference).UseCollation(binaryCollation);
            modelBuilder.Entity<OrganizationBillingCleanup>().Property(x => x.ProviderSubscriptionReference).UseCollation(binaryCollation);
        }
    }

    public override int SaveChanges() => SaveChanges(acceptAllChangesOnSuccess: true);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareForSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        PrepareForSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Runs the persistence guards over the changes detected once, at the start of the pass. With automatic detection on,
    /// every <c>ChangeTracker.Entries()</c> call rescans all tracked entities, so a long-lived context (a sync run saves
    /// once per discovered version) would pay a full scan per guard lookup on every save. The guards therefore see the
    /// Added, Modified and Deleted state as it stood when the save began; none depends on another guard's mutation being
    /// detected first (a new guard that does needs an explicit <c>DetectChanges</c> before it). The caller's setting is
    /// restored before the base save, whose own detection picks up the values the guards normalize or assign.
    /// </summary>
    private void PrepareForSave()
    {
        var autoDetectChanges = ChangeTracker.AutoDetectChangesEnabled;
        if (autoDetectChanges)
            ChangeTracker.DetectChanges();

        ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            EnsureWorkspacePermissionAuditIsAppendOnly();
            EnsureAzureOperationTransitionsAreAppendOnly();
            EnsureAzureProviderRecoveryObservationsAreAppendOnly();
            EnsureAzureProviderAssignmentRebindsAreAppendOnly();
            EnsureElsaInstanceAuditIsAppendOnly();
            EnsureElsaInstanceDurableRowsAreNotDeleted();
            EnsureElsaInstanceIntentRevisionsAreAppendOnly();
            EnsureElsaInstanceLifecycleOutboxIsAppendOnly();
            EnsureElsaInstanceResolvedPlansAreAppendOnly();
            EnsureElsaInstanceRecoveryRequestsAreAppendOnly();
            EnsureManagedElsaHandoffRowsAreAppendOnly();
            EnsureBillingProviderEventsAreAppendOnly();
            EnsureBillingLifecycleNoticesAreAppendOnly();
            EnsureBillingCleanupsAreNotDeleted();
            EnsureBillingSubscriptionsAreConsistent();
            EnsureGovernedReleaseCatalogIsImmutable();
            ValidateManagedElsaHandoffRows();
            ValidateAzureProviderRecoveryObservations();
            ValidateElsaInstancePersistence();
            EnsureOrganizationsForNewWorkspaces();
            ValidateBillingPersistence();
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }

    private void ValidateBillingPersistence()
    {
        foreach (var entry in ChangeTracker.Entries<OrganizationSubscription>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            var subscription = entry.Entity;
            if (subscription.Id == Guid.Empty || subscription.OrganizationId == Guid.Empty)
                throw new InvalidOperationException("A subscription requires stable organization identifiers.");
            subscription.Provider = RequireSafeCode(subscription.Provider, nameof(subscription.Provider));
            subscription.ProviderCustomerReference = OptionalSafeReference(subscription.ProviderCustomerReference, nameof(subscription.ProviderCustomerReference), OrganizationBillingLimits.ProviderReferenceMaxLength);
            subscription.ProviderSubscriptionReference = OptionalSafeReference(subscription.ProviderSubscriptionReference, nameof(subscription.ProviderSubscriptionReference), OrganizationBillingLimits.ProviderReferenceMaxLength);
            subscription.LastProviderEventId = OptionalSafeToken(subscription.LastProviderEventId, nameof(subscription.LastProviderEventId), 256);
            EnsureDefined(subscription.State, nameof(subscription.State));
            subscription.TrialStartedAt = subscription.TrialStartedAt.ToUniversalTime();
            subscription.TrialEndsAt = subscription.TrialEndsAt.ToUniversalTime();
            subscription.LastProviderEventOccurredAt = subscription.LastProviderEventOccurredAt.ToUniversalTime();
            subscription.CreatedAt = subscription.CreatedAt.ToUniversalTime();
            subscription.UpdatedAt = subscription.UpdatedAt.ToUniversalTime();
            if (subscription.LifecycleVersion < 0)
                throw new InvalidOperationException("Subscription lifecycle version is invalid.");
            if (subscription.TrialStartedAt == default || subscription.TrialEndsAt <= subscription.TrialStartedAt ||
                subscription.LastProviderEventOccurredAt == default || subscription.CreatedAt == default || subscription.UpdatedAt == default)
                throw new InvalidOperationException("Subscription lifecycle timestamps are invalid.");
            subscription.ActivatedAt = NormalizeOptionalTimestamp(subscription.ActivatedAt, nameof(subscription.ActivatedAt));
            subscription.PastDueAt = NormalizeOptionalTimestamp(subscription.PastDueAt, nameof(subscription.PastDueAt));
            subscription.GraceEndsAt = NormalizeOptionalTimestamp(subscription.GraceEndsAt, nameof(subscription.GraceEndsAt));
            subscription.ConstrainedAt = NormalizeOptionalTimestamp(subscription.ConstrainedAt, nameof(subscription.ConstrainedAt));
            subscription.SuspendedAt = NormalizeOptionalTimestamp(subscription.SuspendedAt, nameof(subscription.SuspendedAt));
            subscription.RetentionEndsAt = NormalizeOptionalTimestamp(subscription.RetentionEndsAt, nameof(subscription.RetentionEndsAt));
            subscription.RetainedAt = NormalizeOptionalTimestamp(subscription.RetainedAt, nameof(subscription.RetainedAt));
            subscription.DeletedAt = NormalizeOptionalTimestamp(subscription.DeletedAt, nameof(subscription.DeletedAt));
            subscription.EarlyDeletionRequestedAt = NormalizeOptionalTimestamp(subscription.EarlyDeletionRequestedAt, nameof(subscription.EarlyDeletionRequestedAt));
        }

        foreach (var entry in ChangeTracker.Entries<BillingProviderEventInboxEntry>()
                     .Where(x => x.State == EntityState.Added))
        {
            var billingEvent = entry.Entity;
            if (billingEvent.Id == Guid.Empty || billingEvent.OrganizationId == Guid.Empty)
                throw new InvalidOperationException("A billing event requires stable organization identifiers.");
            billingEvent.Provider = RequireSafeCode(billingEvent.Provider, nameof(billingEvent.Provider));
            billingEvent.ProviderEventId = RequireSafeToken(billingEvent.ProviderEventId, nameof(billingEvent.ProviderEventId), 256);
            billingEvent.EventType = RequireSafeCode(billingEvent.EventType, nameof(billingEvent.EventType));
            billingEvent.EventHash = RequireSha256Digest(billingEvent.EventHash, nameof(billingEvent.EventHash));
            billingEvent.ProviderCustomerReference = OptionalSafeReference(billingEvent.ProviderCustomerReference, nameof(billingEvent.ProviderCustomerReference), OrganizationBillingLimits.ProviderReferenceMaxLength);
            billingEvent.ProviderSubscriptionReference = OptionalSafeReference(billingEvent.ProviderSubscriptionReference, nameof(billingEvent.ProviderSubscriptionReference), OrganizationBillingLimits.ProviderReferenceMaxLength);
            billingEvent.RejectionCode = OptionalSafeCode(billingEvent.RejectionCode, nameof(billingEvent.RejectionCode));
            EnsureDefined(billingEvent.ProcessingStatus, nameof(billingEvent.ProcessingStatus));
            if (billingEvent.State.HasValue)
                EnsureDefined(billingEvent.State.Value, nameof(billingEvent.State));
            else if (billingEvent.ProcessingStatus is not BillingProviderEventProcessingStatus.RecordedUnknown)
                throw new InvalidOperationException("A billing event without a lifecycle state must be recorded as unknown.");
            billingEvent.OccurredAt = billingEvent.OccurredAt.ToUniversalTime();
            billingEvent.ReceivedAt = billingEvent.ReceivedAt.ToUniversalTime();
            billingEvent.ProcessedAt = NormalizeOptionalTimestamp(billingEvent.ProcessedAt, nameof(billingEvent.ProcessedAt));
            if (billingEvent.OccurredAt == default || billingEvent.ReceivedAt == default)
                throw new InvalidOperationException("Billing event timestamps are required.");
        }

        foreach (var entry in ChangeTracker.Entries<OrganizationBillingLifecycleNotice>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            var notice = entry.Entity;
            if (notice.Id == Guid.Empty || notice.OrganizationId == Guid.Empty || notice.SubscriptionId == Guid.Empty)
                throw new InvalidOperationException("A billing lifecycle notice requires stable organization and subscription identifiers.");
            EnsureDefined(notice.Kind, nameof(notice.Kind));
            EnsureDefined(notice.State, nameof(notice.State));
            EnsureDefined(notice.DeliveryStatus, nameof(notice.DeliveryStatus));
            notice.CreatedAt = notice.CreatedAt.ToUniversalTime();
            notice.DeliveredAt = NormalizeOptionalTimestamp(notice.DeliveredAt, nameof(notice.DeliveredAt));
            notice.LastFailureCode = OptionalSafeCode(notice.LastFailureCode, nameof(notice.LastFailureCode));
            if (notice.CreatedAt == default || notice.DeliveryAttemptCount < 0)
                throw new InvalidOperationException("Billing lifecycle notice metadata is invalid.");
        }

        foreach (var entry in ChangeTracker.Entries<OrganizationBillingCleanup>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            var cleanup = entry.Entity;
            if (cleanup.Id == Guid.Empty || cleanup.OrganizationId == Guid.Empty || cleanup.SubscriptionId == Guid.Empty)
                throw new InvalidOperationException("A billing cleanup requires stable organization and subscription identifiers.");
            cleanup.CleanupKey = RequireSafeCode(cleanup.CleanupKey, nameof(cleanup.CleanupKey));
            cleanup.Provider = RequireSafeCode(cleanup.Provider, nameof(cleanup.Provider));
            cleanup.ProviderCustomerReference = OptionalSafeReference(cleanup.ProviderCustomerReference, nameof(cleanup.ProviderCustomerReference), OrganizationBillingLimits.ProviderReferenceMaxLength);
            cleanup.ProviderSubscriptionReference = OptionalSafeReference(cleanup.ProviderSubscriptionReference, nameof(cleanup.ProviderSubscriptionReference), OrganizationBillingLimits.ProviderReferenceMaxLength);
            EnsureDefined(cleanup.State, nameof(cleanup.State));
            cleanup.RequestedAt = cleanup.RequestedAt.ToUniversalTime();
            cleanup.NotBeforeAt = cleanup.NotBeforeAt.ToUniversalTime();
            cleanup.LastAttemptAt = NormalizeOptionalTimestamp(cleanup.LastAttemptAt, nameof(cleanup.LastAttemptAt));
            cleanup.CompletedAt = NormalizeOptionalTimestamp(cleanup.CompletedAt, nameof(cleanup.CompletedAt));
            cleanup.LeaseExpiresAt = NormalizeOptionalTimestamp(cleanup.LeaseExpiresAt, nameof(cleanup.LeaseExpiresAt));
            cleanup.LeaseOwner = OptionalSafeCode(cleanup.LeaseOwner, nameof(cleanup.LeaseOwner));
            cleanup.LeaseToken = OptionalSafeCode(cleanup.LeaseToken, nameof(cleanup.LeaseToken));
            cleanup.LastFailureCode = OptionalSafeCode(cleanup.LastFailureCode, nameof(cleanup.LastFailureCode));
            if (cleanup.RequestedAt == default || cleanup.NotBeforeAt == default || cleanup.AttemptCount < 0)
                throw new InvalidOperationException("Billing cleanup metadata is invalid.");
        }
    }

    private void EnsureBillingProviderEventsAreAppendOnly()
    {
        foreach (var entry in ChangeTracker.Entries<BillingProviderEventInboxEntry>()
                     .Where(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Billing provider event inbox entries are append-only.");
    }

    private void EnsureBillingLifecycleNoticesAreAppendOnly()
    {
        foreach (var entry in ChangeTracker.Entries<OrganizationBillingLifecycleNotice>()
                     .Where(x => x.State == EntityState.Deleted))
            throw new InvalidOperationException("Billing lifecycle notices are append-only.");
    }

    private void EnsureBillingCleanupsAreNotDeleted()
    {
        foreach (var entry in ChangeTracker.Entries<OrganizationBillingCleanup>()
                     .Where(x => x.State == EntityState.Deleted))
            throw new InvalidOperationException("Billing cleanup intents cannot be deleted.");
    }

    private void EnsureBillingSubscriptionsAreConsistent()
    {
        foreach (var entry in ChangeTracker.Entries<OrganizationSubscription>()
                     .Where(x => x.State is EntityState.Modified or EntityState.Deleted))
        {
            if (entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Organization subscriptions cannot be deleted.");

            var subscription = entry.Entity;
            EnsureUnchanged(entry, nameof(OrganizationSubscription.Id), subscription.Id);
            EnsureUnchanged(entry, nameof(OrganizationSubscription.OrganizationId), subscription.OrganizationId);
            EnsureUnchanged(entry, nameof(OrganizationSubscription.Provider), subscription.Provider);
            EnsureTimestampUnchanged(entry, nameof(OrganizationSubscription.CreatedAt), subscription.CreatedAt);
            EnsureTimestampUnchanged(entry, nameof(OrganizationSubscription.TrialStartedAt), subscription.TrialStartedAt);
            EnsureTimestampUnchanged(entry, nameof(OrganizationSubscription.TrialEndsAt), subscription.TrialEndsAt);
            var originalState = entry.Property<OrganizationSubscriptionState>(nameof(OrganizationSubscription.State)).OriginalValue;
            var stateChanged = subscription.State != originalState;
            EnsureBoundReference(entry, nameof(OrganizationSubscription.ProviderCustomerReference), subscription.ProviderCustomerReference, subscription.State == OrganizationSubscriptionState.Deleted);
            EnsureBoundReference(entry, nameof(OrganizationSubscription.ProviderSubscriptionReference), subscription.ProviderSubscriptionReference, subscription.State == OrganizationSubscriptionState.Deleted);

            var originalOccurrence = entry.Property<DateTimeOffset>(nameof(OrganizationSubscription.LastProviderEventOccurredAt)).OriginalValue.ToUniversalTime();
            var currentOccurrence = subscription.LastProviderEventOccurredAt.ToUniversalTime();
            if (currentOccurrence < originalOccurrence)
                throw new InvalidOperationException("Subscription event ordering cannot move backwards.");

            var originalEventId = entry.Property<string?>(nameof(OrganizationSubscription.LastProviderEventId)).OriginalValue;
            if (subscription.State != OrganizationSubscriptionState.Deleted && currentOccurrence == originalOccurrence && originalEventId is not null &&
                (subscription.LastProviderEventId is null ||
                 string.CompareOrdinal(subscription.LastProviderEventId, originalEventId) < 0))
                throw new InvalidOperationException("Subscription event ordering cursor changed at the same timestamp.");
            if (currentOccurrence > originalOccurrence && string.IsNullOrWhiteSpace(subscription.LastProviderEventId))
                throw new InvalidOperationException("A newer subscription event requires an event identity.");

            var cursorAdvanced = currentOccurrence > originalOccurrence ||
                (currentOccurrence == originalOccurrence &&
                 subscription.LastProviderEventId is not null &&
                 (originalEventId is null ||
                  string.CompareOrdinal(subscription.LastProviderEventId, originalEventId) > 0));
            if (stateChanged)
            {
                if (!OrganizationSubscriptionLifecycle.CanTransition(originalState, subscription.State) &&
                    !(originalState == OrganizationSubscriptionState.Suspended &&
                      subscription.State == OrganizationSubscriptionState.Deleted &&
                      subscription.EarlyDeletionRequestedAt is not null))
                    throw new InvalidOperationException("Subscription state transition is not allowed.");
                var originalLifecycleVersion = entry.Property<int>(nameof(OrganizationSubscription.LifecycleVersion)).OriginalValue;
                var lifecycleAdvanced = subscription.LifecycleVersion == originalLifecycleVersion + 1;
                if (!cursorAdvanced && !lifecycleAdvanced)
                    throw new InvalidOperationException("A direct subscription state change requires an advanced provider event cursor.");
            }

            var originalLifecycleVersionValue = entry.Property<int>(nameof(OrganizationSubscription.LifecycleVersion)).OriginalValue;
            if (subscription.LifecycleVersion != originalLifecycleVersionValue &&
                subscription.LifecycleVersion != originalLifecycleVersionValue + 1)
                throw new InvalidOperationException("Subscription lifecycle version must advance by one.");
            var lifecycleVersionAdvanced = subscription.LifecycleVersion == originalLifecycleVersionValue + 1;
            if (lifecycleVersionAdvanced && !stateChanged)
                throw new InvalidOperationException("Subscription lifecycle version can only advance with a state transition.");
            var lifecycleTransition = stateChanged && lifecycleVersionAdvanced && !cursorAdvanced;

            EnsureLifecycleTimestamp(entry, nameof(OrganizationSubscription.ActivatedAt), subscription.ActivatedAt, subscription.State, OrganizationSubscriptionState.Active, subscription.LastProviderEventOccurredAt, lifecycleTransition: lifecycleTransition);
            EnsureLifecycleTimestamp(entry, nameof(OrganizationSubscription.PastDueAt), subscription.PastDueAt, subscription.State, OrganizationSubscriptionState.PastDue, subscription.LastProviderEventOccurredAt, lifecycleTransition: lifecycleTransition);
            EnsureDerivedLifecycleDeadline(entry, nameof(OrganizationSubscription.GraceEndsAt), subscription.GraceEndsAt, subscription.PastDueAt, OrganizationSubscriptionLifecycle.PaymentGracePeriod);
            EnsureLifecycleTimestamp(entry, nameof(OrganizationSubscription.ConstrainedAt), subscription.ConstrainedAt, subscription.State, OrganizationSubscriptionState.Constrained, subscription.LastProviderEventOccurredAt, lifecycleTransition: lifecycleTransition);
            EnsureLifecycleTimestamp(entry, nameof(OrganizationSubscription.SuspendedAt), subscription.SuspendedAt, subscription.State, OrganizationSubscriptionState.Suspended, subscription.LastProviderEventOccurredAt, lifecycleTransition: lifecycleTransition);
            EnsureDerivedLifecycleDeadline(entry, nameof(OrganizationSubscription.RetentionEndsAt), subscription.RetentionEndsAt, subscription.SuspendedAt, OrganizationSubscriptionLifecycle.FinalRetentionPeriod);
            EnsureLifecycleTimestamp(entry, nameof(OrganizationSubscription.RetainedAt), subscription.RetainedAt, subscription.State, OrganizationSubscriptionState.Retained, subscription.LastProviderEventOccurredAt, lifecycleTransition: lifecycleTransition);
            EnsureLifecycleTimestamp(entry, nameof(OrganizationSubscription.DeletedAt), subscription.DeletedAt, subscription.State, OrganizationSubscriptionState.Deleted, subscription.LastProviderEventOccurredAt, lifecycleTransition: lifecycleTransition);
        }
    }

    private static void EnsureUnchanged<T>(EntityEntry<OrganizationSubscription> entry, string propertyName, T currentValue)
    {
        if (!EqualityComparer<T>.Default.Equals(entry.Property<T>(propertyName).OriginalValue, currentValue))
            throw new InvalidOperationException($"Subscription {propertyName} is immutable.");
    }

    private static void EnsureTimestampUnchanged(EntityEntry<OrganizationSubscription> entry, string propertyName, DateTimeOffset currentValue)
    {
        var originalValue = entry.Property<DateTimeOffset>(propertyName).OriginalValue;
        if (originalValue.ToUniversalTime() != currentValue.ToUniversalTime())
            throw new InvalidOperationException($"Subscription {propertyName} is immutable.");
    }

    private static void EnsureBoundReference(EntityEntry<OrganizationSubscription> entry, string propertyName, string? currentValue, bool allowClearOnDeletion)
    {
        var originalValue = entry.Property<string?>(propertyName).OriginalValue;
        if (allowClearOnDeletion && currentValue is not null)
            throw new InvalidOperationException($"Subscription {propertyName} must remain cleared after deletion.");
        if (originalValue is not null && !string.Equals(originalValue, currentValue, StringComparison.Ordinal) && !(allowClearOnDeletion && currentValue is null))
            throw new InvalidOperationException($"Subscription {propertyName} cannot be replaced or removed.");
    }

    private static void EnsureLifecycleTimestamp(
        EntityEntry<OrganizationSubscription> entry,
        string propertyName,
        DateTimeOffset? currentValue,
        OrganizationSubscriptionState currentState,
        OrganizationSubscriptionState expectedState,
        DateTimeOffset currentCursor,
        bool allowFuture = false,
        bool lifecycleTransition = false)
    {
        var originalValue = entry.Property<DateTimeOffset?>(propertyName).OriginalValue;
        if (originalValue is not null)
        {
            if (currentValue is null || currentValue.Value.ToUniversalTime() != originalValue.Value.ToUniversalTime())
                throw new InvalidOperationException($"Subscription {propertyName} is bind-once.");
            return;
        }

        if (currentValue is null)
            return;

        if (currentState != expectedState ||
            !lifecycleTransition && !allowFuture && currentValue.Value.ToUniversalTime() != currentCursor.ToUniversalTime() ||
            !lifecycleTransition && allowFuture && currentValue.Value.ToUniversalTime() < currentCursor.ToUniversalTime())
            throw new InvalidOperationException($"Subscription {propertyName} must match its lifecycle event.");
    }

    private static void EnsureDerivedLifecycleDeadline(
        EntityEntry<OrganizationSubscription> entry,
        string propertyName,
        DateTimeOffset? currentValue,
        DateTimeOffset? lifecycleTimestamp,
        TimeSpan period)
    {
        var originalValue = entry.Property<DateTimeOffset?>(propertyName).OriginalValue;
        if (originalValue is not null)
        {
            if (currentValue is null || currentValue.Value.ToUniversalTime() != originalValue.Value.ToUniversalTime())
                throw new InvalidOperationException($"Subscription {propertyName} is bind-once.");
            return;
        }

        if (currentValue is null)
            return;
        if (lifecycleTimestamp is null ||
            currentValue.Value.ToUniversalTime() != lifecycleTimestamp.Value.ToUniversalTime().Add(period))
            throw new InvalidOperationException($"Subscription {propertyName} must match its lifecycle event.");
    }

    private static DateTimeOffset? NormalizeOptionalTimestamp(DateTimeOffset? value, string name)
    {
        if (value is null)
            return null;
        var normalized = value.Value.ToUniversalTime();
        if (normalized == default)
            throw new InvalidOperationException($"{name} is invalid.");
        return normalized;
    }

    private void EnsureGovernedReleaseCatalogIsImmutable()
    {
        if (ChangeTracker.Entries()
            .Any(x => x.Entity is Models.IGovernedReleaseCatalogEntity
                      && (x.State is EntityState.Modified or EntityState.Deleted)))
            throw new InvalidOperationException("Governed release catalog records are immutable.");
    }

    private void EnsureWorkspacePermissionAuditIsAppendOnly()
    {
        var mutatedAuditRecord = ChangeTracker.Entries<Models.WorkspacePermissionAuditRecordEntity>()
            .FirstOrDefault(entry => entry.State is EntityState.Modified or EntityState.Deleted);
        if (mutatedAuditRecord is not null)
            throw new InvalidOperationException("Workspace permission audit records are append-only.");
    }

    private void EnsureAzureOperationTransitionsAreAppendOnly()
    {
        var mutatedTransition = ChangeTracker.Entries<Models.AzureProviderOperationTransitionEntity>()
            .FirstOrDefault(entry => entry.State is EntityState.Modified or EntityState.Deleted);
        if (mutatedTransition is not null)
            throw new InvalidOperationException("Azure provider operation transitions are append-only.");
    }

    private void EnsureElsaInstanceAuditIsAppendOnly()
    {
        var mutatedAuditRecord = ChangeTracker.Entries<Models.ElsaInstanceAuditEventEntity>()
            .FirstOrDefault(entry => entry.State is EntityState.Modified or EntityState.Deleted);
        if (mutatedAuditRecord is not null)
            throw new InvalidOperationException("Elsa instance audit events are append-only.");
    }

    private void EnsureElsaInstanceDurableRowsAreNotDeleted()
    {
        if (ChangeTracker.Entries<Models.ElsaInstanceEntity>().Any(x => x.State == EntityState.Deleted))
            throw new InvalidOperationException("Elsa instances are tombstoned and cannot be deleted.");
        if (ChangeTracker.Entries<Models.ElsaInstanceMigrationEntity>().Any(x => x.State == EntityState.Deleted))
            throw new InvalidOperationException("Elsa instance migrations are durable and cannot be deleted.");
        if (ChangeTracker.Entries<Models.ElsaInstanceOperationEntity>().Any(x => x.State == EntityState.Deleted))
            throw new InvalidOperationException("Elsa instance operations are durable and cannot be deleted.");
    }

    private void EnsureElsaInstanceIntentRevisionsAreAppendOnly()
    {
        if (ChangeTracker.Entries<Models.ElsaInstanceIntentRevisionEntity>()
            .Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Elsa instance intent revisions are append-only.");
    }

    private void EnsureElsaInstanceLifecycleOutboxIsAppendOnly()
    {
        foreach (var entry in ChangeTracker.Entries<Models.ElsaInstanceLifecycleOutboxEntity>()
                     .Where(x => x.State is EntityState.Modified or EntityState.Deleted))
        {
            if (entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Elsa instance lifecycle outbox records are append-only.");

            var changedProperties = entry.Properties
                .Where(property => property.IsModified)
                .Select(property => property.Metadata.Name)
                .ToArray();
            if (changedProperties.Any(property => property is not (
                    nameof(Models.ElsaInstanceLifecycleOutboxEntity.QuarantinedAt) or
                    nameof(Models.ElsaInstanceLifecycleOutboxEntity.QuarantineCode))))
                throw new InvalidOperationException("Elsa instance lifecycle outbox payload is append-only.");

            var originalAt = (DateTimeOffset?)entry.Property(nameof(Models.ElsaInstanceLifecycleOutboxEntity.QuarantinedAt)).OriginalValue;
            var currentAt = entry.Entity.QuarantinedAt;
            var originalCode = (string?)entry.Property(nameof(Models.ElsaInstanceLifecycleOutboxEntity.QuarantineCode)).OriginalValue;
            var currentCode = entry.Entity.QuarantineCode;
            if (originalAt is not null || originalCode is not null ||
                (currentAt is null && currentCode is null))
                throw new InvalidOperationException("Elsa instance lifecycle outbox quarantine metadata is append-only.");
            if (currentAt is null || !string.Equals(currentCode, "outbox.invalid", StringComparison.Ordinal))
                throw new InvalidOperationException("Elsa instance lifecycle outbox quarantine metadata is invalid.");
        }
    }

    private void EnsureElsaInstanceResolvedPlansAreAppendOnly()
    {
        if (ChangeTracker.Entries<Models.ElsaInstanceResolvedPlanEntity>()
            .Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Elsa instance resolved plans are append-only.");
    }

    private void EnsureElsaInstanceRecoveryRequestsAreAppendOnly()
    {
        if (ChangeTracker.Entries<Models.ElsaInstanceRecoveryRequestEntity>()
            .Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Elsa instance recovery requests are append-only.");
    }

    private void EnsureAzureProviderRecoveryObservationsAreAppendOnly()
    {
        if (ChangeTracker.Entries<Models.AzureProviderRecoveryObservationEntity>()
            .Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Azure provider recovery observations are append-only.");
    }

    private void EnsureAzureProviderAssignmentRebindsAreAppendOnly()
    {
        if (ChangeTracker.Entries<Models.AzureProviderAssignmentRebindEntity>()
            .Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Azure provider assignment rebinds are append-only.");
    }

    private void ValidateAzureProviderRecoveryObservations()
    {
        foreach (var entry in ChangeTracker.Entries<Models.AzureProviderRecoveryObservationEntity>()
                     .Where(x => x.State == EntityState.Added))
        {
            var value = entry.Entity;
            var observation = value.ToRecord();
            observation.Validate();
            if (value.Id == Guid.Empty ||
                !string.Equals(value.NaturalKey, observation.ComputeNaturalKey(), StringComparison.Ordinal) ||
                !string.Equals(value.RecordDigest, observation.ComputeRecordDigest(value.Id), StringComparison.Ordinal))
                throw new InvalidOperationException("Azure provider recovery observation digest is invalid.");
        }
    }

    private void EnsureManagedElsaHandoffRowsAreAppendOnly()
    {
        if (ChangeTracker.Entries<Models.ManagedElsaHandoffReplayEntity>()
            .Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Managed Elsa handoff replay records are append-only.");
        if (ChangeTracker.Entries<Models.ManagedElsaHandoffAuditEventEntity>()
            .Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Managed Elsa handoff audit events are append-only.");
    }

    private void ValidateManagedElsaHandoffRows()
    {
        foreach (var entry in ChangeTracker.Entries<Models.ManagedElsaHandoffReplayEntity>()
                     .Where(x => x.State == EntityState.Added))
        {
            var replay = entry.Entity;
            replay.Jti = RequireSafeToken(replay.Jti, nameof(replay.Jti), 128);
            replay.ExpiresAt = replay.ExpiresAt.ToUniversalTime();
            replay.ConsumedAt = replay.ConsumedAt.ToUniversalTime();
            if (replay.ConsumedAt == default || replay.ExpiresAt <= replay.ConsumedAt)
                throw new InvalidOperationException("Managed Elsa handoff replay lifetime is invalid.");
        }

        foreach (var entry in ChangeTracker.Entries<Models.ManagedElsaHandoffAuditEventEntity>()
                     .Where(x => x.State == EntityState.Added))
        {
            var audit = entry.Entity;
            if (audit.Id == Guid.Empty)
                audit.Id = Guid.NewGuid();
            audit.Action = RequireSafeCode(audit.Action, nameof(audit.Action));
            audit.Jti = OptionalSafeToken(audit.Jti, nameof(audit.Jti), 128) ?? "";
            audit.Audience = OptionalAudience(audit.Audience, nameof(audit.Audience));
            if (audit.BindingVersion is <= 0)
                throw new InvalidOperationException("Managed Elsa handoff binding version must be positive when supplied.");
            audit.CorrelationId = OptionalSafeReference(audit.CorrelationId, nameof(audit.CorrelationId), 128);
            if (audit.AccountId == Guid.Empty || audit.OrganizationId == Guid.Empty || audit.InstanceId == Guid.Empty)
                throw new InvalidOperationException("Managed Elsa handoff audit identifiers must be non-empty when supplied.");
            audit.OccurredAt = audit.OccurredAt.ToUniversalTime();
            if (audit.OccurredAt == default)
                throw new InvalidOperationException("Managed Elsa handoff audit timestamp is required.");
        }
    }

    private void ValidateElsaInstancePersistence()
    {
        foreach (var entry in ChangeTracker.Entries<Models.ElsaInstanceEntity>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            var instance = entry.Entity;
            if (instance.Id == Guid.Empty || instance.OrganizationId == Guid.Empty || instance.WorkspaceId == Guid.Empty)
                throw new InvalidOperationException("An Elsa instance requires stable ownership identifiers.");
            RequireDisplayName(instance.Name, nameof(instance.Name), 256);
            instance.Slug = RequireSlug(instance.Slug);
            if (entry.State == EntityState.Modified &&
                !string.Equals(instance.Slug, entry.Property(x => x.Slug).OriginalValue, StringComparison.Ordinal))
                throw new InvalidOperationException("An Elsa instance slug is immutable.");
            instance.DistributionId = RequireCatalogValue(instance.DistributionId, nameof(instance.DistributionId));
            instance.ReleaseLine = RequireCatalogValue(instance.ReleaseLine, nameof(instance.ReleaseLine));
            instance.RequestedVersion = OptionalCatalogValue(instance.RequestedVersion, nameof(instance.RequestedVersion));
            if (instance.RequestedVersion is not null && !BelongsToReleaseLine(instance.ReleaseLine, instance.RequestedVersion))
                throw new InvalidOperationException("Requested version must belong to the selected release line.");
            instance.Channel = RequireCatalogValue(instance.Channel, nameof(instance.Channel));
            instance.PatchUpdates = RequireCatalogValue(instance.PatchUpdates, nameof(instance.PatchUpdates));
            instance.MinorUpdates = RequireCatalogValue(instance.MinorUpdates, nameof(instance.MinorUpdates));
            instance.MajorMigrations = RequireCatalogValue(instance.MajorMigrations, nameof(instance.MajorMigrations));
            instance.PreviewManifestDigest = OptionalPreviewManifestDigest(
                instance.PreviewManifestDigest, nameof(instance.PreviewManifestDigest));
            if (instance.PreviewManifestDigest is not null && instance.RequestedVersion is null)
                throw new InvalidOperationException("Preview manifest consent requires an explicit requested version.");
            instance.TopologyId = RequireCatalogValue(instance.TopologyId, nameof(instance.TopologyId));
            instance.FeaturePresetId = OptionalCatalogValue(instance.FeaturePresetId, nameof(instance.FeaturePresetId));
            ValidateFeatureOverrides(instance.FeatureOverridesJson);
            instance.PackagePolicy = OptionalCatalogValue(instance.PackagePolicy, nameof(instance.PackagePolicy));
            instance.ConfigurationShapeRevisionId = OptionalCatalogValue(instance.ConfigurationShapeRevisionId, nameof(instance.ConfigurationShapeRevisionId));
            instance.TargetMode = RequireCatalogValue(instance.TargetMode, nameof(instance.TargetMode));
            instance.RegionCode = RequireCatalogValue(instance.RegionCode, nameof(instance.RegionCode));
            instance.IsolationProfile = RequireCatalogValue(instance.IsolationProfile, nameof(instance.IsolationProfile));
            instance.CapacityProfile = RequireCatalogValue(instance.CapacityProfile, nameof(instance.CapacityProfile));
            instance.NetworkOutcome = RequireCatalogValue(instance.NetworkOutcome, nameof(instance.NetworkOutcome));
            instance.DomainOutcome = RequireCatalogValue(instance.DomainOutcome, nameof(instance.DomainOutcome));
            instance.DesiredStateRevisionId = OptionalSafeReference(instance.DesiredStateRevisionId, nameof(instance.DesiredStateRevisionId), 128);
            var hasPlan = !string.IsNullOrWhiteSpace(instance.ResolvedPlanId);
            instance.ResolvedPlanId = OptionalSafeReference(instance.ResolvedPlanId, nameof(instance.ResolvedPlanId), 128);
            instance.ResolvedPlanContentHash = OptionalSha256Digest(instance.ResolvedPlanContentHash, nameof(instance.ResolvedPlanContentHash));
            instance.ResolvedPlanUri = OptionalPlanUri(instance.ResolvedPlanUri, nameof(instance.ResolvedPlanUri));
            var planValues = new object?[]
            {
                instance.ResolvedPlanSchemaVersion,
                instance.ResolvedPlanContentHash,
                instance.ResolvedPlanUri
            };
            if (!hasPlan && planValues.Any(x => x is not null))
                throw new InvalidOperationException("Resolved plan fields must be persisted together.");
            if (hasPlan && (instance.ResolvedPlanSchemaVersion is null or < 1 ||
                            string.IsNullOrWhiteSpace(instance.ResolvedPlanContentHash) ||
                            string.IsNullOrWhiteSpace(instance.ResolvedPlanUri)))
                throw new InvalidOperationException("Resolved plan fields must be persisted together.");
            if (hasPlan)
                RequireInstancePlanUri(instance.ResolvedPlanUri!, nameof(instance.ResolvedPlanUri), instance.WorkspaceId, instance.Id, instance.ResolvedPlanId!);

            instance.CurrentReleaseDistributionId = OptionalCatalogValue(instance.CurrentReleaseDistributionId, nameof(instance.CurrentReleaseDistributionId));
            instance.CurrentReleaseLine = OptionalCatalogValue(instance.CurrentReleaseLine, nameof(instance.CurrentReleaseLine));
            instance.CurrentReleaseVersion = OptionalCatalogValue(instance.CurrentReleaseVersion, nameof(instance.CurrentReleaseVersion));
            instance.CurrentReleaseManifestDigest = OptionalSha256Digest(instance.CurrentReleaseManifestDigest, nameof(instance.CurrentReleaseManifestDigest));
            var currentRelease = new string?[]
            {
                instance.CurrentReleaseDistributionId,
                instance.CurrentReleaseLine,
                instance.CurrentReleaseVersion,
                instance.CurrentReleaseManifestDigest,
                instance.CurrentReleaseComponentDigestsJson
            };
            var hasCurrentRelease = currentRelease.Any(x => !string.IsNullOrWhiteSpace(x));
            if (hasCurrentRelease && (!hasPlan || currentRelease.Any(string.IsNullOrWhiteSpace)))
                throw new InvalidOperationException("Current resolved release fields must be persisted together.");
            if (hasCurrentRelease && !BelongsToReleaseLine(instance.CurrentReleaseLine!, instance.CurrentReleaseVersion!))
                throw new InvalidOperationException("Current release version must belong to the selected release line.");
            ValidateComponentDigests(instance.CurrentReleaseComponentDigestsJson);
            instance.CurrentDeploymentId = OptionalSafeReference(instance.CurrentDeploymentId, nameof(instance.CurrentDeploymentId), 128);
            instance.CurrentDeploymentRevisionId = OptionalSafeReference(instance.CurrentDeploymentRevisionId, nameof(instance.CurrentDeploymentRevisionId), 128);
            var endpointProperty = entry.Property(x => x.CurrentDeploymentEndpointUri);
            var allowLegacyEndpoint = entry.State == EntityState.Modified && !endpointProperty.IsModified;
            var persistedEndpoint = instance.CurrentDeploymentEndpointUri;
            instance.CurrentDeploymentEndpointUri = OptionalManagedEndpointOrigin(
                instance.CurrentDeploymentEndpointUri,
                allowLegacyInvalid: allowLegacyEndpoint);
            if (instance.CurrentDeploymentManagedHandoff &&
                (instance.CurrentDeploymentId is null || instance.CurrentDeploymentEndpointUri is null))
            {
                // A legacy-invalid endpoint dropped above takes the handoff bound to it along; any other
                // handoff without a current deployment endpoint is an invalid write.
                if (allowLegacyEndpoint && persistedEndpoint is not null && instance.CurrentDeploymentEndpointUri is null &&
                    !entry.Property(x => x.CurrentDeploymentManagedHandoff).IsModified)
                    instance.CurrentDeploymentManagedHandoff = false;
                else
                    throw new InvalidOperationException("A managed handoff must belong to a current deployment with an endpoint.");
            }
            instance.PlacementAssignmentId = OptionalSafeReference(instance.PlacementAssignmentId, nameof(instance.PlacementAssignmentId), 128);
            instance.ElsaTenantId = OptionalSafeReference(instance.ElsaTenantId, nameof(instance.ElsaTenantId), 128);
            var tenantAudience = OptionalAudience(instance.ElsaTenantAudience, nameof(instance.ElsaTenantAudience));
            if (tenantAudience is not null)
            {
                var expectedAudience = $"urn:elsa:instance:{instance.Id:D}".ToLowerInvariant();
                instance.ElsaTenantAudience = RequireExactAudience(tenantAudience, expectedAudience);
            }
            instance.LastOperationId = OptionalSafeReference(instance.LastOperationId, nameof(instance.LastOperationId), 128);

            if (instance.DeletedAt is null && instance.ObservedLifecycle == ElsaObservedLifecycle.Deleted)
                throw new InvalidOperationException("A deleted instance requires a tombstone timestamp.");
            if (instance.DeletedAt is not null &&
                (instance.ObservedLifecycle != ElsaObservedLifecycle.Deleted || instance.DesiredLifecycle != ElsaDesiredLifecycle.Deleting))
                throw new InvalidOperationException("Only a deleting instance can carry a deleted tombstone.");

            EnsureDefined(instance.DesiredLifecycle, nameof(instance.DesiredLifecycle));
            EnsureDefined(instance.ObservedLifecycle, nameof(instance.ObservedLifecycle));
            EnsureDefined(instance.Health, nameof(instance.Health));
            if (entry.State == EntityState.Added && instance.Version < 1)
                instance.Version = 1;
            if (entry.State == EntityState.Modified)
            {
                var originalVersion = entry.Property(x => x.Version).OriginalValue;
                instance.Version = checked(originalVersion + 1);
            }
        }

        foreach (var entry in ChangeTracker.Entries<Models.ElsaInstanceOperationEntity>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            var operation = entry.Entity;
            EnsureDefined(operation.Action, nameof(operation.Action));
            EnsureDefined(operation.State, nameof(operation.State));
            if (operation.Id == Guid.Empty || operation.OrganizationId == Guid.Empty || operation.WorkspaceId == Guid.Empty)
                throw new InvalidOperationException("An instance operation requires stable ownership identifiers.");
            operation.IdempotencyScope = RequireSafeReference(operation.IdempotencyScope, nameof(operation.IdempotencyScope), 256);
            operation.IdempotencyKey = RequireSafeToken(operation.IdempotencyKey, nameof(operation.IdempotencyKey), 128);
            operation.RequestHash = RequireCanonicalHash(operation.RequestHash, nameof(operation.RequestHash));
            var recoveryEnvelopeCount = new[]
            {
                operation.RecoveryIdempotencyScope,
                operation.RecoveryIdempotencyKey,
                operation.RecoveryRequestHash
            }.Count(x => x is not null);
            if (recoveryEnvelopeCount is not (0 or 3))
                throw new InvalidOperationException("Recovery idempotency evidence must be complete.");
            if (recoveryEnvelopeCount == 3)
            {
                operation.RecoveryIdempotencyScope = RequireSafeReference(operation.RecoveryIdempotencyScope!, nameof(operation.RecoveryIdempotencyScope), 256);
                operation.RecoveryIdempotencyKey = RequireSafeToken(operation.RecoveryIdempotencyKey!, nameof(operation.RecoveryIdempotencyKey), 128);
                operation.RecoveryRequestHash = RequireCanonicalHash(operation.RecoveryRequestHash!, nameof(operation.RecoveryRequestHash));
            }
            operation.WorkerId = OptionalSafeToken(operation.WorkerId, nameof(operation.WorkerId), 256);
            operation.LeaseTokenHash = OptionalCanonicalHash(operation.LeaseTokenHash, nameof(operation.LeaseTokenHash));
            operation.DesiredStateRevisionId = OptionalSafeReference(operation.DesiredStateRevisionId, nameof(operation.DesiredStateRevisionId), 128);
            operation.ResolvedPlanId = OptionalSafeReference(operation.ResolvedPlanId, nameof(operation.ResolvedPlanId), 128);
            operation.FailureCode = OptionalSafeCode(operation.FailureCode, nameof(operation.FailureCode));
            operation.FailureSummary = operation.FailureCode ??
                (string.IsNullOrWhiteSpace(operation.FailureSummary) ? null : "operation.failure");
            operation.ReconciliationEvidenceFingerprint = OptionalCanonicalHash(
                operation.ReconciliationEvidenceFingerprint, nameof(operation.ReconciliationEvidenceFingerprint));
            operation.ReconciliationDiagnosticCode = OptionalSafeCode(
                operation.ReconciliationDiagnosticCode, nameof(operation.ReconciliationDiagnosticCode));
            if ((operation.ReconciliationRetryEvidenceReference is null) !=
                (operation.ReconciliationRetryEvidenceDigest is null))
                throw new InvalidOperationException("Provider reconciliation retry evidence must be complete.");
            if (operation.ReconciliationRetryEvidenceReference is not null)
            {
                var evidence = new ElsaControl.Deployment.Core.Instances.ElsaInstanceProviderRetryEvidence(
                    operation.ReconciliationRetryEvidenceReference,
                    operation.ReconciliationRetryEvidenceDigest!);
                operation.ReconciliationRetryEvidenceReference = evidence.Reference;
                operation.ReconciliationRetryEvidenceDigest = evidence.Digest;
            }
            if (operation.ReconciledObservedLifecycle is { } reconciledLifecycle)
                EnsureDefined(reconciledLifecycle, nameof(operation.ReconciledObservedLifecycle));
            if (operation.ReconciledHealth is { } reconciledHealth)
                EnsureDefined(reconciledHealth, nameof(operation.ReconciledHealth));
            if (operation.ReconciliationVersion < 0 || operation.ReconciledInstanceVersion is < 1)
                throw new InvalidOperationException("Provider reconciliation versions are invalid.");
            operation.DeletionEvidenceFingerprint = OptionalCanonicalHash(
                operation.DeletionEvidenceFingerprint, nameof(operation.DeletionEvidenceFingerprint));
            operation.DeletionDiagnosticCode = OptionalSafeCode(
                operation.DeletionDiagnosticCode, nameof(operation.DeletionDiagnosticCode));
            if ((operation.DeletionEvidenceReference is null) != (operation.DeletionEvidenceDigest is null))
                throw new InvalidOperationException("Deletion evidence must be complete.");
            if (operation.DeletionEvidenceReference is not null)
            {
                var evidence = new ElsaControl.Deployment.Core.Instances.ElsaInstanceCleanupEvidence(
                    operation.DeletionEvidenceReference,
                    operation.DeletionEvidenceDigest!);
                operation.DeletionEvidenceReference = evidence.Reference;
                operation.DeletionEvidenceDigest = evidence.Digest;
            }
            if (operation.ExpectedVersion < 1 || operation.AttemptNumber < 1)
                throw new InvalidOperationException("An instance operation requires positive version and attempt values.");
            if (operation.InstanceId is not null && operation.InstanceId == Guid.Empty)
                throw new InvalidOperationException("An instance operation requires a non-empty instance ID.");
            if (operation.InstanceId is null && operation.Action != ElsaInstanceOperationAction.Create)
                throw new InvalidOperationException("Only create operations may omit an instance ID.");
            if (operation.State == ElsaInstanceOperationState.WaitingForPriorOperation &&
                operation.Action != ElsaInstanceOperationAction.Delete)
                throw new InvalidOperationException("Only delete operations may wait for a prior operation.");

            if (entry.State == EntityState.Modified)
            {
                foreach (var property in new[]
                         {
                             nameof(Models.ElsaInstanceOperationEntity.InstanceId),
                             nameof(Models.ElsaInstanceOperationEntity.OrganizationId),
                             nameof(Models.ElsaInstanceOperationEntity.WorkspaceId),
                             nameof(Models.ElsaInstanceOperationEntity.Action),
                             nameof(Models.ElsaInstanceOperationEntity.IdempotencyScope),
                             nameof(Models.ElsaInstanceOperationEntity.IdempotencyKey),
                             nameof(Models.ElsaInstanceOperationEntity.RequestHash),
                             nameof(Models.ElsaInstanceOperationEntity.ExpectedVersion),
                             nameof(Models.ElsaInstanceOperationEntity.AcceptedAt),
                             nameof(Models.ElsaInstanceOperationEntity.CreatedAt)
                         })
                    EnsureUnchanged(entry, property, entry.Property(property).CurrentValue,
                        "Instance operation envelope fields are immutable.");

                var originalState = (ElsaInstanceOperationState)entry.Property(nameof(Models.ElsaInstanceOperationEntity.State)).OriginalValue!;
                EnsureDefined(originalState, nameof(Models.ElsaInstanceOperationEntity.State));
                var isRecoveryResume = originalState == ElsaInstanceOperationState.RecoveryRequired &&
                    operation.State == ElsaInstanceOperationState.Queued &&
                    operation.AttemptNumber == (int)entry.Property(nameof(Models.ElsaInstanceOperationEntity.AttemptNumber)).OriginalValue! + 1;
                if (!ElsaInstanceOperation.CanTransition(originalState, operation.State) && !isRecoveryResume)
                    throw new InvalidOperationException("Instance operation state transition is not allowed.");
                var originalAttemptNumber = (int)entry.Property(nameof(Models.ElsaInstanceOperationEntity.AttemptNumber)).OriginalValue!;
                if (operation.AttemptNumber < originalAttemptNumber)
                    throw new InvalidOperationException("Instance operation attempt number cannot decrease.");
            }
        }


        foreach (var entry in ChangeTracker.Entries<Models.ElsaInstanceRecoveryRequestEntity>()
                     .Where(x => x.State == EntityState.Added))
        {
            var recovery = entry.Entity;
            if (recovery.Id == Guid.Empty || recovery.OrganizationId == Guid.Empty ||
                recovery.WorkspaceId == Guid.Empty || recovery.InstanceId == Guid.Empty ||
                recovery.OperationId == Guid.Empty || recovery.AttemptNumber < 2)
                throw new InvalidOperationException("A recovery request requires stable ownership and attempt identifiers.");
            recovery.IdempotencyScope = RequireSafeReference(recovery.IdempotencyScope, nameof(recovery.IdempotencyScope), 256);
            recovery.IdempotencyKey = RequireSafeToken(recovery.IdempotencyKey, nameof(recovery.IdempotencyKey), 128);
            recovery.RequestHash = RequireCanonicalHash(recovery.RequestHash, nameof(recovery.RequestHash));
            var recoveryObservationMetadataCount = new object?[]
            {
                recovery.RecoveryObservationReference,
                recovery.RecoveryObservationDigest,
                recovery.ObservedLifecycleAttemptNumber,
                recovery.ObservedInstanceVersion
            }.Count(x => x is not null);
            if (recoveryObservationMetadataCount is not (0 or 4))
                throw new InvalidOperationException("Recovery observation metadata must be complete.");
            if (recoveryObservationMetadataCount == 4)
            {
                var evidence = new ElsaControl.Deployment.Core.Instances.ElsaInstanceProviderRetryEvidence(
                    recovery.RecoveryObservationReference!, recovery.RecoveryObservationDigest!);
                recovery.RecoveryObservationReference = evidence.Reference;
                recovery.RecoveryObservationDigest = evidence.Digest;
                if (recovery.ObservedLifecycleAttemptNumber < 1 || recovery.ObservedInstanceVersion < 1)
                    throw new InvalidOperationException("Recovery observation versions are invalid.");
            }
            if (recovery.AzureDeleteRecoveryAuthority is not null &&
                (!AzureProviderDeleteRecoveryAuthority.TryParse(
                    recovery.AzureDeleteRecoveryAuthority, out var authority) || authority is null ||
                 authority.LifecycleAttemptNumber != recovery.AttemptNumber))
                throw new InvalidOperationException("Azure delete recovery authority is invalid.");
            recovery.AcceptedAt = recovery.AcceptedAt.ToUniversalTime();
            recovery.CreatedAt = recovery.CreatedAt.ToUniversalTime();
            if (recovery.AcceptedAt == default || recovery.CreatedAt == default || recovery.CreatedAt < recovery.AcceptedAt)
                throw new InvalidOperationException("Recovery request timestamps are invalid.");
        }

        foreach (var entry in ChangeTracker.Entries<Models.ElsaInstanceIntentRevisionEntity>()
                     .Where(x => x.State == EntityState.Added))
        {
            var revision = entry.Entity;
            if (revision.Id == Guid.Empty || revision.OrganizationId == Guid.Empty ||
                revision.WorkspaceId == Guid.Empty || revision.InstanceId == Guid.Empty ||
                revision.RevisionNumber < 1)
                throw new InvalidOperationException("An Elsa instance intent revision requires stable ownership and version identifiers.");

            revision.ContentHash = RequireCanonicalHash(revision.ContentHash, nameof(revision.ContentHash));
            revision.DistributionId = RequireCatalogValue(revision.DistributionId, nameof(revision.DistributionId));
            revision.ReleaseLine = RequireCatalogValue(revision.ReleaseLine, nameof(revision.ReleaseLine));
            revision.RequestedVersion = OptionalCatalogValue(revision.RequestedVersion, nameof(revision.RequestedVersion));
            if (revision.RequestedVersion is not null && !BelongsToReleaseLine(revision.ReleaseLine, revision.RequestedVersion))
                throw new InvalidOperationException("Requested version must belong to the selected release line.");
            revision.Channel = RequireCatalogValue(revision.Channel, nameof(revision.Channel));
            revision.PatchUpdates = RequireCatalogValue(revision.PatchUpdates, nameof(revision.PatchUpdates));
            revision.MinorUpdates = RequireCatalogValue(revision.MinorUpdates, nameof(revision.MinorUpdates));
            revision.MajorMigrations = RequireCatalogValue(revision.MajorMigrations, nameof(revision.MajorMigrations));
            revision.PreviewManifestDigest = OptionalPreviewManifestDigest(
                revision.PreviewManifestDigest, nameof(revision.PreviewManifestDigest));
            if (revision.PreviewManifestDigest is not null && revision.RequestedVersion is null)
                throw new InvalidOperationException("Preview manifest consent requires an explicit requested version.");
            revision.TopologyId = RequireCatalogValue(revision.TopologyId, nameof(revision.TopologyId));
            revision.FeaturePresetId = OptionalCatalogValue(revision.FeaturePresetId, nameof(revision.FeaturePresetId));
            ValidateFeatureOverrides(revision.FeatureOverridesJson);
            revision.PackagePolicy = OptionalCatalogValue(revision.PackagePolicy, nameof(revision.PackagePolicy));
            revision.ConfigurationShapeRevisionId = OptionalCatalogValue(revision.ConfigurationShapeRevisionId, nameof(revision.ConfigurationShapeRevisionId));
            revision.TargetMode = RequireCatalogValue(revision.TargetMode, nameof(revision.TargetMode));
            revision.RegionCode = RequireCatalogValue(revision.RegionCode, nameof(revision.RegionCode));
            revision.IsolationProfile = RequireCatalogValue(revision.IsolationProfile, nameof(revision.IsolationProfile));
            revision.CapacityProfile = RequireCatalogValue(revision.CapacityProfile, nameof(revision.CapacityProfile));
            revision.NetworkOutcome = RequireCatalogValue(revision.NetworkOutcome, nameof(revision.NetworkOutcome));
            revision.DomainOutcome = RequireCatalogValue(revision.DomainOutcome, nameof(revision.DomainOutcome));
            EnsureDefined(revision.DesiredLifecycle, nameof(revision.DesiredLifecycle));

            var instance = ChangeTracker.Entries<Models.ElsaInstanceEntity>()
                .Select(x => x.Entity)
                .FirstOrDefault(x => x.Id == revision.InstanceId);
            instance ??= ElsaInstances.Find(revision.InstanceId);
            if (instance is not null &&
                (instance.OrganizationId != revision.OrganizationId || instance.WorkspaceId != revision.WorkspaceId))
                throw new InvalidOperationException("Intent revision ownership must match its instance.");
        }

        foreach (var entry in ChangeTracker.Entries<Models.ElsaInstanceResolvedPlanEntity>()
                     .Where(x => x.State == EntityState.Added))
        {
            var plan = entry.Entity;
            if (plan.Id == Guid.Empty || plan.OrganizationId == Guid.Empty || plan.WorkspaceId == Guid.Empty ||
                plan.InstanceId == Guid.Empty || plan.SchemaVersion < 1 ||
                string.IsNullOrWhiteSpace(plan.SerializedPlan) || plan.SerializedPlan.Length > 1_048_576)
                throw new InvalidOperationException("A resolved plan requires bounded ownership and content.");

            plan.PlanId = RequireSafeReference(plan.PlanId, nameof(plan.PlanId), 128);
            plan.ContentHash = OptionalSha256Digest(plan.ContentHash, nameof(plan.ContentHash))
                ?? throw new InvalidOperationException("Resolved plan content hash is required.");
            RequireInstancePlanUri(plan.PlanUri, nameof(plan.PlanUri), plan.WorkspaceId, plan.InstanceId, plan.PlanId);

            try
            {
                var typedPlan = ResolvedElsaApplicationPlanSerialization.Deserialize(plan.SerializedPlan);
                var canonical = ResolvedElsaApplicationPlanSerialization.Serialize(typedPlan);
                if (!string.Equals(canonical, plan.SerializedPlan, StringComparison.Ordinal) ||
                    !string.Equals(ResolvedElsaApplicationPlanSerialization.ComputeContentHash(typedPlan), plan.ContentHash, StringComparison.Ordinal))
                    throw new InvalidOperationException();
                if (typedPlan.SchemaVersion != plan.SchemaVersion.ToString(CultureInfo.InvariantCulture))
                    throw new InvalidOperationException();
                var findings = ResolvedElsaApplicationPlanValidator.Validate(typedPlan);
                if (findings.Count > 0)
                    throw new InvalidOperationException();
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
            {
                throw new InvalidOperationException("Resolved plan content is invalid.");
            }

            var instance = ChangeTracker.Entries<Models.ElsaInstanceEntity>()
                .Select(x => x.Entity)
                .FirstOrDefault(x => x.Id == plan.InstanceId);
            instance ??= ElsaInstances.Find(plan.InstanceId);
            if (instance is null || instance.OrganizationId != plan.OrganizationId || instance.WorkspaceId != plan.WorkspaceId)
                throw new InvalidOperationException("Resolved plan ownership must match its instance.");
        }

        foreach (var entry in ChangeTracker.Entries<Models.ElsaInstanceLifecycleOutboxEntity>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            var outbox = entry.Entity;
            if (entry.State == EntityState.Added)
            {
                EnsureDefined(outbox.Action, nameof(outbox.Action));
                if (outbox.Id == Guid.Empty || outbox.OrganizationId == Guid.Empty ||
                    outbox.WorkspaceId == Guid.Empty || outbox.InstanceId == Guid.Empty ||
                    outbox.OperationId == Guid.Empty)
                    throw new InvalidOperationException("An Elsa instance lifecycle outbox record requires stable ownership identifiers.");
                outbox.RequestHash = RequireCanonicalHash(outbox.RequestHash, nameof(outbox.RequestHash));
                if (outbox.QuarantinedAt is not null || outbox.QuarantineCode is not null)
                    throw new InvalidOperationException("New Elsa instance lifecycle outbox records cannot be quarantined.");
            }
            else if (outbox.QuarantinedAt is not null &&
                     !string.Equals(outbox.QuarantineCode, "outbox.invalid", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Elsa instance lifecycle outbox quarantine metadata is invalid.");
            }

            var instance = ChangeTracker.Entries<Models.ElsaInstanceEntity>()
                .Select(x => x.Entity)
                .FirstOrDefault(x => x.Id == outbox.InstanceId);
            instance ??= ElsaInstances.Find(outbox.InstanceId);
            if (instance is not null &&
                (instance.OrganizationId != outbox.OrganizationId || instance.WorkspaceId != outbox.WorkspaceId))
                throw new InvalidOperationException("Lifecycle outbox ownership must match its instance.");

            var operation = ChangeTracker.Entries<Models.ElsaInstanceOperationEntity>()
                .Select(x => x.Entity)
                .FirstOrDefault(x => x.Id == outbox.OperationId);
            operation ??= ElsaInstanceOperations.Find(outbox.OperationId);
            if (operation is not null &&
                (operation.Action != outbox.Action ||
                 !string.Equals(operation.RequestHash, outbox.RequestHash, StringComparison.Ordinal) ||
                 (operation.InstanceId is not null && operation.InstanceId != outbox.InstanceId) ||
                 operation.OrganizationId != outbox.OrganizationId || operation.WorkspaceId != outbox.WorkspaceId))
                throw new InvalidOperationException("Lifecycle outbox envelope must match its operation.");
        }

        foreach (var entry in ChangeTracker.Entries<Models.ElsaInstanceIdentityBindingEntity>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            var binding = entry.Entity;
            if (binding.InstanceId == Guid.Empty || binding.BindingVersion < 1)
                throw new InvalidOperationException("An identity binding requires stable versioned ownership.");
            var expectedAudience = $"urn:elsa:instance:{binding.InstanceId:D}".ToLowerInvariant();
            binding.Audience = RequireExactAudience(binding.Audience, expectedAudience);
            binding.VerifiedEndpointOrigin = RequireVerifiedOrigin(binding.VerifiedEndpointOrigin);
            binding.CanonicalCallbackUri = RequireCallbackUri(binding.CanonicalCallbackUri, binding.VerifiedEndpointOrigin);
            if (entry.State == EntityState.Modified)
            {
                if (binding.BindingVersion != (int)entry.Property(x => x.BindingVersion).OriginalValue + 1)
                    throw new InvalidOperationException("Identity binding versions must advance exactly one step.");
                if (binding.ChangedAt <= (DateTimeOffset)entry.Property(x => x.ChangedAt).OriginalValue)
                    throw new InvalidOperationException("Identity binding changes must be strictly later.");
                if (!string.Equals(binding.Audience, entry.Property(x => x.Audience).OriginalValue, StringComparison.Ordinal))
                    throw new InvalidOperationException("Identity binding audience is immutable.");
            }
        }

        foreach (var entry in ChangeTracker.Entries<Models.ElsaInstanceAuditEventEntity>()
                     .Where(x => x.State == EntityState.Added))
        {
            var audit = entry.Entity;
            if (audit.MigrationId == Guid.Empty)
                throw new InvalidOperationException("Migration audit linkage is invalid.");
            audit.EventType = RequireSafeCode(audit.EventType, nameof(audit.EventType));
            audit.DiagnosticCode = OptionalSafeCode(audit.DiagnosticCode, nameof(audit.DiagnosticCode));
            // Human-readable summaries and operator subjects are not durable
            // payload channels. Keep only a stable code and a one-way subject
            // fingerprint at the persistence boundary.
            audit.Summary = audit.Summary is { Length: 78 } reasonHash &&
                            reasonHash.StartsWith("reason.sha256.", StringComparison.Ordinal) &&
                            reasonHash[14..].All(Uri.IsHexDigit)
                ? reasonHash.ToLowerInvariant()
                : audit.DiagnosticCode ?? audit.EventType;
            if (!string.IsNullOrWhiteSpace(audit.OperatorSubject))
            {
                if (audit.OperatorSubject.Length > 512)
                    throw new InvalidOperationException("Operator subject exceeds the safe bound.");
                audit.OperatorSubject = NormalizeSubjectFingerprint(audit.OperatorSubject);
            }
            audit.RequestKeyHash = OptionalCanonicalHash(audit.RequestKeyHash, nameof(audit.RequestKeyHash));
            audit.PriorState = OptionalSafeCode(audit.PriorState, nameof(audit.PriorState));
            audit.NewState = OptionalSafeCode(audit.NewState, nameof(audit.NewState));
            audit.DesiredStateRevisionId = OptionalSafeReference(audit.DesiredStateRevisionId, nameof(audit.DesiredStateRevisionId), 128);
            audit.PlanReference = OptionalPlanUri(audit.PlanReference, nameof(audit.PlanReference));
        }

        foreach (var entry in ChangeTracker.Entries<Models.ElsaInstanceMigrationEntity>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            var migration = entry.Entity;
            if (migration.MigrationId == Guid.Empty || migration.OperationId == Guid.Empty || migration.InstanceId == Guid.Empty)
                throw new InvalidOperationException("An instance migration requires stable identifiers.");
            if (entry.State == EntityState.Modified)
            {
                foreach (var property in ImmutableMigrationProperties)
                    EnsureUnchanged(entry, property, entry.Property(property).CurrentValue,
                        "Instance migration identity and release references are immutable.");
                var originalPhase = (string)entry.Property(nameof(Models.ElsaInstanceMigrationEntity.Phase)).OriginalValue!;
                if (originalPhase == nameof(ElsaInstanceMigrationPhase.Released))
                {
                    foreach (var property in TerminalMigrationReleaseProperties)
                        EnsureUnchanged(entry, property, entry.Property(property).CurrentValue,
                            "Confirmed source release evidence is immutable.");
                }
            }
            migration.Phase = RequireMigrationPhase(migration.Phase);
            migration.SourceAccessMode = RequireSourceAccessMode(migration.SourceAccessMode);
            migration.StartRequestHash = RequireCanonicalHash(migration.StartRequestHash, nameof(migration.StartRequestHash));
            migration.LastRequestHash = RequireCanonicalHash(migration.LastRequestHash, nameof(migration.LastRequestHash));
            migration.SourceReleaseDiagnosticCode = OptionalSafeCode(
                migration.SourceReleaseDiagnosticCode, nameof(migration.SourceReleaseDiagnosticCode));
            migration.SourceReleaseProviderCorrelationId = OptionalSafeReference(
                migration.SourceReleaseProviderCorrelationId, nameof(migration.SourceReleaseProviderCorrelationId), 128);
            migration.SourceReleaseEvidenceReference = OptionalSafeEvidenceUri(
                migration.SourceReleaseEvidenceReference, nameof(migration.SourceReleaseEvidenceReference));
            migration.SourceReleaseEvidenceDigest = OptionalSha256Digest(
                migration.SourceReleaseEvidenceDigest, nameof(migration.SourceReleaseEvidenceDigest));
            if ((migration.SourceReleaseProviderCorrelationId is null) != (migration.SourceReleaseEvidenceReference is null) ||
                (migration.SourceReleaseEvidenceReference is null) != (migration.SourceReleaseEvidenceDigest is null) ||
                migration.Phase == nameof(ElsaInstanceMigrationPhase.Released) && migration.SourceReleaseEvidenceReference is null)
                throw new InvalidOperationException("Source release evidence is incomplete.");
            if ((migration.SourceReleaseClaimToken is null) != (migration.SourceReleaseClaimedUntil is null) ||
                migration.SourceReleaseClaimToken == Guid.Empty || migration.SourceReleaseAttemptCount < 0)
                throw new InvalidOperationException("Source release claim metadata is invalid.");
            ValidateMigrationTuple(migration, source: true);
            ValidateMigrationTuple(migration, source: false);
            if (migration.CutoverAt is not null && migration.SourceRetainUntil is null)
                throw new InvalidOperationException("Migration cutover requires a source retention timestamp.");
            if (migration.SourceRetainUntil is not null && migration.CutoverAt is null)
                throw new InvalidOperationException("Migration source retention requires a cutover timestamp.");
            if (migration.CutoverAt is not null && migration.SourceRetainUntil < migration.CutoverAt.Value.AddDays(30))
                throw new InvalidOperationException("Migration source retention must be at least 30 days after cutover.");
            if (migration.SourceReleasedAt is not null && migration.SourceRetainUntil is null)
                throw new InvalidOperationException("Source release requires a retention timestamp.");
            if (migration.Phase == nameof(ElsaInstanceMigrationPhase.Released) && migration.SourceReleasedAt is null)
                throw new InvalidOperationException("Released migrations require confirmed source release.");
            if ((migration.Phase is nameof(ElsaInstanceMigrationPhase.Cutover) or nameof(ElsaInstanceMigrationPhase.RetainingSource) or
                 nameof(ElsaInstanceMigrationPhase.RetiringSource) or nameof(ElsaInstanceMigrationPhase.Released)) &&
                migration.CutoverAt is null)
                throw new InvalidOperationException("Migration phase requires a completed cutover.");
            if (migration.SourceReleasedAt < migration.CutoverAt)
                throw new InvalidOperationException("Source release cannot precede migration cutover.");
            var hasApproval = migration.EarlyReleaseApprovedByAccountId is not null || migration.EarlyReleaseApprovedAt is not null;
            if (hasApproval && (migration.EarlyReleaseApprovedByAccountId == Guid.Empty || migration.EarlyReleaseApprovedAt is null))
                throw new InvalidOperationException("Early source release approval is incomplete.");
            if (migration.SourceReleasedAt is not null && migration.SourceReleasedAt < migration.SourceRetainUntil &&
                (!hasApproval || migration.EarlyReleaseApprovedAt > migration.SourceReleasedAt))
                throw new InvalidOperationException("Early source release requires prior approval.");
            migration.SourcePlanId = OptionalSafeReference(migration.SourcePlanId, nameof(migration.SourcePlanId), 128);
            migration.SourcePlanUri = OptionalPlanUri(migration.SourcePlanUri, nameof(migration.SourcePlanUri));
            migration.SourceReleaseLine = OptionalCatalogValue(migration.SourceReleaseLine, nameof(migration.SourceReleaseLine));
            migration.SourceVersion = OptionalCatalogValue(migration.SourceVersion, nameof(migration.SourceVersion));
            migration.SourceManifestDigest = OptionalSha256Digest(migration.SourceManifestDigest, nameof(migration.SourceManifestDigest));
            migration.SourceDeploymentId = OptionalSafeReference(migration.SourceDeploymentId, nameof(migration.SourceDeploymentId), 128);
            migration.TargetPlanId = OptionalSafeReference(migration.TargetPlanId, nameof(migration.TargetPlanId), 128);
            migration.TargetPlanUri = OptionalPlanUri(migration.TargetPlanUri, nameof(migration.TargetPlanUri));
            migration.TargetReleaseLine = OptionalCatalogValue(migration.TargetReleaseLine, nameof(migration.TargetReleaseLine));
            migration.TargetVersion = OptionalCatalogValue(migration.TargetVersion, nameof(migration.TargetVersion));
            migration.TargetManifestDigest = OptionalSha256Digest(migration.TargetManifestDigest, nameof(migration.TargetManifestDigest));
            migration.TargetDeploymentId = OptionalSafeReference(migration.TargetDeploymentId, nameof(migration.TargetDeploymentId), 128);
            var instance = ChangeTracker.Entries<Models.ElsaInstanceEntity>().Select(x => x.Entity).FirstOrDefault(x => x.Id == migration.InstanceId);
            instance ??= ElsaInstances.Find(migration.InstanceId);
            if (instance is not null)
            {
                migration.OrganizationId = instance.OrganizationId;
                migration.WorkspaceId = instance.WorkspaceId;
            }
            if (migration.OrganizationId == Guid.Empty)
                migration.OrganizationId = ChangeTracker.Entries<Workspace>().Select(x => x.Entity)
                    .FirstOrDefault(x => x.Id == migration.WorkspaceId)?.OrganizationId ?? Guid.Empty;
            if (migration.OrganizationId == Guid.Empty || migration.WorkspaceId == Guid.Empty)
                throw new InvalidOperationException("Migration ownership is required.");
            RequireInstancePlanUri(migration.SourcePlanUri!, nameof(migration.SourcePlanUri), migration.WorkspaceId, migration.InstanceId, migration.SourcePlanId!);
            RequireInstancePlanUri(migration.TargetPlanUri!, nameof(migration.TargetPlanUri), migration.WorkspaceId, migration.InstanceId, migration.TargetPlanId!);

            if (entry.State == EntityState.Modified)
            {
                var originalPhase = (string)entry.Property(nameof(Models.ElsaInstanceMigrationEntity.Phase)).OriginalValue!;
                if (!CanTransitionMigrationPhase(originalPhase, migration.Phase))
                    throw new InvalidOperationException("Instance migration phase transition is not allowed.");
                var originalUpdatedAt = (DateTimeOffset)entry.Property(nameof(Models.ElsaInstanceMigrationEntity.UpdatedAt)).OriginalValue!;
                if (migration.UpdatedAt <= originalUpdatedAt)
                    throw new InvalidOperationException("Instance migration updates must advance UpdatedAt.");
            }
        }

        ValidateManagedDeploymentRunBindings();
    }

    /// <summary>
    /// A managed deployment run carries a reservation for one explicit Elsa
    /// instance.  The reservation is valid only when the referenced environment
    /// carries the same instance binding in the same workspace.  This validation
    /// intentionally leaves null legacy runs and environments alone.
    /// </summary>
    private void ValidateManagedDeploymentRunBindings()
    {
        var trackedEnvironments = ChangeTracker.Entries<Models.DeploymentEnvironmentEntity>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
            .ToDictionary(entry => entry.Entity.Id, entry => entry.Entity);

        var trackedRunEntries = ChangeTracker.Entries<Models.DeploymentRunEntity>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
            .ToArray();

        foreach (var entry in trackedRunEntries)
        {
            if (entry.State == EntityState.Modified)
            {
                var originalInstanceId = (Guid?)entry.Property(nameof(Models.DeploymentRunEntity.ElsaInstanceId)).OriginalValue;
                if (originalInstanceId is not null && entry.Entity.ElsaInstanceId != originalInstanceId)
                    throw new InvalidOperationException("A managed deployment run instance binding is immutable.");
            }
        }

        var trackedRuns = trackedRunEntries.Select(entry => entry.Entity).ToArray();
        var referencedEnvironmentIds = trackedRuns
            .Select(run => run.EnvironmentId)
            .ToHashSet();
        var localEnvironments = DeploymentEnvironments.Local
            .Where(environment => referencedEnvironmentIds.Contains(environment.Id))
            .ToDictionary(environment => environment.Id, environment => environment);
        var persistedEnvironmentIds = referencedEnvironmentIds
            .Except(localEnvironments.Keys)
            .ToHashSet();
        var persistedEnvironments = persistedEnvironmentIds.Count == 0
            ? new Dictionary<Guid, Models.DeploymentEnvironmentEntity>()
            : DeploymentEnvironments.AsNoTracking()
                .Where(environment => persistedEnvironmentIds.Contains(environment.Id))
                .ToDictionary(environment => environment.Id, environment => environment);

        foreach (var run in trackedRuns)
        {
            var environment = trackedEnvironments.GetValueOrDefault(run.EnvironmentId)
                ?? localEnvironments.GetValueOrDefault(run.EnvironmentId)
                ?? persistedEnvironments.GetValueOrDefault(run.EnvironmentId);

            if (environment is null)
            {
                if (run.ElsaInstanceId is not null)
                    throw new InvalidOperationException("A managed deployment run must match its environment instance binding.");
                continue;
            }

            if (run.ElsaInstanceId is null)
            {
                if (environment.ElsaInstanceId is not null)
                    throw new InvalidOperationException("A managed deployment run must match its environment instance binding.");
                continue;
            }

            if (environment.WorkspaceId != run.WorkspaceId || environment.ElsaInstanceId != run.ElsaInstanceId)
                throw new InvalidOperationException("A managed deployment run must match its environment instance binding.");
        }

        // An environment binding cannot be changed or removed while an
        // existing managed run still points at the old binding.  Exclude only
        // tracked run writes so a coordinated run/environment update is
        // validated against its final values below.
        foreach (var environment in trackedEnvironments.Values)
        {
            var changedRunIds = trackedRuns
                .Where(run => run.EnvironmentId == environment.Id)
                .Select(run => run.Id)
                .ToArray();

            var persistedRuns = DeploymentRuns.AsNoTracking()
                .Where(run => run.EnvironmentId == environment.Id && run.ElsaInstanceId != null)
                .Where(run => !changedRunIds.Contains(run.Id))
                .ToArray();

            var releasedDeletedInstanceId = environment.ElsaInstanceId is null
                ? ChangeTracker.Entries<Models.DeploymentEnvironmentEntity>()
                    .Where(entry => entry.Entity.Id == environment.Id && entry.State == EntityState.Modified)
                    .Select(entry => (Guid?)entry.Property(nameof(Models.DeploymentEnvironmentEntity.ElsaInstanceId)).OriginalValue)
                    .SingleOrDefault()
                : null;
            var releasesDeletedInstance = releasedDeletedInstanceId is not null &&
                ChangeTracker.Entries<Models.ElsaInstanceEntity>().Any(entry =>
                    entry.Entity.Id == releasedDeletedInstanceId &&
                    entry.Entity.WorkspaceId == environment.WorkspaceId &&
                    entry.Entity.ObservedLifecycle == ElsaObservedLifecycle.Deleted) &&
                persistedRuns.All(run => run.ElsaInstanceId == releasedDeletedInstanceId &&
                    run.Status is not (WorkspaceDeploymentRunStatus.Queued or
                        WorkspaceDeploymentRunStatus.Running or
                        WorkspaceDeploymentRunStatus.RecoveryRequired));

            if (!releasesDeletedInstance &&
                persistedRuns.Any(run => run.WorkspaceId != environment.WorkspaceId || run.ElsaInstanceId != environment.ElsaInstanceId))
                throw new InvalidOperationException("An environment binding cannot break an existing managed deployment run.");

            foreach (var run in trackedRuns.Where(run => run.EnvironmentId == environment.Id && run.ElsaInstanceId is not null))
            {
                if (run.WorkspaceId != environment.WorkspaceId || run.ElsaInstanceId != environment.ElsaInstanceId)
                    throw new InvalidOperationException("A managed deployment run must match its environment instance binding.");
            }
        }
    }

    private static void EnsureDefined<TEnum>(TEnum value, string name) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new InvalidOperationException($"{name} contains an unsupported value.");
    }

    private static void EnsureUnchanged(EntityEntry entry, string propertyName, object? currentValue, string message)
    {
        if (!Equals(entry.Property(propertyName).OriginalValue, currentValue))
            throw new InvalidOperationException(message);
    }

    private static string RequireSafeCode(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':')))
            throw new InvalidOperationException($"{name} must be a stable safe code.");
        return value;
    }

    private static string RequireMigrationPhase(string? value)
    {
        var normalized = RequireSafeCode(value, nameof(Models.ElsaInstanceMigrationEntity.Phase));
        if (normalized is not ("Planned" or "Preparing" or "ProvisioningTarget" or "Validating" or "Cutover" or "RetainingSource" or "RetiringSource" or "RolledBack" or "Released" or "Failed"))
            throw new InvalidOperationException("Migration phase is not supported.");
        return normalized;
    }

    private static string RequireSourceAccessMode(string? value)
    {
        var normalized = RequireSafeCode(value, nameof(Models.ElsaInstanceMigrationEntity.SourceAccessMode));
        if (normalized is not ("Running" or "ReadOnly" or "Stopped"))
            throw new InvalidOperationException("Migration source access mode is not supported.");
        return normalized;
    }

    private static bool CanTransitionMigrationPhase(string current, string next) =>
        string.Equals(current, next, StringComparison.Ordinal) || (current, next) switch
        {
            ("Planned", "Preparing" or "ProvisioningTarget" or "Failed") => true,
            ("Preparing", "ProvisioningTarget" or "Validating" or "Cutover" or "Failed") => true,
            ("ProvisioningTarget", "Validating" or "Cutover" or "Failed") => true,
            ("Validating", "Cutover" or "Failed") => true,
            ("Cutover", "RetainingSource" or "RetiringSource" or "RolledBack" or "Failed") => true,
            ("RetainingSource", "RetiringSource" or "RolledBack" or "Failed") => true,
            ("RetiringSource", "Released" or "RolledBack" or "Failed") => true,
            _ => false
        };

    private static string? OptionalSafeCode(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? null : RequireSafeCode(value, name);

    private static string? OptionalCanonicalHash(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Length != 64 || value.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidOperationException($"{name} must be a canonical SHA-256 hash.");
        return value.ToLowerInvariant();
    }

    private static string RequireCanonicalHash(string? value, string name) =>
        OptionalCanonicalHash(value, name) ?? throw new InvalidOperationException($"{name} must be a canonical SHA-256 hash.");

    private static string RequireSafeReference(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} must be a safe reference.");
        var normalized = value.Trim();
        if (normalized.Length > maxLength || normalized.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':' or '/' or '+')))
            throw new InvalidOperationException($"{name} must be a safe reference.");
        return normalized;
    }

    private static string? OptionalSafeReference(string? value, string name, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : RequireSafeReference(value, name, maxLength);

    private static string RequireSafeToken(string? value, string name, int maxLength)
    {
        var normalized = RequireSafeReference(value, name, maxLength);
        if (normalized.Contains('/', StringComparison.Ordinal) || normalized.Contains(':', StringComparison.Ordinal) || normalized.Contains('+', StringComparison.Ordinal))
            throw new InvalidOperationException($"{name} must be a safe token.");
        return normalized;
    }

    private static string? OptionalSafeToken(string? value, string name, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : RequireSafeToken(value, name, maxLength);

    private static string RequireCatalogValue(string? value, string name)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 128 ||
            !char.IsAsciiLetterOrDigit(normalized[0]) ||
            normalized.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or '+')))
            throw new InvalidOperationException($"{name} must be a bounded safe catalog value.");
        return normalized.ToLowerInvariant();
    }

    private static string? OptionalCatalogValue(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? null : RequireCatalogValue(value, name);

    private static string RequireSlug(string? value)
    {
        var normalized = RequireSafeToken(value, nameof(Models.ElsaInstanceEntity.Slug), 128).ToLowerInvariant();
        if (normalized.Length > 96)
            throw new InvalidOperationException("Slug exceeds the safe bound.");
        return normalized;
    }

    private static void RequireDisplayName(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            throw new InvalidOperationException($"{name} must be a bounded display value.");
    }

    private static string? OptionalSha256Digest(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        if (normalized.Length != 71 || !normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
            normalized[7..].Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidOperationException($"{name} must be a SHA-256 digest.");
        return "sha256:" + normalized[7..].ToLowerInvariant();
    }

    private static string? OptionalPreviewManifestDigest(string? value, string name)
    {
        try
        {
            return ElsaInstanceValue.OptionalPreviewManifestDigest(value, name);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"{name} must be a canonical lowercase SHA-256 digest.");
        }
    }

    private static string RequireSha256Digest(string? value, string name) =>
        OptionalSha256Digest(value, name) ?? throw new InvalidOperationException($"{name} must be a SHA-256 digest.");

    private static string? OptionalPlanUri(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var uri = ParseSafeUri(value, name, allowLocalHttp: false);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || !IsResolvedPlanPath(uri.AbsolutePath))
            throw new InvalidOperationException($"{name} must be an absolute HTTPS resolved-plan URI.");
        return uri.AbsoluteUri;
    }

    private static string? OptionalSafeEvidenceUri(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        if (normalized.Length > 2048 || normalized.Any(char.IsControl) ||
            !Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "oci") || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath is "" or "/")
            throw new InvalidOperationException($"{name} must be a safe immutable evidence URI.");
        return uri.AbsoluteUri;
    }

    private static bool IsResolvedPlanPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 7
            && path.EndsWith('/' + segments[6], StringComparison.Ordinal)
            && string.Equals(segments[0], "api", StringComparison.Ordinal)
            && string.Equals(segments[1], "workspaces", StringComparison.Ordinal)
            && Guid.TryParseExact(segments[2], "D", out _)
            && string.Equals(segments[3], "instances", StringComparison.Ordinal)
            && Guid.TryParseExact(segments[4], "D", out _)
            && string.Equals(segments[5], "resolved-plans", StringComparison.Ordinal)
            && IsSafeJsonName(segments[6]);
    }

    private static void RequireInstancePlanUri(string value, string name, Guid? workspaceId, Guid instanceId, string planId)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"{name} must identify the instance resolved plan.");
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!IsResolvedPlanPath(uri.AbsolutePath) || !Guid.TryParseExact(segments[4], "D", out var uriInstanceId) ||
            uriInstanceId != instanceId || !string.Equals(segments[6], planId, StringComparison.Ordinal))
            throw new InvalidOperationException($"{name} must identify the instance resolved plan.");
        if (workspaceId is not null && (!Guid.TryParseExact(segments[2], "D", out var uriWorkspaceId) || uriWorkspaceId != workspaceId))
            throw new InvalidOperationException($"{name} must identify the instance workspace resolved plan.");
    }

    private static string? OptionalEndpointUri(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var uri = ParseSafeUri(value, name, allowLocalHttp: true);
        var localHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                         System.Net.IPAddress.TryParse(uri.Host, out var address) && System.Net.IPAddress.IsLoopback(address));
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !localHttp)
            throw new InvalidOperationException($"{name} must be a safe HTTPS endpoint URI.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static string? OptionalManagedEndpointOrigin(string? value, bool allowLegacyInvalid)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            return new ElsaManagedEndpointOrigin(value).Value;
        }
        catch (ArgumentException)
        {
            if (allowLegacyInvalid)
                return null;
            throw new InvalidOperationException("Managed deployment endpoint origin is invalid.");
        }
    }

    private static Uri ParseSafeUri(string value, string name, bool allowLocalHttp)
    {
        var normalized = value.Trim();
        if (normalized.Length > 2048 || normalized.Any(char.IsControl) || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException($"{name} must be a safe absolute URI.");
        if (uri.Host.Contains('*', StringComparison.Ordinal))
            throw new InvalidOperationException($"{name} must not use wildcard hosts.");
        if (!allowLocalHttp && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{name} must be HTTPS.");
        if (HasUnsafeUriPath(uri.AbsolutePath))
            throw new InvalidOperationException($"{name} contains an unsafe path.");
        return uri;
    }

    private static bool HasUnsafeUriPath(string path) =>
        path.Contains('%', StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal) ||
        path.Contains("//", StringComparison.Ordinal) || path.Split('/').Any(segment => segment is "." or "..");

    private static string? OptionalAudience(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 256 || !normalized.StartsWith("urn:elsa:", StringComparison.Ordinal) ||
            normalized.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is ':' or '.' or '_' or '-')))
            throw new InvalidOperationException($"{name} must be a safe Elsa audience.");
        return normalized;
    }

    private static string RequireExactAudience(string? value, string expected)
    {
        var normalized = OptionalAudience(value, nameof(Models.ElsaInstanceIdentityBindingEntity.Audience));
        if (!string.Equals(normalized, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("Identity audience must be derived from the instance ID.");
        return normalized!;
    }

    private static string RequireCallbackUri(string? value, string verifiedOrigin)
    {
        var normalized = OptionalEndpointUri(value, nameof(Models.ElsaInstanceIdentityBindingEntity.CanonicalCallbackUri));
        var expected = verifiedOrigin.TrimEnd('/') + "/managed-elsa/handoff/callback";
        if (normalized is null || !string.Equals(normalized, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("Canonical callback URI must match the verified endpoint origin.");
        return normalized;
    }

    private static string RequireVerifiedOrigin(string? value)
    {
        var normalized = OptionalEndpointUri(value, nameof(Models.ElsaInstanceIdentityBindingEntity.VerifiedEndpointOrigin));
        if (normalized is null || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            ((!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
              !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                 System.Net.IPAddress.TryParse(uri.Host, out var address) && System.Net.IPAddress.IsLoopback(address))))) ||
            uri.AbsolutePath is not ("" or "/"))
            throw new InvalidOperationException("A verified endpoint origin must be HTTPS and have no path.");
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static bool BelongsToReleaseLine(string releaseLine, string version) =>
        string.Equals(releaseLine, version, StringComparison.OrdinalIgnoreCase) ||
        version.StartsWith(releaseLine + ".", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSubjectFingerprint(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return OptionalSha256Digest(normalized, nameof(Models.ElsaInstanceAuditEventEntity.OperatorSubject))!;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return "sha256:" + digest;
    }

    private static void ValidateFeatureOverrides(string? json)
    {
        if (json is null || json.Length > 32_768)
            throw new InvalidOperationException("Feature overrides JSON exceeds the safe bound.");
        try
        {
            using var document = JsonDocument.Parse(json, SafeJsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Feature overrides must be an object.");
            if (document.RootElement.EnumerateObject().Count() > 256)
                throw new InvalidOperationException("Feature overrides contain too many entries.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name) || !IsSafeJsonName(property.Name) || property.Value.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Feature overrides contain an unsafe entry.");
                var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string? kind = null;
                string? value = null;
                foreach (var field in property.Value.EnumerateObject())
                {
                    if (!fields.Add(field.Name) || field.Name is not ("kind" or "value") || field.Value.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException("Feature overrides contain an unsafe entry.");
                    if (field.Name == "kind") kind = field.Value.GetString(); else value = field.Value.GetString();
                }
                if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl) || ContainsSensitiveMarker(value))
                    throw new InvalidOperationException("Feature overrides contain an unsafe value.");
                var normalizedKind = RequireSafeCode(kind, "feature override kind");
                if (!Enum.TryParse<ElsaFeatureOverrideKind>(normalizedKind, ignoreCase: true, out var parsedKind) ||
                    !Enum.IsDefined(parsedKind) ||
                    !string.Equals(normalizedKind, parsedKind.ToString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Feature override kind is not supported.");
                switch (parsedKind)
                {
                    case ElsaFeatureOverrideKind.Boolean when !bool.TryParse(value, out _):
                        throw new InvalidOperationException("Boolean feature override values must be true or false.");
                    case ElsaFeatureOverrideKind.Number when !decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _):
                        throw new InvalidOperationException("Number feature override values must be invariant decimals.");
                    case ElsaFeatureOverrideKind.Catalog:
                        RequireCatalogValue(value, "feature override value");
                        break;
                }
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Feature overrides JSON is invalid.");
        }
    }

    private static void ValidateComponentDigests(string? json)
    {
        if (json is null)
            return;
        if (json.Length > 65_536)
            throw new InvalidOperationException("Component digest JSON exceeds the safe bound.");
        try
        {
            using var document = JsonDocument.Parse(json, SafeJsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Component digests must be an array.");
            if (document.RootElement.GetArrayLength() is 0 or > 256)
                throw new InvalidOperationException("Component digest count is outside the safe bound.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Component digests contain an unsafe entry.");
                string? componentId = null;
                string? digest = null;
                var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in item.EnumerateObject())
                {
                    if (!fields.Add(field.Name) || field.Name is not ("componentId" or "digest") || field.Value.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException("Component digests contain an unsafe entry.");
                    if (field.Name == "componentId") componentId = field.Value.GetString(); else digest = field.Value.GetString();
                }
                componentId = RequireSafeToken(componentId, "component digest componentId", 128);
                if (!ids.Add(componentId))
                    throw new InvalidOperationException("Component digest IDs must be unique.");
                OptionalSha256Digest(digest, "component digest");
                if (digest is null)
                    throw new InvalidOperationException("Component digest is required.");
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Component digest JSON is invalid.");
        }
    }

    private static readonly string[] ImmutableMigrationProperties =
    [
        nameof(Models.ElsaInstanceMigrationEntity.InstanceId),
        nameof(Models.ElsaInstanceMigrationEntity.OperationId),
        nameof(Models.ElsaInstanceMigrationEntity.OrganizationId),
        nameof(Models.ElsaInstanceMigrationEntity.WorkspaceId),
        nameof(Models.ElsaInstanceMigrationEntity.SourcePlanId),
        nameof(Models.ElsaInstanceMigrationEntity.SourcePlanUri),
        nameof(Models.ElsaInstanceMigrationEntity.SourceReleaseLine),
        nameof(Models.ElsaInstanceMigrationEntity.SourceVersion),
        nameof(Models.ElsaInstanceMigrationEntity.SourceManifestDigest),
        nameof(Models.ElsaInstanceMigrationEntity.SourceDeploymentId),
        nameof(Models.ElsaInstanceMigrationEntity.TargetPlanId),
        nameof(Models.ElsaInstanceMigrationEntity.TargetPlanUri),
        nameof(Models.ElsaInstanceMigrationEntity.TargetReleaseLine),
        nameof(Models.ElsaInstanceMigrationEntity.TargetVersion),
        nameof(Models.ElsaInstanceMigrationEntity.TargetManifestDigest),
        nameof(Models.ElsaInstanceMigrationEntity.TargetDeploymentId),
        nameof(Models.ElsaInstanceMigrationEntity.StartRequestHash),
        nameof(Models.ElsaInstanceMigrationEntity.CreatedAt)
    ];

    private static readonly string[] TerminalMigrationReleaseProperties =
    [
        nameof(Models.ElsaInstanceMigrationEntity.SourceReleasedAt),
        nameof(Models.ElsaInstanceMigrationEntity.SourceReleaseProviderCorrelationId),
        nameof(Models.ElsaInstanceMigrationEntity.SourceReleaseEvidenceReference),
        nameof(Models.ElsaInstanceMigrationEntity.SourceReleaseEvidenceDigest)
    ];

    private static readonly JsonDocumentOptions SafeJsonOptions = new()
    {
        MaxDepth = 16,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    private static bool IsSafeJsonName(string name) =>
        name.Length is > 0 and <= 128 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_');

    private static bool ContainsSensitiveMarker(string value) =>
        value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("connectionstring", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("workflow", StringComparison.OrdinalIgnoreCase);

    private static void ValidateMigrationTuple(Models.ElsaInstanceMigrationEntity migration, bool source)
    {
        var values = source
            ? new string?[] { migration.SourcePlanId, migration.SourcePlanUri, migration.SourceReleaseLine, migration.SourceVersion, migration.SourceManifestDigest, migration.SourceDeploymentId }
            : new string?[] { migration.TargetPlanId, migration.TargetPlanUri, migration.TargetReleaseLine, migration.TargetVersion, migration.TargetManifestDigest, migration.TargetDeploymentId };
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Migration source and target references must be complete tuples.");
        var releaseLine = (source ? migration.SourceReleaseLine : migration.TargetReleaseLine)!.Trim();
        var version = (source ? migration.SourceVersion : migration.TargetVersion)!.Trim();
        if (!BelongsToReleaseLine(releaseLine, version))
            throw new InvalidOperationException("Migration release version must belong to its release line.");
    }

    private void EnsureOrganizationsForNewWorkspaces()
    {
        var workspaceEntries = ChangeTracker.Entries<Workspace>()
            .Where(x => x.State == EntityState.Added)
            .ToList();

        foreach (var entry in workspaceEntries)
        {
            if (entry.Entity.Organization is not null || entry.Entity.OrganizationId != Guid.Empty)
                continue;

            var organizationId = Guid.NewGuid();
            entry.Entity.OrganizationId = organizationId;
            Organizations.Add(new Organization
            {
                Id = organizationId,
                Name = string.IsNullOrWhiteSpace(entry.Entity.Name) ? "Workspace Organization" : entry.Entity.Name
            });
        }
    }
}
