using System.Data;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using ElsaControl.Provisioning.Core.Stores;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.Provisioning.Persistence.EntityFrameworkCore;

/// <summary>
/// Replica-safe queue, events, and stack locks on Control's SQL database.
/// Unique lock keys plus a transaction make acquire fail closed across replicas.
/// </summary>
public sealed class EfProvisioningStore(
    IDbContextFactory<ProvisioningDbContext> factory) :
    IProvisioningOperationStore,
    IProvisioningEventStore,
    IProvisioningStackLockStore,
    IDurableProvisioningStore
{
    public async Task<ProvisioningEnqueueResult> CreateOrGetAsync(
        ProvisioningRequest request,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
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
            var lockHeld = await LockHeldAsync(db, lockKey, cancellationToken).ConfigureAwait(false);
            var lockConflict = lockHeld && request.Command != ProvisioningCommand.Cancel;
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
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
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
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
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
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var running = ProvisioningOperationState.Running.ToString();
        var cancelling = ProvisioningOperationState.Cancelling.ToString();
        var row = await db.Operations.AsNoTracking()
            .Where(item => item.LockKey == lockKey && (item.State == running || item.State == cancelling))
            .OrderBy(item => item.AcceptedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : FromEntity(row);
    }

    public async Task<IReadOnlyList<ProvisioningOperation>> ListRunnableAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit));

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var queued = ProvisioningOperationState.Queued.ToString();
        var rows = await db.Operations.AsNoTracking()
            .Where(item => item.State == queued)
            .OrderBy(item => item.AcceptedAt)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(FromEntity).ToArray();
    }

    public async Task<ProvisioningOperation?> ClaimAsync(
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
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
            Apply(row, running);
            if (operation.Command != ProvisioningCommand.Cancel)
                db.Locks.Add(new ProvisioningLockEntity { LockKey = row.LockKey, OperationId = operation.Id });

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

            return running;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProvisioningOperation?> CompleteAsync(
        Guid operationId,
        ProvisioningOperationState state,
        DateTimeOffset now,
        string? failureCode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await WriteAsync(db, async () =>
        {
            var row = await db.Operations
                .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
                return null;

            var completed = ProvisioningOperationStateMachine.Transition(
                FromEntity(row),
                state,
                now,
                failureCode);
            Apply(row, completed);
            if (ProvisioningOperationStateMachine.IsTerminal(completed.State))
            {
                var held = await db.Locks
                    .SingleOrDefaultAsync(
                        item => item.LockKey == row.LockKey && item.OperationId == operationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (held is not null)
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

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
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
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
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
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await WriteAsync(db, async () =>
        {
            if (await LockHeldAsync(db, lockKey, cancellationToken).ConfigureAwait(false))
                return false;
            db.Locks.Add(new ProvisioningLockEntity { LockKey = lockKey, OperationId = operationId });
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (DbUpdateException)
            {
                return false;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(
        ProvisioningStackKind kind,
        string stackName,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockKey = ProvisioningStackLockKey.Format(kind, ProvisioningStackName.Canonicalize(stackName));
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(db, async () =>
        {
            var held = await db.Locks
                .SingleOrDefaultAsync(
                    item => item.LockKey == lockKey && item.OperationId == operationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (held is not null)
            {
                db.Locks.Remove(held);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid?> GetHolderAsync(
        ProvisioningStackKind kind,
        string stackName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockKey = ProvisioningStackLockKey.Format(kind, ProvisioningStackName.Canonicalize(stackName));
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var held = await db.Locks.AsNoTracking()
            .SingleOrDefaultAsync(item => item.LockKey == lockKey, cancellationToken)
            .ConfigureAwait(false);
        return held?.OperationId;
    }

    public async Task<IReadOnlyList<ProvisioningOperation>> FailInterruptedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var running = ProvisioningOperationState.Running.ToString();
        var cancelling = ProvisioningOperationState.Cancelling.ToString();
        var ids = await db.Operations.AsNoTracking()
            .Where(item => item.State == running || item.State == cancelling)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var failed = new List<ProvisioningOperation>();
        foreach (var id in ids)
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
            RequestJson = ProvisioningRequestSerializer.Serialize(request)
        };

    private static void Apply(ProvisioningOperationEntity row, ProvisioningOperation operation)
    {
        row.State = operation.State.ToString();
        row.StartedAt = operation.StartedAt;
        row.CompletedAt = operation.CompletedAt;
        row.FailureCode = operation.FailureCode;
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
            failureCode: row.FailureCode);
}
