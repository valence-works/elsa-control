using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Persistence port for the acceptance transaction. Implementations must atomically
/// commit the aggregate projection, operation and outbox message and must re-check
/// the expected version/unique reservations inside that transaction.
/// </summary>
public interface IElsaInstanceLifecycleStore
{
    Task<ElsaInstance?> GetInstanceAsync(
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken = default);

    Task<ElsaInstanceOperation?> GetActiveOperationAsync(
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the immutable managed-provisioning snapshot accepted for an instance.
    /// Legacy instances have no snapshot and return null.
    /// </summary>
    Task<ElsaInstanceProvisioningContext?> GetProvisioningContextAsync(
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ElsaInstanceProvisioningContext?>(null);

    /// <summary>
    /// Finds any operation for a workspace/key, including completed operations. This
    /// is needed to replay a create whose service-generated instance ID is unknown to
    /// the retried caller.
    /// </summary>
    Task<ElsaInstanceOperation?> FindOperationByKeyAsync(
        Guid workspaceId,
        string idempotencyKey,
        Guid? instanceId = null,
        ElsaInstanceOperationAction? action = null,
        string? idempotencyScope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True when a customer or operator mutation was accepted at or after
    /// <paramref name="version"/>. Create is founding and does not count.
    /// Only <see cref="ElsaInstanceOperation.SystemOnlyLifecycleActions"/>
    /// may rebase; unknown actions count as customer changes.
    /// Customer or operator Recover mutates the parked row in place, so a
    /// <see cref="ElsaInstanceOperation.RecoveryExpectedVersion"/> at or
    /// after <paramref name="version"/> also counts. System auto-resume
    /// does not stamp a new marker; it preserves an existing customer or
    /// operator marker and therefore still counts when one is present.
    /// <paramref name="exceptOperationId"/> excludes the Delete being
    /// recovered or replayed.
    /// </summary>
    Task<bool> HasCustomerMutationAtOrAfterVersionAsync(
        Guid workspaceId,
        Guid instanceId,
        int version,
        Guid? exceptOperationId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically persists an accepted mutation. A store must return the original
    /// operation/outbox for an exact replay and reject mismatched key/hash, version,
    /// or active-operation races with <see cref="ElsaInstanceLifecycleConflictException"/>.
    /// </summary>
    Task<ElsaInstanceLifecycleAcceptance> CommitAcceptedAsync(
        ElsaInstance? expectedInstance,
        ElsaInstance instance,
        ElsaInstanceOperation operation,
        ElsaInstanceLifecycleOutboxMessage outbox,
        CancellationToken cancellationToken = default);

    async Task<ElsaInstanceLifecycleAcceptance> CommitAcceptedWithContextAsync(
        ElsaInstance? expectedInstance,
        ElsaInstance instance,
        ElsaInstanceOperation operation,
        ElsaInstanceLifecycleOutboxMessage outbox,
        ElsaInstanceAcceptanceContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.DeleteConfirmation is not null)
            throw new InvalidOperationException("Atomic delete confirmation persistence is not configured.");
        if (context.ProvisioningContext is not null)
            throw new InvalidOperationException("Atomic provisioning context persistence is not configured.");
        return await CommitAcceptedAsync(expectedInstance, instance, operation, outbox, cancellationToken);
    }
}
