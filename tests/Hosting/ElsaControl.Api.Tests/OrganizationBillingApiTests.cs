using System.Net;
using System.Net.Http.Json;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.Api.OrganizationBilling;
using ElsaControl.Api.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElsaControl.Api.Tests;

public sealed class OrganizationBillingApiTests : IAsyncLifetime
{
    private readonly FakeBillingProvider _provider = new();
    private readonly ControlApiTestApplication _app;

    public OrganizationBillingApiTests()
    {
        _app = new(
            new Dictionary<string, string?>
            {
                ["Billing:Stripe:Enabled"] = "true",
                ["Billing:Stripe:SecretKey"] = "sk_test_fake",
                ["Billing:Stripe:DefaultPriceId"] = "price_server_default",
                ["Billing:Stripe:CheckoutSuccessUrl"] = "https://console.test/success",
                ["Billing:Stripe:CheckoutCancelUrl"] = "https://console.test/cancel",
                ["Billing:Stripe:PortalReturnUrl"] = "https://console.test/billing"
            },
            services =>
            {
                services.RemoveAll<IBillingProvider>();
                services.AddSingleton<IBillingProvider>(_provider);
            });
    }

    [Fact]
    public async Task Checkout_starts_control_plane_trial_before_calling_provider()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = _app.CreateControlIdentityClient(subject: "billing-owner");
        var organizationId = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!.Organizations.Single().Id;

        var response = await owner.PostControlJsonAsync($"/api/organizations/{organizationId}/billing/checkout", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadControlJsonAsync<OrganizationBillingSessionResponse>();
        Assert.Equal("https://checkout.fake.test/session", session!.Url);
        Assert.NotNull(_provider.LastCheckout);
        Assert.Equal(organizationId, _provider.LastCheckout!.OrganizationId);
        Assert.Equal("price_server_default", _provider.LastCheckout.PriceReference);

        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var subscription = await db.OrganizationSubscriptions.SingleAsync(x => x.OrganizationId == organizationId);
        Assert.Equal(OrganizationSubscriptionState.Trial, subscription.State);
        Assert.Equal(TimeSpan.FromDays(14), subscription.TrialEndsAt - subscription.TrialStartedAt);
    }

