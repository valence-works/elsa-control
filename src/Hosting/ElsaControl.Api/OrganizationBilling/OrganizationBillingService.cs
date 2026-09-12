using ElsaControl.Billing.Stripe;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.OrganizationBilling;

public sealed class OrganizationBillingApiService(
    AccountWorkspaceService accounts,
    OrganizationBillingService billing,
    IBillingProvider provider,
    IOptions<StripeBillingOptions> stripeOptions)
{
    private readonly StripeBillingOptions _stripeOptions = stripeOptions.Value;

    public async Task<OrganizationBillingStatusApiResult> GetStatusAsync(
        TrustedWorkspaceIdentity identity,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var access = await accounts.GetOrganizationAccessAsync(identity, organizationId, OrganizationOperation.ViewOrganization, cancellationToken);
        if (!access.Succeeded)
            return OrganizationBillingStatusApiResult.Denied(access.Failure!.Value);

        var entitlement = await accounts.GetLatestOrganizationEntitlementAsync(organizationId, cancellationToken);
        var subscription = await billing.GetSubscriptionAsync(organizationId, cancellationToken);
        var activeWorkspaces = await accounts.ActiveWorkspaceCountAsync(organizationId, cancellationToken);
        var activeManagedInstances = await accounts.ActiveManagedInstanceCountAsync(organizationId, cancellationToken);

        return OrganizationBillingStatusApiResult.Success(
            OrganizationBillingStatusResponse.From(
                organizationId,
                subscription,
                entitlement,
                activeWorkspaces,
                activeManagedInstances));
    }

    public async Task<OrganizationBillingApiResult> CreateCheckoutAsync(
        TrustedWorkspaceIdentity identity,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var access = await accounts.GetOrganizationAccessAsync(identity, organizationId, OrganizationOperation.ManageBilling, cancellationToken);
        if (!access.Succeeded)
            return OrganizationBillingApiResult.Denied(access.Failure!.Value);

        if (!IsStripeProvider)
            return OrganizationBillingApiResult.Unavailable();

        if (!_stripeOptions.IsCheckoutConfigured)
            return OrganizationBillingApiResult.Unavailable();

        BillingEventConsumptionResult trial;
        try
        {
            trial = await billing.StartTrialAsync(organizationId, provider.Provider, cancellationToken);
        }
        catch (BillingProviderEventConflictException)
        {
            // The organization's subscription belongs to another provider (for
            // example, an operator-granted internal entitlement); it is terminal
            // to a checkout that only ever creates or resumes a Stripe trial.
            return OrganizationBillingApiResult.Terminal();
        }

        var subscription = trial.Subscription ?? await billing.GetSubscriptionAsync(organizationId, cancellationToken);
        if (subscription is null)
            return OrganizationBillingApiResult.Unavailable();
        if (subscription.State != OrganizationSubscriptionState.Trial ||
            subscription.EarlyDeletionRequestedAt is not null ||
            !string.IsNullOrWhiteSpace(subscription.ProviderSubscriptionReference))
            return OrganizationBillingApiResult.Terminal();

        try
        {
            var session = await provider.CreateCheckoutSessionAsync(
                new BillingCheckoutSessionRequest(
                    organizationId,
                    _stripeOptions.DefaultPriceId!,
                    _stripeOptions.CheckoutSuccessUrl!,
                    _stripeOptions.CheckoutCancelUrl!,
                    subscription.TrialEndsAt,
                    subscription.ProviderCustomerReference),
                cancellationToken);
            return OrganizationBillingApiResult.Success(session);
        }
        catch (BillingProviderUnavailableException)
        {
            return OrganizationBillingApiResult.Unavailable();
        }
    }

    public async Task<OrganizationBillingApiResult> CreatePortalAsync(
        TrustedWorkspaceIdentity identity,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var access = await accounts.GetOrganizationAccessAsync(identity, organizationId, OrganizationOperation.ManageBilling, cancellationToken);
        if (!access.Succeeded)
            return OrganizationBillingApiResult.Denied(access.Failure!.Value);

        if (!IsStripeProvider)
            return OrganizationBillingApiResult.Unavailable();

        if (!_stripeOptions.IsPortalConfigured)
            return OrganizationBillingApiResult.Unavailable();

        var subscription = await billing.GetSubscriptionAsync(organizationId, cancellationToken);
        if (subscription is null || string.IsNullOrWhiteSpace(subscription.ProviderCustomerReference))
            return OrganizationBillingApiResult.CustomerUnavailable();

        try
        {
            var session = await provider.CreateCustomerPortalSessionAsync(
                new BillingCustomerPortalSessionRequest(
                    organizationId,
                    subscription.ProviderCustomerReference,
                    _stripeOptions.PortalReturnUrl!),
                cancellationToken);
            return OrganizationBillingApiResult.Success(session);
        }
        catch (BillingProviderUnavailableException)
        {
            return OrganizationBillingApiResult.Unavailable();
        }
    }

    public async Task<OrganizationBillingDeletionApiResult> RequestDeletionAsync(
        TrustedWorkspaceIdentity identity,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var access = await accounts.GetOrganizationAccessAsync(identity, organizationId, OrganizationOperation.ManageBilling, cancellationToken);
        if (!access.Succeeded)
            return OrganizationBillingDeletionApiResult.Denied(access.Failure!.Value);

        var result = await billing.RequestDeletionAsync(organizationId, cancellationToken);
        return result is null
            ? OrganizationBillingDeletionApiResult.NotFound()
            : OrganizationBillingDeletionApiResult.Accepted(result);
    }

    private bool IsStripeProvider => string.Equals(provider.Provider, BillingProviderNames.Stripe, StringComparison.Ordinal);
}

