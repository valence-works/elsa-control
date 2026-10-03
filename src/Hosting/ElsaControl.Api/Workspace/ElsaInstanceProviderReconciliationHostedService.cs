using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Abstractions.Instances;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Workspace;

/// <summary>
/// Polls durable lifecycle reservations after provider submission. Provider
/// operation workers own mutation; this service submits/replays safe provider
/// work and applies the existing provider-neutral reconciliation projection.
/// </summary>
public sealed class ElsaInstanceProviderReconciliationHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<ElsaInstanceLifecycleWorkerOptions> options,
    ILogger<ElsaInstanceProviderReconciliationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ElsaInstanceLifecycleHostedService.NormalizePollInterval(options.Value.PollInterval));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Cancellation from a provider or reconciliation dependency is
                // not a retryable provider failure and must reach the host.
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Managed Elsa instance provider reconciliation failed.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Processes one durable snapshot without waiting on the scheduling timer.
    /// This is also the deterministic seam for proving cancellation and replay
    /// behavior independently of hosted-service timing.
    /// </summary>
    internal async Task ProcessPendingAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var pending = scope.ServiceProvider.GetRequiredService<IElsaInstanceProviderPendingOperationStore>();
        var provider = scope.ServiceProvider.GetRequiredService<IElsaInstanceProviderSubmissionPort>();
        var recoveryProvider = scope.ServiceProvider.GetService<IElsaInstanceProviderRecoveryPort>();
        var submissionStore = scope.ServiceProvider.GetRequiredService<IElsaInstanceProviderSubmissionStore>();
        var reconciler = scope.ServiceProvider.GetRequiredService<IElsaInstanceProviderReconciliationService>();
        var commercialGate = scope.ServiceProvider.GetService<IElsaInstanceCommercialGate>();
        var entitlementHoldStore = scope.ServiceProvider.GetService<IElsaInstanceEntitlementHoldStore>();
        await reconciler.AdvanceDueHumanRequiredClocksAsync(stoppingToken);
        var operations = await pending.ListPendingProviderOperationsAsync(64, stoppingToken);
        foreach (var operation in operations)
        {
            stoppingToken.ThrowIfCancellationRequested();
            await ProcessOperationAsync(
                operation,
                provider,
                recoveryProvider,
                submissionStore,
                reconciler,
                commercialGate,
                entitlementHoldStore,
                stoppingToken);
        }
    }

    private async Task ProcessOperationAsync(
        ElsaInstanceProviderPendingOperation operation,
        IElsaInstanceProviderSubmissionPort provider,
        IElsaInstanceProviderRecoveryPort? recoveryProvider,
        IElsaInstanceProviderSubmissionStore submissionStore,
        IElsaInstanceProviderReconciliationService reconciler,
        IElsaInstanceCommercialGate? commercialGate,
        IElsaInstanceEntitlementHoldStore? entitlementHoldStore,
        CancellationToken stoppingToken)
    {
        if (operation.HandoffInvalid)
        {
            logger.LogWarning("Managed Elsa provider hand-off metadata is invalid for pending operation {OperationId}.", operation.OperationId);
            return;
        }

        if (operation.Submission is { } pendingSubmission)
        {
            try
            {
                if (pendingSubmission.WorkspaceId != operation.WorkspaceId ||
                    pendingSubmission.OperationId != operation.OperationId ||
                    pendingSubmission.OperationAction == ElsaInstanceOperationAction.Delete ||
                    pendingSubmission.Plan is null || pendingSubmission.DeploymentTarget is null)
                {
                    logger.LogWarning("Managed Elsa provider submission is invalid for pending operation {OperationId}.", operation.OperationId);
                    return;
                }
                pendingSubmission.Validate();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                logger.LogWarning("Managed Elsa provider submission is invalid for pending operation {OperationId}.", operation.OperationId);
                return;
            }
        }

        if (operation.Recovery is { } recovery)
        {
            if (operation.Submission is not { } recoverySubmission || recoveryProvider is null)
            {
                // Recovery is an explicit provider boundary. Never downgrade a
                // malformed or unavailable recovery envelope into normal replay.
                logger.LogWarning(
                    "Managed Elsa instance provider recovery is awaiting a valid recovery boundary for operation {OperationId}.",
                    operation.OperationId);
                return;
            }

            // Delete has its own cleanup worker and is never a provider recovery
            // submission. Keep an anomalous delete envelope on that path rather
            // than allowing a recovery adapter to treat it as an apply.
            if (recoverySubmission.OperationAction == ElsaInstanceOperationAction.Delete ||
                recoverySubmission.DesiredLifecycle == ElsaDesiredLifecycle.Deleting)
            {
                logger.LogWarning(
                    "Managed Elsa instance provider delete remains on the cleanup path for operation {OperationId}.",
                    operation.OperationId);
                return;
            }

            var recoveryRequest = new ElsaInstanceProviderRecoveryRequest(recoverySubmission, recovery);
            try
            {
                recoveryRequest.Validate();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                logger.LogWarning(
                    "Managed Elsa instance provider recovery is awaiting a valid recovery boundary for operation {OperationId}.",
                    operation.OperationId);
                return;
            }

            if (!await AuthorizeSubmissionAsync(
                    operation,
                    recoverySubmission,
                    commercialGate,
                    entitlementHoldStore,
                    stoppingToken))
                return;

            try
            {
                var recovered = await recoveryProvider.RecoverAsync(recoveryRequest, stoppingToken);
                recovered.Validate();

                // The explicit recovery port may have resumed an already accepted
                // provider operation, but the lifecycle store still has to cross
                // its durable hand-off boundary before reconciliation can load the
                // RecoveryRequired target. A safe provider code is the correlation
                // marker here; provider operation identity remains in the provider
                // store and is bound by the recovery envelope.
                if (recovered.Outcome == ElsaInstanceProviderRecoveryOutcome.Rejected)
                {
                    logger.LogWarning(
                        "Managed Elsa instance provider recovery was rejected and is awaiting explicit retry for operation {OperationId}.",
                        operation.OperationId);
                    return;
                }

                await submissionStore.CommitProviderSubmissionAsync(new(
                    recoverySubmission.WorkspaceId,
                    recoverySubmission.InstanceId,
                    recoverySubmission.OperationId,
                    recoverySubmission.AttemptNumber,
                    recovered.Code,
                    DateTimeOffset.UtcNow,
                    recoverySubmission.PlacementAssignmentId), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Recovery or its durable hand-off may have failed. Do not
                // reconcile an operation whose lifecycle target is still Queued;
                // the next scan retries this exact recovery envelope.
                logger.LogWarning(
                    "Managed Elsa instance provider recovery is awaiting its durable hand-off for operation {OperationId}.",
                    operation.OperationId);
                return;
            }
        }

        // A process can stop after the durable provider operation was created but
        // before the lifecycle hand-off marker was committed. Replaying the
        // reconstructed submission closes that boundary; the provider's operation
        // identity makes the replay exactly-once.
        if (operation.Recovery is null && operation.Submission is { } submission)
        {
            // Revalidate the durable hand-off immediately before replay. A legacy
            // row without an organization binding remains observable, but its
            // provider mutation is rejected by the provider adapter; new
            // managed-instance submissions always carry both identities.
            if (!await AuthorizeSubmissionAsync(
                    operation,
                    submission,
                    commercialGate,
                    entitlementHoldStore,
                    stoppingToken))
                return;
            try
            {
                var submitted = await provider.SubmitAsync(submission, stoppingToken);
                submitted.Validate();
                await submissionStore.CommitProviderSubmissionAsync(new(
                    submission.WorkspaceId,
                    submission.InstanceId,
                    submission.OperationId,
                    submission.AttemptNumber,
                    submitted.CorrelationId,
                    DateTimeOffset.UtcNow,
                    submitted.PlacementAssignmentId), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Cancellation must never be converted into a retry warning.
                throw;
            }
            catch (ElsaInstanceProviderSubmissionException exception)
                when (exception.Kind == ElsaInstanceProviderSubmissionFailureKind.Rejected)
            {
                // The provider proved that no durable hand-off occurred. Leave the
                // lifecycle reservation queued so a later scan can replay it after
                // configuration is corrected, and do not reconcile remote work that
                // cannot exist.
                logger.LogWarning(
                    "Managed Elsa instance provider hand-off was rejected and is awaiting retry for operation {OperationId}.",
                    operation.OperationId);
                return;
            }
            catch (Exception)
            {
                // The provider may have accepted the idempotent replay before the
                // client observed an error. Always continue to reconciliation so
                // observation can discover and converge that remote work. Keep
                // provider exception text out of logs because adapters may receive
                // sensitive SDK diagnostics.
                logger.LogWarning(
                    "Managed Elsa instance provider hand-off is awaiting retry for operation {OperationId}.",
                    operation.OperationId);
            }
        }

        try
        {
            var result = await reconciler.ReconcileAsync(operation.WorkspaceId, operation.OperationId, stoppingToken);
            logger.LogInformation(
                "Managed Elsa provider reconciliation completed for workspace {WorkspaceId}, instance {InstanceId}, operation {OperationId}, outcome {Outcome}, diagnostic {DiagnosticCode}.",
                operation.WorkspaceId,
                result.Projection.InstanceId,
                operation.OperationId,
                result.Outcome,
                ManagedLifecycleOperationalHealthDiagnosticCodes.IsSafe(result.DiagnosticCode)
                    ? result.DiagnosticCode
                    : ManagedLifecycleOperationalHealthDiagnosticCodes.Unknown);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (KeyNotFoundException)
        {
            // A concurrent terminal/deletion transition removed the recovery target
            // before this read; the next scan will observe the durable outcome.
        }
        catch (ElsaInstanceLifecycleConflictException)
        {
            // Another reconciler won the compare-and-set boundary.
        }
        catch (Exception)
        {
            // Reconciliation is retried on the next poll. Keep provider exception
            // text out of logs because adapters may receive sensitive diagnostics.
            logger.LogWarning(
                "Managed Elsa instance provider reconciliation is awaiting retry for operation {OperationId}.",
                operation.OperationId);
        }
    }

    private static async Task<bool> AuthorizeSubmissionAsync(
        ElsaInstanceProviderPendingOperation operation,
        ElsaInstanceProviderSubmission submission,
        IElsaInstanceCommercialGate? commercialGate,
        IElsaInstanceEntitlementHoldStore? entitlementHoldStore,
        CancellationToken cancellationToken)
    {
        if (submission.OrganizationId is not { } organizationId ||
            submission.InstanceId == Guid.Empty ||
            submission.DesiredLifecycle == ElsaDesiredLifecycle.Deleting)
            return true;

        if (entitlementHoldStore is not null)
        {
            var authorization = await entitlementHoldStore.AuthorizeProviderSubmissionAsync(
                operation.WorkspaceId,
                submission.InstanceId,
                operation.OperationId,
                DateTimeOffset.UtcNow,
                cancellationToken);
            return authorization.Allowed;
        }

        if (commercialGate is not null)
        {
            var commercialDecision = await commercialGate.EvaluateAsync(
                organizationId,
                submission.OperationAction ?? ElsaInstanceOperationAction.Reconcile,
                cancellationToken: cancellationToken);
            return commercialDecision.Allowed;
        }

        return true;
    }
}
