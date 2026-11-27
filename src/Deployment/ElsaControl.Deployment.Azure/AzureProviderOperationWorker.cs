using System.Collections.Concurrent;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Drains accepted Azure operations. The operation store is the queue: claiming
/// remains the executor's compare-and-set boundary, so multiple hosted workers can safely poll
/// the same database without introducing a second, non-durable queue. Recovery-required rows stay
/// durable but are not automatically replayed: explicit provider-state observation must first
/// establish which postcondition committed before another remote mutation is allowed.
/// </summary>
public sealed class AzureProviderOperationWorker(
    IAzureProviderOperationStore store,
    AzureProviderExecutor executor,
    IAzureProviderPlanSource planSource,
    TimeProvider? timeProvider = null,
    IAzurePulumiProvisioningDispatcher? pulumi = null,
    IElsaInstanceProvisioningBackendStore? backends = null,
    IElsaInstanceCommercialGate? commercialGate = null,
    IAzurePulumiReadinessVerifier? readiness = null)
{
    public const string DispatchWorkerId = "pulumi-dispatch";
    private readonly ConcurrentDictionary<Guid, byte> _dispatchedPulumi = new();

    // Keep the executor-only construction seam available to hosts that predate the persisted
    // plan source; the default source still enforces the same admission checks.
    public AzureProviderOperationWorker(
        IAzureProviderOperationStore store,
        AzureProviderExecutor executor,
        TimeProvider timeProvider)
        : this(store, executor, new PersistedAzureProviderPlanSource(), timeProvider)
    {
    }

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<int> ProcessOnceAsync(
        int batchSize = 10,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        var now = _timeProvider.GetUtcNow();
        await store.RecoverStaleAsync(now, cancellationToken);
        var operations = await store.ListRunnableAsync(now, batchSize, cancellationToken);
        var processed = 0;
        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation.Status == AzureProviderOperationStatus.RecoveryRequired)
                continue;
            AzureWorkloadPlan? plan;
            try
            {
                plan = planSource.Resolve(operation);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                plan = null;
            }
            if (plan is null)
            {
                // A malformed or legacy persisted plan must not remain runnable forever. The
                // store performs a versioned compare-and-set and records only stable, value-free
                // diagnostics; terminal Failed is deliberately excluded from the poll query.
                await store.MarkUnrestorableAsync(
                    operation.WorkspaceId,
                    operation.Id,
                    now,
                    operation.Version,
                    cancellationToken);
                continue;
            }

            var request = new AzureProviderExecutionRequest(
                AzureProviderOperationService.CreateOperationRequest(operation),
                plan);
            try
            {
                // Keep malformed persisted inputs on the pre-execution side of the executor's
                // claim boundary. ExecuteAsync performs this check too, but the worker must not
                // classify an exception from the asynchronous execution path as an unrestorable
                // plan after a lease or remote step may already have been started.
                AzureProviderExecutor.ValidateExecutionRequest(request, executor.ProviderScope);
                AzureProviderOperationValidation.ValidateReferences(operation.Resources);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                // Validation is performed before the executor claims the operation or invokes
                // the provider. Persisted data that fails a defense-in-depth check must take the
                // same versioned terminal path as an unrestorable plan, so it cannot starve the
                // queue on every polling interval.
                await store.MarkUnrestorableAsync(
                    operation.WorkspaceId,
                    operation.Id,
                    now,
                    operation.Version,
                    cancellationToken);
                continue;
            }

            var backend = await ReadBackendAsync(operation, cancellationToken).ConfigureAwait(false);
            if (backend == ElsaInstanceProvisioningBackend.Unknown)
            {
                await FailClosedAsync(operation, now, cancellationToken).ConfigureAwait(false);
                processed++;
                continue;
            }

            if (backend == ElsaInstanceProvisioningBackend.Pulumi)
            {
                var reachedReady = operation.InstanceId is { } instanceId && instanceId != Guid.Empty
                    ? await store.HasReachedReadyAsync(operation.WorkspaceId, instanceId, cancellationToken)
                        .ConfigureAwait(false)
                    : true;
                if (!AzurePulumiAllowedOperations.Allows(operation, reachedReady))
                {
                    await FailClosedAsync(
                            operation,
                            now,
                            cancellationToken,
                            ManagedElsaReasonCodeCatalog.ProviderPulumiOperationUnsupported)
                        .ConfigureAwait(false);
                    processed++;
                    continue;
                }

                if (_dispatchedPulumi.ContainsKey(operation.Id)
                    && AzureProviderOperationPhaseOrdering.Compare(
                        operation.Phase,
                        AzureProviderOperationPhase.WorkloadReady) < 0)
                {
                    processed++;
                    continue;
                }

                if (operation.Action != AzureProviderOperationAction.Delete
                    && AzureProviderOperationPhaseOrdering.Compare(
                        operation.Phase,
                        AzureProviderOperationPhase.WorkloadReady) >= 0)
                {
                    if (readiness is null)
                    {
                        await FailClosedAsync(operation, now, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await readiness.VerifyAsync(operation, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await DispatchPulumiAsync(operation, plan, now, cancellationToken).ConfigureAwait(false);
                }

                processed++;
                continue;
            }

            await executor.ExecuteAsync(request, cancellationToken);
            processed++;
        }

        return processed;
    }

    private async Task<ElsaInstanceProvisioningBackend> ReadBackendAsync(
        AzureProviderOperation operation,
        CancellationToken cancellationToken)
    {
        if (backends is null || operation.InstanceId is not { } instanceId || instanceId == Guid.Empty)
            return ElsaInstanceProvisioningBackend.Bicep;
        return await backends.GetBackendAsync(operation.WorkspaceId, instanceId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DispatchPulumiAsync(
        AzureProviderOperation operation,
        AzureWorkloadPlan plan,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (operation.Action != AzureProviderOperationAction.Delete)
        {
            if (IsIdentityBindingMissing(operation))
            {
                await ParkAsync(
                        operation,
                        now,
                        AzureProviderOperationStatus.RecoveryRequired,
                        ManagedElsaReasonCodeCatalog.ProviderIdentityBindingMissing,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (commercialGate is not null)
            {
                var decision = await commercialGate
                    .EvaluateAsync(
                        operation.OrganizationId!.Value,
                        operation.LifecycleAction!.Value,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (!decision.Allowed)
                {
                    await ParkAsync(
                            operation,
                            now,
                            AzureProviderOperationStatus.EntitlementHeld,
                            decision.Code,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
            }
        }

        AzurePulumiDispatchResult? dispatched = null;
        if (pulumi is not null)
            dispatched = await pulumi.DispatchAsync(operation, plan, cancellationToken).ConfigureAwait(false);

        if (dispatched is { Dispatched: true })
        {
            _dispatchedPulumi.TryAdd(operation.Id, 0);
            return;
        }

        await FailClosedAsync(operation, now, cancellationToken).ConfigureAwait(false);
    }

    private Task FailClosedAsync(
        AzureProviderOperation operation,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string? code = null) =>
        ParkAsync(
            operation,
            now,
            AzureProviderOperationStatus.Failed,
            code ?? ManagedElsaReasonCodeCatalog.ProviderDispatchFailed,
            cancellationToken,
            stampFromPulumi: code is null);

    private async Task ParkAsync(
        AzureProviderOperation operation,
        DateTimeOffset now,
        AzureProviderOperationStatus status,
        string code,
        CancellationToken cancellationToken,
        bool stampFromPulumi = false)
    {
        var leaseToken = Guid.NewGuid().ToString("N");
        var claimed = await store.ClaimAsync(
                operation.WorkspaceId,
                operation.Id,
                DispatchWorkerId,
                leaseToken,
                TimeSpan.FromMinutes(1),
                now,
                operation.Version,
                cancellationToken)
            .ConfigureAwait(false);
        if (claimed is null)
            return;

        var finalized = await store.FinalizeAsync(
                operation.WorkspaceId,
                operation.Id,
                leaseToken,
                status,
                code,
                _timeProvider.GetUtcNow(),
                claimed.Version,
                cancellationToken)
            .ConfigureAwait(false);
        if (finalized is null)
            return;

        if (!stampFromPulumi)
            return;

        await store.RecordArmObservationClockAsync(
                operation.WorkspaceId,
                operation.Id,
                _timeProvider.GetUtcNow(),
                0,
                code,
                AzureTransientArmFailure.FromPulumi(null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsIdentityBindingMissing(AzureProviderOperation operation) =>
        operation.OrganizationId is not { } organizationId || organizationId == Guid.Empty ||
        operation.InstanceId is not { } instanceId || instanceId == Guid.Empty ||
        operation.LifecycleAction is not { };
}
