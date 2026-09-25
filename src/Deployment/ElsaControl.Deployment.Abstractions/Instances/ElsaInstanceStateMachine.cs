namespace ElsaControl.Deployment.Abstractions.Instances;

public enum ElsaInstanceOperationAction
{
    Create,
    Reconcile,
    UpdateIntent,
    Start,
    Stop,
    Restart,
    ApproveMinorUpgrade,
    MajorMigration,
    Delete,
    Retry,
    Recover
}

public enum ElsaInstanceOperationState
{
    Accepted,
    WaitingForPriorOperation,
    Queued,
    /// <summary>Provider work held until organization entitlement is restored.</summary>
    EntitlementHeld,
    Running,
    Succeeded,
    Failed,
    RecoveryRequired,
    Cancelled
}

/// <summary>
/// Durable operation envelope values needed by persistence and worker adapters. This
/// contract does not perform I/O and deliberately carries no provider command body.
/// </summary>
public sealed record ElsaInstanceOperation
{
    private ElsaInstanceOperation(
        Guid id,
        Guid instanceId,
        ElsaInstanceOperationAction action,
        string idempotencyScope,
        string idempotencyKey,
        string requestHash,
        int expectedVersion,
        ElsaInstanceOperationState state,
        int attemptNumber,
        DateTimeOffset acceptedAt,
        string? recoveryIdempotencyScope = null,
        string? recoveryIdempotencyKey = null,
        string? recoveryRequestHash = null)
    {
        Id = id;
        InstanceId = instanceId;
        Action = action;
        IdempotencyScope = idempotencyScope;
        IdempotencyKey = idempotencyKey;
        RequestHash = requestHash;
        ExpectedVersion = expectedVersion;
        State = state;
        AttemptNumber = attemptNumber;
        AcceptedAt = acceptedAt;
        RecoveryIdempotencyScope = recoveryIdempotencyScope;
        RecoveryIdempotencyKey = recoveryIdempotencyKey;
        RecoveryRequestHash = recoveryRequestHash;
    }

    public Guid Id { get; }

    public Guid InstanceId { get; }

    public ElsaInstanceOperationAction Action { get; }

    public string IdempotencyScope { get; }

    public string IdempotencyKey { get; }

    public string RequestHash { get; }

    public int ExpectedVersion { get; }

    public ElsaInstanceOperationState State { get; private init; }

    public int AttemptNumber { get; private init; }

    public DateTimeOffset AcceptedAt { get; }

    public string? RecoveryIdempotencyScope { get; private init; }
    public string? RecoveryIdempotencyKey { get; private init; }
    public string? RecoveryRequestHash { get; private init; }

    public bool HoldsReservation => ElsaInstanceOperationGuard.IsActive(State);

    public static ElsaInstanceOperation Create(
        Guid instanceId,
        ElsaInstanceOperationAction action,
        string idempotencyScope,
        string idempotencyKey,
        string requestHash,
        int expectedVersion,
        Guid? operationId = null,
        DateTimeOffset? acceptedAt = null)
    {
        if (instanceId == Guid.Empty)
            throw new ArgumentException("Instance ID is required.", nameof(instanceId));
        ElsaInstanceValue.RequireEnum(action, nameof(action));
        if (operationId == Guid.Empty)
            throw new ArgumentException("Operation ID cannot be empty.", nameof(operationId));
        if (expectedVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion), "Expected version must be positive.");

