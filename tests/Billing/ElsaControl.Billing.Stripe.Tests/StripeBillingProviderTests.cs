using ElsaControl.Billing.Stripe;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Extensions.Options;
using Stripe;
using System.Net;
using System.Net.Http;

namespace ElsaControl.Billing.Stripe.Tests;

public sealed class StripeBillingProviderTests
{
    private static readonly Guid OrganizationId = Guid.Parse("b7e3f4d0-14d9-4b7b-87b6-6b1b05dd6d21");
    private const string WebhookSecret = "whsec_test";

    [Fact]
    public async Task Checkout_uses_opaque_default_price_and_control_plane_trial_end()
    {
        var checkout = new RecordingCheckoutGateway();
        var provider = CreateProvider(checkout: checkout);
        var trialEndsAt = DateTimeOffset.UtcNow.AddDays(14);

        var result = await provider.CreateCheckoutSessionAsync(new BillingCheckoutSessionRequest(
            OrganizationId,
            "price_opaque_default",
            "https://console.test/billing/success",
            "https://console.test/billing/cancel",
            trialEndsAt));

        Assert.Equal("https://checkout.stripe.test/session", result.Url);
        Assert.Equal("price_opaque_default", Assert.Single(checkout.Options!.LineItems!).Price);
        Assert.Equal(trialEndsAt.UtcDateTime, checkout.Options.SubscriptionData!.TrialEnd);
        Assert.Equal(OrganizationId.ToString("D"), checkout.Options.ClientReferenceId);
        Assert.Equal(OrganizationId.ToString("D"), checkout.Options.Metadata!["elsa_control_organization_id"]);
        Assert.Equal(OrganizationId.ToString("D"), checkout.Options.SubscriptionData.Metadata!["elsa_control_organization_id"]);
        Assert.StartsWith("billing-checkout-", checkout.RequestOptions!.IdempotencyKey);
    }

    [Fact]
    public async Task Portal_session_does_not_reuse_an_idempotency_key_for_a_short_lived_url()
    {
        var portal = new RecordingPortalGateway();
        var provider = CreateProvider(portal: portal);

        await provider.CreateCustomerPortalSessionAsync(new BillingCustomerPortalSessionRequest(
            OrganizationId,
            "cus_123",
            "https://elsacloud.app/dashboard"));

        Assert.Null(portal.RequestOptions!.IdempotencyKey);
        Assert.Equal("cus_123", portal.Options!.Customer);
        Assert.Equal("https://elsacloud.app/dashboard", portal.Options.ReturnUrl);
    }

    [Fact]
    public async Task Cleanup_uses_durable_idempotency_key_and_confirms_absence()
    {
        var cleanup = new RecordingCleanupGateway();
        var provider = CreateProvider(cleanup: cleanup);

        var result = await provider.CleanupAsync(new(
            OrganizationId, Guid.NewGuid(), "cleanup-key", "stripe", "cus_123", "sub_123", 1));

        Assert.Equal(OrganizationBillingCleanupOutcome.ConfirmedAbsent, result);
        Assert.Equal("sub_123", cleanup.SubscriptionReference);
        Assert.Equal("cleanup-key", cleanup.RequestOptions!.IdempotencyKey);
    }

    [Fact]
    public async Task Cleanup_without_a_subscription_reference_does_not_confirm_remote_absence()
    {
        var cleanup = new RecordingCleanupGateway();
        var provider = CreateProvider(cleanup: cleanup);

        var result = await provider.CleanupAsync(new(
            OrganizationId, Guid.NewGuid(), "cleanup-key", "stripe", "cus_123", null, 1));

        Assert.Equal(OrganizationBillingCleanupOutcome.Unknown, result);
        Assert.Null(cleanup.SubscriptionReference);
    }

    [Fact]
    public async Task Cleanup_gateway_confirms_an_already_canceled_subscription_without_canceling_again()
    {
        var http = new RecordingStripeHttpClient((HttpStatusCode.OK, "canceled"));
        var gateway = new StripeSubscriptionCleanupGateway(() => new StripeClient("sk_test_no_network", httpClient: http));

        var confirmed = await gateway.CancelOrConfirmAbsentAsync("sub_123", new RequestOptions(), CancellationToken.None);

        Assert.True(confirmed);
        Assert.Equal(["GET"], http.Methods);
    }

