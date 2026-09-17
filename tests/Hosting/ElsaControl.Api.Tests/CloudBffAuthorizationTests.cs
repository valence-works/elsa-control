using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Cloud;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Core.Provisioning;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class CloudBffAuthorizationTests
{
    private const string ClientId = "elsa-cloud-lovable-bff";
    private const string Scope = CloudBffDefaults.DefaultScope;

    [Fact]
    public async Task Valid_bff_token_is_allowed_on_the_me_workspace_and_organization_routes()
    {
        await using var app = CreateBffApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateBffClient(app);

        using var workspaces = await client.GetAsync("/api/me/workspaces");
        using var organizations = await client.GetAsync("/api/me/organizations");

        Assert.Equal(HttpStatusCode.OK, workspaces.StatusCode);
        Assert.Equal(HttpStatusCode.OK, organizations.StatusCode);
    }

    [Fact]
    public async Task Valid_bff_token_is_allowed_on_the_merged_cloud_bootstrap_contract()
    {
        await using var app = CreateBffApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateBffClient(app);

        using var response = await client.PostAsJsonAsync(
            "/api/cloud/bootstrap",
            new CloudBootstrapRequest(),
            ControlApiTestApplication.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bootstrap = await response.Content.ReadFromJsonAsync<CloudBootstrapResponse>(ControlApiTestApplication.JsonOptions);
        Assert.NotNull(bootstrap);
        Assert.NotEqual(Guid.Empty, bootstrap.OrganizationId);
        Assert.NotEqual(Guid.Empty, bootstrap.WorkspaceId);
    }

    [Fact]
    public async Task Bff_endpoint_allowlist_is_exact()
    {
        await using var app = CreateBffApplication();
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        var dataSource = app.Services.GetRequiredService<EndpointDataSource>();
        var allowed = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<CloudBffAllowedEndpointMetadata>() is not null)
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}") ?? [])
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([
            "GET /api/me/organizations",
            "GET /api/me/workspaces",
            "GET /api/workspaces/{workspaceId:guid}/external-engine-connections/",
            "GET /api/workspaces/{workspaceId:guid}/external-engine-connections/{connectionId:guid}",
            "GET /api/workspaces/{workspaceId:guid}/external-engine-connections/{connectionId:guid}/pairing",
            "GET /api/workspaces/{workspaceId:guid}/instances/",
            "GET /api/workspaces/{workspaceId:guid}/instances/onboarding-options",
            "PATCH /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}",
            "POST /api/cloud/bootstrap",
            "POST /api/managed-elsa/handoff/issue",
            "POST /api/organizations/{organizationId:guid}/billing/prepare-hosted-trial",
            "POST /api/workspaces/{workspaceId:guid}/external-engine-connections/",
            "POST /api/workspaces/{workspaceId:guid}/external-engine-connections/{connectionId:guid}/disconnect",
            "POST /api/workspaces/{workspaceId:guid}/external-engine-connections/{connectionId:guid}/repair",
            "POST /api/workspaces/{workspaceId:guid}/external-engine-connections/{connectionId:guid}/studio-destination/confirm",
            "POST /api/workspaces/{workspaceId:guid}/instances/"
        ], allowed);
    }

    [Fact]
    public async Task Bff_can_pair_through_the_narrow_customer_route_but_cannot_call_runtime_routes()
    {
        await using var app = CreateBffApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateBffClient(app);
        var workspaceId = (await client.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!
            .Workspaces.Single().Id;
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/external-engine-connections")
        {
            Content = JsonContent.Create(new CreateExternalEngineConnectionRequest("Customer engine"),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", "bff-pairing");

        using var pairing = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, pairing.StatusCode);
        var result = await pairing.Content.ReadControlJsonAsync<ExternalEnginePairingAttemptResponse>();
        Assert.NotNull(result);

        using var runtime = await client.PostAsJsonAsync(
            $"/api/runtime/external-engine-connections/{result.Connection.Id:D}/authenticate",
            new { }, ControlApiTestApplication.JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, runtime.StatusCode);
        Assert.Contains("cloud-bff.denied", await runtime.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ClientId, "openid profile")]
    [InlineData("other-client", Scope)]
    public async Task Bff_client_or_scope_mismatch_is_rejected_before_customer_route_execution(
        string clientId,
        string scope)
    {
        await using var app = CreateBffApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateBffClient(app, clientId, scope);

        using var response = await client.GetAsync("/api/me/workspaces");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scopeHandle = app.Services.CreateAsyncScope();
        var database = scopeHandle.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Empty(await database.Accounts.ToListAsync());
    }

    [Fact]
    public async Task Dedicated_scope_without_a_client_marker_is_rejected()
    {
        await using var app = CreateBffApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = app.CreateControlIdentityClient(
            subject: "scope-without-client",
            claims: new Dictionary<string, string>
            {
                ["scope"] = "openid",
                ["scp"] = Scope
            });

        using var response = await client.GetAsync("/api/me/workspaces");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Bff_token_is_denied_on_an_unmarked_customer_route_without_provisioning_identity()
    {
        await using var app = CreateBffApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateBffClient(app);

        using var response = await client.GetAsync($"/api/workspaces/{Guid.NewGuid():D}/sources");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("cloud-bff.denied", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoStore);
        await using var scopeHandle = app.Services.CreateAsyncScope();
        var database = scopeHandle.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Empty(await database.Accounts.ToListAsync());
    }

    [Fact]
    public async Task Bff_token_is_denied_on_admin_routes_even_with_a_control_admin_role()
    {
        await using var app = CreateBffApplication(new Dictionary<string, string?>
        {
            [$"{AdminAuthorizationOptions.ConfigurationSection}:AllowAuthenticatedCustomerSession"] = "true"
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateBffClient(app, claims: new Dictionary<string, string>
        {
            ["role"] = AdminAuthorization.ControlAdminRole
        });

        using var response = await client.GetAsync("/api/admin/application");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Bff_allowlisted_onboarding_route_preserves_the_commercial_gate()
    {
        await using var app = CreateBffApplication(configureServices: services =>
            services.AddSingleton<IEngineProvisioningModule, TestProvisioningModule>());
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateBffClient(app);
        var workspaceId = (await client.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!
            .Workspaces.Single().Id;

        using var response = await client.GetAsync($"/api/workspaces/{workspaceId:D}/instances/onboarding-options");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("instance.entitlement-required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ordinary_control_bearer_remains_available_when_bff_restrictions_are_enabled()
    {
        await using var app = CreateBffApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = app.CreateControlIdentityClient(subject: "ordinary-bearer");

        using var response = await client.GetAsync("/api/me/workspaces");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Duplicate_or_conflicting_client_claims_fail_closed()
    {
        var options = new CloudBffOptions { Enabled = true, ClientId = ClientId, Scope = Scope };
        var duplicate = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("azp", ClientId), new Claim("azp", ClientId), new Claim("scp", Scope)],
            ControlIdentityDefaults.Scheme));
        var conflicting = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("azp", ClientId), new Claim("appid", "other-client"), new Claim("scp", Scope)],
            ControlIdentityDefaults.Scheme));

        Assert.Equal(CloudBffTokenDecision.Invalid, CloudBffAuthorization.Classify(duplicate, options));
        Assert.Equal(CloudBffTokenDecision.Invalid, CloudBffAuthorization.Classify(conflicting, options));
    }

    [Fact]
    public void Bff_claim_shape_requires_one_exact_delegated_scope()
    {
        var options = new CloudBffOptions { Enabled = true, ClientId = ClientId, Scope = Scope };

        Assert.Equal(CloudBffTokenDecision.Valid, Classify(options,
            new Claim("appid", ClientId), new Claim("scp", Scope)));
        Assert.Equal(CloudBffTokenDecision.Valid, Classify(options,
            new Claim("azp", ClientId), new Claim("appid", ClientId), new Claim("scp", $"openid {Scope}")));
        Assert.Equal(CloudBffTokenDecision.Invalid, Classify(options,
            new Claim("azp", ClientId), new Claim("roles", Scope)));
        Assert.Equal(CloudBffTokenDecision.Invalid, Classify(options,
            new Claim("azp", ClientId), new Claim("scp", $"{Scope}.Read")));
        Assert.Equal(CloudBffTokenDecision.Invalid, Classify(options,
            new Claim("azp", ClientId), new Claim("scp", $"{Scope},openid")));
        Assert.Equal(CloudBffTokenDecision.Invalid, Classify(options,
            new Claim("azp", ClientId), new Claim("scp", Scope), new Claim("scp", "openid")));
    }

    [Fact]
    public void Dedicated_scope_fails_closed_when_bff_is_disabled_or_unconfigured()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("scp", Scope)],
            ControlIdentityDefaults.Scheme));

        Assert.Equal(CloudBffTokenDecision.Invalid,
            CloudBffAuthorization.Classify(principal, new CloudBffOptions()));
        Assert.Equal(CloudBffTokenDecision.Invalid,
            CloudBffAuthorization.Classify(principal, new CloudBffOptions { Enabled = false, ClientId = ClientId }));
    }

    [Fact]
    public async Task Enabled_bff_configuration_requires_a_client_and_scope()
    {
        var validator = new CloudBffConfigurationValidator(Options.Create(new CloudBffOptions
        {
            Enabled = true,
            ClientId = " ",
            Scope = "scope with whitespace"
        }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));

        Assert.Contains("ClientId", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Scope", exception.Message, StringComparison.Ordinal);
    }

    private static ControlApiTestApplication CreateBffApplication(
        IReadOnlyDictionary<string, string?>? additionalConfiguration = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            [$"{CloudBffOptions.ConfigurationSection}:Enabled"] = "true",
            [$"{CloudBffOptions.ConfigurationSection}:ClientId"] = ClientId,
            [$"{CloudBffOptions.ConfigurationSection}:Scope"] = Scope
        };
        if (additionalConfiguration is not null)
        {
            foreach (var (key, value) in additionalConfiguration)
                configuration[key] = value;
        }

        return new ControlApiTestApplication(configuration, configureServices);
    }

    private static HttpClient CreateBffClient(
        ControlApiTestApplication app,
        string clientId = ClientId,
        string scope = Scope,
        IReadOnlyDictionary<string, string>? claims = null)
    {
        var tokenClaims = new Dictionary<string, string>
        {
            ["azp"] = clientId,
            ["scp"] = scope
        };
        if (claims is not null)
        {
            foreach (var (key, value) in claims)
                tokenClaims[key] = value;
        }

        return app.CreateControlIdentityClient(subject: "bff-user", claims: tokenClaims);
    }

    private static CloudBffTokenDecision Classify(CloudBffOptions options, params Claim[] claims) =>
        CloudBffAuthorization.Classify(
            new ClaimsPrincipal(new ClaimsIdentity(claims, ControlIdentityDefaults.Scheme)),
            options);

    private sealed class TestProvisioningModule : IEngineProvisioningModule
    {
        public string Id => "test";
        public string DisplayName => "Test provider";
    }
}
