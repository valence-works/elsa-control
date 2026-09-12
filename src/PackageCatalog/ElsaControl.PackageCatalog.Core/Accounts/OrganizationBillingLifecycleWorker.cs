namespace ElsaControl.PackageCatalog.Core.Accounts;

/// <summary>
/// One bounded lifecycle pass. The clock is captured once for state advancement
/// and cleanup completion, which keeps a run deterministic even across a minute
/// boundary. Remote cleanup is attempted only after durable retention policy has
/// queued the provider-neutral intent.
/// </summary>
public sealed class OrganizationBillingLifecycleWorker(
    IOrganizationBillingLifecycleStore store,
    TimeProvider? timeProvider = null,
    IOrganizationBillingCleanupProvider? cleanupProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IOrganizationBillingCleanupProvider? _cleanupProvider = cleanupProvider;

    public async Task<OrganizationBillingLifecycleBatchResult> ProcessAvailableAsync(
        string workerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        workerId = workerId.Trim();
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var advances = await store.AdvanceDueAsync(now, cancellationToken);
        if (_cleanupProvider is null)
            return new OrganizationBillingLifecycleBatchResult(advances, 0);

        var cleanupAttempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptNow = _timeProvider.GetUtcNow().ToUniversalTime();
            var item = await store.TryClaimCleanupAsync(workerId, attemptNow, cancellationToken);
            if (item is null)
                break;

            cleanupAttempts++;
            var outcome = OrganizationBillingCleanupOutcome.Unknown;
            string? failureCode = "cleanup.unknown";
            if (string.Equals(item.Provider, BillingProviderNames.Internal, StringComparison.Ordinal))
            {
                // An internally granted entitlement never had an external subscription to
                // cancel, so its cleanup is confirmed without calling any billing provider.
                outcome = OrganizationBillingCleanupOutcome.ConfirmedAbsent;
                failureCode = null;
            }
            else if (string.Equals(item.Provider, _cleanupProvider.Provider, StringComparison.Ordinal))
            {
                try
                {
                    outcome = await _cleanupProvider.CleanupAsync(
                        new OrganizationBillingCleanupRequest(
                            item.OrganizationId,
                            item.SubscriptionId,
                            item.CleanupKey,
                            item.Provider,
                            item.ProviderCustomerReference,
                            item.ProviderSubscriptionReference,
                            item.AttemptCount),
                        cancellationToken);
                    failureCode = outcome == OrganizationBillingCleanupOutcome.ConfirmedAbsent ? null : "cleanup.provider-unavailable";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    outcome = OrganizationBillingCleanupOutcome.Unknown;
                    failureCode = "cleanup.provider-unavailable";
                }
            }
            else
            {
                failureCode = "cleanup.provider-mismatch";
            }

            await store.CompleteCleanupAsync(
                new OrganizationBillingCleanupCompletion(
                    item.Id,
                    item.OrganizationId,
                    item.SubscriptionId,
                    item.LeaseToken,
                    outcome,
                    _timeProvider.GetUtcNow().ToUniversalTime(),
                    failureCode),
                cancellationToken);
        }

        return new OrganizationBillingLifecycleBatchResult(advances, cleanupAttempts);
    }
}
