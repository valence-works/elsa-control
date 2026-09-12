using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Azure;

public enum AzureProviderOperationAction
{
    Reconcile,
    Delete
}

public enum AzureProviderOperationStatus
{
    Accepted,
    Queued,
    EntitlementHeld,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    RecoveryRequired
}

public enum AzureProviderOperationPhase
{
    Planned = 0,
    FoundationSubmitted = 1,
    FoundationReady = 2,
    WorkloadSubmitted = 3,
    WorkloadReady = 4,
    HealthVerified = 5,
    TrafficPromoted = 6,
    CleanupSubmitted = 7,
    CleanupVerified = 8,
    /// <summary>Fresh recovery observed foundation complete before its first checkpoint.</summary>
    FoundationObserved = 9,
    /// <summary>Fresh recovery observed registry access complete.</summary>
    AcrPullObserved = 10,
    /// <summary>Fresh recovery observed secret seeding complete.</summary>
    SeedSecretsObserved = 11,
    /// <summary>Fresh recovery observed the SQL firewall create postcondition.</summary>
    SqlFirewallReady = 12,
    /// <summary>Fresh recovery observed the SQL bootstrap script postcondition.</summary>
    SqlBootstrapReady = 13
}

public static class AzureProviderOperationPhaseOrdering
{
    public static int Compare(AzureProviderOperationPhase left, AzureProviderOperationPhase right) =>
        Rank(left).CompareTo(Rank(right));

    private static int Rank(AzureProviderOperationPhase phase) => phase switch
    {
        AzureProviderOperationPhase.Planned => 0,
        AzureProviderOperationPhase.FoundationSubmitted => 10,
        AzureProviderOperationPhase.FoundationObserved => 11,
        AzureProviderOperationPhase.AcrPullObserved => 12,
        AzureProviderOperationPhase.SeedSecretsObserved => 13,
        AzureProviderOperationPhase.SqlFirewallReady => 14,
        AzureProviderOperationPhase.SqlBootstrapReady => 15,
        AzureProviderOperationPhase.FoundationReady => 20,
        AzureProviderOperationPhase.WorkloadSubmitted => 30,
        AzureProviderOperationPhase.WorkloadReady => 40,
        AzureProviderOperationPhase.HealthVerified => 50,
        AzureProviderOperationPhase.TrafficPromoted => 60,
        AzureProviderOperationPhase.CleanupSubmitted => 70,
        AzureProviderOperationPhase.CleanupVerified => 80,
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };
}

public enum AzureProviderHealth
{
    Unknown,
    Healthy,
    Degraded,
    Unreachable,
    Failed
}

public sealed record AzureProviderOperationRequest(
    Guid WorkspaceId,
    string TargetKey,
    AzureProviderOperationAction Action,
    string IdempotencyKey,
    string PlanFingerprint,
    string TemplateFingerprint,
    string ElsaVersion,
    string ReleaseLine,
    string Topology,
    string Isolation,
    string Location,
    string ImageRepository,
    string ImageDigest,
    string? ReleaseManifestDigest = null,
    string? ReleaseManifestSignatureDigest = null,
    string? ReleaseManifestReference = null,
    string? ReleaseManifestSignatureReference = null,
    IReadOnlyDictionary<string, string>? SecretReferences = null,
    string? ProviderScopeFingerprint = null,
    string? SqlWorkflowPackageVersion = null,
    string? SqlQuartzPackageVersion = null,
    Guid? OrganizationId = null,
    Guid? InstanceId = null,
    ElsaInstanceOperationAction? LifecycleAction = null,
    Guid? ProviderAssignmentId = null,
    AzureWorkloadCapacity? Capacity = null);

public sealed record AzureProviderResourceReferences(
    string? ResourceGroupName = null,
    string? FoundationDeploymentId = null,
    string? WorkloadDeploymentId = null,
    string? WorkloadResourceId = null,
    string? WorkloadRevisionName = null,
    string? StableTrafficRevisionName = null,
    string? WorkloadIdentityResourceId = null,
    string? WorkloadIdentityClientId = null,
    string? WorkloadIdentityPrincipalId = null,
    string? KeyVaultResourceId = null,
    string? KeyVaultUri = null,
    string? SqlServerResourceId = null,
    string? SqlServerFqdn = null,
    string? ContainerAppsEnvironmentResourceId = null,
    string? RegistryResourceId = null,
    string? AcrPullDeploymentId = null,
    string? AcrPullRoleAssignmentId = null);

public sealed record AzureProviderDiagnostic(string Code, string Message);

public sealed record AzureProviderOperation(
    Guid Id,
    Guid WorkspaceId,
    string TargetKey,
    AzureProviderOperationAction Action,
    string IdempotencyKey,
    string RequestHash,
    string OperationIdentity,
    string PlanFingerprint,
    string TemplateFingerprint,
    string ElsaVersion,
    string ReleaseLine,
    string Topology,
    string Isolation,
    string Location,
    string ImageRepository,
    string ImageDigest,
    string? ReleaseManifestDigest,
    string? ReleaseManifestSignatureDigest,
    AzureProviderOperationStatus Status,
    AzureProviderOperationPhase Phase,
    long CheckpointSequence,
    int AttemptNumber,
    long Version,
    AzureProviderResourceReferences Resources,
    string? Endpoint,
    AzureProviderHealth Health,
    IReadOnlyList<AzureProviderDiagnostic> Diagnostics,
    string? WorkerId,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset? HeartbeatAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? ReleaseManifestReference = null,
    string? ReleaseManifestSignatureReference = null,
    [property: JsonIgnore] IReadOnlyDictionary<string, string>? SecretReferences = null,
    [property: JsonIgnore] bool PersistedMetadataInvalid = false,
    string? ProviderScopeFingerprint = null,
    string? SqlWorkflowPackageVersion = null,
    string? SqlQuartzPackageVersion = null,
    Guid? OrganizationId = null,
    Guid? InstanceId = null,
    ElsaInstanceOperationAction? LifecycleAction = null,
    Guid? ProviderAssignmentId = null,
    AzureProviderRunnerStep? AttemptedStep = null,
    AzureWorkloadCapacity? Capacity = null)
{
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> SafeSecretReferences => SecretReferences ?? EmptySecretReferences;

    private static readonly IReadOnlyDictionary<string, string> EmptySecretReferences =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

public sealed class AzureProviderOperationConflictException(AzureProviderOperation operation)
    : InvalidOperationException("Another active Azure operation already owns this target.")
{
    public AzureProviderOperation Operation { get; } = operation;
}

public sealed record AzureProviderOperationTransition(
    Guid Id,
    Guid OperationId,
    long Sequence,
    AzureProviderOperationStatus Status,
    AzureProviderOperationPhase Phase,
    string Code,
    string Message,
    DateTimeOffset OccurredAt);

public sealed record AzureProviderCheckpoint(
    AzureProviderOperationPhase Phase,
    string Code,
    string Message,
    AzureProviderResourceReferences Resources,
    string? Endpoint,
    AzureProviderHealth Health,
    IReadOnlyList<AzureProviderDiagnostic> Diagnostics,
    bool ReplaceResources = false,
    AzureProviderRunnerStep? AttemptedStep = null);
