using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core.Stores;

/// <summary>
/// Module-owned tables in process. Request envelopes live here, not in a
/// static buffer. Durable SQLite is used when a store path is configured.
/// Locks are keyed by (project, stack).
/// </summary>
public sealed class InMemoryProvisioningStore :
    IProvisioningOperationStore,
    IProvisioningEventStore,
    IProvisioningStackLockStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ProvisioningOperation> _operations = [];
    private readonly Dictionary<Guid, ProvisioningRequest> _requests = [];
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
                    LockConflict: existing.State == ProvisioningOperationState.LockConflict,
                    existing.FailureCode));
            }

            var stackName = StackName(request);
            var lockKey = ProvisioningStackLockKey.Format(request.Target.StackKind, stackName);
            var lockHeld = _locks.ContainsKey(lockKey)
                || _operations.Values.Any(operation =>
                    string.Equals(
                        ProvisioningStackLockKey.Format(operation.StackKind, operation.StackName),
                        lockKey,
                        StringComparison.Ordinal)
                    && ProvisioningOperationStateMachine.HoldsStackLock(operation.State));

            // Cancel never takes the stack lock and never conflicts with an
            // in-flight run: it signals that run, or no-ops when idle.
            var lockConflict = lockHeld && request.Command != ProvisioningCommand.Cancel;
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
                failureCode: lockConflict ? ProvisioningFailureCodes.LockConflict : null);

            _operations[operation.Id] = operation;
            _requests[operation.Id] = request;
            _idempotency[request.IdempotencyKey] = operation.Id;
            return Task.FromResult(new ProvisioningEnqueueResult(
                operation.Id,
                operation.State,
                Replayed: false,
                LockConflict: lockConflict,
                operation.FailureCode));
        }
    }

    public Task<ProvisioningOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);
    }

    public Task<ProvisioningRequest?> GetRequestAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(_requests.TryGetValue(operationId, out var request) ? request : null);
    }

    public Task<ProvisioningOperation?> FindRunningAsync(
        ProvisioningStackKind kind,
        string stackName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockKey = ProvisioningStackLockKey.Format(kind, stackName);
        lock (_gate)
        {
            return Task.FromResult(_operations.Values.FirstOrDefault(operation =>
                string.Equals(
                    ProvisioningStackLockKey.Format(operation.StackKind, operation.StackName),
                    lockKey,
                    StringComparison.Ordinal)
                && operation.State is ProvisioningOperationState.Running or ProvisioningOperationState.Cancelling));
        }
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

            var lockKey = ProvisioningStackLockKey.Format(operation.StackKind, operation.StackName);
            if (operation.Command != ProvisioningCommand.Cancel && _locks.ContainsKey(lockKey))
            {
                var conflicted = ProvisioningOperationStateMachine.Transition(
                    operation,
                    ProvisioningOperationState.LockConflict,
                    now,
                    ProvisioningFailureCodes.LockConflict);
                _operations[operationId] = conflicted;
                return Task.FromResult<ProvisioningOperation?>(conflicted);
            }

            var running = ProvisioningOperationStateMachine.Transition(
                operation,
                ProvisioningOperationState.Running,
                now);
            _operations[operationId] = running;
            if (operation.Command != ProvisioningCommand.Cancel)
                _locks[lockKey] = operationId;
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
            var lockKey = ProvisioningStackLockKey.Format(operation.StackKind, operation.StackName);
            if (ProvisioningOperationStateMachine.IsTerminal(completed.State)
                && _locks.TryGetValue(lockKey, out var holder)
                && holder == operationId)
            {
                _locks.Remove(lockKey);
            }

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

    public Task<bool> TryAcquireAsync(
        ProvisioningStackKind kind,
        string stackName,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockKey = ProvisioningStackLockKey.Format(kind, stackName);
        lock (_gate)
        {
            if (_locks.ContainsKey(lockKey))
                return Task.FromResult(false);
            _locks[lockKey] = operationId;
            return Task.FromResult(true);
        }
    }

    public Task ReleaseAsync(
        ProvisioningStackKind kind,
        string stackName,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockKey = ProvisioningStackLockKey.Format(kind, stackName);
        lock (_gate)
        {
            if (_locks.TryGetValue(lockKey, out var holder) && holder == operationId)
                _locks.Remove(lockKey);
        }

        return Task.CompletedTask;
    }

    public Task<Guid?> GetHolderAsync(
        ProvisioningStackKind kind,
        string stackName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockKey = ProvisioningStackLockKey.Format(kind, stackName);
        lock (_gate)
            return Task.FromResult(_locks.TryGetValue(lockKey, out var holder) ? holder : (Guid?)null);
    }

    public async Task<IReadOnlyList<ProvisioningOperation>> FailInterruptedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guid[] interrupted;
        lock (_gate)
        {
            interrupted = _operations.Values
                .Where(operation => ProvisioningOperationStateMachine.HoldsStackLock(operation.State))
                .Select(operation => operation.Id)
                .ToArray();
        }

        var failed = new List<ProvisioningOperation>();
        foreach (var id in interrupted)
        {
            var completed = await CompleteAsync(
                id,
                ProvisioningOperationState.Failed,
                now,
                ProvisioningFailureCodes.Interrupted,
                CancellationToken.None).ConfigureAwait(false);
            if (completed is not null)
                failed.Add(completed);
        }

        return failed;
    }

    public static string HashRequest(ProvisioningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.Serialize(new
        {
            request.Command,
            request.Target.ProviderId,
            request.Target.StackKind,
            Project = ProvisioningProjects.For(request.Target.StackKind),
            StackName = StackName(request),
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

    private static string StackName(ProvisioningRequest request) =>
        ProvisioningStackName.Canonicalize(request.Target.StackName);
}