        return new ElsaInstanceOperation(
            operationId ?? Guid.NewGuid(),
            instanceId,
            action,
            ElsaInstanceReferenceValue.RequireOperationScope(idempotencyScope, nameof(idempotencyScope)),
            ElsaInstanceReferenceValue.RequireOperationKey(idempotencyKey, nameof(idempotencyKey)),
            ElsaInstanceReferenceValue.RequireCanonicalHash(requestHash, nameof(requestHash)),
            expectedVersion,
            ElsaInstanceOperationState.Accepted,
            attemptNumber: 1,
            (acceptedAt ?? DateTimeOffset.UtcNow).ToUniversalTime());
    }

    /// <summary>
    /// Rehydrates an operation from a durable store after the store has validated
    /// its ownership and envelope. The operation state and attempt are restored as
    /// one value so a worker can safely observe recovery attempts without replaying
    /// an artificial in-memory transition sequence.
    /// </summary>
    public static ElsaInstanceOperation Hydrate(
        Guid id,
        Guid instanceId,
        ElsaInstanceOperationAction action,
        string idempotencyScope,
        string idempotencyKey,
        string requestHash,
        int expectedVersion,
        ElsaInstanceOperationState state,
        int attemptNumber,
        DateTimeOffset acceptedAt,
        string? recoveryIdempotencyScope = null,
        string? recoveryIdempotencyKey = null,
        string? recoveryRequestHash = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Operation ID is required.", nameof(id));
        if (instanceId == Guid.Empty)
            throw new ArgumentException("Instance ID is required.", nameof(instanceId));
        ElsaInstanceValue.RequireEnum(action, nameof(action));
        ElsaInstanceValue.RequireEnum(state, nameof(state));
        if (expectedVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion), "Expected version must be positive.");
        if (attemptNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(attemptNumber), "Attempt number must be positive.");
        if (state == ElsaInstanceOperationState.WaitingForPriorOperation &&
            action != ElsaInstanceOperationAction.Delete)
            throw new ArgumentException("Only delete operations can wait for a prior operation.", nameof(state));
        if (new[] { recoveryIdempotencyScope, recoveryIdempotencyKey, recoveryRequestHash }.Count(x => x is not null) is not (0 or 3))
            throw new ArgumentException("Recovery request idempotency evidence must be complete.", nameof(recoveryIdempotencyKey));

        return new ElsaInstanceOperation(
            id,
            instanceId,
            action,
            ElsaInstanceReferenceValue.RequireOperationScope(idempotencyScope, nameof(idempotencyScope)),
            ElsaInstanceReferenceValue.RequireOperationKey(idempotencyKey, nameof(idempotencyKey)),
            ElsaInstanceReferenceValue.RequireCanonicalHash(requestHash, nameof(requestHash)),
            expectedVersion,
            state,
            attemptNumber,
            acceptedAt.ToUniversalTime(),
            recoveryIdempotencyScope is null ? null : ElsaInstanceReferenceValue.RequireOperationScope(recoveryIdempotencyScope, nameof(recoveryIdempotencyScope)),
            recoveryIdempotencyKey is null ? null : ElsaInstanceReferenceValue.RequireOperationKey(recoveryIdempotencyKey, nameof(recoveryIdempotencyKey)),
            recoveryRequestHash is null ? null : ElsaInstanceReferenceValue.RequireCanonicalHash(recoveryRequestHash, nameof(recoveryRequestHash)));
    }

    public ElsaInstanceOperation TransitionTo(ElsaInstanceOperationState next)
    {
        if (!CanTransition(State, next))
            throw new InvalidOperationException($"Operation cannot transition from {State} to {next}.");
        if (next == ElsaInstanceOperationState.WaitingForPriorOperation &&
            Action != ElsaInstanceOperationAction.Delete)
            throw new InvalidOperationException("Only delete operations can wait for a prior operation.");
        return this with { State = next };
    }

    /// <summary>
    /// Recovery is explicit: an uncertain operation cannot be retried by directly
    /// inserting another queued operation.
    /// </summary>
    public ElsaInstanceOperation Recover(string idempotencyScope, string idempotencyKey, string requestHash)
    {
        if (State != ElsaInstanceOperationState.RecoveryRequired)
            throw new InvalidOperationException("Only a recovery-required operation can be recovered.");
        return this with
        {
            State = ElsaInstanceOperationState.Queued,
            AttemptNumber = checked(AttemptNumber + 1),
            RecoveryIdempotencyScope = ElsaInstanceReferenceValue.RequireOperationScope(idempotencyScope, nameof(idempotencyScope)),
            RecoveryIdempotencyKey = ElsaInstanceReferenceValue.RequireOperationKey(idempotencyKey, nameof(idempotencyKey)),
            RecoveryRequestHash = ElsaInstanceReferenceValue.RequireCanonicalHash(requestHash, nameof(requestHash))
        };
    }

    public static bool CanTransition(ElsaInstanceOperationState current, ElsaInstanceOperationState next)
    {
        ElsaInstanceValue.RequireEnum(current, nameof(current));
        ElsaInstanceValue.RequireEnum(next, nameof(next));
        return current == next || (current, next) switch
        {
            (ElsaInstanceOperationState.Accepted, ElsaInstanceOperationState.WaitingForPriorOperation) => true,
            (ElsaInstanceOperationState.Accepted, ElsaInstanceOperationState.Queued) => true,
            (ElsaInstanceOperationState.Accepted, ElsaInstanceOperationState.Succeeded) => true,
            (ElsaInstanceOperationState.Accepted, ElsaInstanceOperationState.Failed) => true,
            (ElsaInstanceOperationState.Accepted, ElsaInstanceOperationState.Cancelled) => true,
            (ElsaInstanceOperationState.WaitingForPriorOperation, ElsaInstanceOperationState.Accepted) => true,
            (ElsaInstanceOperationState.WaitingForPriorOperation, ElsaInstanceOperationState.Queued) => true,
            (ElsaInstanceOperationState.WaitingForPriorOperation, ElsaInstanceOperationState.Failed) => true,
            (ElsaInstanceOperationState.WaitingForPriorOperation, ElsaInstanceOperationState.Cancelled) => true,
            (ElsaInstanceOperationState.Queued, ElsaInstanceOperationState.Running) => true,
            (ElsaInstanceOperationState.Queued, ElsaInstanceOperationState.EntitlementHeld) => true,
            (ElsaInstanceOperationState.EntitlementHeld, ElsaInstanceOperationState.Queued) => true,
            (ElsaInstanceOperationState.EntitlementHeld, ElsaInstanceOperationState.Cancelled) => true,
            (ElsaInstanceOperationState.EntitlementHeld, ElsaInstanceOperationState.Failed) => true,
            (ElsaInstanceOperationState.Queued, ElsaInstanceOperationState.Failed) => true,
            (ElsaInstanceOperationState.Queued, ElsaInstanceOperationState.Cancelled) => true,
            (ElsaInstanceOperationState.Queued, ElsaInstanceOperationState.RecoveryRequired) => true,
            (ElsaInstanceOperationState.Running, ElsaInstanceOperationState.Succeeded) => true,
            (ElsaInstanceOperationState.Running, ElsaInstanceOperationState.Failed) => true,
            (ElsaInstanceOperationState.Running, ElsaInstanceOperationState.Cancelled) => true,
            (ElsaInstanceOperationState.Running, ElsaInstanceOperationState.RecoveryRequired) => true,
            (ElsaInstanceOperationState.RecoveryRequired, ElsaInstanceOperationState.Succeeded) => true,
            (ElsaInstanceOperationState.RecoveryRequired, ElsaInstanceOperationState.Failed) => true,
            (ElsaInstanceOperationState.RecoveryRequired, ElsaInstanceOperationState.Cancelled) => true,
            _ => false
        };
    }
}

