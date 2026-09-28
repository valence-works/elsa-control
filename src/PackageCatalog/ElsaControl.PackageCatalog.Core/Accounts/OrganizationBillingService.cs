namespace ElsaControl.PackageCatalog.Core.Accounts;

/// <summary>
/// Application boundary for consuming normalized billing facts. The EF
/// implementation supplies the transaction; this service keeps provider
/// adapters independent of persistence and authorization.
/// </summary>
public sealed class OrganizationBillingService(
    IOrganizationBillingStore store,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private IOrganizationBillingLifecycleStore LifecycleStore => store as IOrganizationBillingLifecycleStore ??
        throw new InvalidOperationException("The billing store does not support commercial lifecycle processing.");

    public Task<BillingEventConsumptionResult> ConsumeAsync(
        BillingProviderEvent providerEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerEvent);
        return store.ConsumeAsync(providerEvent, _timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<BillingEventConsumptionResult> RecordUnknownAsync(
        BillingProviderEvent providerEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerEvent);
        return store.RecordUnknownAsync(providerEvent, _timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<BillingEventConsumptionResult> StartTrialAsync(
        Guid organizationId,
        string provider,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("Organization ID is required.", nameof(organizationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return store.StartTrialAsync(
            organizationId,
            provider,
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public Task<OrganizationSubscription?> GetSubscriptionAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default) =>
        store.GetSubscriptionAsync(organizationId, cancellationToken);

    public Task<IReadOnlyList<OrganizationBillingLifecycleAdvance>> AdvanceLifecycleAsync(
        CancellationToken cancellationToken = default) =>
        LifecycleStore.AdvanceDueAsync(_timeProvider.GetUtcNow(), cancellationToken);

    public Task<OrganizationBillingLifecycleAdvance?> AdvanceOneAsync(
        Guid organizationId,
        Guid subscriptionId,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("Organization ID is required.", nameof(organizationId));
        if (subscriptionId == Guid.Empty)
            throw new ArgumentException("Subscription ID is required.", nameof(subscriptionId));
        return LifecycleStore.AdvanceOneAsync(
            organizationId,
            subscriptionId,
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public Task<OrganizationBillingLifecycleAdvance?> RequestDeletionAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("Organization ID is required.", nameof(organizationId));
        return LifecycleStore.RequestDeletionAsync(organizationId, _timeProvider.GetUtcNow(), cancellationToken);
    }
}
