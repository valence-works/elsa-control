using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Cloud;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Provisioning;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElsaControl.Api.Tests;

public sealed class HubBffAuthorizationTests
{
    private const string HubIssuer = "https://hub-account.test/auth/v1";
    private const string CloudIssuer = "https://cloud-account.test/auth/v1";
    private const string HubSubject = "c0a80101-0000-4000-8000-000000000001";
    private const string OtherHubSubject = "c0a80101-0000-4000-8000-000000000002";
    private const string ReaderHubSubject = "c0a80101-0000-4000-8000-000000000003";
    private const string CloudSubject = "b6f73e84-5b47-4f4d-ad52-e3c4c9a815c6";
    private const string HandoffCodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    // #823's six read endpoints, #846's non-provisioning membership read, #824's
    // fifteen lifecycle endpoints, hosted-subscription from #825, and account
    // linking from #826. The enumeration helper is the contract: any extra Hub
    // marker fails this test.
    private static readonly string[] ExpectedHubBffAllowlist =
    [
        "GET /api/cloud/compatibility",
        "GET /api/me/organizations",
        "GET /api/me/workspace-memberships",
        "GET /api/me/workspaces",
        "GET /api/organizations/{organizationId:guid}/billing/hosted-subscription",
        "GET /api/workspaces/{workspaceId:guid}/instances/",
        "GET /api/workspaces/{workspaceId:guid}/instances/onboarding-options",
        "GET /api/workspaces/{workspaceId:guid}/instances/operations/by-idempotency-key",
        "GET /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/activity",
        "GET /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/available-releases",
        "GET /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/delete-operations/{operationId:guid}",
        "GET /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/operations/{operationId:guid}/cleanup-observation",
        "GET /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/operations/{operationId:guid}/cleanup-receipt",
        "GET /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/overview",
        "GET /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/provisioning-progress",
        "PATCH /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}",
        "POST /api/cloud/account-links",
        "POST /api/cloud/bootstrap",
        "POST /api/managed-elsa/handoff/issue",
        "POST /api/workspaces/{workspaceId:guid}/instances/",
        "POST /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/apply-release",
        "POST /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/delete",
        "POST /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/delete-confirmations",
        "POST /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/restart"
    ];

    [Fact]
    public async Task Hub_bff_endpoint_allowlist_is_exact()
    {
        await using var app = CreateHubApplication();
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        Assert.Equal(ExpectedHubBffAllowlist.Order(StringComparer.Ordinal), EnumerateHubBffAllowlist(app));
        Assert.Equal(24, ExpectedHubBffAllowlist.Length);
    }

    [Fact]
    public void Hub_and_cloud_bff_markers_are_independent_types()
    {
        Assert.NotEqual(typeof(HubBffAllowedEndpointMetadata), typeof(CloudBffAllowedEndpointMetadata));
        Assert.IsNotType<CloudBffAllowedEndpointMetadata>(HubBffAllowedEndpointMetadata.Instance);
        Assert.IsNotType<HubBffAllowedEndpointMetadata>(CloudBffAllowedEndpointMetadata.Instance);
    }

    [Fact]
    public async Task Owned_hub_session_succeeds_on_the_seven_allowlisted_read_endpoints()
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
        using var memberships = await client.GetAsync("/api/me/workspace-memberships");
        var instanceId = await SeedOwnedInstanceAsync(app, bootstrap.OrganizationId, bootstrap.WorkspaceId);
        using var instances = await client.GetAsync($"/api/workspaces/{bootstrap.WorkspaceId:D}/instances");
        using var overview = await client.GetAsync(
            $"/api/workspaces/{bootstrap.WorkspaceId:D}/instances/{instanceId:D}/overview");