    [Fact]
    public async Task Cleanup_gateway_cancels_an_existing_subscription_and_requires_canceled_status()
    {
        var http = new RecordingStripeHttpClient((HttpStatusCode.OK, "active"), (HttpStatusCode.OK, "canceled"));
        var gateway = new StripeSubscriptionCleanupGateway(() => new StripeClient("sk_test_no_network", httpClient: http));

        var confirmed = await gateway.CancelOrConfirmAbsentAsync("sub_123", new RequestOptions(), CancellationToken.None);

        Assert.True(confirmed);
        Assert.Equal(["GET", "DELETE"], http.Methods);
    }

    [Fact]
    public async Task Cleanup_gateway_confirms_a_subscription_that_stripe_no_longer_has()
    {
        var http = new RecordingStripeHttpClient((HttpStatusCode.NotFound, "missing"));
        var gateway = new StripeSubscriptionCleanupGateway(() => new StripeClient("sk_test_no_network", httpClient: http));

        var confirmed = await gateway.CancelOrConfirmAbsentAsync("sub_123", new RequestOptions(), CancellationToken.None);

        Assert.True(confirmed);
        Assert.Equal(["GET"], http.Methods);
    }

    [Fact]
    public async Task Cleanup_gateway_does_not_confirm_when_cancel_has_not_ended_the_subscription()
    {
        var http = new RecordingStripeHttpClient((HttpStatusCode.OK, "active"), (HttpStatusCode.OK, "active"));
        var gateway = new StripeSubscriptionCleanupGateway(() => new StripeClient("sk_test_no_network", httpClient: http));

        var confirmed = await gateway.CancelOrConfirmAbsentAsync("sub_123", new RequestOptions(), CancellationToken.None);

        Assert.False(confirmed);
        Assert.Equal(["GET", "DELETE"], http.Methods);
    }

    [Fact]
    public void Webhook_signature_is_verified_before_paused_subscription_is_normalized()
    {
        var provider = CreateProvider();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var json = $"{{\"id\":\"evt_paused\",\"object\":\"event\",\"api_version\":\"2026-08-26.dahlia\",\"created\":{timestamp},\"type\":\"customer.subscription.paused\",\"data\":{{\"object\":{{\"id\":\"sub_123\",\"object\":\"subscription\",\"customer\":\"cus_123\",\"status\":\"paused\",\"metadata\":{{\"elsa_control_organization_id\":\"{OrganizationId:D}\"}}}}}}}}";
        var signature = EventUtility.GenerateSignatureHeader(json, WebhookSecret, timestamp);

        var result = provider.VerifyAndNormalizeWebhook(
            System.Text.Encoding.UTF8.GetBytes(json),
            signature,
            DateTimeOffset.FromUnixTimeSeconds(timestamp));

        Assert.Equal(BillingWebhookNormalizationStatus.Known, result.Status);
        Assert.Equal(OrganizationSubscriptionState.Constrained, result.Event!.State);
        Assert.Equal("sub_123", result.Event.ProviderSubscriptionReference);
    }

    [Fact]
    public void Resumed_subscription_is_active_when_status_is_active()
    {
        var provider = CreateProvider();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var json = $"{{\"id\":\"evt_resumed\",\"object\":\"event\",\"api_version\":\"2026-08-26.dahlia\",\"created\":{timestamp},\"type\":\"customer.subscription.resumed\",\"data\":{{\"object\":{{\"id\":\"sub_123\",\"object\":\"subscription\",\"customer\":\"cus_123\",\"status\":\"active\",\"metadata\":{{\"elsa_control_organization_id\":\"{OrganizationId:D}\"}}}}}}}}";
        var signature = EventUtility.GenerateSignatureHeader(json, WebhookSecret, timestamp);

        var result = provider.VerifyAndNormalizeWebhook(
            System.Text.Encoding.UTF8.GetBytes(json),
            signature,
            DateTimeOffset.FromUnixTimeSeconds(timestamp));

        Assert.Equal(BillingWebhookNormalizationStatus.Known, result.Status);
        Assert.Equal(OrganizationSubscriptionState.Active, result.Event!.State);
    }

