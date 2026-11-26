using System.Data;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using ElsaControl.Provisioning.Core.Stores;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.Provisioning.Persistence.EntityFrameworkCore;

/// <summary>
/// Replica-safe queue, events, and fenced stack locks on Control's SQL
/// database. Acquire and renew use conditional updates. Cleanup may reclaim
/// only expired leases.
/// </summary>
public sealed class EfProvisioningStore :
    IProvisioningOperationStore,
    IProvisioningEventStore,
    IProvisioningStackLockStore,
    IDurableProvisioningStore
{
    private readonly IDbContextFactory<ProvisioningDbContext> _factory;
    private readonly ProvisioningReplicaIdentity _replica;

    public EfProvisioningStore(IDbContextFactory<ProvisioningDbContext> factory)
        : this(factory, new ProvisioningReplicaIdentity())
    {
    }

    public EfProvisioningStore(
        IDbContextFactory<ProvisioningDbContext> factory,
        ProvisioningReplicaIdentity replica)
    {
        _factory = factory;
        _replica = replica ?? throw new ArgumentNullException(nameof(replica));
    }

    public async Task<ProvisioningEnqueueResult> CreateOrGetAsync(
        ProvisioningRequest request,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await WriteAsync(db, async () =>
        {
            var existing = await db.Operations
                .AsNoTracking()
                .SingleOrDefaultAsync(row => row.IdempotencyKey == request.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Idempotency key is already bound to a different provisioning request.");
                }

                var state = Enum.Parse<ProvisioningOperationState>(existing.State);
                return new ProvisioningEnqueueResult(
                    existing.Id,
                    state,
                    Replayed: true,
                    LockConflict: state == ProvisioningOperationState.LockConflict,
                    existing.FailureCode);
            }

            var stackName = ProvisioningStackName.Canonicalize(request.Target.StackName);
            var lockKey = ProvisioningStackLockKey.Format(request.Target.StackKind, stackName);
            if (request.Command == ProvisioningCommand.Cancel)
                return await EnqueueCancelAsync(db, request, requestHash, stackName, lockKey, now, cancellationToken)
                    .ConfigureAwait(false);

            var lockHeld = await LockHeldAsync(db, lockKey, cancellationToken).ConfigureAwait(false);
            var lockConflict = lockHeld;
            var stateNow = lockConflict
                ? ProvisioningOperationState.LockConflict
                : ProvisioningOperationState.Queued;
            var operation = new ProvisioningOperation(
                Guid.NewGuid(),
                request.Command,
                stateNow,
                request.Target.ProviderId,
                request.Target.StackKind,
                stackName,
                request.IdempotencyKey,
                requestHash,
                now,
                completedAt: lockConflict ? now : null,
                failureCode: lockConflict ? ProvisioningFailureCodes.LockConflict : null);

            db.Operations.Add(ToEntity(operation, request, lockKey));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ProvisioningEnqueueResult(
                operation.Id,
                operation.State,
                Replayed: false,
                LockConflict: lockConflict,
                operation.FailureCode);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProvisioningOperation?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Operations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : FromEntity(row);
    }

    public async Task<ProvisioningRequest?> GetRequestAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var json = await db.Operations.AsNoTracking()
            .Where(item => item.Id == operationId)
            .Select(item => item.RequestJson)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return json is null ? null : ProvisioningRequestSerializer.Deserialize(json);
    }

    public async Task<ProvisioningOperation?> FindRunningAsync(
        ProvisioningStackKind kind,
        string stackName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockKey = ProvisioningStackLockKey.Format(kind, ProvisioningStackName.Canonicalize(stackName));
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var running = ProvisioningOperationState.Running.ToString();
        var cancelling = ProvisioningOperationState.Cancelling.ToString();
        var rows = await db.Operations.AsNoTracking()
            .Where(item => item.LockKey == lockKey && (item.State == running || item.State == cancelling))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = rows.OrderBy(item => item.AcceptedAt).FirstOrDefault();
        return row is null ? null : FromEntity(row);
    }

    public async Task<ProvisioningOperation?> FindQueuedAsync(
        ProvisioningStackKind kind,
        string stackName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockKey = ProvisioningStackLockKey.Format(kind, ProvisioningStackName.Canonicalize(stackName));
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var queued = ProvisioningOperationState.Queued.ToString();
        var cancel = ProvisioningCommand.Cancel.ToString();
        var rows = await db.Operations.AsNoTracking()
            .Where(item => item.LockKey == lockKey && item.State == queued && item.Command != cancel)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = rows.OrderBy(item => item.AcceptedAt).FirstOrDefault();
        return row is null ? null : FromEntity(row);
    }

    public async Task<IReadOnlyList<ProvisioningOperation>> ListRunnableAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit));

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var queued = ProvisioningOperationState.Queued.ToString();
        var rows = await db.Operations.AsNoTracking()
            .Where(item => item.State == queued)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows
            .OrderBy(item => item.AcceptedAt)
            .Take(limit)
            .Select(FromEntity)
            .ToArray();
    }

    public async Task<ProvisioningOperation?> ClaimAsync(
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await WriteAsync(db, async () =>
        {
            var row = await db.Operations
                .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken)
                .ConfigureAwait(false);
            if (row is null || row.State != ProvisioningOperationState.Queued.ToString())
                return null;

            var operation = FromEntity(row);
            if (operation.Command != ProvisioningCommand.Cancel
                && await LockHeldAsync(db, row.LockKey, cancellationToken).ConfigureAwait(false))
            {
                var conflicted = ProvisioningOperationStateMachine.Transition(
                    operation,
                    ProvisioningOperationState.LockConflict,
                    now,
                    ProvisioningFailureCodes.LockConflict);
                Apply(row, conflicted);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return conflicted;
            }

            var running = ProvisioningOperationStateMachine.Transition(
                operation,
                ProvisioningOperationState.Running,
                now);
            ProvisioningLockEntity? held = null;
            if (operation.Command != ProvisioningCommand.Cancel)
            {
                running = running with
                {
                    LeaseOwner = _replica.Id,
                    LeaseExpiresAt = _replica.ExpiresAt(now),
                    FencingToken = 1
                };
                held = new ProvisioningLockEntity
                {
                    LockKey = row.LockKey,
                    OperationId = operation.Id,
                    Owner = _replica.Id,
                    LeaseExpiresAt = running.LeaseExpiresAt!.Value,
                    FencingToken = 1
                };
                db.Locks.Add(held);
            }

            Apply(row, running);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                var conflicted = ProvisioningOperationStateMachine.Transition(
                    operation,
                    ProvisioningOperationState.LockConflict,
                    now,
                    ProvisioningFailureCodes.LockConflict);
                db.ChangeTracker.Clear();
                var reload = await db.Operations
                    .SingleAsync(item => item.Id == operationId, cancellationToken)
                    .ConfigureAwait(false);
                Apply(reload, conflicted);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return conflicted;
            }

            if (held is not null)
            {
                await StampLeaseExpiryAsync(db, held, row, cancellationToken).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                running = FromEntity(row);
            }

            return running;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProvisioningOperation?> CompleteAsync(
        Guid operationId,
        ProvisioningOperationState state,
        DateTimeOffset now,
        string? failureCode,
        CancellationToken cancellationToken = default,
        bool stateLockMayBeHeld = false,
        long? fencingToken = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await WriteAsync(db, async () =>
        {
            var row = await db.Operations
                .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
                return null;

            var operation = FromEntity(row);
            var held = await db.Locks
                .SingleOrDefaultAsync(item => item.LockKey == row.LockKey, cancellationToken)
                .ConfigureAwait(false);
            if (operation.Command != ProvisioningCommand.Cancel
                && !HoldsFence(held, operationId, fencingToken ?? operation.FencingToken))
            {
                return null;
            }

            var completed = ProvisioningOperationStateMachine.Transition(
                operation,
                state,
                now,
                failureCode) with
            {
                StateLockMayBeHeld = stateLockMayBeHeld
            };
            Apply(row, completed);
            if (ProvisioningOperationStateMachine.IsTerminal(completed.State)
                && !stateLockMayBeHeld
                && HoldsFence(held, operationId, fencingToken ?? operation.FencingToken)
                && held is not null)
            {
                db.Locks.Remove(held);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return completed;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendAsync(
        IReadOnlyList<ProvisioningEvent> events,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
            return;

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(db, async () =>
        {
            foreach (var value in events)
            {
                var sanitised = ProvisioningEventSanitizer.Sanitize(value);
                db.Events.Add(new ProvisioningEventEntity
                {
                    OperationId = sanitised.OperationId,
                    Kind = sanitised.Kind.ToString(),
                    ResourceKind = sanitised.ResourceKind,
                    Message = sanitised.Message,
                    OccurredAt = sanitised.OccurredAt
                });
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProvisioningEvent>> ListAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Events.AsNoTracking()
            .Where(item => item.OperationId == operationId)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(row => new ProvisioningEvent(
            row.OperationId,
            Enum.Parse<ProvisioningEventKind>(row.Kind),
            row.ResourceKind,
            row.Message,
            row.OccurredAt)).ToArray();
    }

    public async Task<bool> TryAcquireAsync(
        ProvisioningStackKind kind,
        string stackName,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockKey = ProvisioningStackLockKey.Format(kind, ProvisioningStackName.Canonicalize(stackName));
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await WriteAsync(db, async () =>
        {
            if (await LockHeldAsync(db, lockKey, cancellationToken).ConfigureAwait(false))
                return false;
            var held = new ProvisioningLockEntity
            {
                LockKey = lockKey,
                OperationId = operationId,
                Owner = _replica.Id,
                LeaseExpiresAt = _replica.ExpiresAt(now),
                FencingToken = 1
            };
            db.Locks.Add(held);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await StampLeaseExpiryAsync(db, held, row: null, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (DbUpdateException)
            {
                return false;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ReleaseAsync(
        ProvisioningStackKind kind,
        string stackName,
        Guid operationId,
        CancellationToken cancellationToken = default,
        long? fencingToken = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockKey = ProvisioningStackLockKey.Format(kind, ProvisioningStackName.Canonicalize(stackName));
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await WriteAsync(db, async () =>
        {
            var held = await db.Locks
                .SingleOrDefaultAsync(item => item.LockKey == lockKey, cancellationToken)
                .ConfigureAwait(false);
            if (!HoldsFence(held, operationId, fencingToken))
                return false;

            db.Locks.Remove(held!);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid?> GetHolderAsync(
        ProvisioningStackKind kind,
        string stackName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockKey = ProvisioningStackLockKey.Format(kind, ProvisioningStackName.Canonicalize(stackName));
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var held = await db.Locks.AsNoTracking()
            .SingleOrDefaultAsync(item => item.LockKey == lockKey, cancellationToken)
            .ConfigureAwait(false);
        return held?.OperationId;
    }

    public async Task<bool> RenewLeaseAsync(
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await WriteAsync(db, async () =>
        {
            var row = await db.Operations
                .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
                return false;

            var held = await db.Locks
                .SingleOrDefaultAsync(
                    item => item.LockKey == row.LockKey
                            && item.OperationId == operationId
                            && item.Owner == _replica.Id
                            && item.FencingToken == row.FencingToken,
                    cancellationToken)
                .ConfigureAwait(false);
            if (held is null)
                return false;

            await StampLeaseExpiryAsync(db, held, row, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RequestCancelAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(db, async () =>
        {
            var row = await db.Operations
                .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken)
                .ConfigureAwait(false);
            if (row is not null && !ProvisioningOperationStateMachine.IsTerminal(
                    Enum.Parse<ProvisioningOperationState>(row.State)))
            {
                row.CancelRequested = true;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsCancelRequestedAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Operations.AsNoTracking()
            .Where(item => item.Id == operationId)
            .Select(item => item.CancelRequested)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProvisioningOperation>> ListStateLockPendingAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Operations.AsNoTracking()
            .Where(item => item.StateLockMayBeHeld)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(FromEntity).ToArray();
    }

    public async Task<bool> HoldsFenceAsync(
        Guid operationId,
        long fencingToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Operations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
            return false;

        var held = await db.Locks.AsNoTracking()
            .SingleOrDefaultAsync(item => item.LockKey == row.LockKey, cancellationToken)
            .ConfigureAwait(false);
        return HoldsFence(held, operationId, fencingToken);
    }

    public async Task<bool> MarkStateLockClearedAsync(
        Guid operationId,
        CancellationToken cancellationToken = default,
        long? fencingToken = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await WriteAsync(db, async () =>
        {
            var row = await db.Operations
                .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
                return false;

            var held = await db.Locks
                .SingleOrDefaultAsync(item => item.LockKey == row.LockKey, cancellationToken)
                .ConfigureAwait(false);
            if (!HoldsFence(held, operationId, fencingToken ?? row.FencingToken))
                return false;

            row.StateLockMayBeHeld = false;
            db.Locks.Remove(held!);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProvisioningOperation>> FailInterruptedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var expiredLockIds = await ListExpiredLockOperationIdsAsync(db, cancellationToken)
            .ConfigureAwait(false);
        var candidates = expiredLockIds.Count == 0
            ? []
            : await db.Operations.AsNoTracking()
                .Where(item => expiredLockIds.Contains(item.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        var expiredIds = candidates
            .Where(item => ProvisioningOperationStateMachine.MayReclaimExpiredLease(
                Enum.Parse<ProvisioningOperationState>(item.State),
                item.StateLockMayBeHeld))
            .Select(item => item.Id)
            .ToList();

        var failed = new List<ProvisioningOperation>();
        foreach (var id in expiredIds)
        {
            var reclaimed = await WriteAsync(db, async () =>
            {
                db.ChangeTracker.Clear();
                var row = await db.Operations
                    .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
                    .ConfigureAwait(false);
                if (row is null)
                    return (ProvisioningOperation?)null;
                var state = Enum.Parse<ProvisioningOperationState>(row.State);
                if (!ProvisioningOperationStateMachine.MayReclaimExpiredLease(state, row.StateLockMayBeHeld))
                    return null;

                var held = await db.Locks
                    .SingleOrDefaultAsync(
                        item => item.LockKey == row.LockKey && item.OperationId == id,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (held is null
                    || !await LeaseExpiredInDatabaseAsync(db, held.LockKey, id, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return null;
                }

                held.Owner = _replica.Id;
                held.FencingToken++;
                if (ProvisioningOperationStateMachine.HoldsStackLock(state))
                {
                    Apply(row, ProvisioningOperationStateMachine.Transition(
                        FromEntity(row),
                        ProvisioningOperationState.Failed,
                        now,
                        ProvisioningFailureCodes.Interrupted) with
                    {
                        StateLockMayBeHeld = true,
                        LeaseOwner = held.Owner,
                        FencingToken = held.FencingToken
                    });
                }
                else
                {
                    row.LeaseOwner = held.Owner;
                    row.FencingToken = held.FencingToken;
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await StampLeaseExpiryAsync(db, held, row, cancellationToken).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return FromEntity(row);
            }, cancellationToken).ConfigureAwait(false);
            if (reclaimed is not null)
                failed.Add(reclaimed);
        }

        return failed;
    }

    private async Task<ProvisioningEnqueueResult> EnqueueCancelAsync(
        ProvisioningDbContext db,
        ProvisioningRequest request,
        string requestHash,
        string stackName,
        string lockKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var runningState = ProvisioningOperationState.Running.ToString();
        var cancellingState = ProvisioningOperationState.Cancelling.ToString();
        var queuedState = ProvisioningOperationState.Queued.ToString();
        var cancelCommand = ProvisioningCommand.Cancel.ToString();
        var runningRows = await db.Operations
            .Where(item => item.LockKey == lockKey && (item.State == runningState || item.State == cancellingState))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var running = runningRows.OrderBy(item => item.AcceptedAt).FirstOrDefault();
        var queuedRows = await db.Operations
            .Where(item => item.LockKey == lockKey && item.State == queuedState && item.Command != cancelCommand)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var queued = queuedRows.OrderBy(item => item.AcceptedAt).FirstOrDefault();

        string code;
        if (running is not null)
        {
            running.CancelRequested = true;
            code = ProvisioningFailureCodes.Cancelled;
        }
        else if (queued is not null)
        {
            var cancelled = ProvisioningOperationStateMachine.Transition(
                FromEntity(queued),
                ProvisioningOperationState.Cancelled,
                now,
                ProvisioningFailureCodes.Cancelled);
            Apply(queued, cancelled);
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
        db.Operations.Add(ToEntity(operation, request, lockKey));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ProvisioningEnqueueResult(
            operation.Id,
            operation.State,
            Replayed: false,
            LockConflict: false,
            operation.FailureCode);
    }

    private bool HoldsFence(ProvisioningLockEntity? held, Guid operationId, long? fencingToken)
    {
        if (held is null || held.OperationId != operationId)
            return false;
        if (!string.Equals(held.Owner, _replica.Id, StringComparison.Ordinal))
            return false;
        if (fencingToken is { } token && held.FencingToken != token)
            return false;
        return true;
    }

    private static async Task<IReadOnlyList<Guid>> ListExpiredLockOperationIdsAsync(
        ProvisioningDbContext db,
        CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlServer())
        {
            return await db.Database.SqlQuery<Guid>(
                    $"""
                     SELECT [OperationId] AS [Value]
                     FROM [{ProvisioningDbContext.SchemaName}].[Locks]
                     WHERE [LeaseExpiresAt] <= TODATETIMEOFFSET(SYSUTCDATETIME(), 0)
                     """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return await db.Database.SqlQuery<Guid>(
                $"""
                 SELECT OperationId AS Value
                 FROM Locks
                 WHERE LeaseExpiresAt <= datetime('now')
                 """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<bool> LeaseExpiredInDatabaseAsync(
        ProvisioningDbContext db,
        string lockKey,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlServer())
        {
            var expired = await db.Database.SqlQuery<int>(
                    $"""
                     SELECT CASE WHEN EXISTS (
                         SELECT 1 FROM [{ProvisioningDbContext.SchemaName}].[Locks]
                         WHERE [LockKey] = {lockKey}
                           AND [OperationId] = {operationId}
                           AND [LeaseExpiresAt] <= TODATETIMEOFFSET(SYSUTCDATETIME(), 0)
                     ) THEN 1 ELSE 0 END AS [Value]
                     """)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);
            return expired == 1;
        }

        var sqliteExpired = await db.Database.SqlQuery<int>(
                $"""
                 SELECT CASE WHEN EXISTS (
                     SELECT 1 FROM Locks
                     WHERE LockKey = {lockKey}
                       AND OperationId = {operationId}
                       AND LeaseExpiresAt <= datetime('now')
                 ) THEN 1 ELSE 0 END AS Value
                 """)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        return sqliteExpired == 1;
    }

    private async Task StampLeaseExpiryAsync(
        ProvisioningDbContext db,
        ProvisioningLockEntity held,
        ProvisioningOperationEntity? row,
        CancellationToken cancellationToken)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(_replica.Lease.TotalSeconds));
        if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE [{ProvisioningDbContext.SchemaName}].[Locks]
                 SET [LeaseExpiresAt] = TODATETIMEOFFSET(DATEADD(second, {seconds}, SYSUTCDATETIME()), 0)
                 WHERE [LockKey] = {held.LockKey}
                   AND [Owner] = {held.Owner}
                   AND [FencingToken] = {held.FencingToken}
                 """,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE Locks
                 SET LeaseExpiresAt = datetime('now', '+' || {seconds} || ' seconds')
                 WHERE LockKey = {held.LockKey}
                   AND Owner = {held.Owner}
                   AND FencingToken = {held.FencingToken}
                 """,
                cancellationToken).ConfigureAwait(false);
        }

        await db.Entry(held).ReloadAsync(cancellationToken).ConfigureAwait(false);
        if (row is not null)
        {
            row.LeaseExpiresAt = held.LeaseExpiresAt;
            row.LeaseOwner = held.Owner;
        }
    }

    private static async Task<T> WriteAsync<T>(
        ProvisioningDbContext db,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            var result = await action().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);
    }

    private static async Task<bool> LockHeldAsync(
        ProvisioningDbContext db,
        string lockKey,
        CancellationToken cancellationToken)
    {
        if (await db.Locks.AnyAsync(item => item.LockKey == lockKey, cancellationToken).ConfigureAwait(false))
            return true;

        var running = ProvisioningOperationState.Running.ToString();
        var cancelling = ProvisioningOperationState.Cancelling.ToString();
        return await db.Operations.AnyAsync(
            item => item.LockKey == lockKey && (item.State == running || item.State == cancelling),
            cancellationToken).ConfigureAwait(false);
    }

    private static ProvisioningOperationEntity ToEntity(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        string lockKey) =>
        new()
        {
            Id = operation.Id,
            Command = operation.Command.ToString(),
            State = operation.State.ToString(),
            ProviderId = operation.ProviderId,
            StackKind = operation.StackKind.ToString(),
            StackName = operation.StackName,
            LockKey = lockKey,
            IdempotencyKey = operation.IdempotencyKey,
            RequestHash = operation.RequestHash,
            AcceptedAt = operation.AcceptedAt,
            StartedAt = operation.StartedAt,
            CompletedAt = operation.CompletedAt,
            FailureCode = operation.FailureCode,
            RequestJson = ProvisioningRequestSerializer.Serialize(request),
            CancelRequested = operation.CancelRequested,
            StateLockMayBeHeld = operation.StateLockMayBeHeld,
            LeaseOwner = operation.LeaseOwner,
            LeaseExpiresAt = operation.LeaseExpiresAt,
            FencingToken = operation.FencingToken
        };

    private static void Apply(ProvisioningOperationEntity row, ProvisioningOperation operation)
    {
        row.State = operation.State.ToString();
        row.StartedAt = operation.StartedAt;
        row.CompletedAt = operation.CompletedAt;
        row.FailureCode = operation.FailureCode;
        row.CancelRequested = operation.CancelRequested;
        row.StateLockMayBeHeld = operation.StateLockMayBeHeld;
        row.LeaseOwner = operation.LeaseOwner;
        row.LeaseExpiresAt = operation.LeaseExpiresAt;
        row.FencingToken = operation.FencingToken;
    }

    private static ProvisioningOperation FromEntity(ProvisioningOperationEntity row) =>
        new(
            row.Id,
            Enum.Parse<ProvisioningCommand>(row.Command),
            Enum.Parse<ProvisioningOperationState>(row.State),
            row.ProviderId,
            Enum.Parse<ProvisioningStackKind>(row.StackKind),
            row.StackName,
            row.IdempotencyKey,
            row.RequestHash,
            row.AcceptedAt,
            startedAt: row.StartedAt,
            completedAt: row.CompletedAt,
            failureCode: row.FailureCode)
        {
            CancelRequested = row.CancelRequested,
            StateLockMayBeHeld = row.StateLockMayBeHeld,
            LeaseOwner = row.LeaseOwner,
            LeaseExpiresAt = row.LeaseExpiresAt,
            FencingToken = row.FencingToken
        };
}
