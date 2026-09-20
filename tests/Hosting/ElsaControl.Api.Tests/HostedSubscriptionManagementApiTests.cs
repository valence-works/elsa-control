using System.Net;
using System.Net.Http.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.OrganizationBilling;
using ElsaControl.Api.Workspace;
using ElsaControl.Billing.Stripe;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElsaControl.Api.Tests;

public sealed class HostedSubscriptionManagementApiTests : IAsyncLifetime
{
    private const string CloudDashboard = "https://elsacloud.app/dashboard";
    private const string BffClientId = "elsa-cloud-lovable-bff";
    private readonly FakeBillingProvider _provider = new();
    private readonly ControlApiTestApplication _app;

    public HostedSubscriptionManagementApiTests()
    {
        _app = new(
            new Dictionary<string, string?>
            {
                ["Billing:Stripe:Enabled"] = "true",
                ["Billing:Stripe:SecretKey"] = "sk_test_fake",
                ["Billing:Stripe:DefaultPriceId"] = "price_server_default",
                ["Billing:Stripe:CheckoutSuccessUrl"] = "https://console.test/success",
                ["Billing:Stripe:CheckoutCancelUrl"] = "https://console.test/cancel",
                ["Billing:Stripe:PortalReturnUrl"] = "https://console.test/billing",
                ["Billing:Stripe:CloudPortalReturnUrl"] = CloudDashboard,
                [$"{CloudBffOptions.ConfigurationSection}:Enabled"] = "true",
                [$"{CloudBffOptions.ConfigurationSection}:ClientId"] = BffClientId,
                [$"{CloudBffOptions.ConfigurationSection}:Scope"] = CloudBffDefaults.DefaultScope
            },
            services =>
            {
                services.RemoveAll<IBillingProvider>();
                services.AddSingleton<IBillingProvider>(_provider);
            });
    }

    [Fact]
    public async Task Hosted_portal_opens_a_session_for_the_caller_billing_customer()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = CreateBffClient("hosted-portal-owner");
        var organizationId = await OrganizationIdAsync(owner);
        await LinkStripeCustomerAsync(organizationId, "cus_hosted_owner");