    [Fact]
    public async Task Checkout_rejects_a_tombstoned_subscription_before_calling_provider()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = _app.CreateControlIdentityClient(subject: "billing-terminal-owner");
        var organizationId = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!.Organizations.Single().Id;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var store = new OrganizationBillingStore(db);
            await store.StartTrialAsync(organizationId, BillingProviderNames.Stripe, DateTimeOffset.UtcNow.AddDays(-2));
            await store.RequestDeletionAsync(organizationId, DateTimeOffset.UtcNow.AddDays(-1));
            var work = Assert.IsType<OrganizationBillingCleanupWorkItem>(
                await store.TryClaimCleanupAsync("test-worker", DateTimeOffset.UtcNow));
            await store.CompleteCleanupAsync(new(
                work.Id,
                work.OrganizationId,
                work.SubscriptionId,
                work.LeaseToken,
                OrganizationBillingCleanupOutcome.ConfirmedAbsent,
                DateTimeOffset.UtcNow));
        }

        var response = await owner.PostControlJsonAsync($"/api/organizations/{organizationId}/billing/checkout", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("billing.subscription-terminal", (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["code"]);
        Assert.Null(_provider.LastCheckout);
    }

    [Fact]
    public async Task Checkout_for_an_internally_granted_organization_returns_terminal_conflict_before_calling_provider()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = _app.CreateControlIdentityClient(subject: "billing-internal-owner");
        var organizationId = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!.Organizations.Single().Id;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var store = new OrganizationBillingStore(db);
            var granted = await store.GrantInternalEntitlementAsync(
                new(organizationId, new("Internal dogfood grant", 2, DateTimeOffset.UtcNow.AddDays(30)), "operator"),
                DateTimeOffset.UtcNow);
            Assert.Equal(OrganizationInternalEntitlementOutcome.Granted, granted.Outcome);
        }

        var response = await owner.PostControlJsonAsync($"/api/organizations/{organizationId}/billing/checkout", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("billing.subscription-terminal", (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["code"]);
        Assert.Null(_provider.LastCheckout);
        await using var verifyScope = _app.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var subscription = await verifyDb.OrganizationSubscriptions.SingleAsync(x => x.OrganizationId == organizationId);
        Assert.Equal(BillingProviderNames.Internal, subscription.Provider);
        Assert.Equal(0, await verifyDb.OrganizationBillingCleanups.CountAsync());
    }

    [Fact]
    public async Task Checkout_fails_closed_before_trial_when_the_injected_provider_is_not_stripe()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = _app.CreateControlIdentityClient(subject: "billing-mismatch-checkout-owner");
        var organizationId = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!.Organizations.Single().Id;
        _provider.Provider = "fake";

        var response = await owner.PostControlJsonAsync($"/api/organizations/{organizationId}/billing/checkout", new { });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(_provider.LastCheckout);
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Empty(await db.OrganizationSubscriptions.ToListAsync());
    }

    [Fact]
    public async Task Portal_fails_closed_before_provider_call_when_the_injected_provider_is_not_stripe()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = _app.CreateControlIdentityClient(subject: "billing-mismatch-portal-owner");
        var organizationId = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!.Organizations.Single().Id;
        var checkout = await owner.PostControlJsonAsync($"/api/organizations/{organizationId}/billing/checkout", new { });
        Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);
        _provider.Provider = "fake";

        var response = await owner.PostControlJsonAsync($"/api/organizations/{organizationId}/billing/portal", new { });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, _provider.PortalCalls);
    }

    [Fact]
    public async Task Organization_member_cannot_start_checkout_and_unknown_webhook_is_audit_only()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = _app.CreateControlIdentityClient(subject: "billing-owner-2");
        var organizationId = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!.Organizations.Single().Id;
        await AddMemberAsync(organizationId, "billing-member");
        var member = _app.CreateControlIdentityClient(subject: "billing-member");

        var forbidden = await member.PostControlJsonAsync($"/api/organizations/{organizationId}/billing/checkout", new { });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        _provider.WebhookResult = BillingWebhookNormalizationResult.UnknownEvent(new BillingProviderEvent(
            organizationId,
            BillingProviderNames.Stripe,
            "evt_unknown",
            "checkout.session.completed",
            null,
            DateTimeOffset.UtcNow,
            "sha256:" + new string('a', 64)));
        var webhook = await PostWebhookAsync("raw-unknown-body");

        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        Assert.Equal("recorded-unknown", (await webhook.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["status"]);
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(1, await db.BillingProviderEvents.CountAsync());
        Assert.Null(await db.OrganizationSubscriptions.SingleOrDefaultAsync(x => x.OrganizationId == organizationId));
        var persistedText = await ReadPersistedBillingTextAsync(db);
        Assert.DoesNotContain("raw-unknown-body", persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain("signature-is-never-persisted", persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain("https://checkout.fake.test/session", persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain("https://portal.fake.test/session", persistedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Billing_status_is_tenant_safe_and_redacts_provider_metadata()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = _app.CreateControlIdentityClient(subject: "billing-status-owner");
        var organizationId = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!.Organizations.Single().Id;
        await AddEntitlementAsync(organizationId);

        var response = await owner.GetAsync($"/api/organizations/{organizationId}/billing/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var status = await response.Content.ReadControlJsonAsync<OrganizationBillingStatusResponse>();
        Assert.NotNull(status);
        Assert.Equal(organizationId, status!.OrganizationId);
        Assert.Null(status.Subscription);
        Assert.True(status.Entitlements!.ManagedHostingEnabled);
        Assert.Equal(3, status.Entitlements.MaxWorkspaces);
        Assert.Equal(2, status.Entitlements.MaxInstances);
        Assert.True(status.Capacity.WorkspacesUsed > 0);
        Assert.Equal(2, status.Capacity.ManagedInstancesLimit);
        Assert.Equal(3, status.Capacity.WorkspacesLimit);
        Assert.Contains("managed-hosting", status.Capabilities);

        var serialized = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("provider", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("customer", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk_test_fake", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("checkout.fake.test", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Billing_status_allows_organization_member_without_allowing_billing_mutation()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = _app.CreateControlIdentityClient(subject: "billing-status-owner-member-check");
        var organizationId = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!.Organizations.Single().Id;
        await AddMemberAsync(organizationId, "billing-status-member");

        var member = _app.CreateControlIdentityClient(subject: "billing-status-member");
        var response = await member.GetAsync($"/api/organizations/{organizationId}/billing/");
        var checkout = await member.PostAsJsonAsync(
            $"/api/organizations/{organizationId}/billing/checkout",
            new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, checkout.StatusCode);
    }

    [Fact]
    public async Task Billing_status_does_not_disclose_another_organization()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = _app.CreateControlIdentityClient(subject: "billing-status-tenant-owner");
        var organizationId = Guid.NewGuid();

        var response = await owner.GetAsync($"/api/organizations/{organizationId}/billing/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("no-cache", response.Headers.Pragma.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        var body = await response.Content.ReadControlJsonAsync<Dictionary<string, string>>();
        Assert.Equal("organization.not-found", body!["code"]);
        Assert.DoesNotContain(organizationId.ToString("D"), await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Only_billing_administrators_can_request_early_deletion_and_the_request_is_idempotent()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = _app.CreateControlIdentityClient(subject: "billing-delete-owner");
        var organizationId = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!.Organizations.Single().Id;
        await owner.PostControlJsonAsync($"/api/organizations/{organizationId}/billing/checkout", new { });
        await AddMemberAsync(organizationId, "billing-delete-member");
        var member = _app.CreateControlIdentityClient(subject: "billing-delete-member");

        var forbidden = await member.PostControlJsonAsync($"/api/organizations/{organizationId}/billing/delete", new { });
        var first = await owner.PostControlJsonAsync($"/api/organizations/{organizationId}/billing/delete", new { });
        var repeated = await owner.PostControlJsonAsync($"/api/organizations/{organizationId}/billing/delete", new { });

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore);
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Single(await db.OrganizationBillingCleanups.Where(x => x.OrganizationId == organizationId).ToListAsync());
        Assert.Equal(3, await db.OrganizationBillingLifecycleNotices.CountAsync(x => x.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Supported_webhook_replay_and_out_of_order_are_deterministic()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = _app.CreateControlIdentityClient(subject: "billing-owner-3");
        var organizationId = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!.Organizations.Single().Id;
        await owner.PostControlJsonAsync($"/api/organizations/{organizationId}/billing/checkout", new { });
        var first = new BillingProviderEvent(organizationId, BillingProviderNames.Stripe, "evt-active", "customer.subscription.updated", OrganizationSubscriptionState.Active, DateTimeOffset.UtcNow, Sha256("active"));
        _provider.WebhookResult = BillingWebhookNormalizationResult.KnownEvent(first);

        var applied = await PostWebhookAsync("raw-supported-body");
        var replayed = await PostWebhookAsync("raw-supported-body");
        var older = first with { ProviderEventId = "evt-older", OccurredAt = first.OccurredAt.AddMinutes(-1), State = OrganizationSubscriptionState.PastDue, EventType = "customer.subscription.updated", EventHash = Sha256("older") };
        _provider.WebhookResult = BillingWebhookNormalizationResult.KnownEvent(older);
        var outOfOrder = await PostWebhookAsync("raw-older-body");

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, outOfOrder.StatusCode);
        Assert.Equal("applied", (await applied.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["status"]);
        Assert.Equal("replayed", (await replayed.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["status"]);
        Assert.Equal("ignored-out-of-order", (await outOfOrder.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["status"]);

        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(2, await db.BillingProviderEvents.CountAsync());
        var persistedText = await ReadPersistedBillingTextAsync(db);
        Assert.DoesNotContain("raw-supported-body", persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain("Stripe-Signature", persistedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://checkout.fake.test/session", persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain("https://portal.fake.test/session", persistedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stripe_webhook_fails_closed_when_the_injected_provider_is_not_stripe()
    {
        _provider.Provider = "fake";

        var response = await PostWebhookAsync("provider-mismatch-body");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, _provider.WebhookVerificationCalls);
    }

    [Fact]
    public async Task Conflicting_webhook_facts_return_a_stable_client_error()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = _app.CreateControlIdentityClient(subject: "billing-conflict-owner");
        var organizationId = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!.Organizations.Single().Id;
        await owner.PostControlJsonAsync($"/api/organizations/{organizationId}/billing/checkout", new { });

        var first = new BillingProviderEvent(
            organizationId,
            BillingProviderNames.Stripe,
            "evt-conflicting-facts",
            "customer.subscription.updated",
            OrganizationSubscriptionState.Active,
            DateTimeOffset.UtcNow,
            Sha256("first-facts"));
        _provider.WebhookResult = BillingWebhookNormalizationResult.KnownEvent(first);
        var applied = await PostWebhookAsync("first-facts-body");

        var conflicting = first with
        {
            State = OrganizationSubscriptionState.PastDue,
            EventHash = Sha256("conflicting-facts")
        };
        _provider.WebhookResult = BillingWebhookNormalizationResult.KnownEvent(conflicting);
        var conflict = await PostWebhookAsync("conflicting-facts-body");

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await conflict.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("webhook.conflict", body!["code"]);

        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(1, await db.BillingProviderEvents.CountAsync());
    }

    [Fact]
    public async Task Invalid_and_malformed_webhook_results_fail_closed_without_persistence()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = _app.CreateControlIdentityClient(subject: "billing-owner-4");
        var organizationId = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!.Organizations.Single().Id;
        _provider.WebhookResult = BillingWebhookNormalizationResult.Invalid("webhook.signature-invalid");

        var invalid = await PostWebhookAsync("sensitive-raw-body");
        _provider.WebhookResult = new BillingWebhookNormalizationResult(BillingWebhookNormalizationStatus.Known, null, "webhook.malformed");
        var malformed = await PostWebhookAsync("sensitive-raw-body-2");
        _provider.WebhookResult = BillingWebhookNormalizationResult.UnknownEvent(new BillingProviderEvent(
            organizationId,
            BillingProviderNames.Stripe,
            "evt-misclassified",
            "checkout.session.completed",
            OrganizationSubscriptionState.Active,
            DateTimeOffset.UtcNow,
            Sha256("misclassified-raw-body")));
        var misclassified = await PostWebhookAsync("misclassified-raw-body");

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, misclassified.StatusCode);
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(0, await db.BillingProviderEvents.CountAsync());
        var persistedText = await ReadPersistedBillingTextAsync(db);
        Assert.DoesNotContain("sensitive-raw-body", persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-raw-body-2", persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain("misclassified-raw-body", persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain("signature-is-never-persisted", persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain("https://checkout.fake.test/session", persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain("https://portal.fake.test/session", persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain(organizationId.ToString("D"), persistedText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Webhook_for_missing_organization_returns_safe_bad_request_without_persistence()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var missingOrganizationId = Guid.NewGuid();
        _provider.WebhookResult = BillingWebhookNormalizationResult.KnownEvent(new BillingProviderEvent(
            missingOrganizationId,
            BillingProviderNames.Stripe,
            "evt-missing-organization",
            "customer.subscription.updated",
            OrganizationSubscriptionState.Active,
            DateTimeOffset.UtcNow,
            Sha256("missing-organization")));

        var response = await PostWebhookAsync("missing-organization-body");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("webhook.invalid", body!["code"]);
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Empty(await db.BillingProviderEvents.ToListAsync());
    }

    [Fact]
    public async Task Disabled_stripe_webhook_returns_safe_bad_request_without_constructing_a_client()
    {
        await using var app = new ControlApiTestApplication();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/billing/webhooks/stripe")
        {
            Content = new StringContent("{}")
        };

        var response = await app.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("Stripe", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await ((IAsyncDisposable)_app).DisposeAsync();

    private async Task AddMemberAsync(Guid organizationId, string subject)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var account = new Account { DisplayName = subject, Email = $"{subject}@example.test" };
        account.ExternalIdentities.Add(new ExternalIdentity
        {
            Account = account,
            Issuer = ControlApiTestApplication.TestControlIdentityIssuer,
            Subject = subject,
            DisplayName = subject,
            Email = account.Email
        });
        db.Accounts.Add(account);
        db.OrganizationMemberships.Add(new OrganizationMembership
        {
            Account = account,
            OrganizationId = organizationId,
            Role = OrganizationRole.Member
        });
        await db.SaveChangesAsync();
    }

    private async Task AddEntitlementAsync(Guid organizationId)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = organizationId,
            CanCreateCustomSources = true,
            MaxSources = 10,
            MaxWorkspaces = 3,
            MaxInstances = 2,
            ManagedHostingEnabled = true,
            DeploymentTargetsEnabled = true,
            PrivateFeedsEnabled = true
        });
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> PostWebhookAsync(string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/billing/webhooks/stripe")
        {
            Content = new StringContent(body)
        };
        request.Headers.Add("Stripe-Signature", "signature-is-never-persisted");
        return await _app.CreateClient().SendAsync(request);
    }

    private static string Sha256(string value) =>
        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task<string> ReadPersistedBillingTextAsync(CatalogDbContext db)
    {
        var inbox = await db.BillingProviderEvents.AsNoTracking().ToListAsync();
        var audits = await db.OrganizationAuditRecords.AsNoTracking().ToListAsync();
        var subscriptions = await db.OrganizationSubscriptions.AsNoTracking().ToListAsync();
        return string.Join(" ", inbox.SelectMany(x => new[]
        {
            x.Provider, x.ProviderEventId, x.EventType, x.EventHash,
            x.ProviderCustomerReference ?? "", x.ProviderSubscriptionReference ?? "", x.RejectionCode ?? ""
        }).Concat(audits.SelectMany(x => new[]
        {
            x.OperatorSubject ?? "", x.Action.ToString(), x.TargetType, x.TargetId, x.Summary
        })).Concat(subscriptions.SelectMany(x => new[]
        {
            x.Provider, x.ProviderCustomerReference ?? "", x.ProviderSubscriptionReference ?? "", x.LastProviderEventId ?? ""
        })));
    }

    private sealed class FakeBillingProvider : IBillingProvider
    {
        public string Provider { get; set; } = BillingProviderNames.Stripe;
        public int WebhookVerificationCalls { get; private set; }
        public int PortalCalls { get; private set; }
        public BillingCheckoutSessionRequest? LastCheckout { get; private set; }
        public BillingWebhookNormalizationResult WebhookResult { get; set; } = BillingWebhookNormalizationResult.Invalid("webhook.invalid");

        public Task<BillingSessionLink> CreateCheckoutSessionAsync(BillingCheckoutSessionRequest request, CancellationToken cancellationToken = default)
        {
            LastCheckout = request;
            return Task.FromResult(new BillingSessionLink("https://checkout.fake.test/session"));
        }

        public Task<BillingSessionLink> CreateCustomerPortalSessionAsync(BillingCustomerPortalSessionRequest request, CancellationToken cancellationToken = default) =>
            CreatePortalSessionAsync();

        private Task<BillingSessionLink> CreatePortalSessionAsync()
        {
            PortalCalls++;
            return Task.FromResult(new BillingSessionLink("https://portal.fake.test/session"));
        }

        public BillingWebhookNormalizationResult VerifyAndNormalizeWebhook(ReadOnlyMemory<byte> rawBody, string signature, DateTimeOffset receivedAt)
        {
            WebhookVerificationCalls++;
            return WebhookResult;
        }
    }
}
