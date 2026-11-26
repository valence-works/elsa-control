namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// The module's own operation table. It does not share Control catalog tables.
/// </summary>
public interface IProvisioningOperationStore
{
    Task<ProvisioningOperation?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ProvisioningOperation?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task AddAsync(ProvisioningOperation operation, CancellationToken cancellationToken = default);

    Task UpdateAsync(ProvisioningOperation operation, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProvisioningOperation>> ListRunnableAsync(
        int batchSize,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Append-only sanitised event table owned by the module.
/// </summary>
public interface IProvisioningEventStore
{
    Task AppendAsync(ProvisioningEvent @event, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProvisioningEvent>> ListAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Per-stack lock table. One in-flight operation per stack.
/// </summary>
public interface IProvisioningStackLockStore
{
    Task<ProvisioningStackLock?> GetAsync(string stackKey, CancellationToken cancellationToken = default);

    Task<bool> TryAcquireAsync(
        string stackKey,
        Guid operationId,
        DateTimeOffset acquiredAt,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(string stackKey, Guid operationId, CancellationToken cancellationToken = default);
}

public sealed record ProvisioningStackLock(string StackKey, Guid OperationId, DateTimeOffset AcquiredAt);
