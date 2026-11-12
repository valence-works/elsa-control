namespace ElsaControl.PackageCatalog.Core.Accounts;

/// <summary>
/// The only provider boundary exposed to the API. Provider identifiers and
/// price identifiers are opaque references; they never determine product
/// capabilities.
/// </summary>
public interface IBillingProvider
{
    string Provider { get; }

    Task<BillingSessionLink> CreateCheckoutSessionAsync(
        BillingCheckoutSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<BillingSessionLink> CreateCustomerPortalSessionAsync(
        BillingCustomerPortalSessionRequest request,
        CancellationToken cancellationToken = default);

    BillingWebhookNormalizationResult VerifyAndNormalizeWebhook(
        ReadOnlyMemory<byte> rawBody,
        string signature,
        DateTimeOffset receivedAt);
}

public sealed record BillingCheckoutSessionRequest(
    Guid OrganizationId,
    string PriceReference,
    string SuccessUrl,
    string CancelUrl,
    DateTimeOffset? TrialEndsAt = null,
    string? ProviderCustomerReference = null,
    string? CheckoutAttemptKey = null);

public sealed record BillingCustomerPortalSessionRequest(
    Guid OrganizationId,
    string ProviderCustomerReference,
    string ReturnUrl);

public sealed record BillingSessionLink(string Url);

public enum BillingWebhookNormalizationStatus
{
    Known,
    Unknown,
    Invalid,
    Ignored
}

/// <summary>
/// A verified webhook either contains a supported normalized event, a
/// correlated unsupported event, or no safe event at all. Invalid results are
/// never sent to persistence.
/// </summary>
public sealed record BillingWebhookNormalizationResult(
    BillingWebhookNormalizationStatus Status,
    BillingProviderEvent? Event = null,
    string? FailureCode = null)
{
    public static BillingWebhookNormalizationResult KnownEvent(BillingProviderEvent providerEvent) =>
        new(BillingWebhookNormalizationStatus.Known, providerEvent);

    public static BillingWebhookNormalizationResult UnknownEvent(BillingProviderEvent providerEvent) =>
        new(BillingWebhookNormalizationStatus.Unknown, providerEvent);

    public static BillingWebhookNormalizationResult Invalid(string code) =>
        new(BillingWebhookNormalizationStatus.Invalid, null, code);

    public static BillingWebhookNormalizationResult Ignored(string code, BillingProviderEvent? providerEvent = null) =>
        new(BillingWebhookNormalizationStatus.Ignored, providerEvent, code);
}

public static class BillingProviderNames
{
    public const string Stripe = "stripe";

    /// <summary>
    /// Reserved for operator-minted entitlements backed by an active customer
    /// Azure subscription bind. It is not a payment provider and must never be
    /// accepted by checkout or webhook ingestion.
    /// </summary>
    public const string AzureBound = "azure-bound";

    /// <summary>
    /// Reserved for operator-granted internal entitlements. It is not a payment
    /// provider: provider checkout and webhook ingestion refuse this value so a
    /// provider pipeline can never create or advance an internal grant.
    /// </summary>
    public const string Internal = "internal";
}

public enum BillingProviderSubscriptionPresence
{
    Live,
    Ended,
    Unknown
}

public interface IBillingProviderSubscriptionStatusLookup
{
    Task<BillingProviderSubscriptionPresence> GetPresenceAsync(
        string provider,
        string subscriptionReference,
        CancellationToken cancellationToken = default);
}

public static class BillingProviderSubscriptionPresenceMapping
{
    public static BillingProviderSubscriptionPresence FromStripeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return BillingProviderSubscriptionPresence.Ended;

        return status.Trim().ToLowerInvariant() switch
        {
            "canceled" or "cancelled" or "incomplete_expired" or "unpaid" =>
                BillingProviderSubscriptionPresence.Ended,
            _ => BillingProviderSubscriptionPresence.Live
        };
    }
}

public sealed class BillingProviderUnavailableException : InvalidOperationException
{
    public BillingProviderUnavailableException(string message)
        : base(message)
    {
    }

    public BillingProviderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
