using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Skeleton store for the module's own tables. Catalog EF persistence is a later
/// leaf so production schema does not change in this spike.
/// </summary>
public sealed class InMemoryProvisioningStore :
    IProvisioningOperationStore,
    IProvisioningEventStore,
    IProvisioningStackLockStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ProvisioningOperation> _operations = [];
    private readonly Dictionary<string, Guid> _idempotency = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, List<ProvisioningEvent>> _events = [];
    private readonly Dictionary<string, ProvisioningStackLock> _locks = new(StringComparer.Ordinal);

    public Task<ProvisioningOperation?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(_operations.TryGetValue(id, out var operation) ? operation : null);
    }

    public Task<ProvisioningOperation?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_idempotency.TryGetValue(idempotencyKey, out var id))
                return Task.FromResult<ProvisioningOperation?>(null);
            return Task.FromResult<ProvisioningOperation?>(_operations[id]);
        }
    }

    public Task AddAsync(ProvisioningOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_operations.ContainsKey(operation.Id))
                throw new InvalidOperationException("Operation already exists.");
            _operations[operation.Id] = operation;
            _idempotency[operation.IdempotencyKey] = operation.Id;
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(ProvisioningOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_operations.ContainsKey(operation.Id))
                throw new InvalidOperationException("Operation does not exist.");
            _operations[operation.Id] = operation;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProvisioningOperation>> ListRunnableAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<ProvisioningOperation> runnable = _operations.Values
                .Where(operation => operation.Status is ProvisioningOperationStatus.Accepted
                    or ProvisioningOperationStatus.Queued
                    or ProvisioningOperationStatus.Cancelling)
                .OrderBy(operation => operation.AcceptedAt)
                .Take(batchSize)
                .ToArray();
            return Task.FromResult(runnable);
        }
    }

    public Task AppendAsync(ProvisioningEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        cancellationToken.ThrowIfCancellationRequested();
        var sanitised = ProvisioningEventSanitizer.Sanitize(@event);
        lock (_gate)
        {
            if (!_events.TryGetValue(sanitised.OperationId, out var list))
            {
                list = [];
                _events[sanitised.OperationId] = list;
            }

            list.Add(sanitised);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProvisioningEvent>> ListAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_events.TryGetValue(operationId, out var list))
                return Task.FromResult<IReadOnlyList<ProvisioningEvent>>([]);
            return Task.FromResult<IReadOnlyList<ProvisioningEvent>>(list.ToArray());
        }
    }

    public Task<ProvisioningStackLock?> GetAsync(string stackKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stackKey);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(_locks.TryGetValue(stackKey, out var held) ? held : null);
    }

    public Task<bool> TryAcquireAsync(
        string stackKey,
        Guid operationId,
        DateTimeOffset acquiredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stackKey);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_locks.TryGetValue(stackKey, out var held) && held.OperationId != operationId)
                return Task.FromResult(false);
            _locks[stackKey] = new ProvisioningStackLock(stackKey, operationId, acquiredAt);
            return Task.FromResult(true);
        }
    }

    public Task ReleaseAsync(string stackKey, Guid operationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stackKey);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_locks.TryGetValue(stackKey, out var held) && held.OperationId == operationId)
                _locks.Remove(stackKey);
        }

        return Task.CompletedTask;
    }
}
