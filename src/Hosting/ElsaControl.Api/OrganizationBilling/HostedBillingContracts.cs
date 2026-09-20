using System.Text.Json.Serialization;

namespace ElsaControl.Api.OrganizationBilling;

public static class HostedSubscriptionManagementActions
{
    public const string OpenPortal = "open-portal";
    public const string NoBillingLinkage = "no-billing-linkage";
    public const string Unavailable = "unavailable";
    public const string ViewOnly = "view-only";
}

public static class HostedBillingCopy
{
    public static HostedBillingCopyHook EngineDeletion { get; } = new(
        "engine-deletion-leaves-subscription-active",
        "Deleting a managed engine does not cancel the Hosted subscription. Billing continues until the customer cancels it in the subscription portal.");

    public static HostedBillingCopyHook SubscriptionCancellation { get; } = new(
        "subscription-cancellation-ends-at-period-end",
        "Canceling the Hosted subscription ends access and billing at the end of the current period. It does not immediately delete the managed engine.");

    public static HostedBillingCopyHook MissingBillingLinkage { get; } = new(
        "billing-linkage-missing",
        "No Stripe billing customer is linked to this Hosted organization yet. Complete checkout, or wait for the billing webhook, before managing the subscription.");

    public static HostedBillingCopyHooks ForLinkedCustomer() =>
        new(EngineDeletion, SubscriptionCancellation, null);

    public static HostedBillingCopyHooks ForMissingLinkage() =>
        new(EngineDeletion, SubscriptionCancellation, MissingBillingLinkage);
}

public sealed record HostedBillingCopyHook(string Code, string Message);

public sealed record HostedBillingCopyHooks(
    HostedBillingCopyHook EngineDeletion,
    HostedBillingCopyHook SubscriptionCancellation,
    HostedBillingCopyHook? MissingBillingLinkage);

public sealed record HostedSubscriptionManagementResponse(
    Guid OrganizationId,
    string? SubscriptionState,
    bool BillingLinked,
    bool PortalAvailable,
    bool CanManageSubscription,
    string ManagementAction,
    HostedBillingCopyHooks Copy);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HostedPortalSessionRequest(string? ReturnUrl = null);

public sealed record HostedPortalSessionResponse(string Url);
