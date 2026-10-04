using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace ElsaControl.Billing.Stripe;

public interface IStripeCheckoutSessionGateway
{
    Task<global::Stripe.Checkout.Session> CreateAsync(global::Stripe.Checkout.SessionCreateOptions options, RequestOptions requestOptions, CancellationToken cancellationToken);
}

public interface IStripeCustomerPortalGateway
{
    Task<global::Stripe.BillingPortal.Session> CreateAsync(global::Stripe.BillingPortal.SessionCreateOptions options, RequestOptions requestOptions, CancellationToken cancellationToken);
}

public interface IStripeSubscriptionCleanupGateway
{
    Task<bool> CancelOrConfirmAbsentAsync(string subscriptionReference, RequestOptions requestOptions, CancellationToken cancellationToken);
}

public sealed class StripeCheckoutSessionGateway(Func<StripeClient> clientFactory) : IStripeCheckoutSessionGateway
{
    public Task<global::Stripe.Checkout.Session> CreateAsync(global::Stripe.Checkout.SessionCreateOptions options, RequestOptions requestOptions, CancellationToken cancellationToken) =>
        new SessionService(clientFactory()).CreateAsync(options, requestOptions, cancellationToken);
}

public sealed class StripeCustomerPortalGateway(Func<StripeClient> clientFactory) : IStripeCustomerPortalGateway
{
    public Task<global::Stripe.BillingPortal.Session> CreateAsync(global::Stripe.BillingPortal.SessionCreateOptions options, RequestOptions requestOptions, CancellationToken cancellationToken) =>
        new global::Stripe.BillingPortal.SessionService(clientFactory()).CreateAsync(options, requestOptions, cancellationToken);
}

public sealed class StripeSubscriptionCleanupGateway(Func<StripeClient> clientFactory) : IStripeSubscriptionCleanupGateway
{
    public async Task<bool> CancelOrConfirmAbsentAsync(string subscriptionReference, RequestOptions requestOptions, CancellationToken cancellationToken)
    {
        try
        {
            var subscriptions = new SubscriptionService(clientFactory());
            var subscription = await subscriptions.GetAsync(subscriptionReference, null, null, cancellationToken);
            if (string.Equals(subscription.Status, "canceled", StringComparison.Ordinal))
                return true;

            var canceled = await subscriptions.CancelAsync(subscriptionReference, null, requestOptions, cancellationToken);
            return string.Equals(canceled.Status, "canceled", StringComparison.Ordinal);
        }
        catch (StripeException exception) when (exception.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return true;
        }
    }
}

