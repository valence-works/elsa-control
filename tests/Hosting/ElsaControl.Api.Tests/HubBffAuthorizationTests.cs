using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Cloud;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Api.Tests;

public sealed class HubBffAuthorizationTests
{
    private const string HubIssuer = "https://hub-account.test/auth/v1";
    private const string CloudIssuer = "https://cloud-account.test/auth/v1";
    private const string HubSubject = "c0a80101-0000-4000-8000-000000000001";
    private const string OtherHubSubject = "c0a80101-0000-4000-8000-000000000002";
    private const string CloudSubject = "b6f73e84-5b47-4f4d-ad52-e3c4c9a815c6";

    // #824 extends this exact set with Hub lifecycle endpoints. Keep the
    // enumeration helper and this expected list as the contract for that work.
    private static readonly string[] ExpectedHubBffAllowlist =
    [
        "GET /api/cloud/compatibility",
        "GET /api/me/organizations",
        "GET /api/me/workspaces",
        "GET /api/workspaces/{workspaceId:guid}/instances/",
        "GET /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/overview",
        "POST /api/cloud/bootstrap"
    ];

    [Fact]
    public async Task Hub_bff_endpoint_allowlist_is_exact()
    {
        await using var app = CreateHubApplication();
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        Assert.Equal(ExpectedHubBffAllowlist.Order(StringComparer.Ordinal), EnumerateHubBffAllowlist(app));
    }

    [Fact]
    public void Hub_and_cloud_bff_markers_are_independent_types()
    {
        Assert.NotEqual(typeof(HubBffAllowedEndpointMetadata), typeof(CloudBffAllowedEndpointMetadata));
        Assert.IsNotType<CloudBffAllowedEndpointMetadata>(HubBffAllowedEndpointMetadata.Instance);
        Assert.IsNotType<HubBffAllowedEndpointMetadata>(CloudBffAllowedEndpointMetadata.Instance);
    }

