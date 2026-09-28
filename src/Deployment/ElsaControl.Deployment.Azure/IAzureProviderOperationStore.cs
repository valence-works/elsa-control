using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// The atomic outcome of an operation create-or-get reservation. <see cref="Replayed"/> is
/// assigned by the durable store while selecting or inserting the reservation; it must not be
/// inferred from executor attempts, which can also increase after retries or restarts.
/// </summary>
public sealed record AzureProviderOperationCreateResult(
    AzureProviderOperation Operation,
    bool Replayed);

/// <summary>
/// Result of the durable authorization boundary immediately before an Azure mutation. The
/// operation snapshot is read from the same compare-and-set transaction that evaluated the
/// commercial projection, so a denied operation cannot race into a provider call.
/// </summary>
public sealed record AzureProviderOperationAuthorizationResult(
    AzureProviderOperation Operation,
    ElsaInstanceCommercialGateDecision Decision);

/// <summary>
/// Optional store capability for the commercial authorization linearization point. Stores that
/// own the entitlement projection should implement this beside the operation CAS methods;
/// lightweight provider-test stores may continue to exercise the executor fallback.
/// </summary>
public interface IAzureProviderOperationAuthorizationStore
{
    Task<AzureProviderOperationAuthorizationResult?> AuthorizeAsync(
        Guid workspaceId,
        Guid operationId,
        string leaseToken,
        IElsaInstanceCommercialGate commercialGate,
        DateTimeOffset now,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);
}

public interface IAzureProviderOperationStore
{
    Task<AzureProviderOperation> CreateOrGetAsync(AzureProviderOperationRequest request, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically creates or gets an operation and reports whether the durable reservation was
    /// already present. The replay bit is part of the persistence contract and must be assigned
    /// by the implementation that owns the create-or-get race.
    /// </summary>
    Task<AzureProviderOperationCreateResult> CreateOrGetWithResultAsync(
        AzureProviderOperationRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<AzureProviderOperation?> GetAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Lists accepted, queued, and entitlement-held provider operations that are not currently
    /// leased. Recovery-required rows are durable but excluded from the automatic queue before
    /// the batch limit; explicit provider observation must use <see cref="ClaimRecoveryAsync"/>.
    /// </summary>
    Task<IReadOnlyList<AzureProviderOperation>> ListRunnableAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default);
    Task<AzureProviderOperation?> GetLatestReconcileAsync(Guid workspaceId, string targetKey, string? providerScopeFingerprint, CancellationToken cancellationToken = default);
    /// <summary>
    /// Transitions an operation whose persisted provider plan cannot be restored to a value-free
    /// failure. Recovery-required operations retain their recovery status and target reservation
    /// for explicit operator reconciliation. The compare-and-set version prevents a stale worker
    /// from changing a concurrently claimed or completed operation.
    /// </summary>
    Task<AzureProviderOperation?> MarkUnrestorableAsync(
        Guid workspaceId,
        Guid operationId,
        DateTimeOffset now,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);
    Task<AzureProviderOperation?> ClaimAsync(Guid workspaceId, Guid operationId, string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default);
    Task<AzureProviderOperation?> ClaimRecoveryAsync(Guid workspaceId, Guid operationId, string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default);
    Task<AzureProviderOperation?> HeartbeatAsync(Guid workspaceId, Guid operationId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default);
    Task<AzureProviderOperation?> CheckpointAsync(Guid workspaceId, Guid operationId, string leaseToken, AzureProviderCheckpoint checkpoint, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default);
    Task<AzureProviderOperation?> FinalizeAsync(Guid workspaceId, Guid operationId, string leaseToken, AzureProviderOperationStatus status, string code, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default);
    Task<int> RecoverStaleAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AzureProviderOperationTransition>> ListTransitionsAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the last ARM observation clock without creating recovery evidence or
    /// bumping the provider version. Used to rate-limit <c>deployment group show</c>.
    /// </summary>
    Task RecordArmObservationClockAsync(
        Guid workspaceId,
        Guid operationId,
        DateTimeOffset observedAt,
        int backoffSeconds,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Records the ARM observation clock and an optional operator-visible reason
    /// without bumping the provider version.
    /// </summary>
    Task RecordArmObservationClockAsync(
        Guid workspaceId,
        Guid operationId,
        DateTimeOffset observedAt,
        int backoffSeconds,
        string? reasonCode,
        CancellationToken cancellationToken = default) =>
        RecordArmObservationClockAsync(workspaceId, operationId, observedAt, backoffSeconds, cancellationToken);

    /// <summary>
    /// Atomically charges one automatic resume when <c>AutoResumeCount</c> is still
    /// below the cap. Returns null when the row is missing or already at the cap.
    /// </summary>
    Task<AzureProviderOperation?> IncrementAutoResumeCountAsync(
        Guid workspaceId,
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<AzureProviderOperation?>(null);

    /// <summary>
    /// Appends a version-neutral auto-resume outcome so operators can see each
    /// attempt. Does not bump the provider concurrency token.
    /// </summary>
    Task RecordAutoResumeOutcomeAsync(
        Guid workspaceId,
        Guid operationId,
        string reasonCode,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
