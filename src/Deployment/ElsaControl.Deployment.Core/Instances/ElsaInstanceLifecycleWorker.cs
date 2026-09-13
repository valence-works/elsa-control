using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.Deployment.Core.Telemetry;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Resumes accepted instance lifecycle work after the request transaction commits.
/// It resolves a safe immutable plan and asks its store to atomically persist that
/// plan, queue a run and reserve a target before optionally handing it to the
/// provider-neutral submission port.
/// </summary>
public sealed class ElsaInstanceLifecycleWorker(
    IElsaInstanceLifecycleWorkerStore store,
    IElsaInstancePlanResolver resolver,
    TimeProvider? timeProvider = null,
    IElsaInstanceProviderSubmissionPort? provider = null,
    IElsaInstanceProviderSubmissionStore? submissionStore = null,
    IElsaInstanceCommercialGate? commercialGate = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IElsaInstanceProviderSubmissionPort? _provider = provider;
    private readonly IElsaInstanceProviderSubmissionStore? _submissionStore = submissionStore;
    private readonly IElsaInstanceCommercialGate? _commercialGate = commercialGate;

    public async Task<ElsaInstanceLifecycleWorkerBatchResult> ProcessAvailableAsync(
        string workerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("Lifecycle worker identity is required.", nameof(workerId));

        var results = new List<ElsaInstanceLifecycleWorkerResult>();
        var providerInvocations = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await store.TryClaimNextAsync(workerId.Trim(), _timeProvider.GetUtcNow(), cancellationToken);
            if (item is null)
                break;

            // A malformed item is isolated to its operation. The queue must continue
            // so one corrupt record cannot starve later accepted work.
            using var telemetry = ManagedLifecycleTelemetry.StartOperation(
                ManagedLifecycleTelemetry.WorkerActivityName,
                item.Operation.Action,
                item.Instance.DesiredLifecycle,
                item.Instance.ObservedLifecycle,
                item.Instance.Health,
                item.Operation.State,
                item.Instance.OrganizationId,
                item.Instance.WorkspaceId,
                item.Instance.Id,
                item.Operation.Id,
                item.Operation.AttemptNumber);
            (ElsaInstanceLifecycleWorkerResult Result, int ProviderInvocations) processed;
            try
            {
                processed = await ProcessClaimedAsync(item, workerId.Trim(), cancellationToken, telemetry);
                var final = processed.Result;
                if (final.Operation.State != item.Operation.State)
                    telemetry.RecordTransition(
                        final.Instance.DesiredLifecycle,
                        final.Instance.ObservedLifecycle,
                        final.Instance.Health,
                        final.Operation.State,
                        final.FailureCode);
                if (final.Outcome is ElsaInstanceLifecycleWorkerOutcome.Failed or ElsaInstanceLifecycleWorkerOutcome.Conflict)
                    telemetry.RecordError(
                        final.Instance.DesiredLifecycle,
                        final.Instance.ObservedLifecycle,
                        final.Instance.Health,
                        final.Operation.State,
                        final.FailureCode);
                telemetry.Complete(
                    final.Outcome.ToString(),
                    final.Instance.DesiredLifecycle,
                    final.Instance.ObservedLifecycle,
                    final.Instance.Health,
                    final.Operation.State,
                    final.FailureCode);
            }
            catch (OperationCanceledException)
            {
                telemetry.RecordError(
                    item.Instance.DesiredLifecycle,
                    item.Instance.ObservedLifecycle,
                    item.Instance.Health,
                    item.Operation.State,
                    "lifecycle.worker.cancelled");
                throw;
            }
            catch
            {
                telemetry.RecordError(
                    item.Instance.DesiredLifecycle,
                    item.Instance.ObservedLifecycle,
                    item.Instance.Health,
                    item.Operation.State,
                    "lifecycle.worker.failed");
                throw;
            }
            results.Add(processed.Result);
            providerInvocations += processed.ProviderInvocations;
        }

        return new ElsaInstanceLifecycleWorkerBatchResult(results, providerInvocations);
    }

    private async Task<(ElsaInstanceLifecycleWorkerResult Result, int ProviderInvocations)> ProcessClaimedAsync(
        ElsaInstanceLifecycleWorkItem item,
        string workerId,
        CancellationToken cancellationToken,
        ManagedLifecycleTelemetry.ManagedLifecycleTelemetryOperation telemetry)
    {
        try
        {
            // GetAsync returning null (catalog reconstruct, preview digest, or
            // managed target/audit) used to throw here and become opaque
            // resolution.invalid. After #413 that path is input-unavailable.
            // The live residual after promote was AttachResolvedPlan throwing
            // when replacing an already-projected pin release.
            if (item.Resolution is null)
                return (await FailAsync(item, workerId, "resolution.input-unavailable", cancellationToken), 0);

            try
            {
                item.Validate();
            }
            catch (InvalidOperationException)
            {
                return (await FailAsync(item, workerId, "resolution.work-item-invalid", cancellationToken), 0);
            }

            ElsaInstancePlanResolutionResult resolution;
            try
            {
                resolution = await resolver.ResolveAsync(item.Resolution.PlanRequest, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return (await FailAsync(item, workerId, "resolution.unexpected", cancellationToken), 0);
            }

            if (!resolution.Succeeded)
                return (await FailAsync(item, workerId, FirstSafeFindingCode(resolution.Findings) ?? "resolution.failed", cancellationToken), 0);

            if (resolution.Plan is null || resolution.Reference is null || resolution.CurrentResolvedRelease is null)
                return (await FailAsync(item, workerId, "resolution.invalid", cancellationToken), 0);

            string planJson;
            string contentHash;
            try
            {
                planJson = ResolvedElsaApplicationPlanSerialization.Serialize(resolution.Plan);
                contentHash = ResolvedElsaApplicationPlanSerialization.ComputeContentHash(resolution.Plan);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return (await FailAsync(item, workerId, "resolution.commit-invalid", cancellationToken), 0);
            }

            if (!string.Equals(contentHash, resolution.Reference.ContentHash, StringComparison.Ordinal) ||
                !Equals(resolution.CurrentResolvedRelease.PlanReference, resolution.Reference) ||
                !string.Equals(resolution.Reference.PlanId, item.Resolution.PlanRequest.PlanId, StringComparison.Ordinal) ||
                !string.Equals(resolution.Reference.PlanUri, item.Resolution.PlanRequest.PlanUri, StringComparison.Ordinal))
                return (await FailAsync(item, workerId, "resolution.invalid", cancellationToken), 0);

            if (item.Resolution.ExpectedPlanDigest is { } reviewedDigest &&
                !string.Equals(contentHash, reviewedDigest, StringComparison.Ordinal))
                return (await FailAsync(item, workerId, "provisioning.plan-changed", cancellationToken), 0);

            ElsaInstance resolvedInstance;
            try
            {
                resolvedInstance = item.Instance.AttachResolvedPlan(
                    resolution.Reference,
                    resolution.CurrentResolvedRelease);
            }
            catch (ArgumentException)
            {
                return (await FailAsync(item, workerId, "resolution.plan-replace-invalid", cancellationToken), 0);
            }

            var queuedOperation = item.Operation.TransitionTo(
                ElsaControl.Deployment.Abstractions.Instances.ElsaInstanceOperationState.Queued);
            ElsaInstanceLifecycleResolutionCommit commit;
            try
            {
                commit = new ElsaInstanceLifecycleResolutionCommit(
                    item.Outbox.WorkspaceId,
                    item.Outbox.InstanceId,
                    item.Outbox.OperationId,
                    item.Outbox.Id,
                    item.Outbox.RequestHash,
                    workerId,
                    queuedOperation,
                    resolvedInstance,
                    new ElsaInstanceLifecycleResolvedPlan(resolution.Reference, planJson),
                    item.Resolution.DeploymentTarget,
                    _timeProvider.GetUtcNow(),
                    item.LeaseToken,
                    item.LeaseVersion);
            }
            catch (InvalidOperationException)
            {
                return (await FailAsync(item, workerId, "resolution.commit-invalid", cancellationToken), 0);
            }

            var result = await store.CommitResolvedAsync(commit, cancellationToken);
            if (_provider is null || result.Outcome is not ElsaInstanceLifecycleWorkerOutcome.Queued)
                return (result, 0);

            if (store is IElsaInstanceEntitlementHoldStore entitlementHoldStore)
            {
                var authorization = await entitlementHoldStore.AuthorizeProviderSubmissionAsync(
                        item.Outbox.WorkspaceId,
                        item.Outbox.InstanceId,
                        item.Outbox.OperationId,
                        _timeProvider.GetUtcNow(),
                        cancellationToken);
                if (!authorization.Allowed)
                {
                    // Authorization already performed the durable Queued -> Held
                    // CAS. Reflect that result locally without opening a second
                    // transaction (or emitting a duplicate audit event).
                    result = result with
                    {
                        Operation = result.Operation.TransitionTo(
                            ElsaControl.Deployment.Abstractions.Instances.ElsaInstanceOperationState.EntitlementHeld),
                        FailureCode = authorization.Code,
                        FailureSummary = authorization.Summary
                    };
                    return (result, 0);
                }
            }
            else if (_commercialGate is not null)
            {
                var commercialDecision = await _commercialGate.EvaluateAsync(
                    item.Instance.OrganizationId, item.Operation.Action, cancellationToken: cancellationToken);
                if (!commercialDecision.Allowed)
                    return (result, 0);
            }

            // Submission happens only after the atomic plan/run reservation. The
            // provider seam is idempotent by operation identity, so an uncertain
            // process boundary cannot turn a replay into a second remote apply.
            var submission = new ElsaInstanceProviderSubmission(
                item.Outbox.WorkspaceId,
                item.Outbox.InstanceId,
                item.Outbox.OperationId,
                item.Operation.AttemptNumber,
                item.Instance.DesiredLifecycle,
                resolution.Plan,
                item.Resolution.DeploymentTarget,
                item.Instance.PlacementIntent.RegionCode,
                item.Instance.OrganizationId,
                item.Operation.Action,
                item.Instance.PlacementAssignmentReference?.AssignmentId ?? item.Operation.Id.ToString("D"));
            try
            {
                var submitted = await _provider.SubmitAsync(submission, cancellationToken);
                submitted.Validate();
                if (_submissionStore is not null)
                    await _submissionStore.CommitProviderSubmissionAsync(new(
                        submission.WorkspaceId,
                        submission.InstanceId,
                        submission.OperationId,
                        submission.AttemptNumber,
                        submitted.CorrelationId,
                        _timeProvider.GetUtcNow(),
                        submitted.PlacementAssignmentId), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ElsaInstanceProviderSubmissionException exception)
                when (exception.Kind == ElsaInstanceProviderSubmissionFailureKind.Rejected)
            {
                // Deterministic local/provider rejection leaves the durable reservation queued.
                // It must not be represented as an uncertain remote hand-off.
                telemetry.RecordError(
                    item.Instance.DesiredLifecycle,
                    item.Instance.ObservedLifecycle,
                    item.Instance.Health,
                    result.Operation.State,
                    "provider.submission.rejected");
            }
            catch
            {
                // The durable queued run remains the source of truth. A provider
                // worker/reconciler may retry the same correlation; if the store
                // is available, it records an uncertain hand-off for explicit
                // recovery. This failure must never roll back a committed plan or
                // create a second run.
                telemetry.RecordError(
                    item.Instance.DesiredLifecycle,
                    item.Instance.ObservedLifecycle,
                    item.Instance.Health,
                    result.Operation.State,
                    "provider.submission.uncertain");
                if (_submissionStore is not null)
                {
                    try
                    {
                        await _submissionStore.CommitProviderSubmissionAsync(new(
                            submission.WorkspaceId,
                            submission.InstanceId,
                            submission.OperationId,
                            submission.AttemptNumber,
                            "provider-submission-uncertain",
                            _timeProvider.GetUtcNow()), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Preserve the committed queued run if the recovery
                        // marker itself loses a race with another worker.
                    }
                }
            }
            return (result, 1);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ElsaInstanceLifecycleConflictException)
        {
            return (Conflict(item), 0);
        }
        catch (InvalidOperationException) when (item is not null)
        {
            return (await FailAsync(item, workerId, "resolution.commit-invalid", cancellationToken), 0);
        }
        catch (Exception) when (item is not null)
        {
            return (await FailAsync(item, workerId, "resolution.unexpected", cancellationToken), 0);
        }
    }

    private async Task<ElsaInstanceLifecycleWorkerResult> FailAsync(
        ElsaInstanceLifecycleWorkItem item,
        string workerId,
        string code,
        CancellationToken cancellationToken)
    {
        var failure = new ElsaInstanceLifecycleResolutionFailure(
            item.Outbox.WorkspaceId,
            item.Outbox.InstanceId,
            item.Outbox.OperationId,
            item.Outbox.Id,
            item.Outbox.RequestHash,
            workerId,
            code,
            FailureSummary(code),
            _timeProvider.GetUtcNow(),
            item.LeaseToken,
            item.LeaseVersion);
        try
        {
            return await store.FailResolutionAsync(failure, cancellationToken);
        }
        catch (ElsaInstanceLifecycleConflictException)
        {
            return Conflict(item);
        }
    }

    private static string FailureSummary(string code) =>
        code switch
        {
            "resolution.invalid" => "Lifecycle work item could not be resolved safely.",
            "resolution.input-unavailable" => "Lifecycle resolution input could not be reconstructed from the catalog projection.",
            "resolution.work-item-invalid" => "Lifecycle work item identity or intent hash is invalid.",
            "resolution.commit-invalid" => "Resolved plan could not be committed as a safe immutable identity.",
            "resolution.plan-replace-invalid" => "Resolved plan could not replace the instance's current release projection.",
            "resolution.unexpected" => "Lifecycle plan resolution failed unexpectedly.",
            _ => "Lifecycle plan resolution was rejected."
        };

    private static string? FirstSafeFindingCode(IReadOnlyList<ElsaInstancePlanResolutionFinding> findings)
    {
        foreach (var finding in findings)
        {
            if (IsSafeDiagnosticCode(finding.Code))
                return finding.Code;
        }

        return null;
    }

    private static bool IsSafeDiagnosticCode(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or ':');

    private static ElsaInstanceLifecycleWorkerResult Conflict(ElsaInstanceLifecycleWorkItem item) =>
        new(
            ElsaInstanceLifecycleWorkerOutcome.Conflict,
            item.Operation,
            item.Instance,
            FailureCode: "lifecycle.claim.conflict",
            FailureSummary: "Lifecycle work item ownership changed before completion.");
}
