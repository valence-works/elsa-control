using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Workspace;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// The read side of the managed-instance boundary. Implementations must scope every
/// query by both workspace and instance identity; callers must not use the values in a
/// request body to establish ownership.
/// </summary>
public interface IManagedElsaInstanceApiStore
{
    Task<ElsaInstancePage> ListInstancesAsync(
        Guid workspaceId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<bool> SlugExistsAsync(
        Guid workspaceId,
        string slug,
        CancellationToken cancellationToken = default);

    Task<ElsaInstanceOperationSummary?> GetOperationAsync(
        Guid workspaceId,
        Guid instanceId,
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, ElsaInstanceOperationSummary>> GetActiveOperationsAsync(
        Guid workspaceId,
        IReadOnlyCollection<Guid> instanceIds,
        CancellationToken cancellationToken = default);

    Task<ElsaInstanceLifecycleTopologySnapshot?> GetLifecycleTopologyAsync(
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ElsaInstanceIntentRevisionSummary>> ListRevisionsAsync(
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken = default);

    Task<ElsaInstanceResolvedPlanSummary?> GetResolvedPlanAsync(
        Guid workspaceId,
        Guid instanceId,
        string planId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ElsaInstanceDeploymentSummary>> ListDeploymentsAsync(
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ElsaInstanceAuditEventSummary>> ListAuditAsync(
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken = default);
}

public sealed record ElsaInstancePage(IReadOnlyList<ElsaInstance> Items, int TotalCount);

public sealed record ElsaInstanceOperationSummary(
    Guid Id,
    Guid InstanceId,
    ElsaInstanceOperationAction Action,
    ElsaInstanceOperationState State,
    int ExpectedVersion,
    int AttemptNumber,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? DesiredStateRevisionId,
    string? ResolvedPlanId,
    Guid? DeploymentRunId,
    string? FailureCode,
    ElsaObservedLifecycle? ReconciledObservedLifecycle,
    ElsaInstanceHealth? ReconciledHealth);

/// <summary>
/// Safe operator projection of one instance's unfinished lifecycle topology.
/// It deliberately excludes request hashes, worker and lease data, recovery
/// evidence, provider payloads, and other credential-bearing values.
/// </summary>
public sealed record ElsaInstanceLifecycleTopologySnapshot(
    Guid InstanceId,
    int InstanceVersion,
    ElsaDesiredLifecycle DesiredLifecycle,
    ElsaObservedLifecycle ObservedLifecycle,
    Guid? LastOperationId,
    IReadOnlyList<ElsaInstanceLifecycleTopologyOperation> Operations);

public sealed record ElsaInstanceLifecycleTopologyOperation(
    Guid Id,
    ElsaInstanceOperationAction Action,
    ElsaInstanceOperationState State,
    int ExpectedVersion,
    int AttemptNumber,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    Guid? DeploymentRunId,
    string? FailureCode,
    string? DeletionDiagnosticCode,
    string? ReconciliationDiagnosticCode,
    ElsaInstanceLifecycleTopologyOutbox? Outbox,
    WorkspaceDeploymentRunStatus? RunStatus = null);

public sealed record ElsaInstanceLifecycleTopologyOutbox(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset? QuarantinedAt,
    string? QuarantineCode);

public sealed class ElsaInstanceLifecycleTopologyChangedException : Exception
{
    public ElsaInstanceLifecycleTopologyChangedException()
        : base("The managed instance lifecycle topology changed while it was being read.")
    {
    }
}

public sealed record ElsaInstanceIntentRevisionSummary(
    Guid Id,
    Guid InstanceId,
    int RevisionNumber,
    string ContentHash,
    string DistributionId,
    string ReleaseLine,
    string? RequestedVersion,
    string Channel,
    string TopologyId,
    string? FeaturePresetId,
    string? PackagePolicy,
    string? ConfigurationShapeRevisionId,
    string TargetMode,
    string RegionCode,
    string IsolationProfile,
    string CapacityProfile,
    string NetworkOutcome,
    string DomainOutcome,
    ElsaDesiredLifecycle DesiredLifecycle,
    DateTimeOffset AuthoredAt,
    Guid? CreatedByAccountId,
    string? PreviewManifestDigest = null);

public sealed record ElsaInstancePlanEvidenceSummary(
    string Kind,
    string Reference,
    string? Digest,
    string Description);

public sealed record ElsaInstanceResolvedPlanSummary(
    ElsaResolvedPlanReference Reference,
    ElsaCurrentResolvedRelease Release,
    string TopologyId,
    IReadOnlyList<string> ComponentIds,
    IReadOnlyList<ElsaInstancePlanEvidenceSummary> Evidence);

public sealed record ElsaInstanceDeploymentSummary(
    Guid RunId,
    Guid SourceRevisionId,
    WorkspaceDeploymentRunStatus Status,
    DeploymentValidationOutcome ValidationOutcome,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int AttemptNumber,
    string? RecoveryReason,
    string? SafeFailureCode);

public sealed record ElsaInstanceAuditEventSummary(
    Guid Id,
    long Sequence,
    string EventType,
    Guid? ActorAccountId,
    string? OperatorSubject,
    Guid? OperationId,
    Guid? MigrationId,
    Guid? DeploymentRunId,
    string? PriorState,
    string? NewState,
    string? DesiredStateRevisionId,
    string? PlanReference,
    string? DiagnosticCode,
    string? Summary,
    string? RequestKeyHash,
    DateTimeOffset OccurredAt);
