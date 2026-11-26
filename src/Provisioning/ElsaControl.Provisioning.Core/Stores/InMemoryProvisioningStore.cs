using System.Collections.Concurrent;
using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core.Stores;

/// <summary>
/// Module-owned tables in process. Tests only. Production uses the EF store
/// on Control's catalog SQL database. Locks are keyed by (project, stack) and
/// fenced by owner, lease, and a monotonic token.
/// </summary>
public sealed class InMemoryProvisioningStore :
    IProvisioningOperationStore,
    IProvisioningEventStore,
    IProvisioningStackLockStore
{
    private readonly object _gate = new();
    private readonly ProvisioningReplicaIdentity _replica;
    private readonly Dictionary<Guid, ProvisioningOperation> _operations = [];
    private readonly Dictionary<Guid, ProvisioningRequest> _requests = [];
    private readonly Dictionary<string, Guid> _idempotency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LockRecord> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, List<ProvisioningEvent>> _events = [];

    public InMemoryProvisioningStore(ProvisioningReplicaIdentity? replica = null)
    {
        _replica = replica ?? new ProvisioningReplicaIdentity();
    }

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
            if (request.Command == ProvisioningCommand.Cancel)
                return Task.FromResult(EnqueueCancel(request, requestHash, stackName, lockKey, now));

            var lockHeld = LockHeld(lockKey);
            var lockConflict = lockHeld;
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
                SameLock(operation, lockKey)
                && operation.State is ProvisioningOperationState.Running or ProvisioningOperationState.Cancelling));
        }
    }

    public Task<ProvisioningOperation?> FindQueuedAsync(
        ProvisioningStackKind kind,
        string stackName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockKey = ProvisioningStackLockKey.Format(kind, ProvisioningStackName.Canonicalize(stackName));
        lock (_gate)
        {
            return Task.FromResult(_operations.Values
                .Where(operation =>
                    SameLock(operation, lockKey)
                    && operation.State == ProvisioningOperationState.Queued
                    && operation.Command != ProvisioningCommand.Cancel)
                .OrderBy(operation => operation.AcceptedAt)
                .FirstOrDefault());
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
            if (operation.Command != ProvisioningCommand.Cancel)
            {
                var held = new LockRecord(operationId, _replica.Id, _replica.ExpiresAt(now), 1);
                _locks[lockKey] = held;
                running = running with
                {
                    LeaseOwner = held.Owner,
                    LeaseExpiresAt = held.LeaseExpiresAt,
                    FencingToken = held.FencingToken
                };
            }

            _operations[operationId] = running;
            return Task.FromResult<ProvisioningOperation?>(running);
        }
    }

    public Task<ProvisioningOperation?> CompleteAsync(
        Guid operationId,
        ProvisioningOperationState state,
        DateTimeOffset now,
        string? failureCode,
        CancellationToken cancellationToken = default,
        bool stateLockMayBeHeld = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var operation))
                return Task.FromResult<ProvisioningOperation?>(null);

            var completed = ProvisioningOperationStateMachine.Transition(operation, state, now, failureCode) with
            {
                StateLockMayBeHeld = stateLockMayBeHeld
            };
            _operations[operationId] = completed;
            var lockKey = ProvisioningStackLockKey.Format(operation.StackKind, operation.StackName);
            if (ProvisioningOperationStateMachine.IsTerminal(completed.State)
                && !stateLockMayBeHeld
                && _locks.TryGetValue(lockKey, out var holder)
                && holder.OperationId == operationId)
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
            _locks[lockKey] = new LockRecord(operationId, _replica.Id, _replica.ExpiresAt(now), 1);
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
            if (_locks.TryGetValue(lockKey, out var holder) && holder.OperationId == operationId)
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
            return Task.FromResult(_locks.TryGetValue(lockKey, out var holder) ? holder.OperationId : (Guid?)null);
    }

    public Task<bool> RenewLeaseAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var operation))
                return Task.FromResult(false);
            var lockKey = ProvisioningStackLockKey.Format(operation.StackKind, operation.StackName);
            if (!_locks.TryGetValue(lockKey, out var held)
                || held.OperationId != operationId
                || !string.Equals(held.Owner, _replica.Id, StringComparison.Ordinal)
                || held.FencingToken != operation.FencingToken)
            {
                return Task.FromResult(false);
            }

            held.LeaseExpiresAt = _replica.ExpiresAt(now);
            _operations[operationId] = operation with { LeaseExpiresAt = held.LeaseExpiresAt };
            return Task.FromResult(true);
        }
    }

    public Task RequestCancelAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_operations.TryGetValue(operationId, out var operation)
                && !ProvisioningOperationStateMachine.IsTerminal(operation.State))
            {
                _operations[operationId] = operation with { CancelRequested = true };
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> IsCancelRequestedAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(
                _operations.TryGetValue(operationId, out var operation) && operation.CancelRequested);
        }
    }

    public Task<IReadOnlyList<ProvisioningOperation>> ListStateLockPendingAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<ProvisioningOperation> pending = _operations.Values
                .Where(operation => operation.StateLockMayBeHeld)
                .ToArray();
            return Task.FromResult(pending);
        }
    }

    public Task MarkStateLockClearedAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var operation))
                return Task.CompletedTask;

            _operations[operationId] = operation with { StateLockMayBeHeld = false };
            var lockKey = ProvisioningStackLockKey.Format(operation.StackKind, operation.StackName);
            if (_locks.TryGetValue(lockKey, out var holder) && holder.OperationId == operationId)
                _locks.Remove(lockKey);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProvisioningOperation>> FailInterruptedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var expired = _locks
                .Where(pair => pair.Value.LeaseExpiresAt <= now)
                .Select(pair => pair.Value.OperationId)
                .ToArray();
            var failed = new List<ProvisioningOperation>();
            foreach (var id in expired)
            {
                if (!_operations.TryGetValue(id, out var operation)
                    || !ProvisioningOperationStateMachine.HoldsStackLock(operation.State))
                {
                    continue;
                }

                var lockKey = ProvisioningStackLockKey.Format(operation.StackKind, operation.StackName);
                if (!_locks.TryGetValue(lockKey, out var held)
                    || held.OperationId != id
                    || held.LeaseExpiresAt > now)
                {
                    continue;
                }

                held.Owner = _replica.Id;
                held.FencingToken++;
                held.LeaseExpiresAt = _replica.ExpiresAt(now);
                var completed = ProvisioningOperationStateMachine.Transition(
                    operation,
                    ProvisioningOperationState.Failed,
                    now,
                    ProvisioningFailureCodes.Interrupted) with
                {
                    StateLockMayBeHeld = true,
                    LeaseOwner = held.Owner,
                    LeaseExpiresAt = held.LeaseExpiresAt,
                    FencingToken = held.FencingToken
                };
                _operations[id] = completed;
                failed.Add(completed);
            }

            return Task.FromResult<IReadOnlyList<ProvisioningOperation>>(failed);
        }
    }

    public static string HashRequest(ProvisioningRequest request) =>
        ProvisioningRequestHash.Compute(request);

    private ProvisioningEnqueueResult EnqueueCancel(
        ProvisioningRequest request,
        string requestHash,
        string stackName,
        string lockKey,
        DateTimeOffset now)
    {
        var running = _operations.Values.FirstOrDefault(operation =>
            SameLock(operation, lockKey)
            && operation.State is ProvisioningOperationState.Running or ProvisioningOperationState.Cancelling);
        var queued = _operations.Values
            .Where(operation =>
                SameLock(operation, lockKey)
                && operation.State == ProvisioningOperationState.Queued
                && operation.Command != ProvisioningCommand.Cancel)
            .OrderBy(operation => operation.AcceptedAt)
            .FirstOrDefault();

        string? code;
        if (running is not null)
        {
            _operations[running.Id] = running with { CancelRequested = true };
            code = ProvisioningFailureCodes.Cancelled;
        }
        else if (queued is not null)
        {
            _operations[queued.Id] = ProvisioningOperationStateMachine.Transition(
                queued,
                ProvisioningOperationState.Cancelled,
                now,
                ProvisioningFailureCodes.Cancelled);
            code = ProvisioningFailureCodes.Cancelled;
        }
        else
        {
            code = ProvisioningFailureCodes.NothingToCancel;
        }

        var operation = new ProvisioningOperation(
            Guid.NewGuid(),
            ProvisioningCommand.Cancel,
            ProvisioningOperationState.Succeeded,
            request.Target.ProviderId,
            request.Target.StackKind,
            stackName,
            request.IdempotencyKey,
            requestHash,
            now,
            completedAt: now,
            failureCode: code);
        _operations[operation.Id] = operation;
        _requests[operation.Id] = request;
        _idempotency[request.IdempotencyKey] = operation.Id;
        return new ProvisioningEnqueueResult(
            operation.Id,
            operation.State,
            Replayed: false,
            LockConflict: false,
            operation.FailureCode);
    }

    private bool LockHeld(string lockKey) =>
        _locks.ContainsKey(lockKey)
        || _operations.Values.Any(operation =>
            SameLock(operation, lockKey)
            && ProvisioningOperationStateMachine.HoldsStackLock(operation.State));

    private static bool SameLock(ProvisioningOperation operation, string lockKey) =>
        string.Equals(
            ProvisioningStackLockKey.Format(operation.StackKind, operation.StackName),
            lockKey,
            StringComparison.Ordinal);

    private static string StackName(ProvisioningRequest request) =>
        ProvisioningStackName.Canonicalize(request.Target.StackName);

    private sealed class LockRecord(Guid operationId, string owner, DateTimeOffset leaseExpiresAt, long fencingToken)
    {
        public Guid OperationId { get; } = operationId;
        public string Owner { get; set; } = owner;
        public DateTimeOffset LeaseExpiresAt { get; set; } = leaseExpiresAt;
        public long FencingToken { get; set; } = fencingToken;
    }
}