public sealed record OrganizationBillingApiResult(
    BillingSessionLink? Session,
    OrganizationWorkspaceFailure? Failure,
    bool ProviderUnavailable,
    bool CustomerNotReady,
    bool SubscriptionTerminal)
{
    public bool Succeeded => Session is not null && Failure is null && !ProviderUnavailable && !CustomerNotReady && !SubscriptionTerminal;

    public static OrganizationBillingApiResult Success(BillingSessionLink session) => new(session, null, false, false, false);
    public static OrganizationBillingApiResult Denied(OrganizationWorkspaceFailure failure) => new(null, failure, false, false, false);
    public static OrganizationBillingApiResult Unavailable() => new(null, null, true, false, false);
    public static OrganizationBillingApiResult CustomerUnavailable() => new(null, null, false, true, false);
    public static OrganizationBillingApiResult Terminal() => new(null, null, false, false, true);
}

public sealed record OrganizationBillingStatusApiResult(
    OrganizationBillingStatusResponse? Status,
    OrganizationWorkspaceFailure? Failure)
{
    public bool Succeeded => Status is not null && Failure is null;

    public static OrganizationBillingStatusApiResult Success(OrganizationBillingStatusResponse status) => new(status, null);
    public static OrganizationBillingStatusApiResult Denied(OrganizationWorkspaceFailure failure) => new(null, failure);
}

public sealed record OrganizationBillingDeletionApiResult(
    OrganizationBillingLifecycleAdvance? Advance,
    OrganizationWorkspaceFailure? Failure,
    bool OrganizationUnavailable)
{
    public bool Succeeded => Advance is not null && Failure is null && !OrganizationUnavailable;

    public static OrganizationBillingDeletionApiResult Accepted(OrganizationBillingLifecycleAdvance advance) => new(advance, null, false);
    public static OrganizationBillingDeletionApiResult Denied(OrganizationWorkspaceFailure failure) => new(null, failure, false);
    public static OrganizationBillingDeletionApiResult NotFound() => new(null, null, true);
}