    [Fact]
    public void Deleted_subscription_is_suspended_even_if_payload_status_is_active()
    {
        var provider = CreateProvider();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var json = $"{{\"id\":\"evt_deleted\",\"object\":\"event\",\"api_version\":\"2026-08-26.dahlia\",\"created\":{timestamp},\"type\":\"customer.subscription.deleted\",\"data\":{{\"object\":{{\"id\":\"sub_123\",\"object\":\"subscription\",\"customer\":\"cus_123\",\"status\":\"active\",\"metadata\":{{\"elsa_control_organization_id\":\"{OrganizationId:D}\"}}}}}}}}";
        var signature = EventUtility.GenerateSignatureHeader(json, WebhookSecret, timestamp);

        var result = provider.VerifyAndNormalizeWebhook(
            System.Text.Encoding.UTF8.GetBytes(json),
            signature,
            DateTimeOffset.FromUnixTimeSeconds(timestamp));

        Assert.Equal(BillingWebhookNormalizationStatus.Known, result.Status);
        Assert.Equal(OrganizationSubscriptionState.Suspended, result.Event!.State);
    }

    [Fact]
    public void Unsupported_subscription_event_is_unknown_even_if_payload_status_is_active()
    {
        var provider = CreateProvider();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var json = $"{{\"id\":\"evt_future_subscription\",\"object\":\"event\",\"api_version\":\"2026-08-26.dahlia\",\"created\":{timestamp},\"type\":\"customer.subscription.foo\",\"data\":{{\"object\":{{\"id\":\"sub_123\",\"object\":\"subscription\",\"customer\":\"cus_123\",\"status\":\"active\",\"metadata\":{{\"elsa_control_organization_id\":\"{OrganizationId:D}\"}}}}}}}}";
        var signature = EventUtility.GenerateSignatureHeader(json, WebhookSecret, timestamp);

        var result = provider.VerifyAndNormalizeWebhook(
            System.Text.Encoding.UTF8.GetBytes(json),
            signature,
            DateTimeOffset.FromUnixTimeSeconds(timestamp));

        Assert.Equal(BillingWebhookNormalizationStatus.Unknown, result.Status);
        Assert.Null(result.Event!.State);
    }

    [Fact]
    public void Supported_subscription_event_without_identity_is_invalid()
    {
        var provider = CreateProvider();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var json = $"{{\"id\":\"evt_missing_identity\",\"object\":\"event\",\"api_version\":\"2026-08-26.dahlia\",\"created\":{timestamp},\"type\":\"customer.subscription.updated\",\"data\":{{\"object\":{{\"object\":\"subscription\",\"status\":\"active\",\"metadata\":{{\"elsa_control_organization_id\":\"{OrganizationId:D}\"}}}}}}}}";
        var signature = EventUtility.GenerateSignatureHeader(json, WebhookSecret, timestamp);

        var result = provider.VerifyAndNormalizeWebhook(
            System.Text.Encoding.UTF8.GetBytes(json),
            signature,
            DateTimeOffset.FromUnixTimeSeconds(timestamp));

        Assert.Equal(BillingWebhookNormalizationStatus.Invalid, result.Status);
        Assert.Null(result.Event);
    }

    [Fact]
    public void Correlated_unsupported_checkout_event_is_recordable_but_not_projected()
    {
        var provider = CreateProvider();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var json = $"{{\"id\":\"evt_checkout\",\"object\":\"event\",\"api_version\":\"2026-08-26.dahlia\",\"created\":{timestamp},\"type\":\"checkout.session.completed\",\"data\":{{\"object\":{{\"id\":\"cs_123\",\"object\":\"checkout.session\",\"client_reference_id\":\"{OrganizationId:D}\",\"customer\":\"cus_123\",\"metadata\":{{\"elsa_control_organization_id\":\"{OrganizationId:D}\"}}}}}}}}";
        var signature = EventUtility.GenerateSignatureHeader(json, WebhookSecret, timestamp);

        var result = provider.VerifyAndNormalizeWebhook(
            System.Text.Encoding.UTF8.GetBytes(json),
            signature,
            DateTimeOffset.FromUnixTimeSeconds(timestamp));

        Assert.Equal(BillingWebhookNormalizationStatus.Unknown, result.Status);
        Assert.Null(result.Event!.State);
        Assert.Equal(OrganizationId, result.Event.OrganizationId);
    }

