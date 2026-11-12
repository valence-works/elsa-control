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
    Task<bool> CancelOrConfirmAbsentAsync(
        string subscriptionReference,
        RequestOptions requestOptions,
        CancellationToken cancellationToken,
        bool refundPaidInvoice = false);

    Task<string?> GetStatusAsync(string subscriptionReference, CancellationToken cancellationToken);
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
    public async Task<bool> CancelOrConfirmAbsentAsync(
        string subscriptionReference,
        RequestOptions requestOptions,
        CancellationToken cancellationToken,
        bool refundPaidInvoice = false)
    {
        try
        {
            if (refundPaidInvoice &&
                !await RefundLatestPaidInvoiceAsync(subscriptionReference, requestOptions, cancellationToken))
                return false;

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

    public async Task<string?> GetStatusAsync(string subscriptionReference, CancellationToken cancellationToken)
    {
        try
        {
            var subscription = await new SubscriptionService(clientFactory()).GetAsync(
                subscriptionReference,
                null,
                null,
                cancellationToken);
            return subscription.Status;
        }
        catch (StripeException exception) when (exception.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<bool> RefundLatestPaidInvoiceAsync(
        string subscriptionReference,
        RequestOptions requestOptions,
        CancellationToken cancellationToken)
    {
        var invoices = await new InvoiceService(clientFactory()).ListAsync(
            new InvoiceListOptions
            {
                Subscription = subscriptionReference,
                Status = "paid",
                Limit = 1,
                Expand = ["data.payments"]
            },
            cancellationToken: cancellationToken);
        var invoice = invoices.Data.FirstOrDefault();
        if (invoice is null || invoice.AmountPaid <= 0)
            return true;

        var payment = invoice.Payments?.Data?
            .Select(x => x.Payment)
            .FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x?.PaymentIntentId) ||
                !string.IsNullOrWhiteSpace(x?.ChargeId));
        var refund = new RefundCreateOptions
        {
            Reason = RefundReasons.RequestedByCustomer
        };
        if (!string.IsNullOrWhiteSpace(payment?.PaymentIntentId))
            refund.PaymentIntent = payment.PaymentIntentId;
        else if (!string.IsNullOrWhiteSpace(payment?.ChargeId))
            refund.Charge = payment.ChargeId;
        else
            return false;

        var refundOptions = new RequestOptions
        {
            IdempotencyKey = OrganizationBillingCleanupKeys.PaidInvoiceRefundIdempotencyKey(
                requestOptions.IdempotencyKey ?? subscriptionReference)
        };
        try
        {
            await new RefundService(clientFactory()).CreateAsync(refund, refundOptions, cancellationToken);
            return true;
        }
        catch (StripeException exception) when (IsAlreadyRefunded(exception))
        {
            return true;
        }
    }

    private static bool IsAlreadyRefunded(StripeException exception) =>
        string.Equals(exception.StripeError?.Code, "charge_already_refunded", StringComparison.Ordinal) ||
        exception.Message.Contains("already been refunded", StringComparison.OrdinalIgnoreCase);
}

public sealed class StripeBillingProvider(
    IOptions<StripeBillingOptions> options,
    IStripeCheckoutSessionGateway checkoutGateway,
    IStripeCustomerPortalGateway portalGateway,
    IStripeSubscriptionCleanupGateway cleanupGateway) : IBillingProvider, IOrganizationBillingCleanupProvider, IBillingProviderSubscriptionStatusLookup
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
        if (BillingWebhookSources.IsHub(request.WebhookSource))
            return OrganizationBillingCleanupOutcome.ManagedByHub;
        if (string.IsNullOrWhiteSpace(request.ProviderSubscriptionReference))
            return OrganizationBillingCleanupOutcome.Unknown;

        try
        {
            var confirmed = await cleanupGateway.CancelOrConfirmAbsentAsync(
                request.ProviderSubscriptionReference,
                new RequestOptions { IdempotencyKey = request.CleanupKey },
                cancellationToken,
                OrganizationBillingCleanupKeys.RequiresPaidInvoiceRefund(request.CleanupKey));
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

    public async Task<BillingProviderSubscriptionPresence> GetPresenceAsync(
        string provider,
        string subscriptionReference,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(provider, Provider, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(subscriptionReference))
            return BillingProviderSubscriptionPresence.Unknown;

        try
        {
            var status = await cleanupGateway.GetStatusAsync(subscriptionReference, cancellationToken);
            return BillingProviderSubscriptionPresenceMapping.FromStripeStatus(status);
        }
        catch (StripeException)
        {
            return BillingProviderSubscriptionPresence.Unknown;
        }
        catch (HttpRequestException)
        {
            return BillingProviderSubscriptionPresence.Unknown;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BillingProviderSubscriptionPresence.Unknown;
        }
    }

    public async Task<BillingSessionLink> CreateCheckoutSessionAsync(
        BillingCheckoutSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureCheckoutConfigured();
        if (request.OrganizationId == Guid.Empty)
            throw new ArgumentException("Organization ID is required.", nameof(request));
        if (request.TrialEndsAt is { } trialEnd && (trialEnd == default || trialEnd <= DateTimeOffset.UtcNow))
            throw new ArgumentException("A future control-plane trial end is required.", nameof(request));

        var organizationId = request.OrganizationId.ToString("D");
        var metadata = new Dictionary<string, string> { [OrganizationMetadataKey] = organizationId };
        var subscriptionData = new SessionSubscriptionDataOptions
        {
            Metadata = new Dictionary<string, string>(metadata)
        };
        if (request.TrialEndsAt is { } paidTrialEnd)
            subscriptionData.TrialEnd = paidTrialEnd.UtcDateTime;
        var idempotencyKey = request.TrialEndsAt is { } trialTicks
            ? $"billing-checkout-{organizationId}-{trialTicks.UtcTicks}"
            : $"billing-checkout-paid-{organizationId}-{ResolveCheckoutAttemptKey(request)}";
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
                    SubscriptionData = subscriptionData
                },
                new RequestOptions { IdempotencyKey = idempotencyKey },
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
            if (!TryMatchWebhookSource(json, signature, receivedAt, out var matchedSource, out var stripeEvent) ||
                matchedSource is null ||
                stripeEvent is null)
                return BillingWebhookNormalizationResult.Invalid("webhook.signature-or-payload-invalid");
            if (!ModeMatches(matchedSource, stripeEvent.Livemode))
                return BillingWebhookNormalizationResult.Invalid("webhook.mode-mismatch");
            if (string.IsNullOrWhiteSpace(stripeEvent.Id) ||
                string.IsNullOrWhiteSpace(stripeEvent.Type) ||
                stripeEvent.Created == default)
                return BillingWebhookNormalizationResult.Invalid("webhook.payload-invalid");
            var hash = Convert.ToHexString(SHA256.HashData(rawBody.Span)).ToLowerInvariant();
            var dataObject = stripeEvent.Data?.Object;
            var correlation = ExtractCorrelation(dataObject);
            var sourceName = StripeBillingOptions.NormalizeSourceName(matchedSource.Name) ?? BillingWebhookSources.Control;
            var providerEvent = CreateProviderEvent(correlation, stripeEvent, hash, sourceName);
            if (correlation.OrganizationId == Guid.Empty)
            {
                return BillingWebhookSources.IsHub(sourceName)
                    ? BillingWebhookNormalizationResult.Ignored("webhook.organization-correlation-missing", providerEvent)
                    : BillingWebhookNormalizationResult.Invalid("webhook.organization-correlation-missing");
            }

            if (!_options.AllowsCloudPrice(matchedSource, correlation.PriceReference))
                return BillingWebhookNormalizationResult.Ignored("webhook.price-not-cloud", providerEvent);

            var state = MapState(stripeEvent.Type, correlation.Status);
            if (state.HasValue)
            {
                if (dataObject is not Subscription subscription ||
                    string.IsNullOrWhiteSpace(subscription.Id) ||
                    string.IsNullOrWhiteSpace(subscription.CustomerId))
                    return BillingWebhookNormalizationResult.Invalid("webhook.payload-invalid");
            }
            providerEvent = providerEvent with { State = state };
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

    private bool TryMatchWebhookSource(
        string json,
        string signature,
        DateTimeOffset receivedAt,
        out StripeWebhookSourceOptions? matchedSource,
        out Event? stripeEvent)
    {
        matchedSource = null;
        stripeEvent = null;
        var matchCount = 0;
        foreach (var source in _options.ResolveWebhookSources())
        {
            if (string.IsNullOrWhiteSpace(source.SigningSecret))
                continue;

            try
            {
                var candidate = EventUtility.ConstructEvent(
                    json,
                    signature,
                    source.SigningSecret,
                    SignatureToleranceSeconds,
                    receivedAt.ToUnixTimeSeconds(),
                    throwOnApiVersionMismatch: true);
                matchCount++;
                if (matchCount == 1)
                {
                    matchedSource = source;
                    stripeEvent = candidate;
                }
            }
            catch (StripeException)
            {
                // This source did not authorize the delivery.
            }
        }

        return matchCount == 1;
    }

    private bool ModeMatches(StripeWebhookSourceOptions source, bool livemode)
    {
        var expected = StripeBillingOptions.NormalizeMode(source.ExpectedMode)
            ?? StripeBillingOptions.NormalizeMode(_options.ExpectedMode);
        if (expected is null)
            return false;
        return expected == StripeBillingOptions.LiveMode ? livemode : !livemode;
    }

    private BillingProviderEvent CreateProviderEvent(
        StripeEventCorrelation correlation,
        Event stripeEvent,
        string hash,
        string sourceName) =>
        new(
            correlation.OrganizationId,
            Provider,
            stripeEvent.Id,
            stripeEvent.Type,
            null,
            new DateTimeOffset(DateTime.SpecifyKind(stripeEvent.Created, DateTimeKind.Utc)),
            $"sha256:{hash}",
            correlation.CustomerReference,
            correlation.SubscriptionReference,
            correlation.ObjectReference,
            correlation.PriceReference,
            correlation.AmountMinorUnits,
            sourceName);

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

    private static string ResolveCheckoutAttemptKey(BillingCheckoutSessionRequest request) =>
        string.IsNullOrWhiteSpace(request.CheckoutAttemptKey)
            ? Guid.NewGuid().ToString("N")
            : request.CheckoutAttemptKey.Trim();

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
        if (!string.IsNullOrWhiteSpace(priceId))
            return priceId;

        if (checkoutSession.Subscription is Subscription subscription)
        {
            var (price, _) = ReadSubscriptionPrice(subscription);
            if (!string.IsNullOrWhiteSpace(price))
                return price;
        }

        return null;
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