public static class ElsaInstanceOperationGuard
{
    /// <summary>
    /// Indicates whether an operation owns the instance's single execution
    /// reservation. Waiting operations are durable successors, but do not own
    /// the reservation held by the operation they are waiting behind.
    /// </summary>
    public static bool IsActive(ElsaInstanceOperationState state)
    {
        ElsaInstanceValue.RequireEnum(state, nameof(state));
        return state is
            ElsaInstanceOperationState.Accepted or
            ElsaInstanceOperationState.Queued or
            ElsaInstanceOperationState.EntitlementHeld or
            ElsaInstanceOperationState.Running or
            ElsaInstanceOperationState.RecoveryRequired;
    }

    /// <summary>
    /// Indicates whether an operation is unfinished and therefore blocks a new
    /// mutation. Waiting successors remain blocking even though they do not hold
    /// the execution reservation themselves.
    /// </summary>
    public static bool IsBlocking(ElsaInstanceOperationState state)
    {
        ElsaInstanceValue.RequireEnum(state, nameof(state));
        return state is
            ElsaInstanceOperationState.Accepted or
            ElsaInstanceOperationState.WaitingForPriorOperation or
            ElsaInstanceOperationState.Queued or
            ElsaInstanceOperationState.EntitlementHeld or
            ElsaInstanceOperationState.Running or
            ElsaInstanceOperationState.RecoveryRequired;
    }

    public static bool IsConflict(ElsaInstanceOperation existing, string idempotencyKey, string requestHash)
    {
        ArgumentNullException.ThrowIfNull(existing);
        idempotencyKey = ElsaInstanceReferenceValue.RequireOperationKey(idempotencyKey, nameof(idempotencyKey));
        requestHash = ElsaInstanceReferenceValue.RequireCanonicalHash(requestHash, nameof(requestHash));
        var sameKey = string.Equals(existing.IdempotencyKey, idempotencyKey, StringComparison.Ordinal);
        var sameRequest = sameKey && string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal);
        return (sameKey && !sameRequest) || (IsBlocking(existing.State) && !sameRequest);
    }

    public static bool IsConflict(
        ElsaInstanceOperation existing,
        string idempotencyScope,
        string idempotencyKey,
        string requestHash)
    {
        ArgumentNullException.ThrowIfNull(existing);
        idempotencyScope = ElsaInstanceReferenceValue.RequireOperationScope(idempotencyScope, nameof(idempotencyScope));
        if (!string.Equals(existing.IdempotencyScope, idempotencyScope, StringComparison.Ordinal))
            return IsBlocking(existing.State);
        return IsConflict(existing, idempotencyKey, requestHash);
    }

    public static bool IsIdempotentReplay(ElsaInstanceOperation existing, string idempotencyKey, string requestHash)
    {
        ArgumentNullException.ThrowIfNull(existing);
        idempotencyKey = ElsaInstanceReferenceValue.RequireOperationKey(idempotencyKey, nameof(idempotencyKey));
        requestHash = ElsaInstanceReferenceValue.RequireCanonicalHash(requestHash, nameof(requestHash));
        return string.Equals(existing.IdempotencyKey, idempotencyKey, StringComparison.Ordinal) &&
               string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal);
    }

    public static bool IsIdempotentReplay(
        ElsaInstanceOperation existing,
        string idempotencyScope,
        string idempotencyKey,
        string requestHash)
    {
        ArgumentNullException.ThrowIfNull(existing);
        idempotencyScope = ElsaInstanceReferenceValue.RequireOperationScope(idempotencyScope, nameof(idempotencyScope));
        return string.Equals(existing.IdempotencyScope, idempotencyScope, StringComparison.Ordinal) &&
               IsIdempotentReplay(existing, idempotencyKey, requestHash);
    }

    public static void EnsureExpectedVersion(ElsaInstance instance, int expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (expectedVersion != instance.Version)
            throw new ElsaInstanceStateConflictException(ElsaInstanceStateConflictReason.VersionConflict);
    }

    public static void EnsureCanAccept(
        ElsaInstance instance,
        ElsaInstanceOperation? activeOperation,
        int expectedVersion,
        string idempotencyKey,
        string requestHash)
    {
        EnsureCanAccept(instance, activeOperation, expectedVersion, activeOperation?.IdempotencyScope, idempotencyKey, requestHash);
    }

    public static void EnsureCanAccept(
        ElsaInstance instance,
        ElsaInstanceOperation? activeOperation,
        int expectedVersion,
        string? idempotencyScope,
        string idempotencyKey,
        string requestHash)
    {
        EnsureExpectedVersion(instance, expectedVersion);
        if (activeOperation is not null && activeOperation.InstanceId != instance.Id)
            throw new ElsaInstanceStateConflictException(ElsaInstanceStateConflictReason.ActiveOperationOwnershipMismatch);
        if (activeOperation is not null && (idempotencyScope is null
                ? IsConflict(activeOperation, idempotencyKey, requestHash)
                : IsConflict(activeOperation, idempotencyScope, idempotencyKey, requestHash)))
            throw new ElsaInstanceStateConflictException(ElsaInstanceStateConflictReason.OperationActive);
    }
}

