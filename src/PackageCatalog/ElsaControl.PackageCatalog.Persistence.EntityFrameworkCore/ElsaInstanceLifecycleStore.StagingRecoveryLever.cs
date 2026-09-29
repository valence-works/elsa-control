using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed partial class EfCoreElsaInstanceLifecycleStore
{
    public async Task<StagingRecoveryLifecycleLeverCommit> AcceptReconcileAndRequireRecoveryAsync(
        Guid instanceId,
        string? operatorSubject,
        CancellationToken cancellationToken = default)
    {
        if (instanceId == Guid.Empty)
            throw new ArgumentException("Instance ID is required.", nameof(instanceId));

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
                    var instance = MapInstance(instanceEntity);

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

                    var priorObservedLifecycle = instanceEntity.ObservedLifecycle;
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
                            priorObservedLifecycle,
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
                    operationEntity.FailureCode = StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode;
                    operationEntity.FailureSummary = StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode;
                    operationEntity.CompletedAt = null;
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
                            diagnosticCode: StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode,
                            summary: "Provider state must be reconciled before retry."),
                        cancellationToken);

                    var fired = await CreateAuditEventAsync(
                        instanceEntity,
                        operationEntity,
                        recoveryPrior,
                        now,
                        cancellationToken,
                        StagingRecoveryLifecycleLeverStoreDefaults.FiredEventType,
                        diagnosticCode: StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode,
                        summary: StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode);
                    fired.Id = firedAuditId;
                    fired.OperatorSubject = NormalizeOperatorSubject(operatorSubject);
                    await dbContext.ElsaInstanceAuditEvents.AddAsync(fired, cancellationToken);
                    await dbContext.SaveChangesAsync(cancellationToken);

                    return new StagingRecoveryLifecycleLeverCommit(
                        MapInstance(instanceEntity),
                        MapOperation(operationEntity));
                },
                async (commit, verificationCancellationToken) =>
                    await dbContext.ElsaInstanceAuditEvents.AsNoTracking().AnyAsync(
                        x => x.Id == firedAuditId &&
                             x.InstanceId == instanceId &&
                             x.OperationId == commit.Operation.Id &&
                             x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.FiredEventType &&
                             x.DiagnosticCode == StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode,
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
