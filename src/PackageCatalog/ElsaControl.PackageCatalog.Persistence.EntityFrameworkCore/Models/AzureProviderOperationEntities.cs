using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;

internal sealed class AzureProviderOperationEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    public Guid? ProviderAssignmentId { get; set; }
    public AzureProviderResourceAssignmentEntity? ProviderAssignment { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? InstanceId { get; set; }
    public ElsaInstanceOperationAction? LifecycleAction { get; set; }
    public string TargetKey { get; set; } = "";
    public AzureProviderOperationAction Action { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string OperationIdentity { get; set; } = "";
    public string PlanFingerprint { get; set; } = "";
    public string TemplateFingerprint { get; set; } = "";
    public string? ProviderScopeFingerprint { get; set; }
    public string? SqlWorkflowPackageVersion { get; set; }
    public string? SqlQuartzPackageVersion { get; set; }
    // Workload capacity is all-or-nothing; rows retained before it existed carry none.
    public int? CapacityMinReplicas { get; set; }
    public int? CapacityMaxReplicas { get; set; }
    public int? CapacityCpuMillicores { get; set; }
    public int? CapacityMemoryMiB { get; set; }
    // Whether the deployment configures the runtime handoff; rows retained before it existed never did.
    public bool ManagedHandoff { get; set; }
    public string ElsaVersion { get; set; } = "";
    public string ReleaseLine { get; set; } = "";
    public string Topology { get; set; } = "";
    public string Isolation { get; set; } = "";
    public string Location { get; set; } = "";
    public string ImageRepository { get; set; } = "";
    public string ImageDigest { get; set; } = "";
    public string? ReleaseManifestDigest { get; set; }
    public string? ReleaseManifestSignatureDigest { get; set; }
    public string? ReleaseManifestReference { get; set; }
    public string? ReleaseManifestSignatureReference { get; set; }
    public string SecretReferencesJson { get; set; } = "{}";
    public AzureProviderOperationStatus Status { get; set; }
    public AzureProviderOperationPhase Phase { get; set; }
    public AzureProviderRunnerStep? AttemptedStep { get; set; }
    public long CheckpointSequence { get; set; }
    public int AttemptNumber { get; set; }
    public long Version { get; set; }
    public string? ResourceGroupName { get; set; }
    public string? FoundationDeploymentId { get; set; }
    public string? WorkloadDeploymentId { get; set; }
    public string? WorkloadResourceId { get; set; }
    public string? WorkloadRevisionName { get; set; }
    public string? StableTrafficRevisionName { get; set; }
    public string? WorkloadIdentityResourceId { get; set; }
    public string? WorkloadIdentityClientId { get; set; }
    public string? WorkloadIdentityPrincipalId { get; set; }
    public string? KeyVaultResourceId { get; set; }
    public string? KeyVaultUri { get; set; }
    public string? SqlServerResourceId { get; set; }
    public string? SqlServerFqdn { get; set; }
    public string? ContainerAppsEnvironmentResourceId { get; set; }
    public string? RegistryResourceId { get; set; }
    public string? AcrPullDeploymentId { get; set; }
    public string? AcrPullRoleAssignmentId { get; set; }
    public string? Endpoint { get; set; }
    public AzureProviderHealth Health { get; set; }
    public string DiagnosticsJson { get; set; } = "[]";
    public string? WorkerId { get; set; }
    public string? LeaseTokenHash { get; set; }
    public string? CompletionLeaseTokenHash { get; set; }
    public string? CompletionFingerprint { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<AzureProviderOperationTransitionEntity> Transitions { get; set; } = [];
}

/// <summary>
/// Provider-owned durable placement assignment. The control plane stores only safe Azure
/// resource references and an opaque assignment identity; provider-specific credentials and
/// payloads are intentionally not representable here.
/// </summary>
internal sealed class AzureProviderResourceAssignmentEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid InstanceId { get; set; }
    public string ProviderScopeFingerprint { get; set; } = "";
    public int NamingVersion { get; set; }
    public string SubscriptionId { get; set; } = "";
    public string ResourceGroupName { get; set; } = "";
    public string WorkloadName { get; set; } = "";
    public string OwnershipKey { get; set; } = "";
    public string Location { get; set; } = "";
    public AzureProviderAssignmentState State { get; set; }
    public long Version { get; set; }
    public Guid? LastOperationId { get; set; }
    public string? FoundationDeploymentId { get; set; }
    public string? WorkloadDeploymentId { get; set; }
    public string? WorkloadResourceId { get; set; }
    public string? WorkloadRevisionName { get; set; }
    public string? StableTrafficRevisionName { get; set; }
    public string? WorkloadIdentityResourceId { get; set; }
    public string? WorkloadIdentityClientId { get; set; }
    public string? WorkloadIdentityPrincipalId { get; set; }
    public string? KeyVaultResourceId { get; set; }
    public string? KeyVaultUri { get; set; }
    public string? SqlServerResourceId { get; set; }
    public string? SqlServerFqdn { get; set; }
    public string? ContainerAppsEnvironmentResourceId { get; set; }
    public string? RegistryResourceId { get; set; }
    public string? AcrPullDeploymentId { get; set; }
    public string? AcrPullRoleAssignmentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public List<AzureProviderOperationEntity> Operations { get; set; } = [];
}

internal sealed class AzureProviderOperationTransitionEntity
{
    public Guid Id { get; set; }
    public Guid OperationId { get; set; }
    public AzureProviderOperationEntity? Operation { get; set; }
    public long Sequence { get; set; }
    public AzureProviderOperationStatus Status { get; set; }
    public AzureProviderOperationPhase Phase { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// Append-only, provider-specific recovery proof. Only typed correlation and
/// digest metadata is retained; provider payloads and secrets are not representable.
/// </summary>
internal sealed class AzureProviderRecoveryObservationEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid InstanceId { get; set; }
    public Guid LifecycleOperationId { get; set; }
    public ElsaInstanceOperationAction LifecycleAction { get; set; }
    public int ObservedLifecycleAttemptNumber { get; set; }
    public int ObservedInstanceVersion { get; set; }
    public Guid ProviderOperationId { get; set; }
    public Guid ProviderAssignmentId { get; set; }
    public string ProviderOperationIdentity { get; set; } = "";
    public string ProviderRequestHash { get; set; } = "";
    public int ProviderAttemptNumber { get; set; }
    public long ProviderVersion { get; set; }
    public long ProviderCheckpointSequence { get; set; }
    public string TargetKey { get; set; } = "";
    public string? ProviderScopeFingerprint { get; set; }
    public string ResolvedPlanId { get; set; } = "";
    public int ResolvedPlanSchemaVersion { get; set; }
    public string ResolvedPlanUri { get; set; } = "";
    public string ResolvedPlanContentHash { get; set; } = "";
    public string ProviderPlanFingerprint { get; set; } = "";
    public string ProviderTemplateFingerprint { get; set; } = "";
    public AzureProviderRunnerStep CompletedStep { get; set; }
    public AzureProviderOperationPhase ObservedPhase { get; set; }
    public AzureProviderHealth ObservedHealth { get; set; }
    public string ResourceFingerprint { get; set; } = "";
    public string PostconditionFingerprint { get; set; } = "";
    public string NaturalKey { get; set; } = "";
    public string RecordDigest { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public AzureProviderRecoveryObservationRecord ToRecord() => new(
        OrganizationId,
        WorkspaceId,
        InstanceId,
        LifecycleOperationId,
        LifecycleAction,
        ObservedLifecycleAttemptNumber,
        ObservedInstanceVersion,
        ProviderOperationId,
        ProviderOperationIdentity,
        ProviderRequestHash,
        ProviderAttemptNumber,
        ProviderVersion,
        ProviderCheckpointSequence,
        ProviderAssignmentId,
        TargetKey,
        ProviderScopeFingerprint,
        ResolvedPlanId,
        ResolvedPlanSchemaVersion,
        ResolvedPlanUri,
        ResolvedPlanContentHash,
        ProviderPlanFingerprint,
        ProviderTemplateFingerprint,
        CompletedStep,
        ObservedPhase,
        ObservedHealth,
        ResourceFingerprint,
        PostconditionFingerprint,
        ObservedAt);
}
