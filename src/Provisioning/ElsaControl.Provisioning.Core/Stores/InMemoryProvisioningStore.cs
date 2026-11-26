using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core.Stores;

/// <summary>
/// Skeleton stand-in for the module's own tables. Production persistence is a
/// later issue; this store is process-local and sufficient for contract tests.
/// </summary>
public sealed class InMemoryProvisioningStore :
    IProvisioningOperationStore,
    IProvisioningEventStore,
    IProvisioningStackLockStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ProvisioningOperation> _operations = [];
    private readonly Dictionary<string, Guid> _idempotency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, List<ProvisioningEvent>> _events = [];

    public Task<ProvisioningEnqueueResult> CreateOrGetAsync(
        ProvisioningRequest request,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        lock (_gate)
        {
            if (_idempotency.TryGetValue(request.IdempotencyKey, out var existingId)
                && _operations.TryGetValue(existingId, out var existing))
            {
                if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Idempotency key is already bound to a different provisioning request.");
                }

                return Task.FromResult(new ProvisioningEnqueueResult(
                    existing.Id,
                    existing.State,
                    Replayed: true,
                    LockConflict: existing.State == ProvisioningOperationState.LockConflict));
            }

            var stackName = ProvisioningStackName.Canonicalize(request.Target.StackName);
            var lockConflict = _locks.ContainsKey(stackName)
                || _operations.Values.Any(operation =>
                    string.Equals(operation.StackName, stackName, StringComparison.Ordinal)
                    && ProvisioningOperationStateMachine.HoldsStackLock(operation.State));

            var state = lockConflict
                ? ProvisioningOperationState.LockConflict
                : ProvisioningOperationState.Queued;
            var operation = new ProvisioningOperation(
                Guid.NewGuid(),
                request.Command,
                state,
                request.Target.ProviderId,
                request.Target.StackKind,
                stackName,
                request.IdempotencyKey,
                requestHash,
                now,
                completedAt: lockConflict ? now : null,
                failureCode: lockConflict ? "stack.lock-conflict" : null);

            _operations[operation.Id] = operation;
            _idempotency[request.IdempotencyKey] = operation.Id;
            return Task.FromResult(new ProvisioningEnqueueResult(
                operation.Id,
                operation.State,
                Replayed: false,
                LockConflict: lockConflict));
        }
    }

    public Task<ProvisioningOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);
    }

    public Task<IReadOnlyList<ProvisioningOperation>> ListRunnableAsync(int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit));

        lock (_gate)
        {
            IReadOnlyList<ProvisioningOperation> runnable = _operations.Values
                .Where(operation => operation.State == ProvisioningOperationState.Queued)
                .OrderBy(operation => operation.AcceptedAt)
                .Take(limit)
                .ToArray();
            return Task.FromResult(runnable);
        }
    }

    public Task<ProvisioningOperation?> ClaimAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var operation)
                || operation.State != ProvisioningOperationState.Queued)
            {
                return Task.FromResult<ProvisioningOperation?>(null);
            }

            if (_locks.ContainsKey(operation.StackName))
            {
                var conflicted = ProvisioningOperationStateMachine.Transition(
                    operation,
                    ProvisioningOperationState.LockConflict,
                    now,
                    "stack.lock-conflict");
                _operations[operationId] = conflicted;
                return Task.FromResult<ProvisioningOperation?>(conflicted);
            }

            var running = ProvisioningOperationStateMachine.Transition(
                operation,
                ProvisioningOperationState.Running,
                now);
            _operations[operationId] = running;
            _locks[operation.StackName] = operationId;
            return Task.FromResult<ProvisioningOperation?>(running);
        }
    }

    public Task<ProvisioningOperation?> CompleteAsync(
        Guid operationId,
        ProvisioningOperationState state,
        DateTimeOffset now,
        string? failureCode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var operation))
                return Task.FromResult<ProvisioningOperation?>(null);

            var completed = ProvisioningOperationStateMachine.Transition(operation, state, now, failureCode);
            _operations[operationId] = completed;
            if (_locks.TryGetValue(operation.StackName, out var holder) && holder == operationId)
                _locks.Remove(operation.StackName);
            return Task.FromResult<ProvisioningOperation?>(completed);
        }
    }

    public Task AppendAsync(IReadOnlyList<ProvisioningEvent> events, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(events);
        foreach (var value in events)
        {
            var sanitised = ProvisioningEventSanitizer.Sanitize(value);
            var list = _events.GetOrAdd(sanitised.OperationId, _ => []);
            lock (list)
                list.Add(sanitised);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProvisioningEvent>> ListAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_events.TryGetValue(operationId, out var list))
            return Task.FromResult<IReadOnlyList<ProvisioningEvent>>([]);

        lock (list)
            return Task.FromResult<IReadOnlyList<ProvisioningEvent>>(list.ToArray());
    }

    public Task<bool> TryAcquireAsync(string stackName, Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonical = ProvisioningStackName.Canonicalize(stackName);
        lock (_gate)
        {
            if (_locks.ContainsKey(canonical))
                return Task.FromResult(false);
            _locks[canonical] = operationId;
            return Task.FromResult(true);
        }
    }

    public Task ReleaseAsync(string stackName, Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonical = ProvisioningStackName.Canonicalize(stackName);
        lock (_gate)
        {
            if (_locks.TryGetValue(canonical, out var holder) && holder == operationId)
                _locks.Remove(canonical);
        }

        return Task.CompletedTask;
    }

    public Task<Guid?> GetHolderAsync(string stackName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonical = ProvisioningStackName.Canonicalize(stackName);
        lock (_gate)
            return Task.FromResult(_locks.TryGetValue(canonical, out var holder) ? holder : (Guid?)null);
    }

    public static string HashRequest(ProvisioningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.Serialize(new
        {
            request.Command,
            request.Target.ProviderId,
            request.Target.StackKind,
            StackName = ProvisioningStackName.Canonicalize(request.Target.StackName),
            request.Target.PlatformStackName,
            request.Spec.RuntimeImage,
            Capabilities = request.Spec.RequiredCapabilities.Order(StringComparer.Ordinal),
            request.Spec.DatabaseKind,
            request.Spec.SecretsKind,
            request.Spec.EndpointKind,
            request.Spec.ScaleKind,
            Config = request.StackConfig.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
