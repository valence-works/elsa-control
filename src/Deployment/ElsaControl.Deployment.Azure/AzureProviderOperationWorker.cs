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
    TimeProvider? timeProvider = null)
{
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

            await executor.ExecuteAsync(request, cancellationToken);
            processed++;
        }

        return processed;
    }
}