/// <summary>
/// Stable state-machine conflict categories. Adapters must use <see cref="Reason"/>
/// rather than the diagnostic message when translating a rejected transition.
/// </summary>
public enum ElsaInstanceStateConflictReason
{
    VersionConflict,
    OperationActive,
    ActiveOperationOwnershipMismatch,
    InvalidState,
}

public sealed class ElsaInstanceStateConflictException : InvalidOperationException
{
    public ElsaInstanceStateConflictException(ElsaInstanceStateConflictReason reason)
        : base(reason switch
        {
            ElsaInstanceStateConflictReason.VersionConflict => "Instance version conflict.",
            ElsaInstanceStateConflictReason.OperationActive => "An instance operation is already active.",
            ElsaInstanceStateConflictReason.ActiveOperationOwnershipMismatch => "The active operation belongs to a different instance.",
            ElsaInstanceStateConflictReason.InvalidState => "The requested operation is not valid for the current instance state.",
            _ => "The instance state conflicts with the requested operation."
        })
    {
        Reason = reason;
    }

    public ElsaInstanceStateConflictReason Reason { get; }
}

public sealed record ElsaInstanceTransitionResult
{
    public ElsaInstanceTransitionResult(ElsaInstance instance, ElsaInstanceOperation operation)
    {
        Instance = instance ?? throw new ArgumentNullException(nameof(instance));
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
    }

    public ElsaInstance Instance { get; }

    public ElsaInstanceOperation Operation { get; }
}

/// <summary>
/// Pure lifecycle transition rules for the instance aggregate. Provider adapters only
/// report observations through this boundary; they do not mutate the aggregate directly.
/// </summary>
public static class ElsaInstanceStateMachine
{
    public static ElsaObservedLifecycle Transition(ElsaObservedLifecycle current, ElsaObservedLifecycle next)
    {
        if (!CanTransition(current, next))
            throw new InvalidOperationException($"Instance lifecycle cannot transition from {current} to {next}.");
        return next;
    }