    [Fact]
    public async Task Owned_hub_session_succeeds_on_the_six_allowlisted_read_endpoints()
    {
        await using var app = CreateHubApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateHubClient(app, HubSubject);

        using var compatibility = await client.GetAsync("/api/cloud/compatibility");
        using var bootstrapResponse = await client.PostAsJsonAsync(
            "/api/cloud/bootstrap", new CloudBootstrapRequest(), ControlApiTestApplication.JsonOptions);
        var bootstrap = await bootstrapResponse.Content.ReadFromJsonAsync<CloudBootstrapResponse>(
            ControlApiTestApplication.JsonOptions);
        Assert.NotNull(bootstrap);
        using var organizations = await client.GetAsync("/api/me/organizations");
        using var workspaces = await client.GetAsync("/api/me/workspaces");
        var instanceId = await SeedOwnedInstanceAsync(app, bootstrap.OrganizationId, bootstrap.WorkspaceId);
        using var instances = await client.GetAsync($"/api/workspaces/{bootstrap.WorkspaceId:D}/instances");
        using var overview = await client.GetAsync(
            $"/api/workspaces/{bootstrap.WorkspaceId:D}/instances/{instanceId:D}/overview");

        Assert.Equal(HttpStatusCode.OK, compatibility.StatusCode);
        Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, organizations.StatusCode);
        Assert.Equal(HttpStatusCode.OK, workspaces.StatusCode);
        Assert.Equal(HttpStatusCode.OK, instances.StatusCode);
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        var listed = await instances.Content.ReadControlJsonAsync<ManagedElsaInstanceListResponse>();
        Assert.NotNull(listed);
        Assert.Contains(listed.Items, item => item.InstanceId == instanceId);
        var overviewBody = await overview.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewResponse>();
        Assert.Equal(instanceId, overviewBody!.Summary.InstanceId);
    }

    [Fact]
    public async Task Hub_session_is_denied_on_cloud_only_and_unmarked_customer_routes_without_running_the_handler()
    {
        await using var app = CreateHubApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateHubClient(app, HubSubject);
        using var bootstrapResponse = await client.PostAsJsonAsync(
            "/api/cloud/bootstrap", new CloudBootstrapRequest(), ControlApiTestApplication.JsonOptions);
        var bootstrap = await bootstrapResponse.Content.ReadFromJsonAsync<CloudBootstrapResponse>(
            ControlApiTestApplication.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);
        Assert.NotNull(bootstrap);

        using var cloudOnly = await client.GetAsync(
            $"/api/workspaces/{bootstrap.WorkspaceId:D}/external-engine-connections");
        using var unmarked = await client.GetAsync(
            $"/api/workspaces/{bootstrap.WorkspaceId:D}/sources");

        await AssertHubBffDeniedAsync(cloudOnly);
        await AssertHubBffDeniedAsync(unmarked);
    }

    [Theory]
    [InlineData("GET", "/api/admin/application")]
    [InlineData("POST", "/api/runtime/external-engine-connections/cccccccc-cccc-cccc-cccc-cccccccccccc/authenticate")]
    [InlineData("POST", "/api/billing/webhooks/stripe")]
    [InlineData("GET", "/api/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/instances/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/health")]
    [InlineData("GET", "/api/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/instances/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/revisions")]
    [InlineData("GET", "/api/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/instances/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/resolved-plans/plan-1")]
    [InlineData("GET", "/api/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/instances/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/deployments")]
    [InlineData("GET", "/api/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/instances/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/audit")]
    [InlineData("POST", "/api/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/instances/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/operations")]
    public async Task Operator_routes_return_hub_bff_denied_for_a_hub_session(string method, string path)
    {
        await using var app = CreateHubApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateHubClient(app, HubSubject);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);

        await AssertHubBffDeniedAsync(response);
    }

    [Fact]
    public async Task Hub_session_gets_existing_denial_on_another_users_workspace_instance_list_and_overview()
    {
        await using var app = CreateHubApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = CreateHubClient(app, HubSubject);
        using var outsider = CreateHubClient(app, OtherHubSubject);

        using var ownerBootstrap = await owner.PostAsJsonAsync(
            "/api/cloud/bootstrap", new CloudBootstrapRequest(), ControlApiTestApplication.JsonOptions);
        using var outsiderBootstrap = await outsider.PostAsJsonAsync(
            "/api/cloud/bootstrap", new CloudBootstrapRequest(), ControlApiTestApplication.JsonOptions);
        var owned = await ownerBootstrap.Content.ReadFromJsonAsync<CloudBootstrapResponse>(
            ControlApiTestApplication.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, ownerBootstrap.StatusCode);
        Assert.Equal(HttpStatusCode.OK, outsiderBootstrap.StatusCode);
        Assert.NotNull(owned);
        var instanceId = await SeedOwnedInstanceAsync(app, owned.OrganizationId, owned.WorkspaceId);

        using var list = await outsider.GetAsync($"/api/workspaces/{owned.WorkspaceId:D}/instances");
        using var overview = await outsider.GetAsync(
            $"/api/workspaces/{owned.WorkspaceId:D}/instances/{instanceId:D}/overview");

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, overview.StatusCode);
        Assert.DoesNotContain("hub-bff.denied", await list.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("hub-bff.denied", await overview.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ordinary_control_bearer_is_unaffected_by_the_hub_allowlist()
    {
        await using var app = CreateHubApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = app.CreateControlIdentityClient(subject: "ordinary-bearer");

        using var workspaces = await client.GetAsync("/api/me/workspaces");
        var context = await workspaces.Content.ReadControlJsonAsync<MeWorkspacesResponse>();
        Assert.Equal(HttpStatusCode.OK, workspaces.StatusCode);
        Assert.NotNull(context);
        var workspaceId = context.Workspaces.Single().Id;

        using var unmarked = await client.GetAsync($"/api/workspaces/{workspaceId:D}/sources");
        using var cloudOnly = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/external-engine-connections");

        Assert.Equal(HttpStatusCode.OK, unmarked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cloudOnly.StatusCode);
    }

    [Fact]
    public async Task Cloud_session_still_reaches_cloud_only_routes_when_hub_allowlist_is_active()
    {
        await using var app = CreateHubApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        using var bootstrap = await cloud.PostAsJsonAsync(
            "/api/cloud/bootstrap", new CloudBootstrapRequest(), ControlApiTestApplication.JsonOptions);
        var owned = await bootstrap.Content.ReadFromJsonAsync<CloudBootstrapResponse>(
            ControlApiTestApplication.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        Assert.NotNull(owned);

        using var cloudOnly = await cloud.GetAsync(
            $"/api/workspaces/{owned.WorkspaceId:D}/external-engine-connections");

        Assert.Equal(HttpStatusCode.OK, cloudOnly.StatusCode);
    }

    internal static string[] EnumerateHubBffAllowlist(ControlApiTestApplication app) =>
        app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<HubBffAllowedEndpointMetadata>() is not null)
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}") ?? [])
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static ControlApiTestApplication CreateHubApplication() => new(new Dictionary<string, string?>
    {
        [$"{HubAccountIdentityDefaults.ConfigurationSection}:Enabled"] = "true",
        [$"{HubAccountIdentityDefaults.ConfigurationSection}:Issuer"] = HubIssuer,
        [$"{HubAccountIdentityDefaults.ConfigurationSection}:Audience"] = "authenticated",
        [$"{HubAccountIdentityDefaults.ConfigurationSection}:TestSigningKey"] = ControlApiTestApplication.TestControlIdentitySigningKey,
        [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Enabled"] = "true",
        [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Issuer"] = CloudIssuer,
        [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Audience"] = "authenticated",
        [$"{CloudAccountIdentityDefaults.ConfigurationSection}:TestSigningKey"] = ControlApiTestApplication.TestControlIdentitySigningKey
    });

    private static HttpClient CreateHubClient(ControlApiTestApplication app, string subject) =>
        CreateIssuerClient(app, HubIssuer, subject);

    private static HttpClient CreateCloudClient(ControlApiTestApplication app, string subject) =>
        CreateIssuerClient(app, CloudIssuer, subject);

    private static HttpClient CreateIssuerClient(ControlApiTestApplication app, string issuer, string subject)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestWorkspaceIdentity.CreateToken(
            subject, issuer, "authenticated",
            claims: new Dictionary<string, string> { ["role"] = "authenticated", ["email"] = "buyer@example.test" }));
        return client;
    }

    private static async Task AssertHubBffDeniedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("hub-bff.denied", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task<Guid> SeedOwnedInstanceAsync(
        ControlApiTestApplication app,
        Guid organizationId,
        Guid workspaceId)
    {
        var instanceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        db.ElsaInstances.Add(new ElsaInstanceEntity
        {
            Id = instanceId,
            OrganizationId = organizationId,
            WorkspaceId = workspaceId,
            Name = "Hub runtime",
            Slug = "hub-runtime",
            DistributionId = "valence-runtime",
            ReleaseLine = "3.8",
            RequestedVersion = "3.8.4",
            Channel = "stable",
            PatchUpdates = "automatic-within-minor",
            MinorUpdates = "explicit-approval",
            MajorMigrations = "explicit-migration",
            TopologyId = "combined",
            FeatureOverridesJson = "{}",
            TargetMode = "managed",
            RegionCode = "westeurope",
            IsolationProfile = "dedicated",
            CapacityProfile = "standard-small",
            NetworkOutcome = "public",
            DomainOutcome = "managed",
            DesiredLifecycle = ElsaDesiredLifecycle.Running,
            ObservedLifecycle = ElsaObservedLifecycle.Ready,
            Health = ElsaInstanceHealth.Healthy,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return instanceId;
    }
}
