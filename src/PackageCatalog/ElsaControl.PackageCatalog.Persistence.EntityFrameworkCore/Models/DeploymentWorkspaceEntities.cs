using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;

internal sealed class DeploymentApplicationEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedByAccountId { get; set; }
    public Guid? UpdatedByAccountId { get; set; }
    public List<DeploymentEnvironmentEntity> Environments { get; set; } = [];
}

internal sealed class DeploymentEnvironmentEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ApplicationId { get; set; }
    public DeploymentApplicationEntity? Application { get; set; }
    /// <summary>
    /// Optional explicit managed-instance binding. A null value deliberately leaves
    /// legacy/customer-owned environments unbound.
    /// </summary>
    public Guid? ElsaInstanceId { get; set; }
    public ElsaInstanceEntity? ElsaInstance { get; set; }
    public string Name { get; set; } = "";
    public EnvironmentTier Tier { get; set; }
    public Guid? TierId { get; set; }
    public DeploymentTierDefinitionEntity? TierDefinition { get; set; }
    public bool TierRequiresReview { get; set; }
    public Guid? DesiredRevisionId { get; set; }
    public Guid? DeployedRevisionId { get; set; }
    public DeploymentStatus DeploymentStatus { get; set; }
    public DriftStatus DriftStatus { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<WorkflowEngineEntity> Engines { get; set; } = [];
    public List<DesiredStateRevisionEntity> Revisions { get; set; } = [];
    public List<ObservabilityBindingEntity> ObservabilityBindings { get; set; } = [];
    public List<DriftReportItemEntity> DriftReports { get; set; } = [];
}

/// <summary>
/// Relational projection of the provider-neutral Elsa instance aggregate. Intent
/// values are normalized so release lines and catalog values remain data, while
/// bounded JSON is used only for typed extension values and component digests.
/// </summary>
internal sealed class ElsaInstanceEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";

    public string DistributionId { get; set; } = "";
    public string ReleaseLine { get; set; } = "";
    public string? RequestedVersion { get; set; }
    public string Channel { get; set; } = "";
    public string PatchUpdates { get; set; } = "";
    public string MinorUpdates { get; set; } = "";
    public string MajorMigrations { get; set; } = "";
    public string? PreviewManifestDigest { get; set; }

    public string TopologyId { get; set; } = "";
    public string? FeaturePresetId { get; set; }
    public string FeatureOverridesJson { get; set; } = "{}";
    public string? PackagePolicy { get; set; }
    public string? ConfigurationShapeRevisionId { get; set; }

    public string TargetMode { get; set; } = "";
    public string RegionCode { get; set; } = "";
    public string IsolationProfile { get; set; } = "";
    public string CapacityProfile { get; set; } = "";
    public string NetworkOutcome { get; set; } = "";
    public string DomainOutcome { get; set; } = "";

    public ElsaDesiredLifecycle DesiredLifecycle { get; set; }
    public ElsaObservedLifecycle ObservedLifecycle { get; set; }
    public ElsaInstanceHealth Health { get; set; }

    public string? DesiredStateRevisionId { get; set; }
    public string? ResolvedPlanId { get; set; }
    public int? ResolvedPlanSchemaVersion { get; set; }
    public string? ResolvedPlanContentHash { get; set; }
    public string? ResolvedPlanUri { get; set; }
    public string? CurrentReleaseDistributionId { get; set; }
    public string? CurrentReleaseLine { get; set; }
    public string? CurrentReleaseVersion { get; set; }
    public string? CurrentReleaseManifestDigest { get; set; }
    public string? CurrentReleaseComponentDigestsJson { get; set; }
    public string? CurrentDeploymentId { get; set; }
    public string? CurrentDeploymentRevisionId { get; set; }
    public string? CurrentDeploymentEndpointUri { get; set; }
    // The provider configured the current deployment's runtime handoff, bound to its endpoint origin.
    public bool CurrentDeploymentManagedHandoff { get; set; }
    public string? PlacementAssignmentId { get; set; }
    public string? ElsaTenantId { get; set; }
    public string? ElsaTenantAudience { get; set; }
    public string? LastOperationId { get; set; }

    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public List<ElsaInstanceOperationEntity> Operations { get; set; } = [];
    public List<ElsaInstanceIntentRevisionEntity> IntentRevisions { get; set; } = [];
    public List<ElsaInstanceAuditEventEntity> AuditEvents { get; set; } = [];
    public List<ElsaInstanceMigrationEntity> Migrations { get; set; } = [];
    public ElsaInstanceIdentityBindingEntity? IdentityBinding { get; set; }
}

