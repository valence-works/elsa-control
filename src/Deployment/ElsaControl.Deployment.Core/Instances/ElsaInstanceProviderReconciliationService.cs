using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Telemetry;

namespace ElsaControl.Deployment.Core.Instances;

public sealed class ElsaInstanceProviderReconciliationService(
    IElsaInstanceProviderReconciliationStore store,
    IElsaInstanceProviderReconciliationPort provider,
    TimeProvider? timeProvider = null) : IElsaInstanceProviderReconciliationService
{
    public const string ConvergedCode = "provider.reconciliation.converged";
    public const string UnknownCode = "provider.reconciliation.unknown";
    public const string AmbiguousCode = "provider.reconciliation.ambiguous";
    public const string InProgressCode = "provider.reconciliation.in-progress";
    public const string HealthFailedCode = "provider.reconciliation.health-failed";
    public const string HealthUnknownCode = "provider.reconciliation.health-unknown";
    public const string FailedCode = "provider.reconciliation.failed";
    public const string UnavailableCode = "provider.reconciliation.unavailable";
    public const string RetrySafeCode = "provider.reconciliation.retry-safe";
    public const string CorrelationMismatchCode = "provider.reconciliation.correlation-mismatch";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ElsaInstanceProviderReconciliationResult> ReconcileAsync(
        Guid workspaceId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (operationId == Guid.Empty)
            throw new ArgumentException("Operation ID is required.", nameof(operationId));

        using var telemetry = ManagedLifecycleTelemetry.StartOperation(
            ManagedLifecycleTelemetry.ReconciliationActivityName,
            ElsaInstanceOperationAction.Reconcile,
            null,
            ElsaObservedLifecycle.Unknown,
            ElsaInstanceHealth.Unknown,
            null,
            workspaceId: workspaceId,
            operationId: operationId);
        try
        {
            var replay = await store.GetResultAsync(workspaceId, operationId, cancellationToken);
            if (replay is not null)
            {
                var replayed = replay with { Replayed = true };
                telemetry.SetCorrelation(
                    null,
                    replayed.Projection.WorkspaceId,
                    replayed.Projection.InstanceId,
                    replayed.Projection.OperationId);
                telemetry.CompleteReplay(
                    null,
                    replayed.Projection.ObservedLifecycle,
                    replayed.Projection.Health,
                    replayed.Projection.OperationState,
                    replayed.DiagnosticCode);
                return replayed;
            }

            var target = await store.GetTargetAsync(workspaceId, operationId, cancellationToken)
                ?? throw new KeyNotFoundException("Provider reconciliation target does not exist.");
            target.Validate();

            var instance = target.Instance;
            var operation = target.Operation;
            telemetry.SetCorrelation(instance.OrganizationId, workspaceId, instance.Id, operation.Id);
            if (operation.AttemptNumber > 1)
                telemetry.RecordRetry(
                    instance.DesiredLifecycle,
                    instance.ObservedLifecycle,
                    instance.Health,
                    operation.State);
            var request = new ElsaInstanceProviderReconciliationRequest(
                workspaceId, instance.Id, operation.Id, operation.AttemptNumber, instance.DesiredLifecycle,
                instance.ResolvedPlanReference, instance.CurrentDeploymentReference, instance.Version);
            ElsaInstanceProviderObservation observation;
            var providerUnavailable = false;
            string? uncertainCode = null;
            try
            {
                observation = await provider.ObserveAsync(request, cancellationToken)
                    ?? throw new InvalidOperationException("Provider returned no observation.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                providerUnavailable = true;
                telemetry.RecordError(
                    instance.DesiredLifecycle,
                    instance.ObservedLifecycle,
                    instance.Health,
                    operation.State,
                    UnavailableCode);
                observation = new(
                    ElsaInstanceProviderObservationKind.Unknown,
                    ElsaObservedLifecycle.Unknown,
                    ElsaInstanceProviderHealthGate.Unknown,
                    operation.Id,
                    operation.AttemptNumber,
                    "provider-unavailable");
            }

            if (observation.OperationId != operation.Id || observation.AttemptNumber != operation.AttemptNumber)
            {
                uncertainCode = CorrelationMismatchCode;
                telemetry.RecordError(
                    instance.DesiredLifecycle,
                    instance.ObservedLifecycle,
                    instance.Health,
                    operation.State,
                    CorrelationMismatchCode);
                observation = new(
                    ElsaInstanceProviderObservationKind.Ambiguous,
                    ElsaObservedLifecycle.Unknown,
                    ElsaInstanceProviderHealthGate.Unknown,
                    operation.Id,
                    operation.AttemptNumber,
                    "correlation-mismatch");
            }

            var projection = Project(instance, operation, observation, _timeProvider.GetUtcNow(),
                uncertainCode ?? (providerUnavailable ? UnavailableCode : null));
            var retryEvidence = projection.Operation.State == ElsaInstanceOperationState.RecoveryRequired
                ? observation.RetryEvidence
                : null;
            var result = await store.CommitAsync(new(
                workspaceId,
                instance.Id,
                operation.Id,
                instance.Version,
                operation.AttemptNumber,
                target.ReconciliationVersion,
                observation.ComputeFingerprint(),
                projection.Instance,
                projection.Operation,
                projection.Code,
                retryEvidence is not null,
                retryEvidence?.Reference,
                retryEvidence?.Digest,
                projection.At), cancellationToken);
            if (result.Projection.OperationState != operation.State)
                telemetry.RecordTransition(
                    instance.DesiredLifecycle,
                    result.Projection.ObservedLifecycle,
                    result.Projection.Health,
                    result.Projection.OperationState,
                    result.DiagnosticCode);
            telemetry.Complete(
                result.Outcome.ToString(),
                instance.DesiredLifecycle,
                result.Projection.ObservedLifecycle,
                result.Projection.Health,
                result.Projection.OperationState,
                result.DiagnosticCode);
            return result;
        }
        catch (OperationCanceledException)
        {
            telemetry.RecordError(
                null,
                ElsaObservedLifecycle.Unknown,
                ElsaInstanceHealth.Unknown,
                null,
                "provider.reconciliation.cancelled");
            throw;
        }
        catch
        {
            telemetry.RecordError(
                null,
                ElsaObservedLifecycle.Unknown,
                ElsaInstanceHealth.Unknown,
                null,
                "provider.reconciliation.failed");
            throw;
        }
    }

    private static (ElsaInstance Instance, ElsaInstanceOperation Operation, string Code, DateTimeOffset At) Project(
        ElsaInstance instance,
        ElsaInstanceOperation operation,
        ElsaInstanceProviderObservation observation,
        DateTimeOffset now,
        string? uncertainCode = null)
    {
        if (observation.Kind != ElsaInstanceProviderObservationKind.Confirmed)
            return (Project(instance, ElsaObservedLifecycle.Unknown, ElsaInstanceHealth.Unknown), operation,
                uncertainCode ?? (observation.Kind == ElsaInstanceProviderObservationKind.Unknown ? UnknownCode : AmbiguousCode), now);

        if (observation.ObservedLifecycle == ElsaObservedLifecycle.Ready)
        {
            if (observation.HealthGate == ElsaInstanceProviderHealthGate.Passed &&
                (instance.DesiredLifecycle == ElsaDesiredLifecycle.Running ||
                 IsWaitingDeletePredecessor(instance, operation, ElsaDesiredLifecycle.Running)))
                return (Project(instance, ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy,
                        observation.CurrentDeploymentReference, observation.HasCurrentDeploymentProjection),
                    operation.TransitionTo(ElsaInstanceOperationState.Succeeded), ConvergedCode, now);

            if (observation.HealthGate == ElsaInstanceProviderHealthGate.Failed)
                return (Project(instance, ElsaObservedLifecycle.Degraded, ElsaInstanceHealth.Degraded),
                    operation.TransitionTo(ElsaInstanceOperationState.Failed), HealthFailedCode, now);

            // A Ready report without a health gate is verifying, not complete.
            // Keep a known in-progress phase (or stale Ready) instead of collapsing
            // both lifecycle and health into Unknown.
            return (Project(instance, ManagedElsaInstanceCustomerProjection.ProjectVerifiedInProgress(instance.ObservedLifecycle),
                    ElsaInstanceHealth.Unknown),
                operation, HealthUnknownCode, now);
        }

        if (observation.ObservedLifecycle == ElsaObservedLifecycle.Stopped &&
            (instance.DesiredLifecycle == ElsaDesiredLifecycle.Stopped ||
             IsWaitingDeletePredecessor(instance, operation, ElsaDesiredLifecycle.Stopped)))
            return (Project(instance, ElsaObservedLifecycle.Stopped, ElsaInstanceHealth.Unknown),
                operation.TransitionTo(ElsaInstanceOperationState.Succeeded), ConvergedCode, now);

        // A confirmed provider absence can complete an unfinished predecessor when
        // a confirmed Delete is already waiting behind it. This only releases the
        // predecessor's execution reservation; the dedicated cleanup boundary still
        // owns the correlated absence proof and instance tombstone.
        if (observation.ObservedLifecycle == ElsaObservedLifecycle.Deleted)
        {
            if (IsWaitingDeletePredecessor(instance, operation, ElsaDesiredLifecycle.Running) ||
                IsWaitingDeletePredecessor(instance, operation, ElsaDesiredLifecycle.Stopped))
                return (Project(instance, ElsaObservedLifecycle.Unknown, ElsaInstanceHealth.Unknown),
                    operation.TransitionTo(ElsaInstanceOperationState.Succeeded), ConvergedCode, now);

            return (Project(instance, ElsaObservedLifecycle.Unknown, ElsaInstanceHealth.Unknown),
                operation, InProgressCode, now);
        }

        if (observation.ObservedLifecycle == ElsaObservedLifecycle.Failed)
            return (Project(instance, ElsaObservedLifecycle.Failed, ElsaInstanceHealth.Unreachable),
                operation.TransitionTo(ElsaInstanceOperationState.Failed), FailedCode, now);

        if (ManagedElsaInstanceCustomerProjection.IsKnownInProgress(observation.ObservedLifecycle))
            return (Project(instance, observation.ObservedLifecycle, ElsaInstanceHealth.Unknown),
                operation, InProgressCode, now);

        if (ManagedElsaInstanceCustomerProjection.IsKnownInProgress(instance.ObservedLifecycle))
            return (Project(instance, instance.ObservedLifecycle, instance.Health),
                operation, InProgressCode, now);

        return (Project(instance, ElsaObservedLifecycle.Unknown, ElsaInstanceHealth.Unknown),
            operation, InProgressCode, now);
    }

    private static bool IsWaitingDeletePredecessor(
        ElsaInstance instance,
        ElsaInstanceOperation operation,
        ElsaDesiredLifecycle predecessorDesiredLifecycle)
    {
        if (instance.DesiredLifecycle != ElsaDesiredLifecycle.Deleting ||
            instance.LastOperationId is not { } lastOperationId ||
            string.Equals(lastOperationId.Value, operation.Id.ToString("D"), StringComparison.Ordinal))
            return false;

        return predecessorDesiredLifecycle switch
        {
            ElsaDesiredLifecycle.Stopped => operation.Action == ElsaInstanceOperationAction.Stop,
            ElsaDesiredLifecycle.Running => operation.Action is not (
                ElsaInstanceOperationAction.Stop or ElsaInstanceOperationAction.Delete),
            _ => false
        };
    }

    private static ElsaInstance Project(
        ElsaInstance instance,
        ElsaObservedLifecycle observed,
        ElsaInstanceHealth health,
        ElsaCurrentDeploymentReference? currentDeploymentReference = null,
        bool replaceCurrentDeploymentReference = false,
        DateTimeOffset? deletedAt = null) =>
        ElsaInstance.Hydrate(
            instance.Id,
            instance.OrganizationId,
            instance.WorkspaceId,
            instance.Name,
            instance.Slug,
            instance.Intent,
            observed,
            health,
            instance.Version,
            instance.IdentityBinding,
            instance.DesiredStateRevisionId,
            instance.ResolvedPlanReference,
            instance.CurrentResolvedRelease,
            replaceCurrentDeploymentReference ? currentDeploymentReference : instance.CurrentDeploymentReference,
            instance.PlacementAssignmentReference,
            instance.ElsaTenantReference,
            instance.LastOperationId,
            deletedAt);
}