        Assert.Equal(HttpStatusCode.OK, compatibility.StatusCode);
        Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, organizations.StatusCode);
        Assert.Equal(HttpStatusCode.OK, workspaces.StatusCode);
        Assert.Equal(HttpStatusCode.OK, memberships.StatusCode);
        Assert.Equal(HttpStatusCode.OK, instances.StatusCode);
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        var listed = await instances.Content.ReadControlJsonAsync<ManagedElsaInstanceListResponse>();
        Assert.NotNull(listed);
        Assert.Contains(listed.Items, item => item.InstanceId == instanceId);
        var overviewBody = await overview.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewResponse>();
        Assert.Equal(instanceId, overviewBody!.Summary.InstanceId);
    }

    [Fact]
    public async Task Owned_hub_session_can_run_the_lifecycle_surface()
    {
        var catalog = CreateCatalogStore(CatalogEntry("3.8.4", 'a'));
        var authorizer = new RecordingHandoffAuthorizer();
        await using var app = CreateLifecycleHubApplication(catalog, authorizer);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateHubClient(app, HubSubject);
        var workspaceId = await BootstrapWorkspaceAsync(client);
        await EnableManagedHostingAsync(app, workspaceId, maxInstances: int.MaxValue);

        using var onboarding = await client.GetAsync($"/api/workspaces/{workspaceId:D}/instances/onboarding-options");
        Assert.Equal(HttpStatusCode.OK, onboarding.StatusCode);

        using var create = await SendCreateAsync(client, workspaceId, "Hub lifecycle", "hub-lifecycle-runtime", "hub-lifecycle-create");
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var created = await create.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
        Assert.NotNull(created);
        var instanceId = created.Instance.InstanceId;
        await MarkOperationSucceededAsync(app, created.Operation.Id);
        await MarkInstanceReadyAsync(app, instanceId);

        using var progress = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/provisioning-progress");
        Assert.True(progress.IsSuccessStatusCode || progress.StatusCode == HttpStatusCode.NotFound);
        Assert.DoesNotContain("hub-bff.denied", await progress.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var reconcile = await client.SendAsync(Mutation(
            HttpMethod.Get,
            $"/api/workspaces/{workspaceId:D}/instances/operations/by-idempotency-key?action=Create&slug=hub-lifecycle-runtime",
            etag: null,
            "hub-lifecycle-create"));
        Assert.Equal(HttpStatusCode.OK, reconcile.StatusCode);

        using var repair = await client.SendAsync(Mutation(
            HttpMethod.Patch,
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}",
            created.Instance.ETag,
            "hub-lifecycle-repair",
            new ManagedElsaInstancePatchRequest(Intent())));
        Assert.Equal(HttpStatusCode.Accepted, repair.StatusCode);
        var repaired = await repair.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
        await MarkOperationSucceededAsync(app, repaired!.Operation.Id);
        await MarkInstanceReadyAsync(app, instanceId);
        var restartEtag = await ReadOverviewETagAsync(client, workspaceId, instanceId);

        using var restart = await client.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/restart",
            restartEtag,
            "hub-lifecycle-restart"));
        Assert.Equal(HttpStatusCode.Accepted, restart.StatusCode);
        var restarted = await restart.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
        await MarkOperationSucceededAsync(app, restarted!.OperationId);
        await MarkInstanceReadyAsync(app, instanceId);
        var applyEtag = await ReadOverviewETagAsync(client, workspaceId, instanceId);

        catalog.SetEntries([CatalogEntry("3.8.4", 'a'), CatalogEntry("3.8.5", 'b')]);
        using var apply = await client.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/apply-release",
            applyEtag,
            "hub-lifecycle-apply",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        Assert.Equal(HttpStatusCode.Accepted, apply.StatusCode);
        var applied = await apply.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
        await MarkOperationSucceededAsync(app, applied!.OperationId);
        await MarkInstanceReadyAsync(app, instanceId);
        var deleteEtag = await ReadOverviewETagAsync(client, workspaceId, instanceId);

        using var activity = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/activity");
        using var releases = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/available-releases");
        Assert.Equal(HttpStatusCode.OK, activity.StatusCode);
        Assert.Equal(HttpStatusCode.OK, releases.StatusCode);

        var organizationId = await ReadOrganizationIdAsync(app, workspaceId);
        authorizer.Configure(organizationId, instanceId);
        using var handoff = await client.PostAsJsonAsync(
            "/api/managed-elsa/handoff/issue",
            new ManagedElsaHandoffIssueRequest(
                organizationId,
                instanceId,
                authorizer.Audience,
                authorizer.RedirectUri.OriginalString,
                ManagedElsaHandoffIssuer.CreateCodeChallenge(HandoffCodeVerifier)),
            ControlApiTestApplication.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, handoff.StatusCode);

        using var confirmationResponse = await client.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/delete-confirmations",
            content: null);
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);
        var confirmation = await confirmationResponse.Content
            .ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.NotNull(confirmation);

        using var deletion = await SendDeleteAsync(
            client, workspaceId, instanceId, deleteEtag, "hub-lifecycle-delete", confirmation.ConfirmationId);
        Assert.Equal(HttpStatusCode.Accepted, deletion.StatusCode);
        var accepted = await deletion.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteAcceptedResponse>();
        Assert.NotNull(accepted);

        using var status = await client.GetAsync(accepted.OperationUrl);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);

        using var receipt = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/operations/{accepted.OperationId:D}/cleanup-receipt");
        using var observation = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/operations/{accepted.OperationId:D}/cleanup-observation");
        Assert.DoesNotContain("hub-bff.denied", await receipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("hub-bff.denied", await observation.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workspace_reader_hub_session_can_read_and_is_denied_on_mutations()
    {
        var catalog = CreateCatalogStore(CatalogEntry("3.8.4", 'a'), CatalogEntry("3.8.5", 'b'));
        await using var app = CreateLifecycleHubApplication(catalog);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = CreateHubClient(app, HubSubject);
        var workspaceId = await BootstrapWorkspaceAsync(owner);
        await EnableManagedHostingAsync(app, workspaceId, maxInstances: int.MaxValue);
        var created = await CreateReadyInstanceAsync(app, owner, workspaceId, "hub-reader-runtime");
        await AddHubWorkspaceMemberAsync(app, workspaceId, ReaderHubSubject, WorkspaceRole.Reader);
        using var reader = CreateHubClient(app, ReaderHubSubject);
        var instanceId = created.Instance.InstanceId;

        using var onboarding = await reader.GetAsync($"/api/workspaces/{workspaceId:D}/instances/onboarding-options");
        using var progress = await reader.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/provisioning-progress");
        using var releases = await reader.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/available-releases");
        using var activity = await reader.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/activity");
        Assert.Equal(HttpStatusCode.OK, onboarding.StatusCode);
        Assert.True(progress.IsSuccessStatusCode || progress.StatusCode == HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.OK, releases.StatusCode);
        Assert.Equal(HttpStatusCode.OK, activity.StatusCode);
        Assert.DoesNotContain("hub-bff.denied", await progress.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var create = await SendCreateAsync(reader, workspaceId, "Reader runtime", "hub-reader-create", "hub-reader-create");
        using var patch = await reader.SendAsync(Mutation(
            HttpMethod.Patch,
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}",
            created.Instance.ETag,
            "hub-reader-repair",
            new ManagedElsaInstancePatchRequest(Intent())));
        using var restart = await reader.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/restart",
            created.Instance.ETag,
            "hub-reader-restart"));
        using var apply = await reader.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/apply-release",
            created.Instance.ETag,
            "hub-reader-apply",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        using var confirmation = await reader.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/delete-confirmations",
            content: null);
        using var deletion = await SendDeleteAsync(
            reader, workspaceId, instanceId, created.Instance.ETag, "hub-reader-delete", Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, restart.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, apply.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, confirmation.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deletion.StatusCode);
        Assert.Contains("Workspace role does not allow", await create.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("Workspace role does not allow", await restart.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("hub-bff.denied", await create.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("hub-bff.denied", await deletion.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Second_hub_create_past_the_plan_limit_returns_instance_limit_reached_and_starts_no_provider_work()
    {
        var catalog = CreateCatalogStore(CatalogEntry("3.8.4", 'a'));
        await using var app = CreateLifecycleHubApplication(catalog);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateHubClient(app, HubSubject);
        var workspaceId = await BootstrapWorkspaceAsync(client);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var organizationId = await db.Workspaces.Where(x => x.Id == workspaceId)
                .Select(x => x.OrganizationId)
                .SingleAsync();
            await new OrganizationBillingStore(db).StartTrialAsync(
                organizationId,
                BillingProviderNames.Stripe,
                DateTimeOffset.UtcNow);
        }

        using var first = await SendCreateAsync(client, workspaceId, "First hub engine", "hub-first-engine", "hub-create-first");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        using var second = await SendCreateAsync(client, workspaceId, "Second hub engine", "hub-second-engine", "hub-create-second");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        Assert.NotNull(problem);
        Assert.Equal("instance_limit_reached", problem["code"].GetString());
        Assert.Equal(1, problem["currentInstances"].GetInt32());
        Assert.Equal(1, problem["maxInstances"].GetInt32());

        await using var verifyScope = app.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(1, await CountRowsAsync(verify, "ElsaInstances"));
        Assert.Equal(1, await CountRowsAsync(verify, "ElsaInstanceOperations"));
        Assert.Equal(1, await CountRowsAsync(verify, "ElsaInstanceLifecycleOutbox"));
    }

    [Fact]
    public async Task Hub_delete_without_a_fresh_confirmation_is_refused_and_a_completed_delete_frees_the_allowance()
    {
        var catalog = CreateCatalogStore(CatalogEntry("3.8.4", 'a'));
        await using var app = CreateLifecycleHubApplication(catalog);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateHubClient(app, HubSubject);
        var workspaceId = await BootstrapWorkspaceAsync(client);
        await EnableManagedHostingAsync(app, workspaceId, maxInstances: 1);
        var created = await CreateReadyInstanceAsync(app, client, workspaceId, "hub-delete-runtime");

        using var missing = await SendDeleteAsync(
            client, workspaceId, created.Instance.InstanceId, created.Instance.ETag, "hub-delete-missing", Guid.Empty);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("instance.delete-confirmation-required", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var staleConfirmation = await client.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations",
            content: null);
        var firstConfirmation = await staleConfirmation.Content
            .ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        using var firstDelete = await SendDeleteAsync(
            client, workspaceId, created.Instance.InstanceId, created.Instance.ETag, "hub-delete-first",
            firstConfirmation!.ConfirmationId);
        Assert.Equal(HttpStatusCode.Accepted, firstDelete.StatusCode);
        var accepted = await firstDelete.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteAcceptedResponse>();
        Assert.NotNull(accepted);

        using var reused = await SendDeleteAsync(
            client, workspaceId, created.Instance.InstanceId,
            firstDelete.Headers.ETag?.ToString() ?? created.Instance.ETag,
            "hub-delete-reused",
            firstConfirmation.ConfirmationId);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);

        await MarkDeleteCompletedAsync(app, created.Instance.InstanceId, accepted.OperationId);

        using var replacement = await SendCreateAsync(
            client, workspaceId, "Replacement hub engine", "hub-replacement-engine", "hub-create-replacement");
        Assert.Equal(HttpStatusCode.Accepted, replacement.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/onboarding-options")]
    [InlineData("POST", "")]
    [InlineData("GET", "/{instanceId}/provisioning-progress")]
    [InlineData("PATCH", "/{instanceId}")]
    [InlineData("GET", "/{instanceId}/available-releases")]
    [InlineData("GET", "/{instanceId}/activity")]
    [InlineData("POST", "/{instanceId}/restart")]
    [InlineData("POST", "/{instanceId}/apply-release")]
    [InlineData("POST", "/{instanceId}/delete-confirmations")]
    [InlineData("POST", "/{instanceId}/delete")]
    [InlineData("GET", "/{instanceId}/delete-operations/{operationId}")]
    [InlineData("GET", "/operations/by-idempotency-key")]
    [InlineData("GET", "/{instanceId}/operations/{operationId}/cleanup-receipt")]
    [InlineData("GET", "/{instanceId}/operations/{operationId}/cleanup-observation")]
    public async Task Hub_session_gets_existing_denial_on_another_organizations_lifecycle_routes(
        string method,
        string suffix)
    {
        var catalog = CreateCatalogStore(CatalogEntry("3.8.4", 'a'));
        await using var app = CreateLifecycleHubApplication(catalog);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = CreateHubClient(app, HubSubject);
        using var outsider = CreateHubClient(app, OtherHubSubject);
        var ownedWorkspaceId = await BootstrapWorkspaceAsync(owner);
        await BootstrapWorkspaceAsync(outsider);
        await EnableManagedHostingAsync(app, ownedWorkspaceId, maxInstances: int.MaxValue);
        var created = await CreateReadyInstanceAsync(app, owner, ownedWorkspaceId, "hub-cross-org-runtime");
        var path = $"/api/workspaces/{ownedWorkspaceId:D}/instances" +
                   suffix.Replace("{instanceId}", created.Instance.InstanceId.ToString("D"), StringComparison.Ordinal)
                       .Replace("{operationId}", Guid.NewGuid().ToString("D"), StringComparison.Ordinal);

        using var request = Mutation(new HttpMethod(method), path, created.Instance.ETag, "hub-cross-org",
            method == "PATCH" ? new ManagedElsaInstancePatchRequest(Intent()) :
            suffix.EndsWith("/apply-release", StringComparison.Ordinal)
                ? new ManagedElsaInstanceApplyReleaseRequest("3.8.5")
                : suffix.EndsWith("/delete", StringComparison.Ordinal)
                    ? new ManagedElsaInstanceDeleteRequest(Guid.NewGuid())
                    : method == "POST" && suffix.Length == 0
                        ? new ManagedElsaInstanceCreateRequest("Cross", "hub-cross-create", Intent())
                        : null);
        using var response = await outsider.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("hub-bff.denied", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hub_session_gets_existing_handoff_denial_on_another_organizations_instance()
    {
        var catalog = CreateCatalogStore(CatalogEntry("3.8.4", 'a'));
        await using var app = CreateLifecycleHubApplication(catalog, enableHandoff: true);
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = CreateHubClient(app, HubSubject);
        using var outsider = CreateHubClient(app, OtherHubSubject);
        var ownedWorkspaceId = await BootstrapWorkspaceAsync(owner);
        await BootstrapWorkspaceAsync(outsider);
        await EnableManagedHostingAsync(app, ownedWorkspaceId, maxInstances: int.MaxValue);
        var created = await CreateReadyInstanceAsync(app, owner, ownedWorkspaceId, "hub-cross-handoff");
        var organizationId = await ReadOrganizationIdAsync(app, ownedWorkspaceId);
        var audience = $"urn:elsa:instance:{created.Instance.InstanceId:D}";
        var redirect = $"https://managed.example.test/instances/{created.Instance.InstanceId:D}/auth/callback";

        using var handoff = await outsider.PostAsJsonAsync(
            "/api/managed-elsa/handoff/issue",
            new ManagedElsaHandoffIssueRequest(
                organizationId,
                created.Instance.InstanceId,
                audience,
                redirect,
                ManagedElsaHandoffIssuer.CreateCodeChallenge(HandoffCodeVerifier)),
            ControlApiTestApplication.JsonOptions);

        Assert.Equal(HttpStatusCode.Forbidden, handoff.StatusCode);
        Assert.DoesNotContain("hub-bff.denied", await handoff.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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
    [InlineData("GET", "/api/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/instances/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")]
    [InlineData("GET", "/api/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/instances/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/health")]
    [InlineData("GET", "/api/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/instances/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/revisions")]
    [InlineData("GET", "/api/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/instances/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/resolved-plans/plan-1")]
    [InlineData("GET", "/api/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/instances/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/deployments")]
    [InlineData("GET", "/api/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/instances/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/audit")]
    [InlineData("POST", "/api/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/instances/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/operations")]
    [InlineData("GET", "/api/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/instances/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/operations")]
    [InlineData("GET", "/api/workspaces/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/instances/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/operations/dddddddd-dddd-dddd-dddd-dddddddddddd")]
    public async Task Never_marked_routes_return_hub_bff_denied_for_a_hub_session(string method, string path)
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

    private static ControlApiTestApplication CreateHubApplication(
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            [$"{HubAccountIdentityDefaults.ConfigurationSection}:Enabled"] = "true",
            [$"{HubAccountIdentityDefaults.ConfigurationSection}:Issuer"] = HubIssuer,
            [$"{HubAccountIdentityDefaults.ConfigurationSection}:Audience"] = "authenticated",
            [$"{HubAccountIdentityDefaults.ConfigurationSection}:TestSigningKey"] = ControlApiTestApplication.TestControlIdentitySigningKey,
            [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Enabled"] = "true",
            [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Issuer"] = CloudIssuer,
            [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Audience"] = "authenticated",
            [$"{CloudAccountIdentityDefaults.ConfigurationSection}:TestSigningKey"] = ControlApiTestApplication.TestControlIdentitySigningKey
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
                configuration[key] = value;
        }

        return new ControlApiTestApplication(configuration, configureServices);
    }

    private static ControlApiTestApplication CreateLifecycleHubApplication(
        MutableReleaseCatalogStore catalog,
        RecordingHandoffAuthorizer? authorizer = null,
        bool enableHandoff = false)
    {
        var extra = new Dictionary<string, string?>();
        if (authorizer is not null || enableHandoff)
            extra[$"{ManagedElsaHandoffDefaults.ConfigurationSection}:Enabled"] = "true";

        return CreateHubApplication(services =>
        {
            services.AddSingleton<IEngineProvisioningModule, TestProvisioningModule>();
            services.RemoveAll<IGovernedReleaseCatalogStore>();
            services.AddSingleton<IGovernedReleaseCatalogStore>(catalog);
            if (authorizer is not null)
            {
                services.RemoveAll<IManagedElsaHandoffAuthorizer>();
                services.AddSingleton<IManagedElsaHandoffAuthorizer>(authorizer);
            }
        }, extra);
    }

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

    private static async Task<Guid> BootstrapWorkspaceAsync(HttpClient client)
    {
        using var bootstrap = await client.PostAsJsonAsync(
            "/api/cloud/bootstrap", new CloudBootstrapRequest(), ControlApiTestApplication.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        var body = await bootstrap.Content.ReadFromJsonAsync<CloudBootstrapResponse>(
            ControlApiTestApplication.JsonOptions);
        Assert.NotNull(body);
        return body.WorkspaceId;
    }

    private static async Task<ManagedElsaInstanceAcceptedResponse> CreateReadyInstanceAsync(
        ControlApiTestApplication app,
        HttpClient client,
        Guid workspaceId,
        string slug)
    {
        using var create = await SendCreateAsync(client, workspaceId, "Hub runtime", slug, $"create-{slug}");
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var created = await create.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
        Assert.NotNull(created);
        await MarkOperationSucceededAsync(app, created.Operation.Id);
        await MarkInstanceReadyAsync(app, created.Instance.InstanceId);
        return created;
    }

    private static Task<HttpResponseMessage> SendCreateAsync(
        HttpClient client,
        Guid workspaceId,
        string name,
        string slug,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId:D}/instances")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceCreateRequest(name, slug, Intent()),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendDeleteAsync(
        HttpClient client,
        Guid workspaceId,
        Guid instanceId,
        string etag,
        string idempotencyKey,
        Guid confirmationId)
    {
        var request = Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/delete",
            etag,
            idempotencyKey,
            new ManagedElsaInstanceDeleteRequest(confirmationId));
        return client.SendAsync(request);
    }

    private static HttpRequestMessage Mutation(
        HttpMethod method,
        string path,
        string? etag,
        string? idempotencyKey,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: ControlApiTestApplication.JsonOptions);
        if (etag is not null)
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static ElsaInstanceIntent Intent() => new(
        new ElsaReleaseIntent("valence-runtime", "3.8", requestedVersion: "3.8.4", channel: "stable"),
        new ElsaApplicationIntent("combined", "starter", new Dictionary<string, ElsaFeatureOverride>(), "approved"),
        new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed"));

    private static async Task EnableManagedHostingAsync(
        ControlApiTestApplication app,
        Guid workspaceId,
        int maxInstances)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var organizationId = await db.Workspaces
            .Where(x => x.Id == workspaceId)
            .Select(x => x.OrganizationId)
            .SingleAsync();
        if (!await db.OrganizationEntitlementSnapshots.AnyAsync(x => x.OrganizationId == organizationId))
        {
            db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
            {
                OrganizationId = organizationId,
                ManagedHostingEnabled = true,
                MaxSources = 5,
                MaxWorkspaces = 5,
                MaxInstances = maxInstances,
                SubscriptionState = OrganizationSubscriptionState.Active
            });
            await db.SaveChangesAsync();
        }
    }

    private static async Task AddHubWorkspaceMemberAsync(
        ControlApiTestApplication app,
        Guid workspaceId,
        string subject,
        WorkspaceRole role)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var account = new Account { DisplayName = subject, Email = $"{subject}@example.test" };
        account.ExternalIdentities.Add(new ExternalIdentity
        {
            Account = account,
            Issuer = HubIssuer,
            Subject = subject,
            Kind = ExternalIdentityKinds.Hub,
            DisplayName = subject,
            Email = $"{subject}@example.test"
        });
        var workspace = await db.Workspaces.Include(x => x.Organization).SingleAsync(x => x.Id == workspaceId);
        account.OrganizationMemberships.Add(new OrganizationMembership
        {
            Account = account,
            OrganizationId = workspace.OrganizationId,
            Role = OrganizationRole.Member
        });
        account.Memberships.Add(new WorkspaceMembership { Account = account, Workspace = workspace, Role = role });
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
    }

    private static async Task<string> ReadOverviewETagAsync(
        HttpClient client,
        Guid workspaceId,
        Guid instanceId)
    {
        using var overview = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/overview");
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        return overview.Headers.ETag?.ToString()
               ?? throw new InvalidOperationException("Overview ETag is missing.");
    }

    private static async Task<Guid> ReadOrganizationIdAsync(ControlApiTestApplication app, Guid workspaceId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return await db.Workspaces.Where(x => x.Id == workspaceId).Select(x => x.OrganizationId).SingleAsync();
    }

    private static async Task MarkOperationSucceededAsync(ControlApiTestApplication app, Guid operationId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var completedAtTicks = DateTimeOffset.UtcNow.UtcTicks;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstanceOperations SET State = {ElsaInstanceOperationState.Succeeded.ToString()}, CompletedAt = {completedAtTicks} WHERE Id = {operationId}");
    }

    private static async Task MarkInstanceReadyAsync(ControlApiTestApplication app, Guid instanceId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ElsaInstances
            SET DesiredLifecycle = {ElsaDesiredLifecycle.Running.ToString()},
                ObservedLifecycle = {ElsaObservedLifecycle.Ready.ToString()},
                Health = {ElsaInstanceHealth.Healthy.ToString()}
            WHERE Id = {instanceId}
            """);
    }

    private static async Task MarkDeleteCompletedAsync(
        ControlApiTestApplication app,
        Guid instanceId,
        Guid operationId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var now = DateTimeOffset.UtcNow;
        var state = ElsaInstanceOperationState.Succeeded.ToString();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstances SET DesiredLifecycle = {ElsaDesiredLifecycle.Deleting.ToString()}, ObservedLifecycle = {ElsaObservedLifecycle.Deleted.ToString()}, Health = {ElsaInstanceHealth.Unknown.ToString()}, DeletedAt = {now.UtcTicks}, CurrentDeploymentId = NULL, CurrentDeploymentRevisionId = NULL, CurrentDeploymentEndpointUri = NULL, CurrentDeploymentManagedHandoff = 0, PlacementAssignmentId = NULL, ElsaTenantId = NULL, ElsaTenantAudience = NULL WHERE Id = {instanceId}");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstanceOperations SET State = {state}, CompletedAt = {now.UtcTicks} WHERE Id = {operationId}");
    }

    private static async Task<int> CountRowsAsync(CatalogDbContext db, string table)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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

    private static MutableReleaseCatalogStore CreateCatalogStore(params GovernedReleaseCatalogEntry[] entries)
    {
        var store = new MutableReleaseCatalogStore();
        store.SetEntries(entries);
        return store;
    }

    private static GovernedReleaseCatalogEntry CatalogEntry(string version, char digestMarker) => new(
        "1.0",
        $"oci://registry.example.test/releases/manifest@sha256:{new string(digestMarker, 64)}",
        $"sha256:{new string(digestMarker, 64)}",
        $"sha256:{new string((char)(digestMarker + 1), 64)}",
        "https://evidence.example.test/signatures/manifest",
        $"sha256:{new string((char)(digestMarker + 2), 64)}",
        "paid",
        new GovernedReleaseDistribution(
            "valence-runtime", "3", "3.8", version, "stable", "supported", null,
            "https://github.com/valence-works/elsa", "0123456789abcdef", "run-1"),
        new GovernedReleaseTopology("combined", "1.0", ["server"], [], [], [], []),
        "supported",
        DateTimeOffset.Parse("2026-09-01T00:00:00Z", CultureInfo.InvariantCulture));

    private sealed class TestProvisioningModule : IEngineProvisioningModule
    {
        public string Id => "test";
        public string DisplayName => "Test provider";
    }

    private sealed class MutableReleaseCatalogStore : IGovernedReleaseCatalogStore
    {
        private IReadOnlyList<GovernedReleaseCatalogEntry> _entries = [];

        public void SetEntries(IReadOnlyList<GovernedReleaseCatalogEntry> entries) => _entries = entries;

        public Task<GovernedReleaseCatalogWriteResult> StoreAsync(
            IReadOnlyList<GovernedReleaseCatalogEntry> entries,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<GovernedReleaseCatalogEntry>> QueryAsync(
            GovernedReleaseCatalogQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GovernedReleaseCatalogEntry>>(_entries
                .Where(entry => query.DistributionId is null ||
                                string.Equals(entry.Distribution.Id, query.DistributionId, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.ReleaseLine is null ||
                                string.Equals(entry.Distribution.ReleaseLine, query.ReleaseLine, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.ReleaseVersion is null ||
                                string.Equals(entry.Distribution.ReleaseVersion, query.ReleaseVersion, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.Channel is null ||
                                string.Equals(entry.Distribution.Channel, query.Channel, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.CatalogLifecycle is null ||
                                string.Equals(entry.CatalogLifecycle, query.CatalogLifecycle, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.RegistryClass is null ||
                                string.Equals(entry.RegistryClass, query.RegistryClass, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.TopologyId is null ||
                                string.Equals(entry.Topology.Id, query.TopologyId, StringComparison.OrdinalIgnoreCase))
                .ToArray());
    }

    private sealed class RecordingHandoffAuthorizer : IManagedElsaHandoffAuthorizer
    {
        public Guid OrganizationId { get; private set; }
        public Guid InstanceId { get; private set; }
        public string Audience { get; private set; } = "urn:elsa:instance:00000000-0000-0000-0000-000000000000";
        public Uri RedirectUri { get; private set; } = new("https://managed.example.test/instances/00000000-0000-0000-0000-000000000000/auth/callback");

        public void Configure(Guid organizationId, Guid instanceId)
        {
            OrganizationId = organizationId;
            InstanceId = instanceId;
            Audience = $"urn:elsa:instance:{instanceId:D}";
            RedirectUri = new($"https://managed.example.test/instances/{instanceId:D}/auth/callback");
        }

        public ValueTask<ManagedElsaHandoffAuthorization?> AuthorizeAsync(
            TrustedWorkspaceIdentity _,
            ManagedElsaHandoffRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ManagedElsaHandoffAuthorization?>(
                request.OrganizationId == OrganizationId &&
                request.InstanceId == InstanceId &&
                request.Audience == Audience &&
                request.RedirectUri == RedirectUri
                    ? new ManagedElsaHandoffAuthorization(
                        Guid.NewGuid(),
                        OrganizationId,
                        InstanceId,
                        Audience,
                        RedirectUri,
                        request.CodeChallenge,
                        new HashSet<string>([ManagedElsaHandoffDefaults.RuntimeSessionScope], StringComparer.Ordinal))
                    : null);

        public ValueTask<bool> IsStillAuthorizedAsync(
            ManagedElsaHandoffClaims claims,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(claims.OrganizationId == OrganizationId && claims.InstanceId == InstanceId);
    }
}
