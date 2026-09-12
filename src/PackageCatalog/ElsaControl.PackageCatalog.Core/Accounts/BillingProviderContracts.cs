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
    DateTimeOffset TrialEndsAt,
    string? ProviderCustomerReference = null);

public sealed record BillingCustomerPortalSessionRequest(
    Guid OrganizationId,
    string ProviderCustomerReference,
    string ReturnUrl);

public sealed record BillingSessionLink(string Url);

public enum BillingWebhookNormalizationStatus
{
    Known,
    Unknown,
    Invalid
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
}

public static class BillingProviderNames
{
    public const string Stripe = "stripe";

    /// <summary>
    /// Reserved for operator-granted internal entitlements. It is not a payment
    /// provider: provider checkout and webhook ingestion refuse this value so a
    /// provider pipeline can never create or advance an internal grant.
    /// </summary>
    public const string Internal = "internal";
}
