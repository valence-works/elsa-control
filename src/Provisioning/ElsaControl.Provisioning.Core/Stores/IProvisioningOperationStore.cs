using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core.Stores;

/// <summary>
/// Module-owned operation table. Not shared with the rest of Control.
/// </summary>
public interface IProvisioningOperationStore
{
    Task<ProvisioningEnqueueResult> CreateOrGetAsync(
        ProvisioningRequest request,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<ProvisioningOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProvisioningOperation>> ListRunnableAsync(int limit, CancellationToken cancellationToken = default);

    Task<ProvisioningOperation?> ClaimAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<ProvisioningOperation?> CompleteAsync(
        Guid operationId,
        ProvisioningOperationState state,
        DateTimeOffset now,
        string? failureCode,
        CancellationToken cancellationToken = default,
        bool stateLockMayBeHeld = false,
        long? fencingToken = null);

    Task<ProvisioningRequest?> GetRequestAsync(Guid operationId, CancellationToken cancellationToken = default);

    Task<ProvisioningOperation?> FindRunningAsync(
        ProvisioningStackKind kind,
        string stackName,
        CancellationToken cancellationToken = default);

    Task<ProvisioningOperation?> FindQueuedAsync(
        ProvisioningStackKind kind,
        string stackName,
        CancellationToken cancellationToken = default);

    Task<bool> RenewLeaseAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task RequestCancelAsync(Guid operationId, CancellationToken cancellationToken = default);

    Task<bool> IsCancelRequestedAsync(Guid operationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProvisioningOperation>> ListStateLockPendingAsync(
        CancellationToken cancellationToken = default);

    Task<bool> HoldsFenceAsync(
        Guid operationId,
        long fencingToken,
        CancellationToken cancellationToken = default);

    Task<bool> MarkStateLockClearedAsync(
        Guid operationId,
        CancellationToken cancellationToken = default,
        long? fencingToken = null);

    /// <summary>
    /// Reclaims operations whose lock lease has expired. Live replicas are left
    /// alone. Returned rows keep the module lock until the Pulumi state lock
    /// is cleared.
    /// </summary>
    Task<IReadOnlyList<ProvisioningOperation>> FailInterruptedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