public sealed class StripeBillingProvider(
    IOptions<StripeBillingOptions> options,
    IStripeCheckoutSessionGateway checkoutGateway,
    IStripeCustomerPortalGateway portalGateway,
    IStripeSubscriptionCleanupGateway cleanupGateway) : IBillingProvider, IOrganizationBillingCleanupProvider
{
    private const long SignatureToleranceSeconds = 300;
    private const string OrganizationMetadataKey = "elsa_control_organization_id";
    private readonly StripeBillingOptions _options = options.Value;

    public string Provider => BillingProviderNames.Stripe;

    public async Task<OrganizationBillingCleanupOutcome> CleanupAsync(
        OrganizationBillingCleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(request.Provider, Provider, StringComparison.Ordinal))
            return OrganizationBillingCleanupOutcome.Unknown;
        if (string.IsNullOrWhiteSpace(request.ProviderSubscriptionReference))
            return OrganizationBillingCleanupOutcome.Unknown;

        try
        {
            var confirmed = await cleanupGateway.CancelOrConfirmAbsentAsync(
                request.ProviderSubscriptionReference,
                new RequestOptions { IdempotencyKey = request.CleanupKey },
                cancellationToken);
            return confirmed ? OrganizationBillingCleanupOutcome.ConfirmedAbsent : OrganizationBillingCleanupOutcome.RetryableFailure;
        }
        catch (StripeException)
        {
            return OrganizationBillingCleanupOutcome.RetryableFailure;
        }
        catch (HttpRequestException)
        {
            return OrganizationBillingCleanupOutcome.RetryableFailure;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OrganizationBillingCleanupOutcome.RetryableFailure;
        }
    }

    public async Task<BillingSessionLink> CreateCheckoutSessionAsync(
        BillingCheckoutSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureCheckoutConfigured();
        if (request.OrganizationId == Guid.Empty)
            throw new ArgumentException("Organization ID is required.", nameof(request));
        if (request.TrialEndsAt == default || request.TrialEndsAt <= DateTimeOffset.UtcNow)
            throw new ArgumentException("A future control-plane trial end is required.", nameof(request));

        var organizationId = request.OrganizationId.ToString("D");
        var metadata = new Dictionary<string, string> { [OrganizationMetadataKey] = organizationId };
        global::Stripe.Checkout.Session session;
        try
        {
            session = await checkoutGateway.CreateAsync(
                new global::Stripe.Checkout.SessionCreateOptions
                {
                    Mode = "subscription",
                    LineItems = [new SessionLineItemOptions { Price = request.PriceReference, Quantity = 1 }],
                    Customer = request.ProviderCustomerReference,
                    ClientReferenceId = organizationId,
                    Metadata = metadata,
                    SuccessUrl = request.SuccessUrl,
                    CancelUrl = request.CancelUrl,
                    SubscriptionData = new SessionSubscriptionDataOptions
                    {
                        TrialEnd = request.TrialEndsAt.UtcDateTime,
                        Metadata = new Dictionary<string, string>(metadata)
                    }
                },
                new RequestOptions { IdempotencyKey = $"billing-checkout-{organizationId}-{request.TrialEndsAt.UtcTicks}" },
                cancellationToken);
        }
        catch (StripeException exception)
        {
            throw new BillingProviderUnavailableException("The Stripe billing provider could not create a checkout session.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new BillingProviderUnavailableException("The Stripe billing provider could not create a checkout session.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BillingProviderUnavailableException("The Stripe billing provider could not create a checkout session.", exception);
        }

        return RequireSessionUrl(session.Url);
    }

    public async Task<BillingSessionLink> CreateCustomerPortalSessionAsync(
        BillingCustomerPortalSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsurePortalConfigured();
        if (request.OrganizationId == Guid.Empty)
            throw new ArgumentException("Organization ID is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ProviderCustomerReference))
            throw new ArgumentException("A provider customer reference is required.", nameof(request));

        global::Stripe.BillingPortal.Session session;
        try
        {
            session = await portalGateway.CreateAsync(
                new global::Stripe.BillingPortal.SessionCreateOptions
                {
                    Customer = request.ProviderCustomerReference,
                    ReturnUrl = request.ReturnUrl
                },
                // Portal sessions are short-lived links. Reusing an idempotency
                // key would replay an expired URL on a later request.
                new RequestOptions(),
                cancellationToken);
        }
        catch (StripeException exception)
        {
            throw new BillingProviderUnavailableException("The Stripe billing provider could not create a portal session.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new BillingProviderUnavailableException("The Stripe billing provider could not create a portal session.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BillingProviderUnavailableException("The Stripe billing provider could not create a portal session.", exception);
        }

        return RequireSessionUrl(session.Url);
    }

    public BillingWebhookNormalizationResult VerifyAndNormalizeWebhook(
        ReadOnlyMemory<byte> rawBody,
        string signature,
        DateTimeOffset receivedAt)
    {
        if (rawBody.IsEmpty || string.IsNullOrWhiteSpace(signature) || !_options.IsWebhookConfigured)
            return BillingWebhookNormalizationResult.Invalid("webhook.invalid");

        try
        {
            var json = Encoding.UTF8.GetString(rawBody.Span);
            var stripeEvent = EventUtility.ConstructEvent(
                json,
                signature,
                _options.WebhookSigningSecret,
                SignatureToleranceSeconds,
                receivedAt.ToUnixTimeSeconds(),
                throwOnApiVersionMismatch: true);
            if (string.IsNullOrWhiteSpace(stripeEvent.Id) ||
                string.IsNullOrWhiteSpace(stripeEvent.Type) ||
                stripeEvent.Created == default)
                return BillingWebhookNormalizationResult.Invalid("webhook.payload-invalid");
            var hash = Convert.ToHexString(SHA256.HashData(rawBody.Span)).ToLowerInvariant();
            var dataObject = stripeEvent.Data?.Object;
            var correlation = ExtractCorrelation(dataObject);
            if (correlation.OrganizationId == Guid.Empty)
                return BillingWebhookNormalizationResult.Invalid("webhook.organization-correlation-missing");

            var state = MapState(stripeEvent.Type, correlation.Status);
            if (state.HasValue)
            {
                if (dataObject is not Subscription subscription ||
                    string.IsNullOrWhiteSpace(subscription.Id) ||
                    string.IsNullOrWhiteSpace(subscription.CustomerId))
                    return BillingWebhookNormalizationResult.Invalid("webhook.payload-invalid");
            }
            var providerEvent = new BillingProviderEvent(
                correlation.OrganizationId,
                Provider,
                stripeEvent.Id,
                stripeEvent.Type,
                state,
                new DateTimeOffset(DateTime.SpecifyKind(stripeEvent.Created, DateTimeKind.Utc)),
                $"sha256:{hash}",
                correlation.CustomerReference,
                correlation.SubscriptionReference,
                correlation.ObjectReference,
                correlation.PriceReference,
                correlation.AmountMinorUnits);
            return state.HasValue
                ? BillingWebhookNormalizationResult.KnownEvent(providerEvent)
                : BillingWebhookNormalizationResult.UnknownEvent(providerEvent);
        }
        catch (StripeException)
        {
            return BillingWebhookNormalizationResult.Invalid("webhook.signature-or-payload-invalid");
        }
        catch (FormatException)
        {
            return BillingWebhookNormalizationResult.Invalid("webhook.payload-invalid");
        }
        catch (ArgumentException)
        {
            return BillingWebhookNormalizationResult.Invalid("webhook.payload-invalid");
        }
        catch (JsonException)
        {
            return BillingWebhookNormalizationResult.Invalid("webhook.payload-invalid");
        }
        catch (NullReferenceException)
        {
            return BillingWebhookNormalizationResult.Invalid("webhook.payload-invalid");
        }
    }

    private void EnsureCheckoutConfigured()
    {
        if (!_options.IsCheckoutConfigured)
            throw new BillingProviderUnavailableException("The Stripe billing provider is not configured.");
    }

    private void EnsurePortalConfigured()
    {
        if (!_options.IsProviderReady)
            throw new BillingProviderUnavailableException("The Stripe billing provider is not configured.");
    }

    private static BillingSessionLink RequireSessionUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new BillingProviderUnavailableException("The billing provider returned an invalid session URL.");
        return new BillingSessionLink(uri.AbsoluteUri);
    }

    private readonly record struct StripeEventCorrelation(
        Guid OrganizationId,
        string? CustomerReference,
        string? SubscriptionReference,
        string? ObjectReference,
        string? PriceReference,
        long? AmountMinorUnits,
        string? Status);

    private static StripeEventCorrelation ExtractCorrelation(IHasObject? dataObject)
    {
        if (dataObject is Subscription subscription)
        {
            var (price, amount) = ReadSubscriptionPrice(subscription);
            return new(
                ReadOrganizationId(subscription.Metadata),
                subscription.CustomerId,
                subscription.Id,
                subscription.Id,
                price,
                amount,
                subscription.Status);
        }

        if (dataObject is Customer customer)
            return new(ReadOrganizationId(customer.Metadata), customer.Id, null, customer.Id, null, null, null);

        if (dataObject is global::Stripe.Checkout.Session checkoutSession)
        {
            var organizationId = ReadOrganizationId(checkoutSession.Metadata);
            if (organizationId == Guid.Empty)
                Guid.TryParse(checkoutSession.ClientReferenceId, out organizationId);
            // Keep ProviderSubscriptionReference unset for checkout sessions so
            // historical inbox rows that stored null continue to replay.
            return new(
                organizationId,
                checkoutSession.CustomerId,
                null,
                checkoutSession.Id,
                ReadCheckoutPrice(checkoutSession),
                checkoutSession.AmountTotal,
                null);
        }

        return new(Guid.Empty, null, null, null, null, null, null);
    }

    private static (string? PriceReference, long? AmountMinorUnits) ReadSubscriptionPrice(Subscription subscription)
    {
        var price = subscription.Items?.Data?.FirstOrDefault()?.Price;
        if (price is null)
            return (null, null);
        return (string.IsNullOrWhiteSpace(price.Id) ? null : price.Id, price.UnitAmount);
    }

    private static string? ReadCheckoutPrice(global::Stripe.Checkout.Session checkoutSession)
    {
        var priceId = checkoutSession.LineItems?.Data?.FirstOrDefault()?.Price?.Id;
        return string.IsNullOrWhiteSpace(priceId) ? null : priceId;
    }

    private static Guid ReadOrganizationId(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue(OrganizationMetadataKey, out var value) &&
        Guid.TryParse(value, out var organizationId)
            ? organizationId
            : Guid.Empty;

    private static OrganizationSubscriptionState? MapState(string eventType, string? status)
    {
        if (!SupportedSubscriptionEventTypes.Contains(eventType))
            return null;

        if (eventType == "customer.subscription.deleted")
            return OrganizationSubscriptionState.Suspended;

        return status switch
        {
            "trialing" => OrganizationSubscriptionState.Trial,
            "active" => OrganizationSubscriptionState.Active,
            "past_due" => OrganizationSubscriptionState.PastDue,
            "unpaid" => OrganizationSubscriptionState.Constrained,
            "paused" => OrganizationSubscriptionState.Constrained,
            "canceled" => OrganizationSubscriptionState.Suspended,
            _ => null
        };
    }

    private static readonly HashSet<string> SupportedSubscriptionEventTypes = new(StringComparer.Ordinal)
    {
        "customer.subscription.created",
        "customer.subscription.updated",
        "customer.subscription.deleted",
        "customer.subscription.paused",
        "customer.subscription.resumed"
    };
}