/// <summary>
/// Immutable, typed snapshot of an Elsa instance's provider-neutral customer
/// intent. This deliberately duplicates the normalized intent fields from the
/// aggregate so every accepted revision remains independently auditable. It does
/// not contain serialized request payloads, workflow definitions, credentials, or
/// provider resource identifiers.
/// </summary>
internal sealed class ElsaInstanceIntentRevisionEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid InstanceId { get; set; }
    public ElsaInstanceEntity? Instance { get; set; }
    public int RevisionNumber { get; set; }
    public string ContentHash { get; set; } = "";

    public string DistributionId { get; set; } = "";
    public string ReleaseLine { get; set; } = "";
    public string? RequestedVersion { get; set; }
    public string Channel { get; set; } = "";
    public string PatchUpdates { get; set; } = "";
    public string MinorUpdates { get; set; } = "";
    public string MajorMigrations { get; set; } = "";
    public string? PreviewManifestDigest { get; set; }

    public string TopologyId { get; set; } = "";
    public string? FeaturePresetId { get; set; }
    public string FeatureOverridesJson { get; set; } = "{}";
    public string? PackagePolicy { get; set; }
    public string? ConfigurationShapeRevisionId { get; set; }

    public string TargetMode { get; set; } = "";
    public string RegionCode { get; set; } = "";
    public string IsolationProfile { get; set; } = "";
    public string CapacityProfile { get; set; } = "";
    public string NetworkOutcome { get; set; } = "";
    public string DomainOutcome { get; set; } = "";
    public ElsaDesiredLifecycle DesiredLifecycle { get; set; }

    public DateTimeOffset AuthoredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByAccountId { get; set; }
}

/// <summary>
/// Durable lifecycle work envelope. The envelope is intentionally small: workers
/// reload the immutable revision and aggregate by ID after the acceptance
/// transaction commits. No request body, plan payload, credential, token, or
/// provider-specific data is copied into the outbox.
/// </summary>
internal sealed class ElsaInstanceLifecycleOutboxEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid InstanceId { get; set; }
    public ElsaInstanceEntity? Instance { get; set; }
    public Guid OperationId { get; set; }
    public ElsaInstanceOperationEntity? Operation { get; set; }
    public ElsaInstanceOperationAction Action { get; set; }
    public string RequestHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? QuarantinedAt { get; set; }
    public string? QuarantineCode { get; set; }
}