        var response = await owner.PostControlJsonAsync(
            $"/api/organizations/{organizationId:D}/billing/hosted-portal",
            new HostedPortalSessionRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var session = await response.Content.ReadControlJsonAsync<HostedPortalSessionResponse>();
        Assert.Equal("https://billing.stripe.test/hosted-session", session!.Url);
        Assert.NotNull(_provider.LastPortal);
        Assert.Equal(organizationId, _provider.LastPortal!.OrganizationId);
        Assert.Equal("cus_hosted_owner", _provider.LastPortal.ProviderCustomerReference);
        Assert.Equal(CloudDashboard, _provider.LastPortal.ReturnUrl);
        Assert.DoesNotContain("sk_test_fake", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("cus_hosted_owner", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hosted_subscription_status_is_truthful_when_billing_is_linked()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = CreateBffClient("hosted-status-owner");
        var organizationId = await OrganizationIdAsync(owner);
        await LinkStripeCustomerAsync(organizationId, "cus_hosted_status");

        var response = await owner.GetAsync($"/api/organizations/{organizationId:D}/billing/hosted-subscription");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadControlJsonAsync<HostedSubscriptionManagementResponse>();
        Assert.Equal(organizationId, status!.OrganizationId);
        Assert.Equal(nameof(OrganizationSubscriptionState.Active), status.SubscriptionState);
        Assert.True(status.BillingLinked);
        Assert.True(status.PortalAvailable);
        Assert.True(status.CanManageSubscription);
        Assert.Equal(HostedSubscriptionManagementActions.OpenPortal, status.ManagementAction);
        Assert.Equal(HostedBillingCopy.EngineDeletion.Code, status.Copy.EngineDeletion.Code);
        Assert.Contains("does not cancel", status.Copy.EngineDeletion.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not immediately delete", status.Copy.SubscriptionCancellation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(status.Copy.MissingBillingLinkage);
        var serialized = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("cus_hosted_status", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_fake", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("providerCustomerReference", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hosted_subscription_status_reports_missing_billing_linkage()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = CreateBffClient("hosted-missing-owner");
        var organizationId = await OrganizationIdAsync(owner);

        var statusResponse = await owner.GetAsync($"/api/organizations/{organizationId:D}/billing/hosted-subscription");
        var portalResponse = await owner.PostControlJsonAsync(
            $"/api/organizations/{organizationId:D}/billing/hosted-portal",
            new HostedPortalSessionRequest());

        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var status = await statusResponse.Content.ReadControlJsonAsync<HostedSubscriptionManagementResponse>();
        Assert.False(status!.BillingLinked);
        Assert.False(status.PortalAvailable);
        Assert.Equal(HostedSubscriptionManagementActions.NoBillingLinkage, status.ManagementAction);
        Assert.Equal(HostedBillingCopy.MissingBillingLinkage.Code, status.Copy.MissingBillingLinkage!.Code);
        Assert.Equal(HttpStatusCode.Conflict, portalResponse.StatusCode);
        Assert.Equal("billing.customer-not-ready", (await portalResponse.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["code"]);
        Assert.Null(_provider.LastPortal);
    }

    [Fact]
    public async Task Hosted_portal_returns_unavailable_when_the_provider_fails()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = CreateBffClient("hosted-provider-fail-owner");
        var organizationId = await OrganizationIdAsync(owner);
        await LinkStripeCustomerAsync(organizationId, "cus_hosted_fail");
        _provider.PortalException = new BillingProviderUnavailableException("The Stripe billing provider could not create a portal session.");

        var response = await owner.PostControlJsonAsync(
            $"/api/organizations/{organizationId:D}/billing/hosted-portal",
            new HostedPortalSessionRequest(CloudDashboard));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, _provider.PortalCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CloudDashboard)]
    [InlineData("https://elsacloud.app/dashboard/billing")]
    public async Task Hosted_portal_accepts_safe_cloud_return_urls(string? returnUrl)
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = CreateBffClient($"hosted-return-ok-{returnUrl ?? "default"}");
        var organizationId = await OrganizationIdAsync(owner);
        await LinkStripeCustomerAsync(organizationId, $"cus_hosted_return_{Guid.NewGuid():N}"[..24]);

        var response = await owner.PostControlJsonAsync(
            $"/api/organizations/{organizationId:D}/billing/hosted-portal",
            new HostedPortalSessionRequest(returnUrl));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(returnUrl ?? CloudDashboard, _provider.LastPortal!.ReturnUrl);
    }

    [Theory]
    [InlineData("http://elsacloud.app/dashboard")]
    [InlineData("https://user@elsacloud.app/dashboard")]
    [InlineData("https://elsacloud.app/dashboard?next=1")]
    [InlineData("https://elsacloud.app/dashboard#fragment")]
    [InlineData("https://elsacloud.app/pricing")]
    [InlineData("https://evil.test/dashboard")]
    [InlineData("https://elsacloud.app.evil.test/dashboard")]
    public async Task Hosted_portal_rejects_unsafe_return_urls(string returnUrl)
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = CreateBffClient($"hosted-return-bad-{returnUrl.GetHashCode(StringComparison.Ordinal)}");
        var organizationId = await OrganizationIdAsync(owner);
        await LinkStripeCustomerAsync(organizationId, $"cus_hosted_bad_{Guid.NewGuid():N}"[..24]);

        var response = await owner.PostControlJsonAsync(
            $"/api/organizations/{organizationId:D}/billing/hosted-portal",
            new HostedPortalSessionRequest(returnUrl));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("billing.return-url-invalid", (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["code"]);
        Assert.Null(_provider.LastPortal);
    }

    [Fact]
    public async Task Hosted_portal_does_not_open_another_customers_session()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = CreateBffClient("hosted-tenant-owner");
        var stranger = CreateBffClient("hosted-tenant-stranger");
        var organizationId = await OrganizationIdAsync(owner);
        await LinkStripeCustomerAsync(organizationId, "cus_hosted_tenant");
        var strangerOrganizationId = await OrganizationIdAsync(stranger);

        var stolen = await stranger.PostControlJsonAsync(
            $"/api/organizations/{organizationId:D}/billing/hosted-portal",
            new HostedPortalSessionRequest());
        var foreignStatus = await stranger.GetAsync($"/api/organizations/{organizationId:D}/billing/hosted-subscription");

        Assert.NotEqual(organizationId, strangerOrganizationId);
        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignStatus.StatusCode);
        Assert.Equal("organization.not-found", (await stolen.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["code"]);
        Assert.Null(_provider.LastPortal);
    }

    [Fact]
    public async Task Organization_member_can_read_hosted_status_but_cannot_open_the_portal()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = CreateBffClient("hosted-member-owner");
        var organizationId = await OrganizationIdAsync(owner);
        await LinkStripeCustomerAsync(organizationId, "cus_hosted_member");
        await AddMemberAsync(organizationId, "hosted-member", OrganizationRole.Member);
        var member = CreateBffClient("hosted-member");

        var status = await member.GetAsync($"/api/organizations/{organizationId:D}/billing/hosted-subscription");
        var portal = await member.PostControlJsonAsync(
            $"/api/organizations/{organizationId:D}/billing/hosted-portal",
            new HostedPortalSessionRequest());

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var body = await status.Content.ReadControlJsonAsync<HostedSubscriptionManagementResponse>();
        Assert.True(body!.BillingLinked);
        Assert.False(body.CanManageSubscription);
        Assert.False(body.PortalAvailable);
        Assert.Equal(HostedSubscriptionManagementActions.ViewOnly, body.ManagementAction);
        Assert.Equal(HttpStatusCode.Forbidden, portal.StatusCode);
        Assert.Null(_provider.LastPortal);
    }

