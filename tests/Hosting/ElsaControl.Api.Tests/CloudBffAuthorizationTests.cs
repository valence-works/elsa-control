using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Cloud;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Provisioning;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public async Task Cloud_compatibility_returns_the_exact_static_no_store_contract()
    {
        await using var app = CreateBffApplication();
        using var client = CreateBffClient(app);

        using var response = await client.GetAsync("/api/cloud/compatibility");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("no-cache", response.Headers.Pragma.ToString(), StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(["contractVersion", "capabilities"],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(1, root.GetProperty("contractVersion").GetInt32());
        Assert.Equal(
        [
            "cloud.bootstrap.v1",
            "hosted.instances.list.v1",
            "hosted.instances.create.v1",
            "hosted.instances.status.v1",
            "hosted.studio.handoff.issue.v1",
            "hosted.instances.quota-problem.v1",
            "hosted.instances.confirmed-delete.v1"
        ],
            root.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.DoesNotContain("environment", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("customer", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deployment", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cloud_compatibility_rejects_an_invalid_bff_credential()
    {
        await using var app = CreateBffApplication();
        using var client = CreateBffClient(app, clientId: "unregistered-client");

        using var response = await client.GetAsync("/api/cloud/compatibility");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("cloud-bff.denied", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cloud_compatibility_requires_authentication_and_preserves_ordinary_control_bearers()
    {
        await using var app = CreateBffApplication();
        using var anonymous = app.CreateClient();
        using var ordinaryBearer = app.CreateControlIdentityClient(subject: "ordinary-compatibility-reader");

        using var anonymousResponse = await anonymous.GetAsync("/api/cloud/compatibility");
        using var ordinaryResponse = await ordinaryBearer.GetAsync("/api/cloud/compatibility");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ordinaryResponse.StatusCode);
        Assert.True(ordinaryResponse.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public void Version_one_client_gating_accepts_additions_and_rejects_rolled_back_requirements()
    {
        var required = new HashSet<string>(StringComparer.Ordinal)
        {
            "cloud.bootstrap.v1",
            "hosted.instances.list.v1"
        };
        var current = new CloudCompatibilityResponse(1,
        [
            "cloud.bootstrap.v1",
            "hosted.instances.list.v1"
        ]);
        var additiveApi = current with
        {
            Capabilities = [.. current.Capabilities, "future.additive-capability.v1"]
        };
        var rolledBackApi = current with
        {
            Capabilities = ["cloud.bootstrap.v1"]
        };
        var unsupportedEnvelope = current with { ContractVersion = 2 };

        Assert.True(SupportsVersionOneClient(current, required));
        Assert.True(SupportsVersionOneClient(additiveApi, required));
        Assert.False(SupportsVersionOneClient(rolledBackApi, required));
        Assert.False(SupportsVersionOneClient(unsupportedEnvelope, required));
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

        var expected = new[]
        {
            "GET /api/cloud/compatibility",
            "GET /api/me/organizations",
            "GET /api/me/workspaces",
            "GET /api/workspaces/{workspaceId:guid}/external-engine-connections/",
            "GET /api/workspaces/{workspaceId:guid}/external-engine-connections/{connectionId:guid}",
            "GET /api/workspaces/{workspaceId:guid}/external-engine-connections/{connectionId:guid}/pairing",
            "GET /api/workspaces/{workspaceId:guid}/instances/",
            "GET /api/workspaces/{workspaceId:guid}/instances/onboarding-options",
            "GET /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/delete-operations/{operationId:guid}",
            "PATCH /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}",
            "POST /api/cloud/bootstrap",
            "POST /api/managed-elsa/handoff/issue",
            "POST /api/organizations/{organizationId:guid}/billing/prepare-hosted-trial",
            "POST /api/workspaces/{workspaceId:guid}/external-engine-connections/",
            "POST /api/workspaces/{workspaceId:guid}/external-engine-connections/{connectionId:guid}/disconnect",
            "POST /api/workspaces/{workspaceId:guid}/external-engine-connections/{connectionId:guid}/repair",
            "POST /api/workspaces/{workspaceId:guid}/external-engine-connections/{connectionId:guid}/studio-destination/confirm",
            "POST /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/delete",
            "POST /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/delete-confirmations",
            "POST /api/workspaces/{workspaceId:guid}/instances/"
        };
        Assert.Equal(expected.Order(StringComparer.Ordinal), allowed);
    }

    [Fact]
    public async Task Bff_delete_routes_are_narrow_and_generic_lifecycle_routes_remain_denied()
    {
        await using var app = CreateBffApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateBffClient(app);
        var workspaceId = (await client.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!
            .Workspaces.Single().Id;
        var instanceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        using var confirmation = await client.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/delete-confirmations",
            content: null);
        using var deletionRequest = new HttpRequestMessage(HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/delete")
        {
            Content = JsonContent.Create(new ManagedElsaInstanceDeleteRequest(Guid.NewGuid()),
                options: ControlApiTestApplication.JsonOptions)
        };
        deletionRequest.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        deletionRequest.Headers.TryAddWithoutValidation("Idempotency-Key", "bff-delete");
        using var deletion = await client.SendAsync(deletionRequest);
        using var operation = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/delete-operations/{operationId:D}");

        using var genericMutationRequest = new HttpRequestMessage(HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/operations")
        {
            Content = JsonContent.Create(new ManagedElsaInstanceOperationRequest(ElsaInstanceOperationAction.Start),
                options: ControlApiTestApplication.JsonOptions)
        };
        genericMutationRequest.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        genericMutationRequest.Headers.TryAddWithoutValidation("Idempotency-Key", "bff-generic-operation");
        using var genericMutation = await client.SendAsync(genericMutationRequest);
        using var genericRead = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/operations/{operationId:D}");

        Assert.Equal(HttpStatusCode.NotFound, confirmation.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deletion.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, operation.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, genericMutation.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, genericRead.StatusCode);
        Assert.Contains("cloud-bff.denied", await genericMutation.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("cloud-bff.denied", await genericRead.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bff_owner_can_complete_the_narrow_delete_flow_while_a_reader_is_denied()
    {
        await using var app = CreateBffApplication(configureServices: services =>
        {
            services.AddSingleton<IEngineProvisioningModule, TestProvisioningModule>();
            services.RemoveAll<IGovernedReleaseCatalogStore>();
            services.AddSingleton<IGovernedReleaseCatalogStore>(new StaticReleaseCatalogStore());
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = CreateBffClient(app);
        var workspaceId = (await owner.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!
            .Workspaces.Single().Id;
        await EnableManagedHostingAsync(app, workspaceId);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceCreateRequest("BFF runtime", "bff-delete-runtime", Intent()),
                options: ControlApiTestApplication.JsonOptions)
        };
        createRequest.Headers.Add("Idempotency-Key", "bff-create-delete-runtime");
        using var create = await owner.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var created = await create.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
        Assert.NotNull(created);

        const string readerSubject = "bff-delete-reader";
        await app.AddWorkspaceMemberAsync(workspaceId, readerSubject, WorkspaceRole.Reader);
        using var reader = CreateBffClient(app, subject: readerSubject);
        using var readerConfirmation = await reader.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created!.Instance.InstanceId:D}/delete-confirmations",
            content: null);
        Assert.Equal(HttpStatusCode.Forbidden, readerConfirmation.StatusCode);

        using var confirmationResponse = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations",
            content: null);
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);
        var confirmation = await confirmationResponse.Content
            .ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.NotNull(confirmation);

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceDeleteRequest(confirmation!.ConfirmationId),
                options: ControlApiTestApplication.JsonOptions)
        };
        deleteRequest.Headers.TryAddWithoutValidation("If-Match", created.Instance.ETag);
        deleteRequest.Headers.Add("Idempotency-Key", "bff-delete-runtime");
        using var deletion = await owner.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.Accepted, deletion.StatusCode);
        var accepted = await deletion.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteAcceptedResponse>();
        Assert.NotNull(accepted);

        using var status = await owner.GetAsync(accepted!.OperationUrl);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var operation = await status.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteOperationResponse>();
        Assert.NotNull(operation);
        Assert.Equal(accepted.OperationId, operation!.OperationId);
        Assert.Equal(ElsaInstanceOperationState.WaitingForPriorOperation, operation.State);
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
        IReadOnlyDictionary<string, string>? claims = null,
        string subject = "bff-user")
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

        return app.CreateControlIdentityClient(subject: subject, claims: tokenClaims);
    }

    private static async Task EnableManagedHostingAsync(ControlApiTestApplication app, Guid workspaceId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var organizationId = await db.Workspaces
            .Where(x => x.Id == workspaceId)
            .Select(x => x.OrganizationId)
            .SingleAsync();
        db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = organizationId,
            ManagedHostingEnabled = true,
            MaxSources = 5,
            MaxWorkspaces = 5,
            MaxInstances = 1,
            SubscriptionState = OrganizationSubscriptionState.Active
        });
        await db.SaveChangesAsync();
    }

    private static ElsaInstanceIntent Intent() => new(
        new ElsaReleaseIntent("valence-runtime", "3.8", channel: "stable"),
        new ElsaApplicationIntent("combined", "starter", new Dictionary<string, ElsaFeatureOverride>(), "approved"),
        new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed"));

    private static CloudBffTokenDecision Classify(CloudBffOptions options, params Claim[] claims) =>
        CloudBffAuthorization.Classify(
            new ClaimsPrincipal(new ClaimsIdentity(claims, ControlIdentityDefaults.Scheme)),
            options);

    private static bool SupportsVersionOneClient(
        CloudCompatibilityResponse response,
        IReadOnlySet<string> requiredCapabilities) =>
        response.ContractVersion == 1 && requiredCapabilities.IsSubsetOf(response.Capabilities);

    private sealed class TestProvisioningModule : IEngineProvisioningModule
    {
        public string Id => "test";
        public string DisplayName => "Test provider";
    }

    private sealed class StaticReleaseCatalogStore : IGovernedReleaseCatalogStore
    {
        private static readonly GovernedReleaseCatalogEntry Entry = new(
            "1.0",
            $"oci://registry.example.test/releases/manifest@sha256:{new string('a', 64)}",
            $"sha256:{new string('a', 64)}",
            $"sha256:{new string('b', 64)}",
            "https://evidence.example.test/signatures/manifest",
            $"sha256:{new string('c', 64)}",
            "paid",
            new GovernedReleaseDistribution(
                "valence-runtime", "3", "3.8", "3.8.4", "stable", "supported", null,
                "https://github.com/valence-works/elsa", "0123456789abcdef", "run-1"),
            new GovernedReleaseTopology("combined", "1.0", ["server"], [], [], [], []),
            "supported",
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        public Task<GovernedReleaseCatalogWriteResult> StoreAsync(
            IReadOnlyList<GovernedReleaseCatalogEntry> entries,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<GovernedReleaseCatalogEntry>> QueryAsync(
            GovernedReleaseCatalogQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GovernedReleaseCatalogEntry>>([Entry]);
    }
}