internal sealed class ElsaInstanceOperationEntity
{
    public Guid Id { get; set; }
    public Guid? InstanceId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public ElsaInstanceEntity? Instance { get; set; }
    public ElsaInstanceOperationAction Action { get; set; }
    public string IdempotencyScope { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    // Compatibility projection of the latest recovery request. The append-only
    // recovery-request ledger, not these mutable fields, is authoritative.
    public string? RecoveryIdempotencyScope { get; set; }
    public string? RecoveryIdempotencyKey { get; set; }
    public string? RecoveryRequestHash { get; set; }
    public int ExpectedVersion { get; set; }
    public ElsaInstanceOperationState State { get; set; }
    public int AttemptNumber { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? WorkerId { get; set; }
    /// <summary>One-way SHA-256 lease proof; the bearer token is never persisted.</summary>
    public string? LeaseTokenHash { get; set; }
    public int LeaseVersion { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public string? DesiredStateRevisionId { get; set; }
    public string? ResolvedPlanId { get; set; }
    public Guid? DeploymentRunId { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureSummary { get; set; }
    public int ReconciliationVersion { get; set; }
    public string? ReconciliationEvidenceFingerprint { get; set; }
    public string? ReconciliationDiagnosticCode { get; set; }
    public string? ReconciliationRetryEvidenceReference { get; set; }
    public string? ReconciliationRetryEvidenceDigest { get; set; }
    public ElsaObservedLifecycle? ReconciledObservedLifecycle { get; set; }
    public ElsaInstanceHealth? ReconciledHealth { get; set; }
    public int? ReconciledInstanceVersion { get; set; }
    public DateTimeOffset? ReconciledAt { get; set; }
    public string? DeletionEvidenceFingerprint { get; set; }
    public string? DeletionEvidenceReference { get; set; }
    public string? DeletionEvidenceDigest { get; set; }
    public string? DeletionDiagnosticCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Append-only authority for a recovery request. An operation can enter recovery
/// more than once, so recovery idempotency cannot live on the mutable operation
/// row without making an older key reusable.
/// </summary>
internal sealed class ElsaInstanceRecoveryRequestEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid InstanceId { get; set; }
    public Guid OperationId { get; set; }
    public ElsaInstanceOperationEntity? Operation { get; set; }
    public int AttemptNumber { get; set; }
    public string IdempotencyScope { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string? RecoveryObservationReference { get; set; }
    public string? RecoveryObservationDigest { get; set; }
    public int? ObservedLifecycleAttemptNumber { get; set; }
    public int? ObservedInstanceVersion { get; set; }
    /// <summary>
    /// Canonical value-free provider authority for an explicit Azure Delete recovery. It is
    /// nullable so existing local and non-Azure recovery rows retain their legacy shape.
    /// </summary>
    public string? AzureDeleteRecoveryAuthority { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Immutable, safe resolved-plan record. The canonical JSON is retained only after
/// the worker validates and normalizes the typed plan; no resolver input or provider
/// payload is stored here.
/// </summary>
internal sealed class ElsaInstanceResolvedPlanEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid InstanceId { get; set; }
    public ElsaInstanceEntity? Instance { get; set; }
    public string PlanId { get; set; } = "";
    public int SchemaVersion { get; set; }
    public string ContentHash { get; set; } = "";
    public string PlanUri { get; set; } = "";
    public string SerializedPlan { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ElsaInstanceAuditEventEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid InstanceId { get; set; }
    public ElsaInstanceEntity? Instance { get; set; }
    public long Sequence { get; set; }
    public string EventType { get; set; } = "";
    public Guid? ActorAccountId { get; set; }
    public string? OperatorSubject { get; set; }
    public Guid? OperationId { get; set; }
    public Guid? MigrationId { get; set; }
    public Guid? DeploymentRunId { get; set; }
    public string? PriorState { get; set; }
    public string? NewState { get; set; }
    public string? DesiredStateRevisionId { get; set; }
    public string? PlanReference { get; set; }
    public string? DiagnosticCode { get; set; }
    public string? Summary { get; set; }
    public string? RequestKeyHash { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class ElsaInstanceIdentityBindingEntity
{
    public Guid InstanceId { get; set; }
    public ElsaInstanceEntity? Instance { get; set; }
    public string Audience { get; set; } = "";
    public string CanonicalCallbackUri { get; set; } = "";
    public string VerifiedEndpointOrigin { get; set; } = "";
    public int BindingVersion { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}

internal sealed class ElsaInstanceMigrationEntity
{
    public Guid MigrationId { get; set; }
    public Guid OperationId { get; set; }
    public Guid InstanceId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public ElsaInstanceEntity? Instance { get; set; }
    public string? SourcePlanId { get; set; }
    public string? SourcePlanUri { get; set; }
    public string? SourceReleaseLine { get; set; }
    public string? SourceVersion { get; set; }
    public string? SourceManifestDigest { get; set; }
    public string? SourceDeploymentId { get; set; }
    public string? TargetPlanId { get; set; }
    public string? TargetPlanUri { get; set; }
    public string? TargetReleaseLine { get; set; }
    public string? TargetVersion { get; set; }
    public string? TargetManifestDigest { get; set; }
    public string? TargetDeploymentId { get; set; }
    public string StartRequestHash { get; set; } = "";
    public string LastRequestHash { get; set; } = "";
    public string Phase { get; set; } = "";
    public string SourceAccessMode { get; set; } = "";
    public DateTimeOffset? CutoverAt { get; set; }
    public DateTimeOffset? SourceRetainUntil { get; set; }
    public Guid? EarlyReleaseApprovedByAccountId { get; set; }
    public DateTimeOffset? EarlyReleaseApprovedAt { get; set; }
    public DateTimeOffset? SourceReleasedAt { get; set; }
    public Guid? SourceReleaseClaimToken { get; set; }
    public DateTimeOffset? SourceReleaseClaimedUntil { get; set; }
    public int SourceReleaseAttemptCount { get; set; }
    public string? SourceReleaseDiagnosticCode { get; set; }
    public string? SourceReleaseProviderCorrelationId { get; set; }
    public string? SourceReleaseEvidenceReference { get; set; }
    public string? SourceReleaseEvidenceDigest { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class DeploymentTierDefinitionEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsDefault { get; set; }
    public DeploymentTierStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedByAccountId { get; set; }
    public Guid? UpdatedByAccountId { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public Guid? ArchivedByAccountId { get; set; }
    public List<DeploymentTierCapabilityAssignmentEntity> Capabilities { get; set; } = [];
    public List<DeploymentEnvironmentEntity> Environments { get; set; } = [];
    public List<DeploymentTierChangeRecordEntity> Changes { get; set; } = [];
}

internal sealed class DeploymentTierCapabilityAssignmentEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid TierId { get; set; }
    public DeploymentTierDefinitionEntity? Tier { get; set; }
    public string CapabilityId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByAccountId { get; set; }
}

internal sealed class DeploymentTierChangeRecordEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid TierId { get; set; }
    public DeploymentTierDefinitionEntity? Tier { get; set; }
    public Guid? ActorAccountId { get; set; }
    public string ChangeType { get; set; } = "";
    public string Summary { get; set; } = "";
    public DateTimeOffset ChangedAt { get; set; }
    public int AffectedEnvironmentCount { get; set; }
}

internal sealed class WorkflowEngineEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid EnvironmentId { get; set; }
    public DeploymentEnvironmentEntity? Environment { get; set; }
    public Guid? CredentialReferenceId { get; set; }
    public DeploymentCredentialReferenceEntity? CredentialReferenceMetadata { get; set; }
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string? Region { get; set; }
    public string? Version { get; set; }
    public CertificateStatus CertificateStatus { get; set; }
    public string CredentialProvider { get; set; } = "";
    public string CredentialReference { get; set; } = "";
    public EngineCredentialAssignmentStatus CredentialAssignmentStatus { get; set; }
    public CredentialVerificationStatus CredentialVerificationStatus { get; set; }
    public DateTimeOffset? CredentialLastVerifiedAt { get; set; }
    public DeploymentHealth Health { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? LastVerificationAt { get; set; }
    public string VerificationMessage { get; set; } = "";
    public string? HostingProvider { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<EngineCapabilityEntity> Capabilities { get; set; } = [];
    public List<RuntimeControlEntity> Controls { get; set; } = [];
}

internal sealed class DeploymentSecretStoreEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public DeploymentSecretStoreType Type { get; set; }
    public string? Description { get; set; }
    public DeploymentSecretStoreStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedByAccountId { get; set; }
    public Guid? UpdatedByAccountId { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public Guid? ArchivedByAccountId { get; set; }
    public List<DeploymentCredentialReferenceEntity> CredentialReferences { get; set; } = [];
}

internal sealed class DeploymentCredentialReferenceEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SecretStoreId { get; set; }
    public DeploymentSecretStoreEntity? SecretStore { get; set; }
    public string Name { get; set; } = "";
    public string Reference { get; set; } = "";
    public string? ProtectedSecret { get; set; }
    public DateTimeOffset? ProtectedSecretUpdatedAt { get; set; }
    public string? Description { get; set; }
    public DeploymentSecretStoreStatus Status { get; set; }
    public CredentialVerificationStatus VerificationStatus { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedByAccountId { get; set; }
    public Guid? UpdatedByAccountId { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public Guid? ArchivedByAccountId { get; set; }
    public List<WorkflowEngineEntity> Engines { get; set; } = [];
}

internal sealed class WorkspaceDeploymentArtifactEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string ArtifactId { get; set; } = "";
    public string LayoutVersion { get; set; } = "";
    public string ContentDigestAlgorithm { get; set; } = "";
    public string ContentDigest { get; set; } = "";
    public string EnvelopeVersion { get; set; } = "";
    public string ArtifactTypeId { get; set; } = "";
    public string ArtifactSchemaVersion { get; set; } = "";
    public string? ManifestDigestAlgorithm { get; set; }
    public string? ManifestDigest { get; set; }
    public string PayloadReferenceJson { get; set; } = "";
    public string ProducerJson { get; set; } = "";
    public string DisplayMetadataJson { get; set; } = "";
    public string CompatibilityHintsJson { get; set; } = "[]";
    public WorkspaceArtifactFormat Format { get; set; }
    public string ReferenceProvider { get; set; } = "";
    public string Reference { get; set; } = "";
    public string? ManifestName { get; set; }
    public string? ManifestVersion { get; set; }
    public string? ManifestEnvironment { get; set; }
    public int ResourceCount { get; set; }
    public string ResourceSummaryJson { get; set; } = "[]";
    public WorkspaceArtifactChecksumStatus ChecksumStatus { get; set; }
    public WorkspaceArtifactInspectionStatus InspectionStatus { get; set; }
    public string DiagnosticsJson { get; set; } = "[]";
    public WorkspaceArtifactLifecycleStatus Status { get; set; } = WorkspaceArtifactLifecycleStatus.Active;
    public DateTimeOffset RegisteredAt { get; set; }
    public Guid RegisteredByAccountId { get; set; }
    public DateTimeOffset? LastInspectedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public Guid? ArchivedByAccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class WorkspaceArtifactUploadSessionEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public WorkspaceArtifactUploadStatus Status { get; set; }
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public long? DeclaredSizeBytes { get; set; }
    public long? UploadedSizeBytes { get; set; }
    public string? StagedFilePath { get; set; }
    public string? IdempotencyKey { get; set; }
    public string DiagnosticsJson { get; set; } = "[]";
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? CompletedArtifactRecordId { get; set; }
    public Guid CreatedByAccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class EngineCapabilityEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid EngineId { get; set; }
    public WorkflowEngineEntity? Engine { get; set; }
    public string CapabilityId { get; set; } = "";
    public string Label { get; set; } = "";
    public CapabilityBoundary Boundary { get; set; }
}

internal sealed class RuntimeControlEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid EngineId { get; set; }
    public WorkflowEngineEntity? Engine { get; set; }
    public string ControlId { get; set; } = "";
    public string Label { get; set; } = "";
    public CapabilityBoundary Boundary { get; set; }
    public string RequiredCapabilityId { get; set; } = "";
    public string Description { get; set; } = "";
}

internal sealed class RuntimeControlExecutionEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid EngineId { get; set; }
    public WorkflowEngineEntity? Engine { get; set; }
    public Guid EnvironmentId { get; set; }
    public string ControlId { get; set; } = "";
    public string ControlLabel { get; set; } = "";
    public CapabilityBoundary Boundary { get; set; }
    public string RequiredCapabilityId { get; set; } = "";
    public Guid ConfirmationId { get; set; }
    public Guid ActorAccountId { get; set; }
    public RuntimeControlExecutionStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Message { get; set; } = "";
}

internal sealed class DesiredStateRevisionEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid EnvironmentId { get; set; }
    public DeploymentEnvironmentEntity? Environment { get; set; }
    public int RevisionNumber { get; set; }
    public string Label { get; set; } = "";
    public string? Commit { get; set; }
    public string ContentHash { get; set; } = "";
    public string DesiredStateJson { get; set; } = "";
    public DateTimeOffset AuthoredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByAccountId { get; set; }
    public List<StructuredDesiredStateRecordEntity> Records { get; set; } = [];
}

internal sealed class StructuredDesiredStateRecordEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid RevisionId { get; set; }
    public DesiredStateRevisionEntity? Revision { get; set; }
    public DesiredStateRecordKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string ContentHash { get; set; } = "";
    public Guid? ArtifactRecordId { get; set; }
    public string? ArtifactId { get; set; }
    public string? ArtifactTypeId { get; set; }
    public string? ArtifactDigestAlgorithm { get; set; }
    public string? ArtifactDigest { get; set; }
}

internal sealed class WorkspacePermissionGrantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid AccountId { get; set; }
    public string Permission { get; set; } = "";
    public Guid? GrantedByAccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedByAccountId { get; set; }
}

internal sealed class WorkspacePermissionAuditRecordEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid GrantId { get; set; }
    public Guid AccountId { get; set; }
    public string Permission { get; set; } = "";
    public WorkspacePermissionAuditAction Action { get; set; }
    public Guid? ActorAccountId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class ActionConfirmationEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public ConfirmationActionType ActionType { get; set; }
    public string TargetId { get; set; } = "";
    public Guid ConfirmedByAccountId { get; set; }
    public DateTimeOffset ConfirmedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

internal sealed class DeploymentRunEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    /// <summary>
    /// The managed instance reservation scope. Legacy/customer-owned deployment
    /// runs remain null so adding managed-instance persistence does not change
    /// their existing queue semantics.
    /// </summary>
    public Guid? ElsaInstanceId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid EnvironmentId { get; set; }
    public DeploymentEnvironmentEntity? Environment { get; set; }
    public Guid EngineId { get; set; }
    public Guid SourceRevisionId { get; set; }
    public Guid? PreviousDeployedRevisionId { get; set; }
    public Guid? RollbackSourceRunId { get; set; }
    public WorkspaceDeploymentRunStatus Status { get; set; }
    public DeploymentValidationOutcome ValidationOutcome { get; set; }
    public Guid ConfirmationId { get; set; }
    public Guid ActorAccountId { get; set; }
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? WorkerId { get; set; }
    public DateTimeOffset? WorkerHeartbeatAt { get; set; }
    public int AttemptNumber { get; set; }
    public string? RecoveryReason { get; set; }
    public string? FailureMessage { get; set; }
    public List<DeploymentRunHistoryEventEntity> History { get; set; } = [];
}

internal sealed class DeploymentRunHistoryEventEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid RunId { get; set; }
    public DeploymentRunEntity? Run { get; set; }
    public WorkspaceDeploymentRunStatus Status { get; set; }
    public string Message { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class DeploymentCommandEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid RunId { get; set; }
    public DeploymentRunEntity? Run { get; set; }
    public Guid EnvironmentId { get; set; }
    public Guid EngineId { get; set; }
    public DeploymentCommandAction Action { get; set; }
    public DeploymentCommandStatus Status { get; set; }
    public string ArtifactJson { get; set; } = "";
    public Guid? RevisionId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string? WorkerId { get; set; }
    public string? LeaseToken { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public int AttemptNumber { get; set; }
    public int? PercentComplete { get; set; }
    public string? ProgressMessage { get; set; }
    public string? ObservedArtifactDigestAlgorithm { get; set; }
    public string? ObservedArtifactDigest { get; set; }
    public string? RuntimeReference { get; set; }
    public string DiagnosticsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? AvailableAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<DeploymentCommandEventEntity> Events { get; set; } = [];
}

internal sealed class DeploymentCommandEventEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid CommandId { get; set; }
    public DeploymentCommandEntity? Command { get; set; }
    public Guid RunId { get; set; }
    public DeploymentCommandStatus Status { get; set; }
    public string Message { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class DeploymentCommandWebhookNotificationEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid EngineId { get; set; }
    public Guid CommandId { get; set; }
    public WebhookNotificationStatus Status { get; set; }
    public string SafePayloadJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

internal sealed class ObservabilityBindingEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid EnvironmentId { get; set; }
    public DeploymentEnvironmentEntity? Environment { get; set; }
    public Guid? EngineId { get; set; }
    public ObservabilityBindingKind Kind { get; set; }
    public string Provider { get; set; } = "";
    public ObservabilityBindingStatus Status { get; set; }
    public string Scope { get; set; } = "";
    public Guid? CorrelatedRevisionId { get; set; }
    public string? Sample { get; set; }
}

internal sealed class DriftReportItemEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid EnvironmentId { get; set; }
    public DeploymentEnvironmentEntity? Environment { get; set; }
    public Guid EngineId { get; set; }
    public string Area { get; set; } = "";
    public string Desired { get; set; } = "";
    public string Observed { get; set; } = "";
    public DriftAction Action { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
}
