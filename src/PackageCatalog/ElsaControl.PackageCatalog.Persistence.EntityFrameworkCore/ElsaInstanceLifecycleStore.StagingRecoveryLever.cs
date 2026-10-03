using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed partial class EfCoreElsaInstanceLifecycleStore
{
    public async Task<StagingRecoveryLifecycleLeverCommit> AcceptReconcileAndRequireRecoveryAsync(
        Guid instanceId,
        string? operatorSubject,
        string reason = StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode,
        CancellationToken cancellationToken = default)
    {
        if (instanceId == Guid.Empty)
            throw new ArgumentException("Instance ID is required.", nameof(instanceId));
        if (!StagingRecoveryLifecycleLeverStoreDefaults.IsAllowedReason(reason))
            throw new ArgumentException("The staging recovery lever reason is not allowed.", nameof(reason));

        dbContext.ChangeTracker.Clear();
        var firedAuditId = Guid.NewGuid();
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        try
        {
            return await dbContext.ExecuteInTransactionAsync(
                IsolationLevel.Serializable,
                async () =>
                {
                    var instanceEntity = await LoadTrackedInstanceAsync(instanceId, cancellationToken)
                        ?? throw new KeyNotFoundException("Elsa instance does not exist.");

                    var activeOperation = await dbContext.ElsaInstanceOperations
                        .AsNoTracking()
                        .Where(x => x.InstanceId == instanceId &&
                                    (x.State == ElsaInstanceOperationState.Accepted ||
                                     x.State == ElsaInstanceOperationState.WaitingForPriorOperation ||
                                     x.State == ElsaInstanceOperationState.Queued ||
                                     x.State == ElsaInstanceOperationState.EntitlementHeld ||
                                     x.State == ElsaInstanceOperationState.Running ||
                                     x.State == ElsaInstanceOperationState.RecoveryRequired))
                        .OrderByDescending(x => x.AcceptedAt)
                        .ThenByDescending(x => x.CreatedAt)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (activeOperation is not null)
                        throw Conflict("An instance operation is already active.", ElsaInstanceLifecycleConflictReason.OperationActive);

                    var preFireLifecycle = instanceEntity.ObservedLifecycle;
                    var preFireHealth = instanceEntity.Health;
                    if (preFireLifecycle != ElsaObservedLifecycle.Ready ||
                        preFireHealth != ElsaInstanceHealth.Healthy)
                        throw Conflict(
                            "The instance must be Ready and Healthy before the staging recovery lever can fire.",
                            ElsaInstanceLifecycleConflictReason.InvalidState);

                    var instance = MapInstance(instanceEntity);

                    ElsaInstanceTransitionResult transition;
                    try
                    {
                        transition = ElsaInstanceStateMachine.Request(
                            instance,
                            ElsaInstanceOperationAction.Reconcile,
                            activeOperation: null,
                            expectedVersion: instance.Version,
                            idempotencyKey: $"staging-lever-{Guid.NewGuid():N}",
                            requestHash: ComputeStagingLeverRequestHash(instance.Version),
                            idempotencyScope: $"instance/{instance.Id:D}/operations");
                    }
                    catch (ElsaInstanceStateConflictException exception)
                    {
                        throw new ElsaInstanceLifecycleConflictException(
                            "The state machine refused the recovery-required transition.",
                            exception.Reason == ElsaInstanceStateConflictReason.OperationActive
                                ? ElsaInstanceLifecycleConflictReason.OperationActive
                                : ElsaInstanceLifecycleConflictReason.InvalidState);
                    }

                    var commercialDecision = await _commercialGate.EvaluateAsync(
                        instance.OrganizationId,
                        transition.Operation.Action,
                        cancellationToken: cancellationToken);
                    if (!commercialDecision.Allowed)
                        throw new ElsaInstanceLifecycleConflictException(
                            commercialDecision.Summary,
                            ElsaInstanceLifecycleConflictReason.CommercialDenied,
                            commercialDecision.Code,
                            commercialDecision.CurrentInstanceCount,
                            commercialDecision.MaxInstances);

                    ApplyAggregate(instanceEntity, transition.Instance);
                    instanceEntity.UpdatedAt = now;

                    var operationEntity = ToEntity(transition.Operation, transition.Instance, now);
                    operationEntity.DesiredStateRevisionId = instanceEntity.DesiredStateRevisionId;
                    await dbContext.ElsaInstanceOperations.AddAsync(operationEntity, cancellationToken);
                    await dbContext.ElsaInstanceLifecycleOutbox.AddAsync(
                        ToEntity(
                            new ElsaInstanceLifecycleOutboxMessage(
                                Guid.NewGuid(),
                                instance.WorkspaceId,
                                instance.Id,
                                transition.Operation.Id,
                                transition.Operation.Action,
                                transition.Operation.RequestHash,
                                now),
                            transition.Instance),
                        cancellationToken);
                    await dbContext.ElsaInstanceAuditEvents.AddAsync(
                        await CreateAuditEventAsync(
                            instanceEntity,
                            operationEntity,
                            preFireLifecycle,
                            now,
                            cancellationToken,
                            StagingRecoveryLifecycleLeverStoreDefaults.AcceptedEventType,
                            summary: StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode),
                        cancellationToken);
                    await dbContext.SaveChangesAsync(cancellationToken);

                    TransitionPersistedOperation(
                        operationEntity,
                        ElsaInstanceOperationState.Queued,
                        now);
                    await dbContext.SaveChangesAsync(cancellationToken);

                    var recoveryPrior = instanceEntity.ObservedLifecycle;
                    TransitionPersistedOperation(
                        operationEntity,
                        ElsaInstanceOperationState.RecoveryRequired,
                        now);
                    operationEntity.FailureCode = reason;
                    operationEntity.FailureSummary = reason;
                    operationEntity.ReconciliationDiagnosticCode = reason;
                    operationEntity.CompletedAt = null;
                    ApplyReasonClock(
                        operationEntity,
                        previousCode: null,
                        nextCode: reason,
                        now,
                        restartClock: true);
                    instanceEntity.ObservedLifecycle = ElsaObservedLifecycle.Unknown;
                    instanceEntity.Health = ElsaInstanceHealth.Unknown;
                    instanceEntity.UpdatedAt = now;

                    await dbContext.ElsaInstanceAuditEvents.AddAsync(
                        await CreateAuditEventAsync(
                            instanceEntity,
                            operationEntity,
                            recoveryPrior,
                            now,
                            cancellationToken,
                            StagingRecoveryLifecycleLeverStoreDefaults.RecoveryRequiredEventType,
                            diagnosticCode: reason,
                            summary: "Provider state must be reconciled before retry."),
                        cancellationToken);
                    await dbContext.SaveChangesAsync(cancellationToken);

                    var fired = await CreateAuditEventAsync(
                        instanceEntity,
                        operationEntity,
                        preFireLifecycle,
                        now,
                        cancellationToken,
                        StagingRecoveryLifecycleLeverStoreDefaults.FiredEventType,
                        diagnosticCode: reason,
                        summary: reason);
                    fired.Id = firedAuditId;
                    fired.OperatorSubject = NormalizeOperatorSubject(operatorSubject);
                    // Catalog sanitization rewrites Summary to DiagnosticCode. PriorState
                    // is the durable QA-readable pre-fire snapshot (Ready:Healthy).
                    fired.PriorState = StagingRecoveryLifecycleLeverStoreDefaults.FormatFiredSnapshot(
                        preFireLifecycle,
                        preFireHealth);
                    await dbContext.ElsaInstanceAuditEvents.AddAsync(fired, cancellationToken);
                    await dbContext.SaveChangesAsync(cancellationToken);

                    return new StagingRecoveryLifecycleLeverCommit(
                        MapInstance(instanceEntity),
                        MapOperation(operationEntity),
                        reason);
                },
                async (commit, verificationCancellationToken) =>
                    await dbContext.ElsaInstanceAuditEvents.AsNoTracking().AnyAsync(
                        x => x.Id == firedAuditId &&
                             x.InstanceId == instanceId &&
                             x.OperationId == commit.Operation.Id &&
                             x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.FiredEventType &&
                             x.DiagnosticCode == reason,
                        verificationCancellationToken),
                cancellationToken);
        }
        catch (ElsaInstanceLifecycleConflictException)
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
        catch (KeyNotFoundException)
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            throw Conflict("An instance operation is already active.", ElsaInstanceLifecycleConflictReason.OperationActive);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            dbContext.ChangeTracker.Clear();
            throw Conflict("An instance operation is already active.", ElsaInstanceLifecycleConflictReason.OperationActive);
        }
    }

    public async Task<StagingRecoveryLifecycleLeverCommit> ResetLeverParkedReconcileAsync(
        Guid instanceId,
        string? operatorSubject,
        CancellationToken cancellationToken = default)
    {
        if (instanceId == Guid.Empty)
            throw new ArgumentException("Instance ID is required.", nameof(instanceId));

        dbContext.ChangeTracker.Clear();
        var resetAuditId = Guid.NewGuid();
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        try
        {
            return await dbContext.ExecuteInTransactionAsync(
                IsolationLevel.Serializable,
                async () =>
                {
                    var instanceEntity = await LoadTrackedInstanceAsync(instanceId, cancellationToken)
                        ?? throw new KeyNotFoundException("Elsa instance does not exist.");

                    var operationEntity = await dbContext.ElsaInstanceOperations
                        .Where(x => x.InstanceId == instanceId &&
                                    x.Action == ElsaInstanceOperationAction.Reconcile &&
                                    x.State == ElsaInstanceOperationState.RecoveryRequired)
                        .OrderByDescending(x => x.AcceptedAt)
                        .ThenByDescending(x => x.CreatedAt)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (operationEntity is null)
                        throw Conflict(
                            "No lever-parked RecoveryRequired reconcile is active.",
                            ElsaInstanceLifecycleConflictReason.InvalidState);
                    var leverFired = await dbContext.ElsaInstanceAuditEvents
                        .AsNoTracking()
                        .AnyAsync(
                            x => x.InstanceId == instanceId &&
                                 x.OperationId == operationEntity.Id &&
                                 x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.FiredEventType,
                            cancellationToken);
                    var providerKey = AzureProviderOperationValidation.LifecycleIdempotencyKey(operationEntity.Id);
                    var hasProviderOperation = await dbContext.AzureProviderOperations
                        .AsNoTracking()
                        .AnyAsync(x => x.IdempotencyKey == providerKey, cancellationToken);
                    if (!leverFired ||
                        hasProviderOperation ||
                        !StagingRecoveryLifecycleLeverStoreDefaults.IsAllowedReason(operationEntity.FailureCode) ||
                        !string.Equals(
                            operationEntity.ReconciliationDiagnosticCode,
                            operationEntity.FailureCode,
                            StringComparison.Ordinal) ||
                        operationEntity.DeploymentRunId is not null ||
                        operationEntity.ReconciliationRetryEvidenceReference is not null ||
                        operationEntity.ReconciliationRetryEvidenceDigest is not null)
                        throw Conflict(
                            "Only a staging-lever RecoveryRequired park without provider correlation can be reset.",
                            ElsaInstanceLifecycleConflictReason.InvalidState);

                    var priorObserved = instanceEntity.ObservedLifecycle;
                    TransitionPersistedOperation(
                        operationEntity,
                        ElsaInstanceOperationState.Succeeded,
                        now);
                    operationEntity.CompletedAt = now;
                    ApplyReasonClock(
                        operationEntity,
                        CurrentParkReason(operationEntity, null),
                        nextCode: null,
                        now,
                        restartClock: true);
                    operationEntity.ReasonEnteredAt = null;
                    operationEntity.RequiresHumanAt = null;
                    RestoreObservedReady(instanceEntity);
                    instanceEntity.UpdatedAt = now;

                    var reset = await CreateAuditEventAsync(
                        instanceEntity,
                        operationEntity,
                        priorObserved,
                        now,
                        cancellationToken,
                        StagingRecoveryLifecycleLeverStoreDefaults.ResetEventType,
                        diagnosticCode: StagingRecoveryLifecycleLeverStoreDefaults.ResetCode,
                        summary: StagingRecoveryLifecycleLeverStoreDefaults.ResetCode);
                    reset.Id = resetAuditId;
                    reset.OperatorSubject = NormalizeOperatorSubject(operatorSubject);
                    await dbContext.ElsaInstanceAuditEvents.AddAsync(reset, cancellationToken);
                    await dbContext.SaveChangesAsync(cancellationToken);

                    return new StagingRecoveryLifecycleLeverCommit(
                        MapInstance(instanceEntity),
                        MapOperation(operationEntity),
                        operationEntity.FailureCode ?? StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode);
                },
                async (commit, verificationCancellationToken) =>
                    await dbContext.ElsaInstanceAuditEvents.AsNoTracking().AnyAsync(
                        x => x.Id == resetAuditId &&
                             x.InstanceId == instanceId &&
                             x.OperationId == commit.Operation.Id &&
                             x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.ResetEventType &&
                             x.DiagnosticCode == StagingRecoveryLifecycleLeverStoreDefaults.ResetCode,
                        verificationCancellationToken),
                cancellationToken);
        }
        catch (ElsaInstanceLifecycleConflictException)
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
        catch (KeyNotFoundException)
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            throw Conflict("An instance operation is already active.", ElsaInstanceLifecycleConflictReason.OperationActive);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            dbContext.ChangeTracker.Clear();
            throw Conflict("An instance operation is already active.", ElsaInstanceLifecycleConflictReason.OperationActive);
        }
    }

    private static void RestoreObservedReady(ElsaInstanceEntity instance)
    {
        if (instance.ObservedLifecycle == ElsaObservedLifecycle.Unknown)
        {
            instance.ObservedLifecycle = ElsaInstanceStateMachine.Transition(
                ElsaObservedLifecycle.Unknown,
                ElsaObservedLifecycle.Provisioning);
        }

        if (instance.ObservedLifecycle != ElsaObservedLifecycle.Ready)
        {
            instance.ObservedLifecycle = ElsaInstanceStateMachine.Transition(
                instance.ObservedLifecycle,
                ElsaObservedLifecycle.Ready);
        }

        instance.Health = ElsaInstanceHealth.Unknown;
    }

    private static void TransitionPersistedOperation(
        ElsaInstanceOperationEntity entity,
        ElsaInstanceOperationState next,
        DateTimeOffset now)
    {
        var transitioned = MapOperation(entity).TransitionTo(next);
        entity.State = transitioned.State;
        entity.UpdatedAt = now;
    }

    private static string ComputeStagingLeverRequestHash(int expectedVersion)
    {
        var canonical = new StringBuilder()
            .Append(ElsaInstanceOperationAction.Reconcile).Append('\n')
            .Append(expectedVersion.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static string? NormalizeOperatorSubject(string? operatorSubject)
    {
        if (string.IsNullOrWhiteSpace(operatorSubject))
            return null;
        var normalized = operatorSubject.Trim();
        if (normalized.Length is 0 or > 71 || normalized.Any(char.IsControl))
            return null;
        return normalized;
    }
}