    public static bool CanTransition(ElsaObservedLifecycle current, ElsaObservedLifecycle next)
    {
        ElsaInstanceValue.RequireEnum(current, nameof(current));
        ElsaInstanceValue.RequireEnum(next, nameof(next));
        return current == next || (current, next) switch
        {
            (ElsaObservedLifecycle.Pending, ElsaObservedLifecycle.Provisioning or ElsaObservedLifecycle.Deleting or ElsaObservedLifecycle.Failed or ElsaObservedLifecycle.Unknown) => true,
            (ElsaObservedLifecycle.Provisioning, ElsaObservedLifecycle.Ready or ElsaObservedLifecycle.Degraded or ElsaObservedLifecycle.Failed or ElsaObservedLifecycle.Deleting or ElsaObservedLifecycle.Unknown) => true,
            (ElsaObservedLifecycle.Ready, ElsaObservedLifecycle.Updating or ElsaObservedLifecycle.Stopping or ElsaObservedLifecycle.Degraded or ElsaObservedLifecycle.Deleting or ElsaObservedLifecycle.Unknown) => true,
            (ElsaObservedLifecycle.Degraded, ElsaObservedLifecycle.Ready or ElsaObservedLifecycle.Updating or ElsaObservedLifecycle.Stopping or ElsaObservedLifecycle.Failed or ElsaObservedLifecycle.Deleting or ElsaObservedLifecycle.Unknown) => true,
            (ElsaObservedLifecycle.Updating, ElsaObservedLifecycle.Ready or ElsaObservedLifecycle.Degraded or ElsaObservedLifecycle.Failed or ElsaObservedLifecycle.Deleting or ElsaObservedLifecycle.Unknown) => true,
            (ElsaObservedLifecycle.Stopping, ElsaObservedLifecycle.Stopped or ElsaObservedLifecycle.Failed or ElsaObservedLifecycle.Deleting or ElsaObservedLifecycle.Unknown) => true,
            (ElsaObservedLifecycle.Stopped, ElsaObservedLifecycle.Provisioning or ElsaObservedLifecycle.Deleting or ElsaObservedLifecycle.Unknown) => true,
            (ElsaObservedLifecycle.Failed, ElsaObservedLifecycle.Provisioning or ElsaObservedLifecycle.Deleting or ElsaObservedLifecycle.Unknown) => true,
            (ElsaObservedLifecycle.Unknown, ElsaObservedLifecycle.Provisioning or ElsaObservedLifecycle.Deleting or ElsaObservedLifecycle.Failed or ElsaObservedLifecycle.Degraded or ElsaObservedLifecycle.Stopped) => true,
            (ElsaObservedLifecycle.Deleting, ElsaObservedLifecycle.Deleted or ElsaObservedLifecycle.Unknown) => true,
            _ => false
        };
    }