    [Fact]
    public void Invalid_signature_fails_closed_without_normalization()
    {
        var provider = CreateProvider();
        var result = provider.VerifyAndNormalizeWebhook(
            System.Text.Encoding.UTF8.GetBytes("{\"id\":\"evt_bad\"}"),
            "t=1,v1=bad",
            DateTimeOffset.UtcNow);

        Assert.Equal(BillingWebhookNormalizationStatus.Invalid, result.Status);
        Assert.Null(result.Event);
    }

    [Fact]
    public void Mismatched_stripe_api_version_fails_closed_after_signature_verification()
    {
        var provider = CreateProvider();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var json = $"{{\"id\":\"evt_old_version\",\"object\":\"event\",\"api_version\":\"2019-01-01\",\"created\":{timestamp},\"type\":\"customer.subscription.updated\",\"data\":{{\"object\":{{\"id\":\"sub_123\",\"object\":\"subscription\",\"customer\":\"cus_123\",\"status\":\"active\",\"metadata\":{{\"elsa_control_organization_id\":\"{OrganizationId:D}\"}}}}}}}}";
        var signature = EventUtility.GenerateSignatureHeader(json, WebhookSecret, timestamp);

        var result = provider.VerifyAndNormalizeWebhook(
            System.Text.Encoding.UTF8.GetBytes(json),
            signature,
            DateTimeOffset.FromUnixTimeSeconds(timestamp));

        Assert.Equal(BillingWebhookNormalizationStatus.Invalid, result.Status);
        Assert.Null(result.Event);
    }

    [Fact]
    public void Signed_malformed_event_fails_closed_without_persistence_facts()
    {
        var provider = CreateProvider();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const string json = "{\"id\":\"evt_malformed\",\"object\":\"event\"}";
        var signature = EventUtility.GenerateSignatureHeader(json, WebhookSecret, timestamp);

        var result = provider.VerifyAndNormalizeWebhook(
            System.Text.Encoding.UTF8.GetBytes(json),
            signature,
            DateTimeOffset.FromUnixTimeSeconds(timestamp));

        Assert.Equal(BillingWebhookNormalizationStatus.Invalid, result.Status);
        Assert.Null(result.Event);
    }

    [Fact]
    public async Task Checkout_http_failure_is_wrapped_as_a_stable_provider_error()
    {
        var provider = CreateProvider(checkout: new ThrowingCheckoutGateway(new HttpRequestException("transport secret")));

        var exception = await Assert.ThrowsAsync<BillingProviderUnavailableException>(() => provider.CreateCheckoutSessionAsync(new BillingCheckoutSessionRequest(
            OrganizationId,
            "price_opaque_default",
            "https://console.test/billing/success",
            "https://console.test/billing/cancel",
            DateTimeOffset.UtcNow.AddDays(14))));

        Assert.Equal("The Stripe billing provider could not create a checkout session.", exception.Message);
    }

    [Fact]
    public async Task Portal_timeout_is_wrapped_as_a_stable_provider_error()
    {
        var provider = CreateProvider(portal: new ThrowingPortalGateway(new OperationCanceledException("provider timeout")));

        var exception = await Assert.ThrowsAsync<BillingProviderUnavailableException>(() => provider.CreateCustomerPortalSessionAsync(new BillingCustomerPortalSessionRequest(
            OrganizationId,
            "cus_123",
            "https://console.test/billing")));

        Assert.Equal("The Stripe billing provider could not create a portal session.", exception.Message);
    }

