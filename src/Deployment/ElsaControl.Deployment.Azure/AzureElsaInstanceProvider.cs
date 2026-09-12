using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Bridges the provider-neutral managed-instance lifecycle to the durable Azure
/// operation store. Only immutable plan identities and safe observed facts cross
/// this boundary; Azure resource IDs stay in the provider store.
/// </summary>
public sealed class AzureElsaInstanceProvider(
    IAzureProviderOperationService operationService,
    IAzureProviderOperationStore operationStore,
    IAzureProviderResourceAssignmentStore assignmentStore,
    TimeProvider? timeProvider = null,
    AzureElsaInstanceProviderOptions? options = null,
    AzureProviderExecutor? executor = null,
    IAzureProviderRecoveryObserver? recoveryObserver = null,
    IAzureProviderRecoveryObservationStore? recoveryObservationStore = null) :
    IElsaInstanceProviderSubmissionPort,
    IElsaInstanceProviderReconciliationPort,
    IElsaInstanceProviderCleanupPort,
    IElsaInstanceProviderRecoveryPort,
    IElsaInstanceProviderDeleteRecoveryPort
{
    private readonly AzureElsaInstanceProviderOptions _options = options ?? new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly AzureProviderExecutor? _executor = executor;
    private readonly IAzureProviderRecoveryObserver? _recoveryObserver = recoveryObserver;
    private readonly IAzureProviderRecoveryObservationStore? _recoveryObservationStore = recoveryObservationStore;

    public async Task<ElsaInstanceProviderSubmissionResult> SubmitAsync(
        ElsaInstanceProviderSubmission request,
        CancellationToken cancellationToken = default)
    {
        AzureProviderOperationSubmission submission;
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            request.Validate();
            EnsureEnabled();

            var location = request.Location?.Trim();
            if (string.IsNullOrWhiteSpace(location))
                throw new InvalidOperationException("Managed instance provider placement is unavailable.");

            var target = new AzureWorkloadTarget(WorkloadName(request.InstanceId), location);
            var translation = AzureWorkloadPlanTranslator.Translate(request.Plan, target);
            if (!translation.IsAccepted)
                throw new InvalidOperationException("The resolved plan is outside the governed Azure provider profile.");

            var assignment = await assignmentStore.CreateOrGetAsync(
                new(
                    request.WorkspaceId,
                    request.OrganizationId!.Value,
                    request.InstanceId,
                    _options.ProviderScopeFingerprint!,
                    _options.SubscriptionId,
                    _options.ResourceGroupNamePrefix,
                    target.WorkloadName,
                    location,
                    _options.ResourceGroupNamingVersion),
                _timeProvider.GetUtcNow(),
                cancellationToken);
            submission = new(
                IdempotencyKey(request.OperationId),
                _options.TemplateFingerprint,
                translation.Plan!,
                _options.ProviderScopeFingerprint,
                request.OrganizationId,
                request.InstanceId,
                request.OperationAction,
                assignment.Id);
        }
        catch (ElsaInstanceProviderSubmissionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new ElsaInstanceProviderSubmissionException(
                ElsaInstanceProviderSubmissionFailureKind.Rejected,
                exception);
        }

        try
        {
            var result = operationService is IAzureProviderOperationReplayService replayService
                ? await replayService.SubmitWithReplayAsync(request.WorkspaceId, submission, cancellationToken)
                : new AzureProviderOperationSubmissionResult(
                    await operationService.SubmitAsync(request.WorkspaceId, submission, cancellationToken),
                    Replayed: false);
            return new(result.Operation.OperationIdentity, result.Replayed, submission.ProviderAssignmentId?.ToString("D"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ElsaInstanceProviderSubmissionException(
                ElsaInstanceProviderSubmissionFailureKind.OutcomeUnknown,
                exception);
        }
    }

    public async Task<ElsaInstanceProviderObservation> ObserveAsync(
        ElsaInstanceProviderReconciliationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.WorkspaceId == Guid.Empty || request.InstanceId == Guid.Empty || request.OperationId == Guid.Empty ||
            request.AttemptNumber < 1)
            throw new ArgumentException("Provider reconciliation request identity is invalid.", nameof(request));
        EnsureEnabled();

        var operation = await operationStore.GetLatestReconcileAsync(
            request.WorkspaceId,
            WorkloadName(request.InstanceId),
            _options.ProviderScopeFingerprint,
            cancellationToken);
        if (operation is null)
            return Unknown(request);

        // The store lookup is intentionally broad enough to recover the latest
        // operation after a restart, but the returned row is still untrusted at
        // this boundary. Bind it to the exact lifecycle operation before exposing
        // any provider state; otherwise a stale or cross-scope operation could be
        // projected as the current instance's health.
        if (operation.WorkspaceId != request.WorkspaceId ||
            !string.Equals(operation.TargetKey, WorkloadName(request.InstanceId), StringComparison.OrdinalIgnoreCase) ||
            operation.Action != AzureProviderOperationAction.Reconcile ||
            !string.Equals(operation.IdempotencyKey, IdempotencyKey(request.OperationId), StringComparison.Ordinal) ||
            operation.InstanceId is { } boundInstanceId && boundInstanceId != request.InstanceId ||
            !string.Equals(operation.ProviderScopeFingerprint, NormalizeScope(_options.ProviderScopeFingerprint), StringComparison.Ordinal))
            return CorrelationMismatch(request);

        var correlation = operation.OperationIdentity;
        if (operation.Status == AzureProviderOperationStatus.Succeeded &&
            operation.Health is AzureProviderHealth.Healthy or AzureProviderHealth.Degraded &&
            !ElsaManagedEndpointOrigin.TryCreate(operation.Endpoint, out _))
            return EndpointInvalid(request);

        var retryEvidence = operation.Status == AzureProviderOperationStatus.RecoveryRequired
            ? await TryRecordRecoveryObservationAsync(request, operation, cancellationToken)
            : null;

        return operation.Status switch
        {
            AzureProviderOperationStatus.Succeeded when operation.Health == AzureProviderHealth.Healthy =>
                new(ElsaInstanceProviderObservationKind.Confirmed, ElsaObservedLifecycle.Ready,
                    ElsaInstanceProviderHealthGate.Passed, request.OperationId, request.AttemptNumber,
                    correlation, retryEvidence: null, currentDeploymentReference: CurrentDeployment(operation)),
            AzureProviderOperationStatus.Succeeded when operation.Health == AzureProviderHealth.Degraded =>
                new(ElsaInstanceProviderObservationKind.Confirmed, ElsaObservedLifecycle.Ready,
                    ElsaInstanceProviderHealthGate.Failed, request.OperationId, request.AttemptNumber,
                    correlation, retryEvidence: null, currentDeploymentReference: CurrentDeployment(operation)),
            AzureProviderOperationStatus.Succeeded when operation.Health == AzureProviderHealth.Unreachable =>
                new(ElsaInstanceProviderObservationKind.Confirmed, ElsaObservedLifecycle.Ready,
                    ElsaInstanceProviderHealthGate.Unknown, request.OperationId, request.AttemptNumber,
                    correlation),
            AzureProviderOperationStatus.Succeeded when operation.Health == AzureProviderHealth.Failed =>
                new(ElsaInstanceProviderObservationKind.Confirmed, ElsaObservedLifecycle.Failed,
                    ElsaInstanceProviderHealthGate.Failed, request.OperationId, request.AttemptNumber,
                    correlation),
            AzureProviderOperationStatus.Failed =>
                new(ElsaInstanceProviderObservationKind.Confirmed, ElsaObservedLifecycle.Failed,
                    ElsaInstanceProviderHealthGate.Failed, request.OperationId, request.AttemptNumber,
                    correlation),
            AzureProviderOperationStatus.Cancelled =>
                new(ElsaInstanceProviderObservationKind.Confirmed, ElsaObservedLifecycle.Failed,
                    ElsaInstanceProviderHealthGate.Failed, request.OperationId, request.AttemptNumber,
                    correlation),
            _ => new(ElsaInstanceProviderObservationKind.Confirmed, ElsaObservedLifecycle.Provisioning,
                ElsaInstanceProviderHealthGate.Unknown, request.OperationId, request.AttemptNumber, correlation,
                retryEvidence)
        };
    }

    /// <summary>
    /// Normal reconciliation is the producer for recovery proof. It observes the exact
    /// retained provider operation before an explicit recovery request and records only one
    /// typed, immutable postcondition. A failed observation remains unknown and cannot become
    /// retry evidence.
    /// </summary>
    private async Task<ElsaInstanceProviderRetryEvidence?> TryRecordRecoveryObservationAsync(
        ElsaInstanceProviderReconciliationRequest request,
        AzureProviderOperation operation,
        CancellationToken cancellationToken)
    {
        if (_recoveryObserver is null || _recoveryObservationStore is null ||
            request.ResolvedPlanReference is null || request.InstanceVersion < 1 ||
            operation.OrganizationId is not { } organizationId ||
            operation.InstanceId != request.InstanceId ||
            operation.LifecycleAction is not { } lifecycleAction ||
            operation.ProviderAssignmentId is not { } assignmentId)
            return null;

        try
        {
            var assignment = await assignmentStore.GetAsync(request.WorkspaceId, assignmentId, cancellationToken);
            if (assignment is null ||
                assignment.Id != assignmentId ||
                assignment.WorkspaceId != request.WorkspaceId ||
                assignment.OrganizationId != organizationId ||
                assignment.InstanceId != request.InstanceId ||
                assignment.LastOperationId != operation.Id ||
                !string.Equals(assignment.ProviderScopeFingerprint, NormalizeScope(_options.ProviderScopeFingerprint), StringComparison.Ordinal) ||
                !string.Equals(assignment.WorkloadName, WorkloadName(request.InstanceId), StringComparison.OrdinalIgnoreCase))
                return null;

            var retainedPlan = AzureProviderOperationService.TryRestorePlan(operation);
            if (retainedPlan is null ||
                !string.Equals(retainedPlan.Fingerprint, operation.PlanFingerprint, StringComparison.Ordinal))
                return null;

            var observed = await _recoveryObserver.ObserveAsync(
                new AzureProviderRecoveryRequest(operation, retainedPlan, assignment), cancellationToken);
            observed.Validate();
            if (observed.Kind != AzureProviderRecoveryObservationKind.Confirmed || observed.CompletedStep is null)
                return null;

            var observedPhase = AzureProviderRecoveryObservationSupport.RecoveryPhase(observed.CompletedStep.Value);
            if (!AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
                    operation.AttemptedStep, operation.Phase, observed.CompletedStep.Value, observedPhase))
                return null;
            var resourceFingerprint = AzureProviderRecoveryObservationRecord.ComputeResourceFingerprint(observed.Resources);
            var record = new AzureProviderRecoveryObservationRecord(
                organizationId,
                request.WorkspaceId,
                request.InstanceId,
                request.OperationId,
                lifecycleAction,
                request.AttemptNumber,
                request.InstanceVersion,
                operation.Id,
                operation.OperationIdentity,
                operation.RequestHash,
                operation.AttemptNumber,
                operation.Version,
                operation.CheckpointSequence,
                assignment.Id,
                operation.TargetKey,
                operation.ProviderScopeFingerprint,
                request.ResolvedPlanReference.PlanId,
                request.ResolvedPlanReference.SchemaVersion,
                request.ResolvedPlanReference.PlanUri,
                request.ResolvedPlanReference.ContentHash,
                operation.PlanFingerprint,
                operation.TemplateFingerprint,
                observed.CompletedStep.Value,
                observedPhase,
                observed.Health,
                resourceFingerprint,
                AzureProviderRecoveryObservationRecord.ComputePostconditionFingerprint(observed, resourceFingerprint),
                _timeProvider.GetUtcNow());
            var receipt = await _recoveryObservationStore.CreateOrGetAsync(record, cancellationToken);
            return new ElsaInstanceProviderRetryEvidence(receipt.Reference, receipt.Digest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<ElsaInstanceProviderRecoveryResult> RecoverAsync(
        ElsaInstanceProviderRecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        EnsureEnabled();

        var submission = request.Submission;
        if (submission.PlacementAssignmentId is not { } assignmentText ||
            !Guid.TryParseExact(assignmentText, "D", out var assignmentId))
            return RecoveryRejected("azure.recovery.assignment-invalid");

        // This durable lookup happens before any Azure observation. Every tuple is checked
        // against the lifecycle request so stale or cross-scope input cannot probe Azure.
        var operation = await operationStore.GetLatestReconcileAsync(
            submission.WorkspaceId,
            WorkloadName(submission.InstanceId),
            _options.ProviderScopeFingerprint,
            cancellationToken);
        if (operation is null)
            return RecoveryRequired("azure.recovery.operation-unavailable");
        if (operation.WorkspaceId != submission.WorkspaceId ||
            !string.Equals(operation.TargetKey, WorkloadName(submission.InstanceId), StringComparison.OrdinalIgnoreCase) ||
            operation.Action != AzureProviderOperationAction.Reconcile ||
            !string.Equals(operation.IdempotencyKey, IdempotencyKey(submission.OperationId), StringComparison.Ordinal) ||
            operation.OrganizationId != submission.OrganizationId ||
            operation.InstanceId != submission.InstanceId ||
            operation.LifecycleAction != submission.OperationAction ||
            operation.ProviderAssignmentId != assignmentId ||
            !string.Equals(operation.ProviderScopeFingerprint, NormalizeScope(_options.ProviderScopeFingerprint), StringComparison.Ordinal))
            return RecoveryRejected("azure.recovery.identity-mismatch");

        // Recovery never resolves the current catalog intent. It may only use the exact
        // provider plan retained by this operation. Translate the already-resolved lifecycle
        // plan into the retained Azure target and compare its governed fingerprint before any
        // observation; otherwise a caller could pair a valid recovery envelope with a different
        // plan while still selecting the same provider operation.
        AzureWorkloadPlan? retainedPlan;
        try
        {
            retainedPlan = AzureProviderOperationService.TryRestorePlan(operation);
        }
        catch (InvalidOperationException)
        {
            retainedPlan = null;
        }
        if (retainedPlan is null ||
            !string.Equals(retainedPlan.Fingerprint, operation.PlanFingerprint, StringComparison.Ordinal))
            return RecoveryRejected("azure.recovery.plan-unavailable");

        var translatedRequestedPlan = AzureWorkloadPlanTranslator.Translate(
            submission.Plan,
            new AzureWorkloadTarget(operation.TargetKey, operation.Location));
        if (!translatedRequestedPlan.IsAccepted ||
            translatedRequestedPlan.Plan is null ||
            !string.Equals(translatedRequestedPlan.Plan.Fingerprint, retainedPlan.Fingerprint, StringComparison.Ordinal))
            return RecoveryRejected("azure.recovery.plan-mismatch");

        // Post-claim replay is read-only, but still requires proof that this exact
        // accepted lifecycle recovery authorized the current provider successor.
        var isReplay = operation.Status is AzureProviderOperationStatus.Running or AzureProviderOperationStatus.Succeeded;
        if (!isReplay && operation.Status != AzureProviderOperationStatus.RecoveryRequired)
            return RecoveryRejected("azure.recovery.state-invalid");

        if (_recoveryObservationStore is null)
            return isReplay
                ? RecoveryRejected("azure.recovery.observation-unavailable")
                : RecoveryRequired("azure.recovery.observation-unavailable");

        var envelope = request.Envelope;
        AzureProviderRecoveryObservationRecord? recordedObservation;
        try
        {
            var binding = new AzureProviderRecoveryObservationBinding(
                    envelope.RecoveryRequestId,
                    envelope.OrganizationId,
                    envelope.WorkspaceId,
                    envelope.InstanceId,
                    envelope.LifecycleOperationId,
                    envelope.ObservedLifecycleAttemptNumber,
                    envelope.ObservedInstanceVersion,
                    envelope.AcceptedLifecycleAttemptNumber,
                    envelope.AcceptedInstanceVersion,
                    envelope.IdempotencyScope,
                    envelope.IdempotencyKey,
                    envelope.RequestHash,
                    envelope.ObservationReference,
                    envelope.ObservationDigest);
            recordedObservation = isReplay
                ? await _recoveryObservationStore.GetAndValidateForAcceptedRecoveryReplayAsync(binding, cancellationToken)
                : await _recoveryObservationStore.GetAndValidateForAcceptedRecoveryAsync(binding, cancellationToken);
        }
        catch (ArgumentException)
        {
            recordedObservation = null;
        }
        catch (InvalidOperationException)
        {
            recordedObservation = null;
        }
        if (recordedObservation is null ||
            !IsRecordedObservationAuthoritative(recordedObservation, operation, assignmentId, submission, isReplay))
            return isReplay
                ? RecoveryRejected("azure.recovery.observation-invalid")
                : RecoveryRequired("azure.recovery.observation-invalid");

        var assignment = await assignmentStore.GetAsync(submission.WorkspaceId, assignmentId, cancellationToken);
        if (assignment is null ||
            assignment.Id != assignmentId ||
            assignment.WorkspaceId != submission.WorkspaceId ||
            assignment.OrganizationId != submission.OrganizationId ||
            assignment.InstanceId != submission.InstanceId ||
            assignment.LastOperationId != operation.Id ||
            !string.Equals(assignment.ProviderScopeFingerprint, NormalizeScope(_options.ProviderScopeFingerprint), StringComparison.Ordinal) ||
            !string.Equals(assignment.WorkloadName, WorkloadName(submission.InstanceId), StringComparison.OrdinalIgnoreCase))
            return RecoveryRejected("azure.recovery.assignment-mismatch");
        if (isReplay)
            return operation.Status == AzureProviderOperationStatus.Succeeded
                ? new(ElsaInstanceProviderRecoveryOutcome.Succeeded, "azure.operation.no-op")
                : new(ElsaInstanceProviderRecoveryOutcome.InProgress, "azure.operation.in-progress");
        if (_recoveryObserver is null || _executor is null)
            return RecoveryRequired("azure.recovery.unavailable");

        // Re-observe after the accepted ledger check and immediately before the recovery CAS.
        // This is the only point at which provider state may authorize a claim.
        AzureProviderRecoveryObservation observed;
        try
        {
            observed = await _recoveryObserver.ObserveAsync(
                new AzureProviderRecoveryRequest(operation, retainedPlan, assignment), cancellationToken);
            observed.Validate();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return RecoveryRequired("azure.recovery.observation-failed");
        }
        if (observed.Kind != AzureProviderRecoveryObservationKind.Confirmed)
            return RecoveryRequired(observed.Code);

        var result = await _executor.RecoverAsync(operation, retainedPlan, observed, cancellationToken);
        return result.Outcome switch
        {
            AzureProviderExecutionOutcome.Succeeded or AzureProviderExecutionOutcome.NoOp =>
                new(ElsaInstanceProviderRecoveryOutcome.Succeeded, result.Code),
            AzureProviderExecutionOutcome.InProgress =>
                new(ElsaInstanceProviderRecoveryOutcome.InProgress, result.Code),
            AzureProviderExecutionOutcome.Failed =>
                new(ElsaInstanceProviderRecoveryOutcome.Failed, result.Code),
            _ => new(ElsaInstanceProviderRecoveryOutcome.RecoveryRequired, result.Code)
        };
    }

    private static ElsaInstanceProviderRecoveryResult RecoveryRequired(string code) =>
        new(ElsaInstanceProviderRecoveryOutcome.RecoveryRequired, code);

    private static ElsaInstanceProviderRecoveryResult RecoveryRejected(string code) =>
        new(ElsaInstanceProviderRecoveryOutcome.Rejected, code);

    private static bool IsRecordedObservationAuthoritative(
        AzureProviderRecoveryObservationRecord observation,
        AzureProviderOperation operation,
        Guid assignmentId,
        ElsaInstanceProviderSubmission submission,
        bool isReplay)
    {
        // The provider tuple is the pre-Recover snapshot. The recovery ledger, validated by the
        // observation store, owns the lifecycle attempt/version transition; these checks only
        // ensure the record was produced for this exact retained provider operation and not a
        // stale or cross-scope row.
        return observation.OrganizationId == submission.OrganizationId &&
               observation.WorkspaceId == submission.WorkspaceId &&
               observation.InstanceId == submission.InstanceId &&
               observation.LifecycleOperationId == submission.OperationId &&
               observation.LifecycleAction == submission.OperationAction &&
               observation.ProviderOperationId == operation.Id &&
               string.Equals(observation.ProviderOperationIdentity, operation.OperationIdentity, StringComparison.Ordinal) &&
               string.Equals(observation.ProviderRequestHash, operation.RequestHash, StringComparison.Ordinal) &&
               (isReplay
                   ? (long)observation.ProviderAttemptNumber + 1 == operation.AttemptNumber &&
                     observation.ProviderVersion < operation.Version &&
                     observation.ProviderCheckpointSequence <= operation.CheckpointSequence
                   : observation.ProviderAttemptNumber == operation.AttemptNumber &&
                     observation.ProviderVersion == operation.Version &&
                     observation.ProviderCheckpointSequence == operation.CheckpointSequence) &&
               observation.ProviderAssignmentId == assignmentId &&
               string.Equals(observation.TargetKey, operation.TargetKey, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(observation.ProviderScopeFingerprint, operation.ProviderScopeFingerprint, StringComparison.Ordinal) &&
               string.Equals(observation.ProviderPlanFingerprint, operation.PlanFingerprint, StringComparison.Ordinal) &&
               string.Equals(observation.ProviderTemplateFingerprint, operation.TemplateFingerprint, StringComparison.Ordinal) &&
               (isReplay || AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
                   operation.AttemptedStep, operation.Phase, observation.CompletedStep, observation.ObservedPhase));
    }

    public async Task<ElsaInstanceCleanupObservation> CleanupAsync(
        ElsaInstanceCleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        EnsureEnabled();

        if (request.PlacementAssignment is null ||
            !Guid.TryParseExact(request.PlacementAssignment.AssignmentId, "D", out var assignmentId))
            return CleanupUnknown(request, "deletion.provider-assignment-unavailable");

        var assignment = await assignmentStore.GetAsync(request.WorkspaceId, assignmentId, cancellationToken);
        if (assignment is null ||
            assignment.Id != assignmentId ||
            assignment.WorkspaceId != request.WorkspaceId ||
            assignment.InstanceId != request.InstanceId ||
            !string.Equals(assignment.ProviderScopeFingerprint, NormalizeScope(_options.ProviderScopeFingerprint), StringComparison.Ordinal) ||
            !string.Equals(assignment.WorkloadName, WorkloadName(request.InstanceId), StringComparison.OrdinalIgnoreCase))
            return CleanupUnknown(request, "deletion.provider-assignment-invalid", ElsaInstanceCleanupObservationKind.Ambiguous);

        // A completed provider delete can be observed again after the lifecycle worker
        // restarts or loses its response. Observe its durable evidence without reserving
        // another delete for an assignment whose resources are already absent.
        if (assignment.State == AzureProviderAssignmentState.Deleted)
        {
            var completed = assignment.LastOperationId is { } operationId
                ? await operationStore.GetAsync(request.WorkspaceId, operationId, cancellationToken)
                : null;
            // The assignment retains its immutable group name after deletion; only
            // live resource inventory is cleared by the durable store.
            return completed is not null && assignment.Resources == new AzureProviderResourceReferences(assignment.ResourceGroupName)
                ? ObserveCleanup(request, assignment, completed)
                : CleanupUnknown(request, "deletion.provider-evidence-unavailable");
        }

        var reconcile = await operationStore.GetLatestReconcileAsync(
            request.WorkspaceId,
            WorkloadName(request.InstanceId),
            _options.ProviderScopeFingerprint,
            cancellationToken);
        if (reconcile is null ||
            reconcile.WorkspaceId != request.WorkspaceId ||
            reconcile.InstanceId != request.InstanceId ||
            reconcile.OrganizationId is null ||
            reconcile.Action != AzureProviderOperationAction.Reconcile ||
            reconcile.ProviderAssignmentId != assignment.Id ||
            !string.Equals(reconcile.TargetKey, WorkloadName(request.InstanceId), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(reconcile.ProviderScopeFingerprint, NormalizeScope(_options.ProviderScopeFingerprint), StringComparison.Ordinal) ||
            AzureProviderOperationService.TryRestorePlan(reconcile) is not { } plan)
            return CleanupUnknown(request, "deletion.provider-plan-unavailable");

        AzureProviderOperation operation;
        try
        {
            operation = await operationService.SubmitDeleteAsync(
                request.WorkspaceId,
                new AzureProviderOperationSubmission(
                    IdempotencyKey(request.OperationId),
                    reconcile.TemplateFingerprint,
                    plan,
                    reconcile.ProviderScopeFingerprint,
                    reconcile.OrganizationId,
                    request.InstanceId,
                    ElsaInstanceOperationAction.Delete,
                    assignment.Id),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return CleanupUnknown(request, "deletion.provider-unavailable");
        }

        return ObserveCleanup(request, assignment, operation);
    }

    private ElsaInstanceCleanupObservation ObserveCleanup(
        ElsaInstanceCleanupRequest request,
        AzureProviderResourceAssignment assignment,
        AzureProviderOperation observed)
    {
        if (observed.WorkspaceId != request.WorkspaceId ||
            observed.InstanceId != request.InstanceId ||
            observed.OrganizationId != assignment.OrganizationId ||
            observed.ProviderAssignmentId != assignment.Id ||
            observed.Action != AzureProviderOperationAction.Delete ||
            observed.LifecycleAction != ElsaInstanceOperationAction.Delete ||
            !AzureProviderOperationValidation.IsLifecycleDeleteIdempotencyKey(observed.IdempotencyKey, request.OperationId) ||
            !string.Equals(observed.TargetKey, WorkloadName(request.InstanceId), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(observed.ProviderScopeFingerprint, NormalizeScope(_options.ProviderScopeFingerprint), StringComparison.Ordinal))
            return CleanupUnknown(request, "deletion.provider-correlation-invalid", ElsaInstanceCleanupObservationKind.Ambiguous);

        var cleanupInventoryCleared =
            observed.Resources == new AzureProviderResourceReferences(assignment.ResourceGroupName) &&
            observed.Endpoint is null;
        return observed.Status == AzureProviderOperationStatus.Succeeded &&
               observed.Phase == AzureProviderOperationPhase.CleanupVerified &&
               cleanupInventoryCleared
            ? new(ElsaInstanceCleanupObservationKind.ConfirmedAbsent, request.OperationId,
                request.AttemptNumber, "deletion.provider-confirmed-absent")
            : ObservePendingCleanup(observed, cleanupInventoryCleared);

        ElsaInstanceCleanupObservation ObservePendingCleanup(
            AzureProviderOperation pending,
            bool inventoryCleared)
        {
            var finalizationInProgress = assignment.State == AzureProviderAssignmentState.Deleted &&
                pending.Status == AzureProviderOperationStatus.Running &&
                pending.Phase == AzureProviderOperationPhase.CleanupVerified &&
                inventoryCleared;
            var providerOperationInProgress = assignment.State != AzureProviderAssignmentState.Deleted &&
                pending.Status is AzureProviderOperationStatus.Accepted or AzureProviderOperationStatus.Queued or AzureProviderOperationStatus.Running;
            var diagnosticCode = pending.Status is AzureProviderOperationStatus.Failed or AzureProviderOperationStatus.Cancelled
                ? "deletion.provider-cleanup-failed"
                : "deletion.provider-cleanup-pending";
            return CleanupUnknown(
                request,
                diagnosticCode,
                finalizationInProgress || providerOperationInProgress
                    ? ElsaInstanceCleanupObservationKind.InProgress
                    : ElsaInstanceCleanupObservationKind.Unknown);
        }
    }

    public async Task<ElsaInstanceCleanupObservation> RecoverDeleteAsync(
        ElsaInstanceDeleteRecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        EnsureEnabled();

        if (_executor is null || operationStore is not IAzureProviderDeleteRecoveryStore recoveryStore)
            return CleanupUnknown(request.Cleanup, "deletion.recovery.capability-unavailable", ElsaInstanceCleanupObservationKind.Unavailable);

        var authority = await recoveryStore.GetDeleteRecoveryAuthorityAsync(
            request.Cleanup.WorkspaceId,
            request.RecoveryRequestId,
            request.Cleanup.InstanceId,
            request.Cleanup.OperationId,
            cancellationToken);
        if (authority is null)
            return CleanupUnknown(request.Cleanup, "deletion.recovery.authority-unavailable", ElsaInstanceCleanupObservationKind.Ambiguous);

        var operation = await operationStore.GetAsync(
            request.Cleanup.WorkspaceId,
            authority.ProviderOperationId,
            cancellationToken);
        var plan = operation is null ? null : AzureProviderOperationService.TryRestorePlan(operation);
        if (plan is null)
            return CleanupUnknown(request.Cleanup, "deletion.recovery.plan-unavailable", ElsaInstanceCleanupObservationKind.Ambiguous);

        var assignment = await assignmentStore.GetAsync(
            request.Cleanup.WorkspaceId,
            authority.ProviderAssignmentId,
            cancellationToken);
        if (!IsAssignmentBound(assignment))
            return CleanupUnknown(request.Cleanup, "deletion.recovery.assignment-invalid", ElsaInstanceCleanupObservationKind.Ambiguous);

        var result = await _executor.RecoverDeleteAsync(
            new AzureProviderDeleteRecoveryClaimRequest(
                request.RecoveryRequestId,
                request.Cleanup.WorkspaceId,
                request.Cleanup.InstanceId,
                request.Cleanup.OperationId,
                request.Cleanup.AttemptNumber,
                request.InstanceVersion,
                request.WorkerId,
                request.LeaseToken,
                request.LeaseVersion),
            plan,
            cancellationToken);
        if (result is null)
            return CleanupUnknown(request.Cleanup, "deletion.recovery.claim-lost", ElsaInstanceCleanupObservationKind.Ambiguous);

        assignment = await assignmentStore.GetAsync(
            request.Cleanup.WorkspaceId, authority.ProviderAssignmentId, cancellationToken);
        if (!IsAssignmentBound(assignment))
            return CleanupUnknown(request.Cleanup, "deletion.recovery.assignment-invalid", ElsaInstanceCleanupObservationKind.Ambiguous);

        // Both a fresh claim and a post-claim replay must pass the same exact cleanup classifier.
        // In particular, a successful executor outcome is not absence evidence by itself.
        if (result.Operation.Status == AzureProviderOperationStatus.Succeeded &&
            (assignment!.State != AzureProviderAssignmentState.Deleted ||
             assignment.Resources != new AzureProviderResourceReferences(assignment.ResourceGroupName)))
            return CleanupUnknown(request.Cleanup, "deletion.recovery.assignment-incomplete");
        return ObserveCleanup(request.Cleanup, assignment!, result.Operation);

        bool IsAssignmentBound(AzureProviderResourceAssignment? candidate) =>
            candidate is not null && candidate.Id == authority.ProviderAssignmentId &&
            candidate.WorkspaceId == request.Cleanup.WorkspaceId &&
            candidate.OrganizationId == operation!.OrganizationId &&
            candidate.InstanceId == request.Cleanup.InstanceId &&
            candidate.LastOperationId == authority.ProviderOperationId &&
            string.Equals(candidate.WorkloadName, WorkloadName(request.Cleanup.InstanceId), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.ProviderScopeFingerprint, NormalizeScope(_options.ProviderScopeFingerprint), StringComparison.Ordinal);
    }

    private void EnsureEnabled()
    {
        try
        {
            _options.Validate();
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("Managed instance provider authority is invalid.");
        }
    }

    private static ElsaInstanceProviderObservation Unknown(ElsaInstanceProviderReconciliationRequest request) =>
        new(ElsaInstanceProviderObservationKind.Unknown, ElsaObservedLifecycle.Unknown,
            ElsaInstanceProviderHealthGate.Unknown, request.OperationId, request.AttemptNumber,
            "provider-operation-unavailable");

    private static ElsaInstanceProviderObservation CorrelationMismatch(ElsaInstanceProviderReconciliationRequest request) =>
        new(ElsaInstanceProviderObservationKind.Ambiguous, ElsaObservedLifecycle.Unknown,
            ElsaInstanceProviderHealthGate.Unknown, request.OperationId, request.AttemptNumber,
            "provider-operation-correlation-mismatch");

    private static ElsaInstanceProviderObservation EndpointInvalid(ElsaInstanceProviderReconciliationRequest request) =>
        new(ElsaInstanceProviderObservationKind.Ambiguous, ElsaObservedLifecycle.Unknown,
            ElsaInstanceProviderHealthGate.Unknown, request.OperationId, request.AttemptNumber,
            "provider-operation-endpoint-invalid");

    private static ElsaInstanceCleanupObservation CleanupUnknown(
        ElsaInstanceCleanupRequest request,
        string code,
        ElsaInstanceCleanupObservationKind kind = ElsaInstanceCleanupObservationKind.Unknown) =>
        new(kind, request.OperationId, request.AttemptNumber, code);

    /// <summary>
    /// A succeeded operation whose plan configured the handoff has deployed and promoted a revision carrying it:
    /// the production runner refuses to deploy such a plan without Control's handoff inputs and rejects a
    /// callback that is not the one of this endpoint. That is the fact Control gates Open on.
    /// </summary>
    private static ElsaCurrentDeploymentReference? CurrentDeployment(AzureProviderOperation operation) =>
        new(operation.OperationIdentity, $"attempt-{operation.AttemptNumber}", operation.Endpoint, operation.ManagedHandoff);

    internal static string WorkloadName(Guid instanceId) => $"e{instanceId:N}"[..16];

    internal static string IdempotencyKey(Guid operationId) => $"elsa-instance-operation:{operationId:D}";

    private static string? NormalizeScope(string? value) => value?.Trim().ToLowerInvariant();
}
