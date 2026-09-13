using System.Collections.ObjectModel;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Orchestrates the durable Azure operation around an injected provider runner. The runner is
/// the only component that may cross the Azure/Bicep execution boundary; this class owns claim,
/// checkpoint, idempotency and recovery semantics.
/// </summary>
public sealed class AzureProviderExecutor
{
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(15);
    private readonly IAzureProviderOperationStore _store;
    private readonly IAzureProviderRunner _runner;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeSpan _heartbeatInterval;
    private readonly string _workerId;
    private readonly IElsaInstanceCommercialGate? _commercialGate;
    private readonly IAzureProviderResourceAssignmentStore? _assignmentStore;

    public AzureProviderExecutor(
        IAzureProviderOperationStore store,
        IAzureProviderRunner runner,
        TimeProvider? timeProvider = null,
        TimeSpan? leaseDuration = null,
        string? workerId = null,
        TimeSpan? heartbeatInterval = null,
        IElsaInstanceCommercialGate? commercialGate = null,
        IAzureProviderResourceAssignmentStore? assignmentStore = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _leaseDuration = leaseDuration ?? DefaultLeaseDuration;
        if (_leaseDuration <= TimeSpan.Zero || _leaseDuration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "The provider lease must be positive and no longer than one hour.");
        _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval(_leaseDuration);
        if (_heartbeatInterval <= TimeSpan.Zero || _heartbeatInterval >= _leaseDuration)
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval), "The heartbeat interval must be positive and shorter than the lease.");
        _workerId = workerId ?? $"azure-executor-{Guid.NewGuid():N}";
        AzureProviderOperationValidation.ValidateWorkerId(_workerId);
        _commercialGate = commercialGate;
        _assignmentStore = assignmentStore;
    }

    /// <summary>
    /// Applies an accepted Azure workload plan. A completed idempotent operation returns
    /// <see cref="AzureProviderExecutionOutcome.NoOp"/> and does not invoke the runner again.
    /// </summary>
    public Task<AzureProviderExecutionResult> ApplyAsync(
        AzureProviderOperationRequest operationRequest,
        AzureWorkloadPlan plan,
        CancellationToken cancellationToken = default) =>
        ExecuteApplyAsync(operationRequest, plan, cancellationToken);

    private Task<AzureProviderExecutionResult> ExecuteApplyAsync(
        AzureProviderOperationRequest operationRequest,
        AzureWorkloadPlan plan,
        CancellationToken cancellationToken)
    {
        if (operationRequest is null)
            throw new ArgumentNullException(nameof(operationRequest));
        if (operationRequest.Action != AzureProviderOperationAction.Reconcile)
            throw new ArgumentException("Apply operations must use the Reconcile action.", nameof(operationRequest));
        return ExecuteAsync(new AzureProviderExecutionRequest(operationRequest, plan), cancellationToken);
    }

    public Task<AzureProviderExecutionResult> DeleteAsync(
        AzureProviderOperationRequest operationRequest,
        AzureWorkloadPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (operationRequest is null)
            throw new ArgumentNullException(nameof(operationRequest));

        if (operationRequest.Action != AzureProviderOperationAction.Delete)
            throw new ArgumentException("Delete operations must use the Delete action.", nameof(operationRequest));

        return ExecuteAsync(new AzureProviderExecutionRequest(operationRequest, plan), cancellationToken);
    }

    /// <summary>
    /// Claims and executes one explicitly accepted Azure Delete recovery. This entrypoint is
    /// deliberately separate from <see cref="ExecuteAsync"/>: it never selects a latest
    /// RecoveryRequired row or performs an automatic recovery claim.
    /// </summary>
    public async Task<AzureProviderExecutionResult?> RecoverDeleteAsync(
        AzureProviderDeleteRecoveryClaimRequest request,
        AzureWorkloadPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        request.Validate();
        if (_store is not IAzureProviderDeleteRecoveryStore recoveryStore)
            return null;

        // Resolve and validate the exact durable authority before attempting the claim. The
        // caller's plan is only a transport value; the persisted operation remains authoritative
        // and must be restored and compared before any state mutation or remote call.
        var authority = await recoveryStore.GetDeleteRecoveryAuthorityAsync(
            request.WorkspaceId,
            request.RecoveryRequestId,
            request.InstanceId,
            request.LifecycleOperationId,
            cancellationToken);
        if (authority is null || authority.LifecycleAttemptNumber != request.LifecycleAttemptNumber ||
            authority.InstanceVersion != request.InstanceVersion)
            return null;

        var retained = await _store.GetAsync(
            request.WorkspaceId,
            authority.ProviderOperationId,
            cancellationToken);
        if (retained is null || !IsDeleteRecoveryAuthorityBound(request, authority, retained) ||
            !IsPlanForOperation(retained, plan))
            return null;

        if (retained.Status != AzureProviderOperationStatus.RecoveryRequired ||
            retained.AttemptNumber != authority.ProviderAttemptNumber ||
            retained.Version != authority.ProviderVersion ||
            retained.CheckpointSequence != authority.ProviderCheckpointSequence)
            return CreateDeleteRecoveryReplayResult(request, authority, retained);

        var claimed = await recoveryStore.ClaimDeleteRecoveryAsync(
            request,
            _leaseDuration,
            _timeProvider.GetUtcNow(),
            cancellationToken);
        if (claimed is null)
        {
            var current = await _store.GetAsync(
                request.WorkspaceId,
                authority.ProviderOperationId,
                cancellationToken);
            return CreateDeleteRecoveryReplayResult(request, authority, current);
        }

        // The store's claim is the durable linearization point. Treat a malformed result as
        // unavailable rather than allowing an unbound operation to reach the runner.
        if (!IsDeleteRecoveryAuthorityBound(request, authority, claimed) ||
            !IsDeleteRecoverySuccessor(authority, claimed) ||
            claimed.Status != AzureProviderOperationStatus.Running ||
            !string.Equals(claimed.WorkerId, request.WorkerId, StringComparison.Ordinal) ||
            !IsPlanForOperation(claimed, plan))
            return null;

        return await ExecuteClaimedAsync(CopySafePlan(plan), claimed, request.LeaseToken, cancellationToken);
    }

    private static bool IsPlanForOperation(AzureProviderOperation operation, AzureWorkloadPlan plan)
    {
        try
        {
            if (AzureProviderOperationService.TryRestorePlan(operation) is null)
                return false;
            ValidateExecutionRequest(new AzureProviderExecutionRequest(
                AzureProviderOperationService.CreateOperationRequest(operation),
                plan));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsDeleteRecoveryAuthorityBound(
        AzureProviderDeleteRecoveryClaimRequest request,
        AzureProviderDeleteRecoveryAuthority authority,
        AzureProviderOperation operation) =>
        operation.Id == authority.ProviderOperationId &&
        operation.WorkspaceId == request.WorkspaceId &&
        operation.InstanceId == request.InstanceId &&
        operation.OrganizationId is { } organizationId && organizationId != Guid.Empty &&
        operation.Action == AzureProviderOperationAction.Delete &&
        operation.LifecycleAction == ElsaInstanceOperationAction.Delete &&
        operation.ProviderAssignmentId == authority.ProviderAssignmentId &&
        AzureProviderOperationValidation.IsLifecycleDeleteIdempotencyKey(
            operation.IdempotencyKey, request.LifecycleOperationId) &&
        string.Equals(operation.OperationIdentity, authority.ProviderOperationIdentity, StringComparison.Ordinal) &&
        string.Equals(operation.RequestHash, authority.ProviderRequestHash, StringComparison.Ordinal) &&
        string.Equals(operation.TargetKey, authority.TargetKey, StringComparison.Ordinal) &&
        string.Equals(operation.ProviderScopeFingerprint, authority.ProviderScopeFingerprint, StringComparison.Ordinal) &&
        string.Equals(operation.PlanFingerprint, authority.ProviderPlanFingerprint, StringComparison.Ordinal) &&
        string.Equals(operation.TemplateFingerprint, authority.ProviderTemplateFingerprint, StringComparison.Ordinal);

    private static bool IsDeleteRecoverySuccessor(
        AzureProviderDeleteRecoveryAuthority authority,
        AzureProviderOperation operation) =>
        authority.ProviderAttemptNumber < int.MaxValue &&
        operation.AttemptNumber == authority.ProviderAttemptNumber + 1 &&
        operation.Version > authority.ProviderVersion &&
        operation.CheckpointSequence >= authority.ProviderCheckpointSequence &&
        (operation.Phase == AzureProviderOperationPhase.CleanupSubmitted &&
             operation.AttemptedStep == AzureProviderRunnerStep.Cleanup ||
         operation.Phase == AzureProviderOperationPhase.CleanupVerified && operation.AttemptedStep is null) &&
        operation.Status is AzureProviderOperationStatus.Running or AzureProviderOperationStatus.Succeeded or
            AzureProviderOperationStatus.Failed or AzureProviderOperationStatus.Cancelled or
            AzureProviderOperationStatus.RecoveryRequired;

    private static AzureProviderExecutionResult? CreateDeleteRecoveryReplayResult(
        AzureProviderDeleteRecoveryClaimRequest request,
        AzureProviderDeleteRecoveryAuthority authority,
        AzureProviderOperation? operation)
    {
        if (operation is null || !IsDeleteRecoveryAuthorityBound(request, authority, operation) ||
            !IsDeleteRecoverySuccessor(authority, operation))
            return null;

        return operation.Status switch
        {
            AzureProviderOperationStatus.Running => Result(
                operation,
                AzureProviderExecutionOutcome.InProgress,
                "azure.delete-recovery.in-progress",
                "The accepted Azure delete recovery is already in progress."),
            AzureProviderOperationStatus.Succeeded => Result(
                operation,
                AzureProviderExecutionOutcome.NoOp,
                "azure.delete-recovery.completed",
                "The accepted Azure delete recovery is already complete."),
            AzureProviderOperationStatus.RecoveryRequired => Result(
                operation,
                AzureProviderExecutionOutcome.RecoveryRequired,
                "azure.delete-recovery.recovery-required",
                "The Azure delete recovery became uncertain and requires a fresh explicit recovery."),
            _ => Result(
                operation,
                AzureProviderExecutionOutcome.Failed,
                "azure.delete-recovery.terminal",
                "The accepted Azure delete recovery reached a terminal state and requires a new lifecycle recovery.")
        };
    }

    public Task<AzureProviderExecutionResult> ExecuteAsync(
        AzureProviderExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateExecutionRequest(request);
        return ExecuteCoreAsync(request with { Plan = CopySafePlan(request.Plan) }, cancellationToken);
    }

    /// <summary>
    /// Resumes an explicitly accepted recovery after a provider-owned, read-only observation.
    /// The observation checkpoints one completed step only; the normal executor still owns all
    /// later steps and their SQL, workload, health and traffic gates.
    /// </summary>
    public async Task<AzureProviderExecutionResult> RecoverAsync(
        AzureProviderOperation operation,
        AzureWorkloadPlan plan,
        AzureProviderRecoveryObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(observation);
        var recovery = new AzureProviderRecoveryRequest(operation, CopySafePlan(plan));
        recovery.Validate();
        observation.Validate();

        if (operation.Status == AzureProviderOperationStatus.Succeeded)
            return Result(operation, AzureProviderExecutionOutcome.NoOp, "azure.operation.no-op", "The Azure workload already matches the retained plan.");
        if (operation.Status == AzureProviderOperationStatus.Running)
            return Result(operation, AzureProviderExecutionOutcome.InProgress, "azure.operation.in-progress", "The Azure operation is already owned by another worker.");
        if (operation.Status != AzureProviderOperationStatus.RecoveryRequired)
            return ResultForObservedState(operation, "azure.operation.recovery-invalid", "The Azure operation is not awaiting explicit recovery.");
        if (observation.Kind != AzureProviderRecoveryObservationKind.Confirmed)
            return Result(operation, AzureProviderExecutionOutcome.RecoveryRequired, observation.Code, observation.Message);

        var observedStep = observation.CompletedStep!.Value;
        // The legacy combined SQL step, and later workload steps, are intentionally not
        // recoverable from a single observation. They do not provide a safe precondition for
        // this executor's staged SQL recovery handoff.
        if (observedStep is not (AzureProviderRunnerStep.Foundation or AzureProviderRunnerStep.AcrPull or
            AzureProviderRunnerStep.SqlFirewallCreate or AzureProviderRunnerStep.SqlBootstrapScript or
            AzureProviderRunnerStep.SqlFirewallCleanup))
            return RecoveryInsufficient(operation);
        AzureProviderOperationPhase observedPhase;
        try
        {
            observedPhase = AzureProviderRecoveryObservationSupport.RecoveryPhase(observedStep);
        }
        catch (ArgumentException)
        {
            return RecoveryInsufficient(operation);
        }

        // These are eligibility checks, not post-claim defenses. No provider attempt, version,
        // lease or checkpoint may change when the retained evidence cannot cover the current
        // uncertain step.
        if (!AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
                operation.AttemptedStep, operation.Phase, observedStep, observedPhase) ||
            observedStep == AzureProviderRunnerStep.Foundation &&
            !AzureProviderRecoveryObservationSupport.IsFoundationOnlyEligible(operation) ||
            observedStep == AzureProviderRunnerStep.AcrPull &&
            !AzureProviderRecoveryObservationSupport.IsAcrPullEligible(operation) ||
            observedStep == AzureProviderRunnerStep.SqlFirewallCreate &&
            !AzureProviderRecoveryObservationSupport.IsSqlFirewallCreateEligible(operation) ||
            observedStep == AzureProviderRunnerStep.SqlBootstrapScript &&
            !AzureProviderRecoveryObservationSupport.IsSqlBootstrapScriptEligible(operation) ||
            observedStep == AzureProviderRunnerStep.SqlFirewallCleanup &&
            !AzureProviderRecoveryObservationSupport.IsSqlFirewallCleanupEligible(operation))
            return RecoveryInsufficient(operation);

        var now = _timeProvider.GetUtcNow();
        var leaseToken = Guid.NewGuid().ToString("N");
        var claimed = await _store.ClaimRecoveryAsync(
            operation.WorkspaceId,
            operation.Id,
            _workerId,
            leaseToken,
            _leaseDuration,
            now,
            operation.Version,
            cancellationToken);
        if (claimed is null)
        {
            var latest = await _store.GetAsync(operation.WorkspaceId, operation.Id, cancellationToken) ?? operation;
            return ResultForObservedState(latest, "azure.operation.claim-lost", "The Azure operation is owned by another worker or changed concurrently.");
        }

        AzureProviderOperation? checkpointed;
        try
        {
            checkpointed = await CheckpointAsync(
                claimed,
                leaseToken,
                new AzureProviderCheckpoint(
                    observedPhase,
                    observation.Code,
                    observation.Message,
                    observation.Resources,
                    observation.Endpoint ?? claimed.Endpoint,
                    observation.Health == AzureProviderHealth.Unknown ? claimed.Health : observation.Health,
                    [],
                    AttemptedStep: null),
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            // A claimed recovery can still fail its durable assignment/phase invariant, or be
            // cancelled, while the store reloads current state. Use the claimed snapshot and its
            // expected-version CAS to convert this value-free uncertain checkpoint to recovery.
            // FinalizeResultAsync retains that CAS; if the store itself fails, that exception must
            // escape rather than being reported as a fabricated persisted recovery result.
            return await MarkRecoveryAsync(claimed, leaseToken, "azure.recovery.checkpoint-uncertain", "The observed Azure recovery step could not be durably checkpointed.");
        }
        if (checkpointed is null)
            return await GetConcurrentResultAsync(claimed);

        return await ExecuteClaimedAsync(plan, checkpointed, leaseToken, cancellationToken);
    }

    private async Task<AzureProviderExecutionResult> ExecuteCoreAsync(
        AzureProviderExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var operationRequest = AzureProviderOperationValidation.Normalize(request.Operation);
        AzureProviderOperation operation;
        try
        {
            operation = await _store.CreateOrGetAsync(operationRequest, now, cancellationToken);
        }
        catch (AzureProviderOperationConflictException exception)
        {
            return ResultForObservedState(
                exception.Operation,
                "azure.operation.target-busy",
                "Another Azure operation currently owns this target.");
        }

        if (operation.Status == AzureProviderOperationStatus.Succeeded)
            return Result(operation, AzureProviderExecutionOutcome.NoOp, "azure.operation.no-op", "The Azure workload already matches the requested plan.");
        if (operation.Status is AzureProviderOperationStatus.Failed or AzureProviderOperationStatus.Cancelled)
            return Result(operation, AzureProviderExecutionOutcome.Failed, "azure.operation.terminal", "The Azure operation is already terminal and requires a new idempotency key.");
        if (operation.Status == AzureProviderOperationStatus.Running)
            return Result(operation, AzureProviderExecutionOutcome.InProgress, "azure.operation.in-progress", "The Azure operation is already owned by another worker.");

        var leaseToken = Guid.NewGuid().ToString("N");
        var claimed = operation.Status == AzureProviderOperationStatus.RecoveryRequired
            ? await _store.ClaimRecoveryAsync(operation.WorkspaceId, operation.Id, _workerId, leaseToken, _leaseDuration, now, operation.Version, cancellationToken)
            : await _store.ClaimAsync(operation.WorkspaceId, operation.Id, _workerId, leaseToken, _leaseDuration, now, operation.Version, cancellationToken);
        if (claimed is null)
        {
            var latest = await _store.GetAsync(operation.WorkspaceId, operation.Id, cancellationToken) ?? operation;
            return ResultForObservedState(
                latest,
                "azure.operation.claim-lost",
                "The Azure operation is owned by another worker or changed concurrently.");
        }

        return await ExecuteClaimedAsync(request.Plan, claimed, leaseToken, cancellationToken);
    }

    private async Task<AzureProviderExecutionResult> ExecuteClaimedAsync(
        AzureWorkloadPlan plan,
        AzureProviderOperation claimed,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        AzureProviderResourceAssignment? assignment;
        try
        {
            assignment = await LoadAssignmentAsync(claimed, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return await FinalizeResultAsync(
                claimed,
                leaseToken,
                AzureProviderOperationStatus.Failed,
                "azure.assignment.invalid",
                "The durable Azure provider assignment is unavailable or invalid.");
        }

        // Every provider mutation, including cleanup, must pass the durable
        // identity-binding boundary before the first remote runner call.
        var commercialResult = await RevalidateCommercialAsync(claimed, leaseToken, cancellationToken);
        if (commercialResult is not null)
            return commercialResult;

        if (claimed.Action == AzureProviderOperationAction.Delete)
        {
            // The durable assignment is the ownership authority. A legacy unbound test seam may
            // still recover the latest reconcile snapshot, but production delete never infers
            // ownership from whichever operation happened to be most recent.
            if (assignment is not null)
                claimed = claimed with { Resources = assignment.Resources };
            else
            {
                var latestReconcile = await _store.GetLatestReconcileAsync(
                    claimed.WorkspaceId,
                    claimed.TargetKey,
                    claimed.ProviderScopeFingerprint,
                    CancellationToken.None);
                if (latestReconcile is not null)
                    claimed = claimed with { Resources = latestReconcile.Resources };
            }

            return await ExecuteDeleteAsync(plan, claimed, assignment, leaseToken, cancellationToken);
        }

        return await ExecuteReconcileAsync(plan, claimed, assignment, leaseToken, cancellationToken);
    }

    private async Task<AzureProviderExecutionResult> ExecuteReconcileAsync(
        AzureWorkloadPlan plan,
        AzureProviderOperation operation,
        AzureProviderResourceAssignment? assignment,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (operation.Status != AzureProviderOperationStatus.Running)
                return Result(operation, MapOutcome(operation.Status), "azure.operation.state-changed", "The Azure operation changed state before the next lifecycle step.");

            IReadOnlyList<(AzureProviderRunnerStep Step, AzureProviderOperationPhase Phase)> next;
            try
            {
                next = NextReconcileStep(operation.Phase);
            }
            catch (InvalidOperationException)
            {
                return await FinalizeResultAsync(operation, leaseToken, AzureProviderOperationStatus.Failed, "azure.operation.phase.invalid", "The reconcile operation has an invalid lifecycle phase.");
            }
            if (next.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(operation.Resources.WorkloadResourceId))
                    return await MarkRecoveryAsync(operation, leaseToken, "azure.workload.identity.missing", "The provider did not retain an owned workload resource identity.");
                return await FinalizeResultAsync(operation, leaseToken, AzureProviderOperationStatus.Succeeded, "azure.operation.succeeded", "Azure workload reconciliation completed.");
            }

            foreach (var (step, phase) in next)
            {
                if (cancellationToken.IsCancellationRequested)
                    return await MarkRecoveryAsync(operation, leaseToken, "azure.step.cancelled", "The Azure lifecycle step was cancelled before the next remote mutation.");

                // This is the last authorization check before the remote runner
                // call. If entitlement was downgraded before this CAS, the
                // operation is held for retry and no provider mutation occurs.
                var entitlementResult = await RevalidateCommercialAsync(operation, leaseToken, cancellationToken);
                if (entitlementResult is not null)
                    return entitlementResult;

                var attempted = await MarkAttemptedStepAsync(operation, leaseToken, step);
                if (attempted is null)
                    return await GetConcurrentResultAsync(operation);
                operation = attempted;

                var command = new AzureProviderRunnerCommand(
                    step,
                    plan,
                    operation.Resources,
                    operation.Resources.StableTrafficRevisionName,
                    operation.AttemptNumber > 1,
                    operation.AttemptNumber,
                    CreateExecutionContext(operation, assignment),
                    assignment);
                AzureProviderRunnerResult runnerResult;
                try
                {
                    var run = await RunRunnerAsync(command, operation, leaseToken, cancellationToken);
                    runnerResult = run.Result;
                    operation = run.Operation;
                    // Runner postconditions keep their existing step contract. The
                    // executor may persist a finer recovery checkpoint independently.
                    var runnerPhase = step is AzureProviderRunnerStep.AcrPull or AzureProviderRunnerStep.SeedSecrets
                        ? AzureProviderOperationPhase.FoundationSubmitted
                        : phase;
                    ValidateRunnerResult(runnerResult, step, runnerPhase, requiresHealthyEndpoint: step == AzureProviderRunnerStep.Promotion);
                }
                catch (RunnerExecutionException exception) when (exception.Cause is OperationCanceledException || cancellationToken.IsCancellationRequested)
                {
                    return await MarkRecoveryAsync(exception.Operation, leaseToken, "azure.step.cancelled", "The Azure lifecycle step was interrupted before its result could be confirmed.");
                }
                catch (RunnerExecutionException exception)
                {
                    return await MarkRecoveryAsync(exception.Operation, leaseToken, "azure.step.uncertain", "The Azure lifecycle step failed before its external result could be confirmed.");
                }
                catch (LeaseLostException)
                {
                    return await GetConcurrentResultAsync(operation);
                }
                catch (OperationCanceledException)
                {
                    return await MarkRecoveryAsync(operation, leaseToken, "azure.step.cancelled", "The Azure lifecycle step was interrupted before its result could be confirmed.");
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    return await MarkRecoveryAsync(operation, leaseToken, "azure.step.cancelled", "The Azure lifecycle step was interrupted before its result could be confirmed.");
                }
                catch (Exception)
                {
                    return await MarkRecoveryAsync(operation, leaseToken, "azure.step.uncertain", "The Azure lifecycle step failed before its external result could be confirmed.");
                }

                if (runnerResult.Outcome is AzureProviderRunnerOutcome.Failed or AzureProviderRunnerOutcome.Uncertain)
                {
                    var failureOperation = await PersistRunnerReferencesAsync(
                        operation,
                        leaseToken,
                        runnerResult,
                        CancellationToken.None,
                        preserveStableTrafficRevision: step == AzureProviderRunnerStep.Promotion,
                        attemptedStep: step);
                    if (failureOperation is null)
                        return await GetConcurrentResultAsync(operation);
                    operation = failureOperation;
                    if (step == AzureProviderRunnerStep.Promotion)
                        return await HandlePromotionFailureAsync(plan, operation, assignment, leaseToken, runnerResult, CancellationToken.None);

                    return await FinalizeResultAsync(
                        operation,
                        leaseToken,
                        runnerResult.Outcome == AzureProviderRunnerOutcome.Uncertain ? AzureProviderOperationStatus.RecoveryRequired : AzureProviderOperationStatus.Failed,
                        SafeStepCode(step, runnerResult.Outcome),
                        SafeStepMessage(step, runnerResult.Outcome),
                        CancellationToken.None);
                }

                if (step == AzureProviderRunnerStep.Health &&
                    (runnerResult.Health == AzureProviderHealth.Unknown || string.IsNullOrWhiteSpace(runnerResult.Endpoint)))
                {
                    var incomplete = await PersistRunnerReferencesAsync(operation, leaseToken, runnerResult, CancellationToken.None, attemptedStep: step);
                    if (incomplete is null)
                        return await GetConcurrentResultAsync(operation);
                    return await FinalizeResultAsync(
                        incomplete,
                        leaseToken,
                        AzureProviderOperationStatus.RecoveryRequired,
                        "azure.health.incomplete",
                        "The provider did not return a complete health result for the candidate.",
                        CancellationToken.None);
                }
                if (step == AzureProviderRunnerStep.Health && runnerResult.Health != AzureProviderHealth.Healthy)
                {
                    var unhealthy = await CheckpointAsync(
                        operation,
                        leaseToken,
                        new AzureProviderCheckpoint(
                            phase,
                            "azure.health.unhealthy",
                            "The candidate did not pass health verification.",
                            runnerResult.Resources,
                            runnerResult.Endpoint,
                            runnerResult.Health,
                            SafeDiagnostics(runnerResult.Diagnostics)),
                        CancellationToken.None);
                    if (unhealthy is null)
                        return await GetConcurrentResultAsync(operation);
                    return await FinalizeResultAsync(unhealthy, leaseToken, AzureProviderOperationStatus.Failed, "azure.health.unhealthy", "The candidate did not pass health verification.");
                }

                var checkpoint = new AzureProviderCheckpoint(
                    phase,
                    SafeStepCode(step, runnerResult.Outcome),
                    SafeStepMessage(step, runnerResult.Outcome),
                    runnerResult.Resources,
                    runnerResult.Endpoint,
                    runnerResult.Health,
                    SafeDiagnostics(runnerResult.Diagnostics));
                AzureProviderOperation? checkpointed;
                try
                {
                    checkpointed = await CheckpointAsync(operation, leaseToken, checkpoint, CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    return await MarkRecoveryAsync(operation, leaseToken, "azure.step.cancelled", "The Azure lifecycle step completed but its checkpoint could not be confirmed.");
                }
                if (checkpointed is null)
                    return await GetConcurrentResultAsync(operation);
                operation = checkpointed;
                if (cancellationToken.IsCancellationRequested)
                    return await MarkRecoveryAsync(operation, leaseToken, "azure.step.cancelled", "The Azure lifecycle step completed but the operation was cancelled before the next remote mutation.");
            }
        }
    }

    private async Task<AzureProviderExecutionResult?> RevalidateCommercialAsync(
        AzureProviderOperation operation,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        if (operation.OrganizationId is not { } organizationId || organizationId == Guid.Empty ||
            operation.InstanceId is not { } instanceId || instanceId == Guid.Empty ||
            operation.LifecycleAction is not { } lifecycleAction)
        {
            var bindingHeld = await _store.FinalizeAsync(
                operation.WorkspaceId,
                operation.Id,
                leaseToken,
                AzureProviderOperationStatus.EntitlementHeld,
                ElsaInstanceCommercialOperation.BindingRequired,
                _timeProvider.GetUtcNow(),
                operation.Version,
                cancellationToken);
            if (bindingHeld is null)
                return await GetConcurrentResultAsync(operation);
            return Result(
                bindingHeld,
                AzureProviderExecutionOutcome.InProgress,
                ElsaInstanceCommercialOperation.BindingRequired,
                "The managed-instance provider operation is missing its durable identity binding.");
        }

        if (operation.Action == AzureProviderOperationAction.Delete)
            return null;

        if (_commercialGate is null)
            return null;

        // The catalog-backed store owns the durable linearization point. It evaluates the
        // current entitlement while holding the operation transaction and, when denied, clears
        // this lease in the same CAS that records EntitlementHeld. A downgrade committed before
        // this boundary therefore cannot race into the provider runner.
        if (_store is IAzureProviderOperationAuthorizationStore authorizationStore)
        {
            var authorization = await authorizationStore.AuthorizeAsync(
                operation.WorkspaceId,
                operation.Id,
                leaseToken,
                _commercialGate,
                _timeProvider.GetUtcNow(),
                operation.Version,
                cancellationToken);
            if (authorization is null)
                return await GetConcurrentResultAsync(operation);
            if (authorization.Decision.Allowed)
                return null;

            return Result(
                authorization.Operation,
                AzureProviderExecutionOutcome.InProgress,
                authorization.Decision.Code,
                authorization.Decision.Summary);
        }

        var decision = await _commercialGate.EvaluateAsync(
            organizationId,
            lifecycleAction,
            cancellationToken: cancellationToken);
        if (decision.Allowed)
            return null;

        var fallbackHeld = await _store.FinalizeAsync(
            operation.WorkspaceId,
            operation.Id,
            leaseToken,
            AzureProviderOperationStatus.EntitlementHeld,
            decision.Code,
            _timeProvider.GetUtcNow(),
            operation.Version,
            cancellationToken);
        if (fallbackHeld is null)
            return await GetConcurrentResultAsync(operation);
        return Result(
            fallbackHeld,
            AzureProviderExecutionOutcome.InProgress,
            decision.Code,
            decision.Summary);
    }

    private async Task<AzureProviderExecutionResult> ExecuteDeleteAsync(
        AzureWorkloadPlan plan,
        AzureProviderOperation operation,
        AzureProviderResourceAssignment? assignment,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        if (operation.Phase is AzureProviderOperationPhase.Planned)
        {
            var submitted = await CheckpointAsync(
                operation,
                leaseToken,
                new AzureProviderCheckpoint(
                    AzureProviderOperationPhase.CleanupSubmitted,
                    "azure.cleanup.submitted",
                    "Exact owned-resource cleanup was submitted.",
                    operation.Resources,
                    null,
                    AzureProviderHealth.Unknown,
                    [],
                    AttemptedStep: AzureProviderRunnerStep.Cleanup),
                CancellationToken.None);
            if (submitted is null)
                return await GetConcurrentResultAsync(operation);
            operation = submitted;
        }

        if (cancellationToken.IsCancellationRequested)
            return await MarkRecoveryAsync(operation, leaseToken, "azure.cleanup.cancelled", "Owned-resource cleanup was cancelled before the remote cleanup step.");

        if (operation.Phase == AzureProviderOperationPhase.CleanupVerified)
            return await FinalizeResultAsync(operation, leaseToken, AzureProviderOperationStatus.Succeeded, "azure.cleanup.succeeded", "Exact owned-resource cleanup was verified.");
        if (operation.Phase != AzureProviderOperationPhase.CleanupSubmitted)
            return await FinalizeResultAsync(operation, leaseToken, AzureProviderOperationStatus.Failed, "azure.cleanup.phase.invalid", "The delete operation has an invalid cleanup phase.");

        var attempted = await MarkAttemptedStepAsync(operation, leaseToken, AzureProviderRunnerStep.Cleanup);
        if (attempted is null)
            return await GetConcurrentResultAsync(operation);
        operation = attempted;

        AzureProviderRunnerResult runnerResult;
        try
        {
            var command = new AzureProviderRunnerCommand(
                AzureProviderRunnerStep.Cleanup,
                plan,
                operation.Resources,
                operation.Resources.StableTrafficRevisionName,
                operation.AttemptNumber > 1,
                operation.AttemptNumber,
                CreateExecutionContext(operation, assignment),
                assignment);
            var run = await RunRunnerAsync(command, operation, leaseToken, cancellationToken);
            runnerResult = run.Result;
            operation = run.Operation;
            ValidateRunnerResult(runnerResult, AzureProviderRunnerStep.Cleanup, AzureProviderOperationPhase.CleanupVerified, requiresHealthyEndpoint: false);
        }
        catch (RunnerExecutionException exception) when (exception.Cause is OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            return await MarkRecoveryAsync(exception.Operation, leaseToken, "azure.cleanup.cancelled", "Owned-resource cleanup was interrupted before absence could be confirmed.");
        }
        catch (RunnerExecutionException exception)
        {
            return await MarkRecoveryAsync(exception.Operation, leaseToken, "azure.cleanup.uncertain", "Owned-resource cleanup failed before absence could be confirmed.");
        }
        catch (LeaseLostException)
        {
            return await GetConcurrentResultAsync(operation);
        }
        catch (OperationCanceledException)
        {
            return await MarkRecoveryAsync(operation, leaseToken, "azure.cleanup.cancelled", "Owned-resource cleanup was interrupted before absence could be confirmed.");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return await MarkRecoveryAsync(operation, leaseToken, "azure.cleanup.cancelled", "Owned-resource cleanup was interrupted before absence could be confirmed.");
        }
        catch (Exception)
        {
            return await MarkRecoveryAsync(operation, leaseToken, "azure.cleanup.uncertain", "Owned-resource cleanup failed before absence could be confirmed.");
        }

        if (runnerResult.Outcome is AzureProviderRunnerOutcome.Failed or AzureProviderRunnerOutcome.Uncertain)
        {
            var failureOperation = await PersistRunnerReferencesAsync(operation, leaseToken, runnerResult, CancellationToken.None, attemptedStep: AzureProviderRunnerStep.Cleanup);
            if (failureOperation is null)
                return await GetConcurrentResultAsync(operation);
            operation = failureOperation;
            return await FinalizeResultAsync(
                operation,
                leaseToken,
                runnerResult.Outcome == AzureProviderRunnerOutcome.Uncertain ? AzureProviderOperationStatus.RecoveryRequired : AzureProviderOperationStatus.Failed,
                SafeStepCode(AzureProviderRunnerStep.Cleanup, runnerResult.Outcome),
                SafeStepMessage(AzureProviderRunnerStep.Cleanup, runnerResult.Outcome),
                CancellationToken.None);
        }

        if (!runnerResult.OwnedResourcesAbsent || runnerResult.Resources != new AzureProviderResourceReferences() || runnerResult.Endpoint is not null || runnerResult.Health != AzureProviderHealth.Unknown)
        {
            return await FinalizeResultAsync(operation, leaseToken, AzureProviderOperationStatus.Failed, "azure.cleanup.ownership.unverified", "The provider did not prove exact owned-resource absence.");
        }

        AzureProviderOperation? verified;
        try
        {
            verified = await CheckpointAsync(
                operation,
                leaseToken,
                new AzureProviderCheckpoint(
                    AzureProviderOperationPhase.CleanupVerified,
                    SafeStepCode(AzureProviderRunnerStep.Cleanup, runnerResult.Outcome),
                    SafeStepMessage(AzureProviderRunnerStep.Cleanup, runnerResult.Outcome),
                    new(),
                    null,
                    AzureProviderHealth.Unknown,
                    SafeDiagnostics(runnerResult.Diagnostics),
                    ReplaceResources: true),
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return await MarkRecoveryAsync(operation, leaseToken, "azure.cleanup.cancelled", "Owned-resource cleanup completed but its absence checkpoint could not be confirmed.");
        }
        if (verified is null)
            return await GetConcurrentResultAsync(operation);
        try
        {
            return await FinalizeResultAsync(verified, leaseToken, AzureProviderOperationStatus.Succeeded, "azure.cleanup.succeeded", "Exact owned-resource cleanup was verified.", CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return await MarkRecoveryAsync(verified, leaseToken, "azure.cleanup.cancelled", "Owned-resource cleanup completed but its final state could not be confirmed.");
        }
    }

    private async Task<AzureProviderExecutionResult> HandlePromotionFailureAsync(
        AzureWorkloadPlan plan,
        AzureProviderOperation operation,
        AzureProviderResourceAssignment? assignment,
        string leaseToken,
        AzureProviderRunnerResult promotion,
        CancellationToken cancellationToken)
    {
        // A first deployment has no previously verified revision to restore. Do not ask the
        // runner to invent one: an uncertain promotion without a stable revision must remain in
        // durable recovery until an operator can establish the traffic state.
        if (string.IsNullOrWhiteSpace(operation.Resources.StableTrafficRevisionName))
        {
            return await MarkRecoveryAsync(
                operation,
                leaseToken,
                promotion.Outcome == AzureProviderRunnerOutcome.Uncertain
                    ? "azure.promotion.uncertain"
                    : "azure.promotion.rollback-unavailable",
                promotion.Outcome == AzureProviderRunnerOutcome.Uncertain
                    ? "Candidate promotion was uncertain and no previously verified stable traffic revision was available."
                    : "Candidate promotion failed and no previously verified stable traffic revision was available to restore.");
        }

        var attempted = await MarkAttemptedStepAsync(operation, leaseToken, AzureProviderRunnerStep.RestoreStableTraffic);
        if (attempted is null)
            return await GetConcurrentResultAsync(operation);
        operation = attempted;

        var rollbackCommand = new AzureProviderRunnerCommand(
            AzureProviderRunnerStep.RestoreStableTraffic,
            plan,
            operation.Resources,
            operation.Resources.StableTrafficRevisionName,
            operation.AttemptNumber > 1,
            operation.AttemptNumber,
            CreateExecutionContext(operation, assignment),
            assignment);
        try
        {
            var run = await RunRunnerAsync(rollbackCommand, operation, leaseToken, cancellationToken);
            var rollback = run.Result;
            operation = run.Operation;
            ValidateRunnerResult(rollback, AzureProviderRunnerStep.RestoreStableTraffic, AzureProviderOperationPhase.HealthVerified, requiresHealthyEndpoint: false);
            if ((rollback.Outcome == AzureProviderRunnerOutcome.Completed || rollback.Outcome == AzureProviderRunnerOutcome.NoOp) &&
                rollback.StableTrafficRestored &&
                string.Equals(rollback.Resources.StableTrafficRevisionName, operation.Resources.StableTrafficRevisionName, StringComparison.Ordinal))
            {
                var restored = await CheckpointAsync(
                    operation,
                    leaseToken,
                    new AzureProviderCheckpoint(
                        AzureProviderOperationPhase.HealthVerified,
                        "azure.promotion.restored",
                        "Stable traffic was restored after candidate promotion failed.",
                        rollback.Resources,
                        rollback.Endpoint,
                        rollback.Health,
                        SafeDiagnostics(rollback.Diagnostics)),
                    CancellationToken.None);
                if (restored is not null)
                    operation = restored;
                return await FinalizeResultAsync(
                    operation,
                    leaseToken,
                    promotion.Outcome == AzureProviderRunnerOutcome.Uncertain
                        ? AzureProviderOperationStatus.RecoveryRequired
                        : AzureProviderOperationStatus.Failed,
                    promotion.Outcome == AzureProviderRunnerOutcome.Uncertain ? "azure.promotion.uncertain" : SafeStepCode(AzureProviderRunnerStep.Promotion, promotion.Outcome),
                    promotion.Outcome == AzureProviderRunnerOutcome.Uncertain
                        ? "Candidate promotion was uncertain; stable traffic was restored but operator recovery is required."
                        : SafeStepMessage(AzureProviderRunnerStep.Promotion, promotion.Outcome),
                    CancellationToken.None);
            }
        }
        catch (RunnerExecutionException exception)
        {
            operation = exception.Operation;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // A cancelled rollback is still an uncertain external operation and is recorded below.
        }
        catch (Exception)
        {
            // The durable recovery state is the safe result when rollback itself is not confirmed.
        }

        return await MarkRecoveryAsync(operation, leaseToken, "azure.promotion.rollback-uncertain", "Candidate promotion and stable-traffic restoration could not both be confirmed.");
    }

    private async Task<AzureProviderOperation?> CheckpointAsync(
        AzureProviderOperation operation,
        string leaseToken,
        AzureProviderCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        AzureProviderOperationValidation.ValidateCheckpoint(checkpoint);
        return await _store.CheckpointAsync(
            operation.WorkspaceId,
            operation.Id,
            leaseToken,
            checkpoint,
            _timeProvider.GetUtcNow(),
            operation.Version,
            cancellationToken);
    }

    private async Task<AzureProviderOperation?> MarkAttemptedStepAsync(
        AzureProviderOperation operation,
        string leaseToken,
        AzureProviderRunnerStep step)
    {
        if (operation.AttemptedStep == step)
            return operation;

        return await CheckpointAsync(
            operation,
            leaseToken,
            new AzureProviderCheckpoint(
                operation.Phase,
                "azure.step.attempted",
                "The Azure lifecycle step was marked before its remote call.",
                operation.Resources,
                operation.Endpoint,
                operation.Health,
                [],
                AttemptedStep: step),
            CancellationToken.None);
    }

    private async Task<AzureProviderOperation?> PersistRunnerReferencesAsync(
        AzureProviderOperation operation,
        string leaseToken,
        AzureProviderRunnerResult runnerResult,
        CancellationToken cancellationToken,
        bool preserveStableTrafficRevision = false,
        AzureProviderRunnerStep? attemptedStep = null)
    {
        // A failed or uncertain provider result can still contain the only durable handles to
        // resources created before the result was produced. Keep the current phase monotonic and
        // merge these handles in the store so recovery and delete can continue safely.
        var resources = preserveStableTrafficRevision
            ? runnerResult.Resources with { StableTrafficRevisionName = null }
            : runnerResult.Resources;
        // Reference-only checkpoints are partial by contract. A runner may omit observations when
        // a lifecycle step does not own them, so preserve the last authoritative values rather
        // than turning an omitted endpoint/Unknown health into a destructive overwrite.
        var endpoint = runnerResult.Endpoint ?? operation.Endpoint;
        var health = runnerResult.Health == AzureProviderHealth.Unknown ? operation.Health : runnerResult.Health;
        return await CheckpointAsync(
            operation,
            leaseToken,
            new AzureProviderCheckpoint(
                operation.Phase,
                "azure.step.references",
                "Provider resource references were retained for recovery.",
                resources,
                endpoint,
                health,
                SafeDiagnostics(runnerResult.Diagnostics),
                AttemptedStep: attemptedStep ?? operation.AttemptedStep),
            cancellationToken);
    }

    private async Task<AzureProviderExecutionResult> MarkRecoveryAsync(
        AzureProviderOperation operation,
        string leaseToken,
        string code,
        string message)
    {
        return await FinalizeResultAsync(operation, leaseToken, AzureProviderOperationStatus.RecoveryRequired, code, message, CancellationToken.None);
    }

    private async Task<AzureProviderExecutionResult> FinalizeResultAsync(
        AzureProviderOperation operation,
        string leaseToken,
        AzureProviderOperationStatus status,
        string code,
        string message,
        CancellationToken cancellationToken = default)
    {
        AzureProviderOperationValidation.ValidateCode(code);
        var finalized = await _store.FinalizeAsync(
            operation.WorkspaceId,
            operation.Id,
            leaseToken,
            status,
            code,
            _timeProvider.GetUtcNow(),
            operation.Version,
            cancellationToken);
        if (finalized is null)
            return await GetConcurrentResultAsync(operation);
        return Result(finalized, MapOutcome(finalized.Status), code, message);
    }

    private async Task<AzureProviderExecutionResult> GetConcurrentResultAsync(AzureProviderOperation operation)
    {
        var latest = await _store.GetAsync(operation.WorkspaceId, operation.Id, CancellationToken.None) ?? operation;
        return ResultForObservedState(
            latest,
            "azure.operation.concurrent-update",
            "The Azure operation changed concurrently and must be observed by its current owner.");
    }

    private async Task<(AzureProviderRunnerResult Result, AzureProviderOperation Operation)> RunRunnerAsync(
        AzureProviderRunnerCommand command,
        AzureProviderOperation operation,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        using var runnerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var runnerTask = _runner.RunAsync(command, runnerCancellation.Token);
            while (true)
            {
                var delayTask = Task.Delay(_heartbeatInterval, cancellationToken);
                var completed = await Task.WhenAny(runnerTask, delayTask);
                if (completed == runnerTask)
                    return (await runnerTask, operation);

                if (cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        runnerCancellation.Cancel();
                    }
                    catch (AggregateException)
                    {
                        // Cancellation is best-effort for provider implementations whose
                        // callbacks may fail while cancellation is being signalled.
                    }
                    ObserveCompletion(runnerTask);
                    throw new OperationCanceledException(cancellationToken);
                }
                var renewed = await _store.HeartbeatAsync(
                    operation.WorkspaceId,
                    operation.Id,
                    leaseToken,
                    _leaseDuration,
                    _timeProvider.GetUtcNow(),
                    operation.Version,
                    cancellationToken);
                if (renewed is null)
                {
                    try
                    {
                        runnerCancellation.Cancel();
                    }
                    catch (AggregateException)
                    {
                        // Cancellation is best-effort. The runner contract still requires
                        // every remote step to be idempotent when the external job cannot stop.
                    }
                    ObserveCompletion(runnerTask);
                    throw new LeaseLostException();
                }
                operation = renewed;
            }
        }
        catch (LeaseLostException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RunnerExecutionException(operation, exception);
        }
    }

    private static void ObserveCompletion(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static IReadOnlyList<(AzureProviderRunnerStep Step, AzureProviderOperationPhase Phase)> NextReconcileStep(AzureProviderOperationPhase phase) =>
        phase switch
        {
            AzureProviderOperationPhase.Planned or AzureProviderOperationPhase.FoundationSubmitted =>
                phase == AzureProviderOperationPhase.Planned
                    ? [(AzureProviderRunnerStep.Foundation, AzureProviderOperationPhase.FoundationSubmitted)]
                    : [
                        (AzureProviderRunnerStep.AcrPull, AzureProviderOperationPhase.FoundationSubmitted),
                        (AzureProviderRunnerStep.SeedSecrets, AzureProviderOperationPhase.FoundationSubmitted),
                        (AzureProviderRunnerStep.SqlFirewallCreate, AzureProviderOperationPhase.SqlFirewallReady),
                        (AzureProviderRunnerStep.SqlBootstrapScript, AzureProviderOperationPhase.SqlBootstrapReady),
                        (AzureProviderRunnerStep.SqlFirewallCleanup, AzureProviderOperationPhase.FoundationReady)
                    ],
            AzureProviderOperationPhase.FoundationObserved => [
                (AzureProviderRunnerStep.AcrPull, AzureProviderOperationPhase.AcrPullObserved),
                (AzureProviderRunnerStep.SeedSecrets, AzureProviderOperationPhase.SeedSecretsObserved),
                (AzureProviderRunnerStep.SqlFirewallCreate, AzureProviderOperationPhase.SqlFirewallReady),
                (AzureProviderRunnerStep.SqlBootstrapScript, AzureProviderOperationPhase.SqlBootstrapReady),
                (AzureProviderRunnerStep.SqlFirewallCleanup, AzureProviderOperationPhase.FoundationReady)
            ],
            AzureProviderOperationPhase.AcrPullObserved => [
                (AzureProviderRunnerStep.SeedSecrets, AzureProviderOperationPhase.SeedSecretsObserved),
                (AzureProviderRunnerStep.SqlFirewallCreate, AzureProviderOperationPhase.SqlFirewallReady),
                (AzureProviderRunnerStep.SqlBootstrapScript, AzureProviderOperationPhase.SqlBootstrapReady),
                (AzureProviderRunnerStep.SqlFirewallCleanup, AzureProviderOperationPhase.FoundationReady)
            ],
            AzureProviderOperationPhase.SeedSecretsObserved =>
                [
                    (AzureProviderRunnerStep.SqlFirewallCreate, AzureProviderOperationPhase.SqlFirewallReady),
                    (AzureProviderRunnerStep.SqlBootstrapScript, AzureProviderOperationPhase.SqlBootstrapReady),
                    (AzureProviderRunnerStep.SqlFirewallCleanup, AzureProviderOperationPhase.FoundationReady)
                ],
            AzureProviderOperationPhase.SqlFirewallReady =>
                [(AzureProviderRunnerStep.SqlBootstrapScript, AzureProviderOperationPhase.SqlBootstrapReady)],
            AzureProviderOperationPhase.SqlBootstrapReady =>
                [(AzureProviderRunnerStep.SqlFirewallCleanup, AzureProviderOperationPhase.FoundationReady)],
            AzureProviderOperationPhase.FoundationReady or AzureProviderOperationPhase.WorkloadSubmitted =>
                [(AzureProviderRunnerStep.Workload, AzureProviderOperationPhase.WorkloadReady)],
            AzureProviderOperationPhase.WorkloadReady =>
                [(AzureProviderRunnerStep.Health, AzureProviderOperationPhase.HealthVerified)],
            AzureProviderOperationPhase.HealthVerified =>
                [(AzureProviderRunnerStep.Promotion, AzureProviderOperationPhase.TrafficPromoted)],
            AzureProviderOperationPhase.TrafficPromoted => [],
            _ => throw new InvalidOperationException("The reconcile operation has an invalid lifecycle phase.")
        };

    private static AzureProviderExecutionResult RecoveryInsufficient(AzureProviderOperation operation) =>
        Result(operation, AzureProviderExecutionOutcome.RecoveryRequired,
            "azure.recovery.observation-insufficient",
            "The retained Azure recovery observation cannot authorize the current lifecycle checkpoint.");

    private static void ValidateRunnerResult(
        AzureProviderRunnerResult result,
        AzureProviderRunnerStep step,
        AzureProviderOperationPhase expectedPhase,
        bool requiresHealthyEndpoint)
    {
        if (result is null)
            throw new ArgumentException("The provider runner returned no result.", nameof(result));
        if (!Enum.IsDefined(result.Outcome) || !Enum.IsDefined(result.Phase) || !Enum.IsDefined(result.Health))
            throw new ArgumentException("The provider runner returned an invalid result enum.", nameof(result));
        AzureProviderOperationValidation.ValidateCode(result.Code);
        AzureProviderOperationValidation.ValidateMessage(result.Message);
        AzureProviderOperationValidation.ValidateReferences(result.Resources);
        AzureProviderOperationValidation.ValidateEndpoint(result.Endpoint);
        if (result.Diagnostics is null || result.Diagnostics.Count > 20)
            throw new ArgumentException("The provider runner returned unbounded diagnostics.", nameof(result));
        foreach (var diagnostic in result.Diagnostics)
        {
            if (diagnostic is null)
                throw new ArgumentException("The provider runner returned a null diagnostic.", nameof(result));
            AzureProviderOperationValidation.ValidateCode(diagnostic.Code);
            if (diagnostic.Message is null || diagnostic.Message.Length > 2000)
                throw new ArgumentException("The provider runner returned an unbounded diagnostic.", nameof(result));
        }

        if (result.Outcome is AzureProviderRunnerOutcome.Completed or AzureProviderRunnerOutcome.NoOp)
        {
            if (result.Phase != expectedPhase)
                throw new ArgumentException("The provider runner completed a different lifecycle phase.", nameof(result));
            ValidateStepPostcondition(step, result);
            if (requiresHealthyEndpoint && (result.Health != AzureProviderHealth.Healthy || string.IsNullOrWhiteSpace(result.Endpoint)))
                throw new ArgumentException("A successful promotion step must return a healthy HTTPS endpoint.", nameof(result));
        }
    }

    private static void ValidateStepPostcondition(AzureProviderRunnerStep step, AzureProviderRunnerResult result)
    {
        var resources = result.Resources;
        var foundationComplete =
            Has(resources.ResourceGroupName) && Has(resources.FoundationDeploymentId) &&
            Has(resources.WorkloadIdentityResourceId) && Has(resources.WorkloadIdentityClientId) &&
            Has(resources.WorkloadIdentityPrincipalId) && Has(resources.KeyVaultResourceId) &&
            Has(resources.KeyVaultUri) && Has(resources.SqlServerResourceId) && Has(resources.SqlServerFqdn) &&
            Has(resources.ContainerAppsEnvironmentResourceId);
        var registryComplete =
            Has(resources.RegistryResourceId) && Has(resources.AcrPullDeploymentId) &&
            Has(resources.AcrPullRoleAssignmentId);
        var workloadComplete =
            Has(resources.WorkloadDeploymentId) && Has(resources.WorkloadResourceId) &&
            Has(resources.WorkloadRevisionName);

        var valid = step switch
        {
            AzureProviderRunnerStep.Foundation => foundationComplete,
            AzureProviderRunnerStep.AcrPull => foundationComplete && registryComplete,
            AzureProviderRunnerStep.SeedSecrets or AzureProviderRunnerStep.SqlBootstrap or
                AzureProviderRunnerStep.SqlFirewallCreate or AzureProviderRunnerStep.SqlBootstrapScript or
                AzureProviderRunnerStep.SqlFirewallCleanup => foundationComplete && registryComplete,
            AzureProviderRunnerStep.Workload or AzureProviderRunnerStep.Health or AzureProviderRunnerStep.Promotion =>
                foundationComplete && registryComplete && workloadComplete,
            AzureProviderRunnerStep.RestoreStableTraffic =>
                foundationComplete && registryComplete && workloadComplete &&
                Has(resources.StableTrafficRevisionName) && result.StableTrafficRestored,
            // Cleanup has a dedicated exact-absence classifier after result validation so a
            // confirmed-but-incomplete cleanup becomes Failed rather than uncertain recovery.
            AzureProviderRunnerStep.Cleanup => true,
            _ => false
        };
        if (!valid)
            throw new ArgumentException("The provider runner did not prove the lifecycle step postcondition.", nameof(result));

        static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);
    }

    private static string SafeStepMessage(AzureProviderRunnerStep step, AzureProviderRunnerOutcome outcome) =>
        outcome switch
        {
            AzureProviderRunnerOutcome.Completed => $"Azure {StepLabel(step)} step completed.",
            AzureProviderRunnerOutcome.NoOp => $"Azure {StepLabel(step)} step was already converged.",
            AzureProviderRunnerOutcome.Failed => $"Azure {StepLabel(step)} step failed.",
            AzureProviderRunnerOutcome.Uncertain => $"Azure {StepLabel(step)} step requires recovery.",
            _ => "Azure provider step returned an invalid outcome."
        };

    private static string SafeStepCode(AzureProviderRunnerStep step, AzureProviderRunnerOutcome outcome) =>
        outcome switch
        {
            AzureProviderRunnerOutcome.Completed => "azure.step.completed",
            AzureProviderRunnerOutcome.NoOp => "azure.step.no-op",
            AzureProviderRunnerOutcome.Failed => "azure.step.failed",
            AzureProviderRunnerOutcome.Uncertain => "azure.step.uncertain",
            _ => "azure.step.invalid"
        };

    private static string StepLabel(AzureProviderRunnerStep step) =>
        step switch
        {
            AzureProviderRunnerStep.Foundation => "foundation",
            AzureProviderRunnerStep.AcrPull => "registry access",
            AzureProviderRunnerStep.SeedSecrets => "credential reference seeding",
            AzureProviderRunnerStep.SqlBootstrap => "database bootstrap",
            AzureProviderRunnerStep.SqlFirewallCreate => "SQL firewall creation",
            AzureProviderRunnerStep.SqlBootstrapScript => "SQL bootstrap script",
            AzureProviderRunnerStep.SqlFirewallCleanup => "SQL firewall cleanup",
            AzureProviderRunnerStep.Workload => "workload",
            AzureProviderRunnerStep.Health => "health verification",
            AzureProviderRunnerStep.Promotion => "traffic promotion",
            AzureProviderRunnerStep.RestoreStableTraffic => "stable traffic restoration",
            AzureProviderRunnerStep.Cleanup => "cleanup",
            _ => "provider"
        };

    private static IReadOnlyList<AzureProviderDiagnostic> SafeDiagnostics(IReadOnlyList<AzureProviderDiagnostic> diagnostics) =>
        AzureProviderSafeDiagnostics.Normalize(diagnostics);

    private static AzureProviderExecutionContext CreateExecutionContext(
        AzureProviderOperation operation,
        AzureProviderResourceAssignment? assignment = null) => new(
        operation.WorkspaceId,
        operation.OrganizationId ?? throw new InvalidOperationException("The provider organization binding is unavailable."),
        operation.InstanceId ?? throw new InvalidOperationException("The provider instance binding is unavailable."),
        operation.Id,
        operation.OperationIdentity,
        operation.IdempotencyKey,
        operation.TargetKey,
        (operation.ProviderAssignmentId ?? throw new InvalidOperationException("The provider assignment binding is unavailable.")).ToString("D"),
        operation.PlanFingerprint,
        operation.TemplateFingerprint,
        assignment?.ProviderScopeFingerprint ?? operation.ProviderScopeFingerprint);

    private async Task<bool> ScopeMatchesAssignmentAsync(
        AzureProviderOperation operation,
        AzureProviderResourceAssignment assignment,
        CancellationToken cancellationToken)
    {
        if (string.Equals(assignment.ProviderScopeFingerprint, operation.ProviderScopeFingerprint, StringComparison.Ordinal))
            return true;
        if (operation.ProviderScopeFingerprint is null || _assignmentStore is null)
            return false;
        try
        {
            return await _assignmentStore.HasRebindLineageAsync(
                operation.WorkspaceId,
                assignment.Id,
                operation.ProviderScopeFingerprint,
                assignment.ProviderScopeFingerprint,
                cancellationToken);
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private async Task<AzureProviderResourceAssignment?> LoadAssignmentAsync(
        AzureProviderOperation operation,
        CancellationToken cancellationToken)
    {
        if (operation.ProviderAssignmentId is null)
        {
            // Unbound legacy rows are held by RevalidateCommercialAsync so they can be
            // explicitly repaired. Once the durable organization/instance lifecycle binding is
            // present, however, a missing assignment is corruption and must not reach
            // CreateExecutionContext or the provider runner.
            if (operation.OrganizationId is { } organizationId && organizationId != Guid.Empty &&
                operation.InstanceId is { } instanceId && instanceId != Guid.Empty &&
                operation.LifecycleAction is not null)
                throw new InvalidOperationException("The provider assignment binding is unavailable.");

            return null;
        }
        if (_assignmentStore is null)
            return null;

        var assignment = await _assignmentStore.GetAsync(
            operation.WorkspaceId,
            operation.ProviderAssignmentId.Value,
            cancellationToken);
        if (assignment is null ||
            assignment.Id != operation.ProviderAssignmentId ||
            assignment.WorkspaceId != operation.WorkspaceId ||
            assignment.OrganizationId != operation.OrganizationId ||
            assignment.InstanceId != operation.InstanceId ||
            !await ScopeMatchesAssignmentAsync(operation, assignment, cancellationToken) ||
            !string.Equals(assignment.WorkloadName, operation.TargetKey, StringComparison.OrdinalIgnoreCase) ||
            (assignment.State == AzureProviderAssignmentState.Deleted ||
             operation.Action == AzureProviderOperationAction.Delete &&
             operation.Phase == AzureProviderOperationPhase.CleanupVerified) &&
            !AzureProviderDeleteRecoverySupport.IsVerifiedCleanupEligible(operation, assignment) ||
            operation.Action != AzureProviderOperationAction.Delete &&
            assignment.State is AzureProviderAssignmentState.Unknown or AzureProviderAssignmentState.Deleting)
            throw new InvalidOperationException("The provider assignment binding is invalid.");
        return assignment;
    }

    private sealed class LeaseLostException : Exception;

    private sealed class RunnerExecutionException(AzureProviderOperation operation, Exception cause) : Exception(cause.Message, cause)
    {
        public AzureProviderOperation Operation { get; } = operation;
        public Exception Cause { get; } = cause;
    }

    internal static void ValidateExecutionRequest(AzureProviderExecutionRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (request.Operation is null)
            throw new ArgumentNullException(nameof(request.Operation));
        if (request.Plan is null)
            throw new ArgumentNullException(nameof(request.Plan));

        var operation = AzureProviderOperationValidation.Normalize(request.Operation);
        var plan = request.Plan;
        if (!IsFingerprint(plan.Fingerprint) || !string.Equals(plan.Fingerprint, operation.PlanFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The provider plan fingerprint does not match the operation request.", nameof(request));
        if (string.IsNullOrWhiteSpace(plan.WorkloadName) || plan.WorkloadName.Length is < 3 or > 16 ||
            !char.IsAsciiLetterOrDigit(plan.WorkloadName[0]) || !char.IsAsciiLetterOrDigit(plan.WorkloadName[^1]) ||
            plan.WorkloadName.Any(x => !char.IsAsciiLetterOrDigit(x) && x != '-'))
            throw new ArgumentException("The provider plan workload name is required.", nameof(request));
        if (!string.Equals(plan.WorkloadName, operation.TargetKey, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The provider plan workload name does not match the operation target.", nameof(request));
        if (!string.Equals(plan.Location, operation.Location, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.ElsaVersion, operation.ElsaVersion, StringComparison.Ordinal) ||
            !string.Equals(plan.ReleaseLine, operation.ReleaseLine, StringComparison.Ordinal) ||
            !string.Equals(plan.Topology, operation.Topology, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.Isolation, operation.Isolation, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.ImageRepository, operation.ImageRepository, StringComparison.Ordinal) ||
            !string.Equals($"sha256:{plan.ImageDigest}", operation.ImageDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.ReleaseManifestDigest, operation.ReleaseManifestDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.ReleaseManifestSignatureDigest, operation.ReleaseManifestSignatureDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.ReleaseManifestReference, operation.ReleaseManifestReference, StringComparison.Ordinal) ||
            !string.Equals(plan.ReleaseManifestSignatureReference, operation.ReleaseManifestSignatureReference, StringComparison.Ordinal) ||
            !string.Equals(plan.SqlWorkflowPackageVersion, operation.SqlWorkflowPackageVersion, StringComparison.Ordinal) ||
            !string.Equals(plan.SqlQuartzPackageVersion, operation.SqlQuartzPackageVersion, StringComparison.Ordinal) ||
            plan.Capacity != operation.Capacity ||
            plan.ManagedHandoff != operation.ManagedHandoff ||
            !SecretReferencesMatch(plan.SecretReferences, operation.SecretReferences))
            throw new ArgumentException("The provider plan does not match the operation request.", nameof(request));

        if (!AzureProviderOperationValidation.IsSafeImmutableEvidenceReference(plan.ReleaseManifestReference, plan.ReleaseManifestDigest) ||
            !AzureProviderOperationValidation.IsSafeImmutableEvidenceReference(plan.ReleaseManifestSignatureReference, plan.ReleaseManifestSignatureDigest))
            throw new ArgumentException("Provider evidence references must be safe immutable locators.", nameof(request));
        if (!IsSha256Digest(plan.ReleaseManifestDigest) || !IsSha256Digest(plan.ReleaseManifestSignatureDigest))
            throw new ArgumentException("The provider plan must include verified release-manifest digests.", nameof(request));
        if (string.IsNullOrWhiteSpace(plan.ImageDigest) || plan.ImageDigest.Length != 64 || !plan.ImageDigest.All(Uri.IsHexDigit))
            throw new ArgumentException("The provider plan image digest must be exactly 64 hexadecimal characters.", nameof(request));
        if (!string.Equals(
                plan.ImageRepository,
                AzureWorkloadPlanTranslator.SupportedRepository,
                StringComparison.Ordinal))
            throw new ArgumentException("The provider plan image must use the governed Azure repository.", nameof(request));
        if (!AzureProviderOperationValidation.IsSafePackageVersion(plan.SqlWorkflowPackageVersion) ||
            !AzureProviderOperationValidation.IsSafePackageVersion(plan.SqlQuartzPackageVersion))
            throw new ArgumentException("The provider plan must include safe release package metadata.", nameof(request));

        if (!AzureProviderOperationValidation.IsSafeSecretReferences(plan.SecretReferences))
            throw new ArgumentException("Secret references must be safe provider locators.", nameof(request));
    }

    private static AzureProviderExecutionOutcome MapOutcome(AzureProviderOperationStatus status) =>
        status switch
        {
            AzureProviderOperationStatus.Succeeded => AzureProviderExecutionOutcome.Succeeded,
            AzureProviderOperationStatus.Failed or AzureProviderOperationStatus.Cancelled => AzureProviderExecutionOutcome.Failed,
            AzureProviderOperationStatus.RecoveryRequired => AzureProviderExecutionOutcome.RecoveryRequired,
            _ => AzureProviderExecutionOutcome.InProgress
        };

    private static AzureProviderExecutionResult Result(AzureProviderOperation operation, AzureProviderExecutionOutcome outcome, string code, string message)
    {
        AzureProviderOperationValidation.ValidateCode(code);
        AzureProviderOperationValidation.ValidateMessage(message);
        return new(operation, outcome, code, message);
    }

    private static AzureProviderExecutionResult ResultForObservedState(
        AzureProviderOperation operation,
        string inProgressCode,
        string inProgressMessage) => operation.Status switch
        {
            AzureProviderOperationStatus.Succeeded =>
                Result(operation, AzureProviderExecutionOutcome.NoOp, "azure.operation.no-op", "The Azure workload already matches the requested plan."),
            AzureProviderOperationStatus.Failed or AzureProviderOperationStatus.Cancelled =>
                Result(operation, AzureProviderExecutionOutcome.Failed, "azure.operation.terminal", "The Azure operation is terminal and requires a new idempotency key."),
            AzureProviderOperationStatus.RecoveryRequired =>
                Result(operation, AzureProviderExecutionOutcome.RecoveryRequired, "azure.operation.recovery-required", "The Azure operation requires explicit provider recovery."),
            _ => Result(operation, AzureProviderExecutionOutcome.InProgress, inProgressCode, inProgressMessage)
        };

    private static bool IsSha256Digest(string? value) =>
        value is not null && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
        value.Length == "sha256:".Length + 64 && value["sha256:".Length..].All(Uri.IsHexDigit);

    private static bool SecretReferencesMatch(
        IReadOnlyDictionary<string, string>? planReferences,
        IReadOnlyDictionary<string, string>? operationReferences) =>
        planReferences is not null && operationReferences is not null &&
        planReferences.Count == operationReferences.Count &&
        planReferences.All(pair => string.Equals(pair.Key, pair.Key.Trim().ToLowerInvariant(), StringComparison.Ordinal)) &&
        operationReferences.All(pair =>
            planReferences.TryGetValue(pair.Key, out var value) &&
            string.Equals(value, pair.Value, StringComparison.Ordinal));

    private static bool IsFingerprint(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static TimeSpan DefaultHeartbeatInterval(TimeSpan leaseDuration)
    {
        var ticks = Math.Min(TimeSpan.FromMinutes(1).Ticks, leaseDuration.Ticks / 3);
        return TimeSpan.FromTicks(Math.Min(Math.Max(1, ticks), leaseDuration.Ticks - 1));
    }

    private static AzureWorkloadPlan CopySafePlan(AzureWorkloadPlan plan)
    {
        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in plan.SecretReferences)
        {
            if (!references.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException("Secret references must have unique keys.", nameof(plan));
        }

        return plan with { SecretReferences = new ReadOnlyDictionary<string, string>(references) };
    }
}