    [Fact]
    public async Task Cloud_bff_cannot_use_the_control_console_portal_route()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var owner = CreateBffClient("hosted-console-portal-owner");
        var organizationId = await OrganizationIdAsync(owner);
        await LinkStripeCustomerAsync(organizationId, "cus_hosted_console");

        using var consolePortal = await owner.PostControlJsonAsync(
            $"/api/organizations/{organizationId:D}/billing/portal",
            new { });

        Assert.Equal(HttpStatusCode.Forbidden, consolePortal.StatusCode);
        Assert.Contains("cloud-bff.denied", await consolePortal.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(_provider.LastPortal);
    }

    [Fact]
    public void Engine_deletion_and_subscription_cancellation_copy_stay_distinct()
    {
        Assert.Equal("engine-deletion-leaves-subscription-active", HostedBillingCopy.EngineDeletion.Code);
        Assert.Contains("does not cancel", HostedBillingCopy.EngineDeletion.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not immediately delete", HostedBillingCopy.SubscriptionCancellation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pricing", HostedBillingCopy.EngineDeletion.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CloudDashboard, null, CloudDashboard)]
    [InlineData(CloudDashboard, CloudDashboard, CloudDashboard)]
    [InlineData(CloudDashboard, "https://elsacloud.app/dashboard/billing", "https://elsacloud.app/dashboard/billing")]
    public void Safe_return_urls_resolve_to_the_cloud_dashboard(string configured, string? requested, string expected)
    {
        Assert.True(HostedBillingReturnUrls.TryResolve(configured, requested, out var resolved));
        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData(null, CloudDashboard)]
    [InlineData("", CloudDashboard)]
    [InlineData("http://elsacloud.app/dashboard", null)]
    [InlineData(CloudDashboard, "http://elsacloud.app/dashboard")]
    [InlineData(CloudDashboard, "https://user@elsacloud.app/dashboard")]
    [InlineData(CloudDashboard, "https://elsacloud.app/dashboard?next=1")]
    [InlineData(CloudDashboard, "https://elsacloud.app/dashboard#x")]
    [InlineData(CloudDashboard, "https://elsacloud.app/pricing")]
    [InlineData(CloudDashboard, "https://evil.test/dashboard")]
    public void Unsafe_return_urls_are_rejected(string? configured, string? requested)
    {
        Assert.False(HostedBillingReturnUrls.TryResolve(configured, requested, out var resolved));
        Assert.Equal("", resolved);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await ((IAsyncDisposable)_app).DisposeAsync();

    private HttpClient CreateBffClient(string subject) =>
        _app.CreateControlIdentityClient(
            subject: subject,
            claims: new Dictionary<string, string>
            {
                ["azp"] = BffClientId,
                ["scp"] = CloudBffDefaults.DefaultScope
            });

    private static async Task<Guid> OrganizationIdAsync(HttpClient client) =>
        (await client.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!.Organizations.Single().Id;

    private async Task LinkStripeCustomerAsync(Guid organizationId, string customerReference)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var now = DateTimeOffset.UtcNow;
        var subscription = OrganizationSubscriptionLifecycle.CreateTrial(organizationId, BillingProviderNames.Stripe, now);
        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Active, now);
        subscription.ProviderCustomerReference = customerReference;
        subscription.ProviderSubscriptionReference = "sub_hosted_test";
        db.OrganizationSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
    }

    private async Task AddMemberAsync(Guid organizationId, string subject, OrganizationRole role)
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
            Role = role
        });
        await db.SaveChangesAsync();
    }

    private sealed class FakeBillingProvider : IBillingProvider
    {
        public string Provider { get; set; } = BillingProviderNames.Stripe;
        public int PortalCalls { get; private set; }
        public BillingCustomerPortalSessionRequest? LastPortal { get; private set; }
        public Exception? PortalException { get; set; }

        public Task<BillingSessionLink> CreateCheckoutSessionAsync(
            BillingCheckoutSessionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BillingSessionLink("https://checkout.fake.test/session"));

        public Task<BillingSessionLink> CreateCustomerPortalSessionAsync(
            BillingCustomerPortalSessionRequest request,
            CancellationToken cancellationToken = default)
        {
            PortalCalls++;
            LastPortal = request;
            if (PortalException is not null)
                return Task.FromException<BillingSessionLink>(PortalException);
            return Task.FromResult(new BillingSessionLink("https://billing.stripe.test/hosted-session"));
        }

        public BillingWebhookNormalizationResult VerifyAndNormalizeWebhook(
            ReadOnlyMemory<byte> rawBody,
            string signature,
            DateTimeOffset receivedAt) =>
            BillingWebhookNormalizationResult.Invalid("webhook.invalid");
    }
}
