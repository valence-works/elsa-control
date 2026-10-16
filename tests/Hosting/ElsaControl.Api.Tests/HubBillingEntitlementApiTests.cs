using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Cloud;
using ElsaControl.Api.OrganizationBilling;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Api.Tests;

public sealed class HubBillingEntitlementApiTests
{
    private const string HubIssuer = "https://hub-account.test/auth/v1";
    private const string HubSubject = "c0a80101-0000-4000-8000-000000000001";
    private const string OtherHubSubject = "c0a80101-0000-4000-8000-000000000002";

    [Fact]
    public async Task Hub_session_reads_owned_hosted_subscription_including_source()
    {
        await using var app = CreateHubApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateHubClient(app, HubSubject);
        var owned = await BootstrapAsync(client);
        await SeedHubSubscriptionAsync(app, owned.OrganizationId);

        using var response = await client.GetAsync(
            $"/api/organizations/{owned.OrganizationId:D}/billing/hosted-subscription");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadControlJsonAsync<HostedSubscriptionManagementResponse>();
        Assert.Equal(owned.OrganizationId, status!.OrganizationId);
        Assert.Equal(nameof(OrganizationSubscriptionState.Active), status.SubscriptionState);
        Assert.Equal(BillingWebhookSources.Hub, status.Source);
        Assert.True(status.BillingLinked);
    }

    [Fact]
    public async Task Hub_session_gets_organization_not_found_for_another_organization()
    {
        await using var app = CreateHubApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = CreateHubClient(app, HubSubject);
        using var outsider = CreateHubClient(app, OtherHubSubject);
        var owned = await BootstrapAsync(owner);
        await BootstrapAsync(outsider);

        using var response = await outsider.GetAsync(
            $"/api/organizations/{owned.OrganizationId:D}/billing/hosted-subscription");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("organization.not-found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("hub-bff.denied", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("POST", "hosted-portal")]
    [InlineData("POST", "prepare-hosted-trial")]
    [InlineData("POST", "checkout")]
    [InlineData("POST", "portal")]
    [InlineData("POST", "delete")]
    public async Task Hub_session_is_denied_on_billing_routes_that_are_not_hub_allowlisted(string method, string suffix)
    {
        await using var app = CreateHubApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateHubClient(app, HubSubject);
        var owned = await BootstrapAsync(client);
        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            $"/api/organizations/{owned.OrganizationId:D}/billing/{suffix}")
        {
            Content = JsonContent.Create(new { })
        };
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("hub-bff.denied", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static ControlApiTestApplication CreateHubApplication() => new(new Dictionary<string, string?>
    {
        [$"{HubAccountIdentityDefaults.ConfigurationSection}:Enabled"] = "true",
        [$"{HubAccountIdentityDefaults.ConfigurationSection}:Issuer"] = HubIssuer,
        [$"{HubAccountIdentityDefaults.ConfigurationSection}:Audience"] = "authenticated",
        [$"{HubAccountIdentityDefaults.ConfigurationSection}:TestSigningKey"] = ControlApiTestApplication.TestControlIdentitySigningKey,
        ["Billing:Stripe:Enabled"] = "true",
        ["Billing:Stripe:SecretKey"] = "sk_test_fake",
        ["Billing:Stripe:DefaultPriceId"] = "price_server_default",
        ["Billing:Stripe:CheckoutSuccessUrl"] = "https://console.test/success",
        ["Billing:Stripe:CheckoutCancelUrl"] = "https://console.test/cancel",
        ["Billing:Stripe:PortalReturnUrl"] = "https://console.test/billing"
    });

    private static HttpClient CreateHubClient(ControlApiTestApplication app, string subject)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestWorkspaceIdentity.CreateToken(
            subject, HubIssuer, "authenticated",
            claims: new Dictionary<string, string> { ["role"] = "authenticated", ["email"] = "buyer@example.test" }));
        return client;
    }

    private static async Task<CloudBootstrapResponse> BootstrapAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/cloud/bootstrap", new CloudBootstrapRequest(), ControlApiTestApplication.JsonOptions);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CloudBootstrapResponse>(ControlApiTestApplication.JsonOptions);
        Assert.NotNull(body);
        return body;
    }

    private static async Task SeedHubSubscriptionAsync(ControlApiTestApplication app, Guid organizationId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var now = DateTimeOffset.UtcNow;
        var subscription = OrganizationSubscriptionLifecycle.CreateTrial(organizationId, BillingProviderNames.Stripe, now);
        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Active, now);
        subscription.ProviderCustomerReference = "cus_hub_owned";
        subscription.ProviderSubscriptionReference = "sub_hub_owned";
        subscription.WebhookSource = BillingWebhookSources.Hub;
        db.OrganizationSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
    }
}