    public static ElsaInstanceTransitionResult Request(
        ElsaInstance instance,
        ElsaInstanceOperationAction action,
        ElsaInstanceOperation? activeOperation = null,
        int? expectedVersion = null,
        string? idempotencyKey = null,
        string? requestHash = null,
        ElsaInstanceIntent? requestedIntent = null,
        bool minorApproved = false,
        bool migrationAuthorized = false,
        string? idempotencyScope = null)
    {
        ArgumentNullException.ThrowIfNull(instance);

        ElsaInstanceValue.RequireEnum(action, nameof(action));
        var key = ElsaInstanceReferenceValue.RequireOperationKey(
            idempotencyKey ?? $"{action}-{instance.Version}", nameof(idempotencyKey));
        var hash = ElsaInstanceReferenceValue.RequireCanonicalHash(
            requestHash ?? requestedIntent?.ComputeCanonicalHash() ?? instance.ComputeCanonicalIntentHash(), nameof(requestHash));
        var expected = expectedVersion ?? instance.Version;

        var operationScope = idempotencyScope ?? $"instance/{instance.Id:D}/{action}";
        if (activeOperation is not null && activeOperation.InstanceId != instance.Id)
            throw new ElsaInstanceStateConflictException(ElsaInstanceStateConflictReason.ActiveOperationOwnershipMismatch);

        if (action == ElsaInstanceOperationAction.Recover)
        {
            if (activeOperation is not null &&
                string.Equals(activeOperation.RecoveryIdempotencyScope, operationScope, StringComparison.Ordinal) &&
                string.Equals(activeOperation.RecoveryIdempotencyKey, key, StringComparison.Ordinal) &&
                string.Equals(activeOperation.RecoveryRequestHash, hash, StringComparison.Ordinal))
                return new ElsaInstanceTransitionResult(instance, activeOperation);
            if (activeOperation is null || activeOperation.State != ElsaInstanceOperationState.RecoveryRequired)
                throw new ElsaInstanceStateConflictException(ElsaInstanceStateConflictReason.InvalidState);
            ElsaInstanceOperationGuard.EnsureExpectedVersion(instance, expected);
            // A delete recovery resumes cleanup through the same operation. It
            // must not turn an unknown/deleting observation into provisioning.
            var recoveredInstance = activeOperation.Action == ElsaInstanceOperationAction.Delete ||
                                    instance.Intent.DesiredLifecycle == ElsaDesiredLifecycle.Deleting
                ? instance
                : RequestReconciliation(instance);
            return new ElsaInstanceTransitionResult(recoveredInstance, activeOperation.Recover(operationScope, key, hash));
        }

        if (activeOperation is not null && ElsaInstanceOperationGuard.IsIdempotentReplay(activeOperation, operationScope, key, hash))
            return new ElsaInstanceTransitionResult(instance, activeOperation);

        if (action == ElsaInstanceOperationAction.Delete && activeOperation?.Action == ElsaInstanceOperationAction.Delete &&
            instance.Intent.DesiredLifecycle == ElsaDesiredLifecycle.Deleting &&
            ElsaInstanceOperationGuard.IsBlocking(activeOperation.State))
            throw new ElsaInstanceStateConflictException(ElsaInstanceStateConflictReason.OperationActive);

        // Stop and confirmed Delete are safe exits from entitlement-held work. The
        // acceptance store supersedes that held predecessor in the same transaction,
        // so these exits must not be stranded behind a provider mutation that cannot
        // currently be submitted.
        var supersedesEntitlementHeld = activeOperation is { State: ElsaInstanceOperationState.EntitlementHeld } &&
                                        activeOperation.Action != ElsaInstanceOperationAction.Delete &&
                                        action is ElsaInstanceOperationAction.Stop or ElsaInstanceOperationAction.Delete;
        var waitForPriorOperation = action == ElsaInstanceOperationAction.Delete &&
                                    activeOperation is not null &&
                                    ElsaInstanceOperationGuard.IsBlocking(activeOperation.State) &&
                                    !supersedesEntitlementHeld;
        if (waitForPriorOperation)
            ElsaInstanceOperationGuard.EnsureExpectedVersion(instance, expected);
        else
            ElsaInstanceOperationGuard.EnsureCanAccept(
                instance,
                supersedesEntitlementHeld ? null : activeOperation,
                expected,
                operationScope,
                key,
                hash);
        var next = instance;

        switch (action)
        {
            case ElsaInstanceOperationAction.Start:
                EnsureNotDeleted(instance, action);
                if (instance.ObservedLifecycle == ElsaObservedLifecycle.Failed && instance.Intent.DesiredLifecycle != ElsaDesiredLifecycle.Stopped)
                    throw new ElsaInstanceStateConflictException(ElsaInstanceStateConflictReason.InvalidState);
                if (instance.ObservedLifecycle is not (ElsaObservedLifecycle.Stopped or ElsaObservedLifecycle.Failed))
                    throw new ElsaInstanceStateConflictException(ElsaInstanceStateConflictReason.InvalidState);
                next = instance with
                {
                    Intent = instance.Intent with { DesiredLifecycle = ElsaDesiredLifecycle.Running },
                    ObservedLifecycle = ElsaObservedLifecycle.Provisioning,
                    Version = checked(instance.Version + 1)
                };
                break;

            case ElsaInstanceOperationAction.Stop:
                EnsureNotDeleted(instance, action);
                next = RequestStop(instance);
                break;

            case ElsaInstanceOperationAction.Restart:
                EnsureNotDeleted(instance, action);
                next = RequestRestart(instance);
                break;

            case ElsaInstanceOperationAction.Delete:
                if (instance.ObservedLifecycle == ElsaObservedLifecycle.Deleted)
                {
                    var completedDelete = ElsaInstanceOperation.Create(
                        instance.Id,
                        action,
                        operationScope,
                        key,
                        hash,
                        expected).TransitionTo(ElsaInstanceOperationState.Succeeded);
                    return new ElsaInstanceTransitionResult(instance, completedDelete);
                }
                next = instance with
                {
                    Intent = instance.Intent with { DesiredLifecycle = ElsaDesiredLifecycle.Deleting },
                    ObservedLifecycle = instance.ObservedLifecycle == ElsaObservedLifecycle.Unknown
                        ? ElsaObservedLifecycle.Unknown
                        : Transition(instance.ObservedLifecycle, ElsaObservedLifecycle.Deleting),
                    Version = checked(instance.Version + 1)
                };
                break;

            case ElsaInstanceOperationAction.Reconcile:
                EnsureNotDeleted(instance, action);
                next = RequestReconciliation(instance);
                break;

            case ElsaInstanceOperationAction.Retry:
                EnsureNotDeleted(instance, action);
                if (instance.ObservedLifecycle is not (ElsaObservedLifecycle.Failed or ElsaObservedLifecycle.Degraded))
                    throw new ElsaInstanceStateConflictException(ElsaInstanceStateConflictReason.InvalidState);
                next = RequestReconciliation(instance);
                break;

            case ElsaInstanceOperationAction.UpdateIntent:
                EnsureNotDeleted(instance, action);
                next = ApplyIntentUpdate(instance, requestedIntent ?? throw new ArgumentNullException(nameof(requestedIntent)),
                    minorApproved: false, migrationAuthorized: false);
                break;

            case ElsaInstanceOperationAction.ApproveMinorUpgrade:
                EnsureNotDeleted(instance, action);
                next = ApplyIntentUpdate(instance, requestedIntent ?? throw new ArgumentNullException(nameof(requestedIntent)),
                    minorApproved: true, migrationAuthorized: false);
                break;

            case ElsaInstanceOperationAction.MajorMigration:
                EnsureNotDeleted(instance, action);
                next = ApplyIntentUpdate(instance, requestedIntent ?? throw new ArgumentNullException(nameof(requestedIntent)),
                    minorApproved: minorApproved, migrationAuthorized: migrationAuthorized);
                break;

            case ElsaInstanceOperationAction.Create:
                EnsureNotDeleted(instance, action);
                break;
        }

        var operation = ElsaInstanceOperation.Create(
            instance.Id,
            action,
            operationScope,
            key,
            hash,
            expected);
        if (waitForPriorOperation)
            operation = operation.TransitionTo(ElsaInstanceOperationState.WaitingForPriorOperation);
        next = next with { LastOperationId = new ElsaLastOperationId(operation.Id) };
        return new ElsaInstanceTransitionResult(next, operation);
    }