    private static StripeBillingProvider CreateProvider(
        IStripeCheckoutSessionGateway? checkout = null,
        IStripeCustomerPortalGateway? portal = null,
        IStripeSubscriptionCleanupGateway? cleanup = null) =>
        new(
            Options.Create(new StripeBillingOptions
            {
                Enabled = true,
                SecretKey = "sk_test_no_network",
                WebhookSigningSecret = WebhookSecret,
                DefaultPriceId = "price_configured",
                CheckoutSuccessUrl = "https://console.test/success",
                CheckoutCancelUrl = "https://console.test/cancel",
                PortalReturnUrl = "https://console.test/billing"
            }),
            checkout ?? new RecordingCheckoutGateway(),
            portal ?? new RecordingPortalGateway(),
            cleanup ?? new RecordingCleanupGateway());

    private sealed class RecordingCheckoutGateway : IStripeCheckoutSessionGateway
    {
        public global::Stripe.Checkout.SessionCreateOptions? Options { get; private set; }
        public RequestOptions? RequestOptions { get; private set; }

        public Task<global::Stripe.Checkout.Session> CreateAsync(global::Stripe.Checkout.SessionCreateOptions options, RequestOptions requestOptions, CancellationToken cancellationToken)
        {
            Options = options;
            RequestOptions = requestOptions;
            return Task.FromResult(new global::Stripe.Checkout.Session { Url = "https://checkout.stripe.test/session" });
        }
    }

    private sealed class RecordingPortalGateway : IStripeCustomerPortalGateway
    {
        public global::Stripe.BillingPortal.SessionCreateOptions? Options { get; private set; }
        public RequestOptions? RequestOptions { get; private set; }

        public Task<global::Stripe.BillingPortal.Session> CreateAsync(global::Stripe.BillingPortal.SessionCreateOptions options, RequestOptions requestOptions, CancellationToken cancellationToken) =>
            CaptureAsync(options, requestOptions);

        private Task<global::Stripe.BillingPortal.Session> CaptureAsync(global::Stripe.BillingPortal.SessionCreateOptions options, RequestOptions requestOptions)
        {
            Options = options;
            RequestOptions = requestOptions;
            return Task.FromResult(new global::Stripe.BillingPortal.Session { Url = "https://billing.stripe.test/session" });
        }
    }

    private sealed class ThrowingCheckoutGateway(Exception exception) : IStripeCheckoutSessionGateway
    {
        public Task<global::Stripe.Checkout.Session> CreateAsync(global::Stripe.Checkout.SessionCreateOptions options, RequestOptions requestOptions, CancellationToken cancellationToken) =>
            Task.FromException<global::Stripe.Checkout.Session>(exception);
    }

    private sealed class ThrowingPortalGateway(Exception exception) : IStripeCustomerPortalGateway
    {
        public Task<global::Stripe.BillingPortal.Session> CreateAsync(global::Stripe.BillingPortal.SessionCreateOptions options, RequestOptions requestOptions, CancellationToken cancellationToken) =>
            Task.FromException<global::Stripe.BillingPortal.Session>(exception);
    }

    private sealed class RecordingCleanupGateway : IStripeSubscriptionCleanupGateway
    {
        public string? SubscriptionReference { get; private set; }
        public RequestOptions? RequestOptions { get; private set; }

        public Task<bool> CancelOrConfirmAbsentAsync(string subscriptionReference, RequestOptions requestOptions, CancellationToken cancellationToken)
        {
            SubscriptionReference = subscriptionReference;
            RequestOptions = requestOptions;
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingStripeHttpClient(params (HttpStatusCode Status, string SubscriptionStatus)[] responses) : IHttpClient
    {
        private readonly Queue<(HttpStatusCode Status, string SubscriptionStatus)> _responses = new(responses);
        public List<string> Methods { get; } = [];

        public Task<StripeResponse> MakeRequestAsync(StripeRequest request, CancellationToken cancellationToken = default)
        {
            Methods.Add(request.Method.ToString().ToUpperInvariant());
            var response = _responses.Dequeue();
            var content = response.Status == HttpStatusCode.NotFound
                ? "{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"No such subscription\"}}"
                : $"{{\"id\":\"sub_123\",\"object\":\"subscription\",\"status\":\"{response.SubscriptionStatus}\"}}";
            return Task.FromResult(new StripeResponse(response.Status, new HttpResponseMessage().Headers, content));
        }

        public Task<StripeStreamedResponse> MakeStreamingRequestAsync(StripeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The cleanup gateway does not make streaming requests.");
    }
}