    public static ElsaInstanceTransitionResult WithIntent(
        ElsaInstance instance,
        ElsaInstanceIntent intent,
        int expectedVersion,
        ElsaInstanceOperation? activeOperation = null,
        string? idempotencyKey = null,
        string? requestHash = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(intent);
        return Request(
            instance,
            ElsaInstanceOperationAction.UpdateIntent,
            activeOperation,
            expectedVersion,
            idempotencyKey,
            requestHash,
            requestedIntent: intent);
    }

    public static ElsaInstance Report(ElsaInstance instance, ElsaObservedLifecycle observed)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (observed == ElsaObservedLifecycle.Deleted)
            throw new InvalidOperationException("Deletion requires correlated positive absence evidence.");
        if (observed == ElsaObservedLifecycle.Ready && instance.ObservedLifecycle == ElsaObservedLifecycle.Unknown)
            throw new InvalidOperationException("An unknown instance requires reconciliation before it can be ready.");
        Transition(instance.ObservedLifecycle, observed);
        var health = observed switch
        {
            ElsaObservedLifecycle.Ready => ElsaInstanceHealth.Healthy,
            ElsaObservedLifecycle.Degraded => ElsaInstanceHealth.Degraded,
            ElsaObservedLifecycle.Unknown => ElsaInstanceHealth.Unknown,
            ElsaObservedLifecycle.Failed => ElsaInstanceHealth.Unreachable,
            _ => instance.Health
        };
        return instance.ProjectObservation(observed, health, instance.DeletedAt);
    }

    /// <summary>
    /// Projects a retained tombstone after the deletion coordinator has atomically
    /// verified the owned-resource absence proof and operation/run correlation.
    /// Provider adapters must not call this method directly.
    /// </summary>
    public static ElsaInstance FinalizeDeletion(ElsaInstance instance, DateTimeOffset deletedAt)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Intent.DesiredLifecycle != ElsaDesiredLifecycle.Deleting)
            throw new InvalidOperationException("An instance can be deleted only after deletion intent is recorded.");
        // A retained Delete may still carry a stale Ready observation after provider
        // cleanup succeeds. The deletion coordinator verifies absence before calling
        // this terminal projection, and the Deleting intent remains mandatory.
        if (instance.ObservedLifecycle is not (ElsaObservedLifecycle.Deleting or
            ElsaObservedLifecycle.Unknown or ElsaObservedLifecycle.Ready))
            throw new InvalidOperationException("An instance cannot finalize deletion from its current lifecycle state.");
        if (deletedAt == default)
            throw new ArgumentException("Deletion timestamp is required.", nameof(deletedAt));

        return instance.ProjectObservation(
            ElsaObservedLifecycle.Deleted,
            ElsaInstanceHealth.Unknown,
            deletedAt.ToUniversalTime()) with
        {
            CurrentDeploymentReference = null,
            PlacementAssignmentReference = null,
            ElsaTenantReference = null
        };
    }

    private static ElsaInstance ApplyIntentUpdate(
        ElsaInstance instance,
        ElsaInstanceIntent intent,
        bool minorApproved,
        bool migrationAuthorized)
    {
        if (intent.DesiredLifecycle != instance.Intent.DesiredLifecycle)
            throw new ElsaInstanceStateConflictException(ElsaInstanceStateConflictReason.InvalidState);

        var transition = ElsaReleaseTransitionRules.Classify(instance.Intent.Release.Selection, intent.Release.Selection);
        if (!transition.IsAllowed(minorApproved, migrationAuthorized))
            throw new ElsaInstanceStateConflictException(ElsaInstanceStateConflictReason.InvalidState);

        return instance with { Intent = intent, Version = checked(instance.Version + 1) };
    }

    private static ElsaInstance RequestStop(ElsaInstance instance)
    {
        var observed = instance.ObservedLifecycle switch
        {
            ElsaObservedLifecycle.Ready or ElsaObservedLifecycle.Degraded => ElsaObservedLifecycle.Stopping,
            ElsaObservedLifecycle.Stopped => ElsaObservedLifecycle.Stopped,
            ElsaObservedLifecycle.Pending or ElsaObservedLifecycle.Provisioning or ElsaObservedLifecycle.Failed or ElsaObservedLifecycle.Unknown or ElsaObservedLifecycle.Stopping => instance.ObservedLifecycle,
            _ => throw new ElsaInstanceStateConflictException(ElsaInstanceStateConflictReason.InvalidState)
        };
        if (observed != instance.ObservedLifecycle)
            Transition(instance.ObservedLifecycle, observed);
        return instance with
        {
            Intent = instance.Intent with { DesiredLifecycle = ElsaDesiredLifecycle.Stopped },
            ObservedLifecycle = observed,
            Version = checked(instance.Version + 1)
        };
    }

    private static ElsaInstance RequestRestart(ElsaInstance instance)
    {
        if (instance.ObservedLifecycle is not (ElsaObservedLifecycle.Ready or ElsaObservedLifecycle.Degraded or ElsaObservedLifecycle.Stopped))
            throw new ElsaInstanceStateConflictException(ElsaInstanceStateConflictReason.InvalidState);
        var observed = instance.ObservedLifecycle == ElsaObservedLifecycle.Stopped
            ? ElsaObservedLifecycle.Provisioning
            : ElsaObservedLifecycle.Updating;
        return instance with
        {
            Intent = instance.Intent with { DesiredLifecycle = ElsaDesiredLifecycle.Running },
            ObservedLifecycle = observed,
            Version = checked(instance.Version + 1)
        };
    }

    private static ElsaInstance RequestReconciliation(ElsaInstance instance)
    {
        if (instance.Intent.DesiredLifecycle == ElsaDesiredLifecycle.Stopped)
            return instance;

        var observed = instance.ObservedLifecycle switch
        {
            ElsaObservedLifecycle.Pending or ElsaObservedLifecycle.Unknown or ElsaObservedLifecycle.Stopped or ElsaObservedLifecycle.Failed => ElsaObservedLifecycle.Provisioning,
            ElsaObservedLifecycle.Ready or ElsaObservedLifecycle.Degraded => ElsaObservedLifecycle.Updating,
            _ => instance.ObservedLifecycle
        };
        if (observed != instance.ObservedLifecycle)
            Transition(instance.ObservedLifecycle, observed);
        return instance with { ObservedLifecycle = observed };
    }

    private static void EnsureNotDeleted(ElsaInstance instance, ElsaInstanceOperationAction action)
    {
        if (instance.ObservedLifecycle == ElsaObservedLifecycle.Deleted || instance.Intent.DesiredLifecycle == ElsaDesiredLifecycle.Deleting)
            throw new ElsaInstanceStateConflictException(ElsaInstanceStateConflictReason.InvalidState);
    }
}

public enum ElsaReleaseTransitionKind
{
    None,
    Patch,
    Minor,
    Major
}

public sealed record ElsaReleaseTransition
{
    public ElsaReleaseTransition(ElsaReleaseSelection current, ElsaReleaseSelection target, ElsaReleaseTransitionKind kind)
    {
        Current = current ?? throw new ArgumentNullException(nameof(current));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Kind = ElsaInstanceValue.RequireEnum(kind, nameof(kind));
    }

    public ElsaReleaseSelection Current { get; }

    public ElsaReleaseSelection Target { get; }

    public ElsaReleaseTransitionKind Kind { get; }

    public bool IsAllowed(bool minorApproved, bool migrationAuthorized) => Kind switch
    {
        ElsaReleaseTransitionKind.None or ElsaReleaseTransitionKind.Patch => true,
        ElsaReleaseTransitionKind.Minor => minorApproved,
        ElsaReleaseTransitionKind.Major => migrationAuthorized,
        _ => false
    };
}

public static class ElsaReleaseTransitionRules
{
    public static ElsaReleaseTransition Classify(ElsaReleaseSelection current, ElsaReleaseSelection target)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(target);

        var sameDistribution = string.Equals(current.DistributionId, target.DistributionId, StringComparison.OrdinalIgnoreCase);
        var sameLine = string.Equals(current.ReleaseLine, target.ReleaseLine, StringComparison.OrdinalIgnoreCase);
        var kind = !sameDistribution
            ? ElsaReleaseTransitionKind.Major
            : sameLine
                ? string.Equals(current.Version, target.Version, StringComparison.OrdinalIgnoreCase)
                    ? ElsaReleaseTransitionKind.None
                    : ElsaReleaseTransitionKind.Patch
                : ClassifyLineChange(current.ReleaseLine, target.ReleaseLine);

        return new ElsaReleaseTransition(current, target, kind);
    }

    public static ElsaReleaseTransition Classify(ElsaReleaseIntent current, ElsaReleaseIntent target) =>
        Classify(current.Selection, target.Selection);

    public static void EnsureAllowed(
        ElsaReleaseSelection current,
        ElsaReleaseSelection target,
        bool minorApproved,
        bool migrationAuthorized)
    {
        var transition = Classify(current, target);
        if (!transition.IsAllowed(minorApproved, migrationAuthorized))
            throw new InvalidOperationException($"{transition.Kind} release transitions require explicit authorization.");
    }

    private static ElsaReleaseTransitionKind ClassifyLineChange(string current, string target)
    {
        if (!TryReadMajor(current, out var currentMajor) || !TryReadMajor(target, out var targetMajor))
            return ElsaReleaseTransitionKind.Major;
        return currentMajor == targetMajor ? ElsaReleaseTransitionKind.Minor : ElsaReleaseTransitionKind.Major;
    }

    private static bool TryReadMajor(string releaseLine, out int major)
    {
        var firstPart = releaseLine.Split('.', 2)[0];
        return int.TryParse(firstPart, out major) && major >= 0;
    }
}
