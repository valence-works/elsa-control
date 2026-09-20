using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using ElsaControl.Api.Admin.Workspaces;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Provisioning;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElsaControl.Api.Tests;

public sealed class ManagedElsaInstanceApiTests : IClassFixture<ManagedElsaInstanceApiTests.Fixture>
{
    private readonly Fixture _fixture;

    public ManagedElsaInstanceApiTests(Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Disabled_provisioning_rejects_creation_and_options_without_hiding_existing_instances()
    {
        await using var app = new ControlApiTestApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = app.CreateControlIdentityClient(subject: "provisioning-disabled-owner");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/instances")
        {
            Content = JsonContent.Create(new ManagedElsaInstanceCreateRequest("Runtime", "runtime", Intent()),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", "disabled-provisioning");

        using var options = await client.GetAsync($"/api/workspaces/{workspaceId}/instances/onboarding-options");
        using var create = await client.SendAsync(request);
        using var list = await client.GetAsync($"/api/workspaces/{workspaceId}/instances");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, options.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, create.StatusCode);
        Assert.Contains("engine-provisioning.disabled", await create.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(0, await CountOperationsAsync(app));
    }

    [Fact]
    public async Task Onboarding_options_are_server_owned_and_workspace_scoped()
    {
        var app = await PrepareApplicationAsync([], [
            CatalogEntry("future-runtime", "5.0", "5.0.1", "stable", "combined", "supported", "paid"),
            CatalogEntry("ambiguous-runtime", "4.2", "4.2.0", "stable", "combined", "supported", "paid"),
            CatalogEntry("ambiguous-runtime", "4.2", "4.2.0", "stable", "combined", "supported", "paid", digestMarker: 'd'),
            CatalogEntry("ambiguous-runtime", "4.2", "4.2.0", "stable", "combined", "preview", "paid", digestMarker: 'e'),
            CatalogEntry("preview-runtime", "4.1", "4.1.0-preview.1", "preview", "combined", "preview", "paid"),
            CatalogEntry("community-runtime", "3.9", "3.9.0", "stable", "combined", "supported", "community")
        ]);
        var client = app.CreateControlIdentityClient(subject: "managed-owner");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);

        var response = await client.GetAsync($"/api/workspaces/{workspaceId}/instances/onboarding-options");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = await response.Content.ReadFromJsonAsync<ManagedElsaInstanceOnboardingOptionsResponse>(ControlApiTestApplication.JsonOptions);
        Assert.NotNull(options);
        Assert.Equal("managed", options.LaunchProfile.TargetMode);
        Assert.Equal("westeurope", options.LaunchProfile.RegionCode);
        Assert.Equal("dedicated", options.LaunchProfile.IsolationProfile);
        Assert.Equal("standard-small", options.LaunchProfile.CapacityProfile);
        Assert.Equal("public", options.LaunchProfile.NetworkOutcome);
        Assert.Equal("managed", options.LaunchProfile.DomainOutcome);
        Assert.Contains(_fixture.ReleaseCatalog.Queries, query => query.CatalogLifecycle == "supported" && query.RegistryClass == "paid");
        Assert.Contains(_fixture.ReleaseCatalog.Queries, query => query.CatalogLifecycle == "preview" && query.RegistryClass == "paid");
        var release = Assert.Single(options.Releases);
        Assert.Equal("future-runtime", release.DistributionId);
        Assert.Equal("5.0", release.ReleaseLine);
        Assert.Equal("5.0.1", release.Version);
        Assert.Equal("stable", release.Channel);
        Assert.Equal("combined", release.TopologyId);
        var preview = Assert.Single(options.PreviewReleases!);
        Assert.Equal("preview-runtime", preview.DistributionId);
        Assert.Equal("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", preview.ManifestDigest);
        Assert.DoesNotContain(options.PreviewReleases!, x => x.DistributionId == "ambiguous-runtime");
    }

    [Fact]
    public async Task Onboarding_options_are_not_advertised_when_commercial_admission_is_constrained()
    {
        var app = await PrepareApplicationAsync([], [
            CatalogEntry("future-runtime", "5.0", "5.0.1", "stable", "combined", "supported", "paid")
        ]);
        var client = app.CreateControlIdentityClient(subject: "managed-constrained-owner");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        await SetSubscriptionStateAsync(app, workspaceId, OrganizationSubscriptionState.Constrained);

        var response = await client.GetAsync($"/api/workspaces/{workspaceId}/instances/onboarding-options");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(ElsaInstanceCommercialOperation.LifecycleConstrained,
            await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(_fixture.ReleaseCatalog.Query);
    }

    [Fact]
    public async Task Stripe_hosted_limit_returns_safe_counts_without_queueing_a_second_provider_operation()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("hosted-one-engine-owner");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
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

        await CreateCanonicalInstanceAsync(client, workspaceId, "hosted-first-engine");
        using var second = await SendCreateRequestAsync(
            client,
            workspaceId,
            "Second hosted runtime",
            "hosted-second-engine",
            Intent(),
            "create-hosted-second-engine");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        Assert.NotNull(problem);
        Assert.Equal("instance_limit_reached", problem["code"].GetString());
        Assert.Equal(1, problem["currentInstances"].GetInt32());
        Assert.Equal(1, problem["maxInstances"].GetInt32());
        Assert.DoesNotContain("stripe", await second.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await using var verifyScope = app.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(1, await CountRowsAsync(verify, "ElsaInstances"));
        Assert.Equal(1, await CountRowsAsync(verify, "ElsaInstanceOperations"));
        Assert.Equal(1, await CountRowsAsync(verify, "ElsaInstanceLifecycleOutbox"));
    }

    [Fact]
    public async Task Expired_Stripe_trial_rejects_create_without_queueing_provider_work()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("expired-hosted-trial-owner");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var organizationId = await db.Workspaces.Where(x => x.Id == workspaceId)
                .Select(x => x.OrganizationId)
                .SingleAsync();
            await new OrganizationBillingStore(db).StartTrialAsync(
                organizationId,
                BillingProviderNames.Stripe,
                DateTimeOffset.UtcNow.AddDays(-15));
        }

        using var response = await SendCreateRequestAsync(
            client,
            workspaceId,
            "Expired trial runtime",
            "expired-trial-runtime",
            Intent(),
            "create-expired-trial-runtime");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("instance.entitlement-expired", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var verifyScope = app.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(0, await CountRowsAsync(verify, "ElsaInstances"));
        Assert.Equal(0, await CountRowsAsync(verify, "ElsaInstanceOperations"));
        Assert.Equal(0, await CountRowsAsync(verify, "ElsaInstanceLifecycleOutbox"));
    }

    [Fact]
    public async Task Create_rejects_a_release_outside_the_eligible_catalog()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-ineligible-release-owner");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var intent = Intent() with
        {
            Release = new ElsaReleaseIntent("unavailable-runtime", "5.0", "5.0.1", "stable")
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/instances")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceCreateRequest("Future runtime", "future-runtime", intent),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", "create-ineligible-runtime");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("instance.catalog-selection-unavailable", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_accepts_only_an_explicit_matching_preview_manifest_consent()
    {
        const string digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var app = await PrepareApplicationAsync([], [
            CatalogEntry("preview-runtime", "4.1", "4.1.0-preview.1", "preview", "combined", "preview", "paid")
        ]);
        var client = app.CreateTrustedWorkspaceClient("managed-preview-owner");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var intent = Intent() with
        {
            Release = new ElsaReleaseIntent("preview-runtime", "4.1", "4.1.0-preview.1", "preview",
                previewManifestDigest: digest)
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/instances")
        {
            Content = JsonContent.Create(new ManagedElsaInstanceCreateRequest("Preview runtime", "preview-runtime", intent),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", "create-preview-runtime");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<ManagedElsaInstanceAcceptedResponse>(ControlApiTestApplication.JsonOptions);
        Assert.NotNull(accepted);
        var revisions = await client.GetControlJsonAsync<ManagedElsaInstanceRevisionsResponse>(
            $"/api/workspaces/{workspaceId}/instances/{accepted!.Instance.InstanceId}/revisions");
        var revision = Assert.Single(revisions!.Items);
        Assert.Equal(digest, revision.PreviewManifestDigest);
    }

    [Fact]
    public async Task Create_rejects_preview_without_explicit_manifest_consent()
    {
        var app = await PrepareApplicationAsync([], [PreviewCatalogEntry()]);
        var client = app.CreateTrustedWorkspaceClient("managed-preview-no-consent");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);

        var response = await SendCreateRequestAsync(
            client, workspaceId, "Preview runtime", "preview-runtime", PreviewIntent(), "create-preview-no-consent");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("instance.catalog-selection-unavailable", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(0, await CountRowsAsync(db, "ElsaInstances"));
    }

    [Fact]
    public async Task Create_rejects_preview_with_a_nonmatching_manifest_digest()
    {
        var app = await PrepareApplicationAsync([], [PreviewCatalogEntry()]);
        var client = app.CreateTrustedWorkspaceClient("managed-preview-wrong-digest");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);

        var response = await SendCreateRequestAsync(
            client, workspaceId, "Preview runtime", "preview-wrong-digest", PreviewIntent(Digest('b')), "create-preview-wrong-digest");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("instance.catalog-selection-unavailable", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(0, await CountRowsAsync(db, "ElsaInstances"));
    }

    [Fact]
    public async Task Update_rejects_preview_digest_mismatch_before_persisting_a_revision()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-preview-update");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(client, workspaceId, "preview-update-runtime");
        await MarkOperationSucceededAsync(app, created.Operation.Id);
        var revisionsBefore = await client.GetControlJsonAsync<ManagedElsaInstanceRevisionsResponse>(
            $"/api/workspaces/{workspaceId}/instances/{created.Instance.InstanceId}/revisions");
        var detailBefore = await client.GetControlJsonAsync<ManagedElsaInstanceResponse>(
            $"/api/workspaces/{workspaceId}/instances/{created.Instance.InstanceId}");

        _fixture.ReleaseCatalog.SetEntries([PreviewCatalogEntry()]);
        using var patch = new HttpRequestMessage(HttpMethod.Patch,
            $"/api/workspaces/{workspaceId}/instances/{created.Instance.InstanceId}")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstancePatchRequest(PreviewIntent(Digest('b'))),
                options: ControlApiTestApplication.JsonOptions)
        };
        patch.Headers.Add("Idempotency-Key", "update-preview-wrong-digest");
        patch.Headers.TryAddWithoutValidation("If-Match", created.Instance.ETag);

        var response = await client.SendAsync(patch);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("instance.catalog-selection-unavailable", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var revisionsAfter = await client.GetControlJsonAsync<ManagedElsaInstanceRevisionsResponse>(
            $"/api/workspaces/{workspaceId}/instances/{created.Instance.InstanceId}/revisions");
        var detailAfter = await client.GetControlJsonAsync<ManagedElsaInstanceResponse>(
            $"/api/workspaces/{workspaceId}/instances/{created.Instance.InstanceId}");
        Assert.NotNull(revisionsBefore);
        Assert.NotNull(revisionsAfter);
        Assert.Equal(revisionsBefore!.Items.Count, revisionsAfter!.Items.Count);
        Assert.Equal(revisionsBefore.Items.Single().ContentHash, revisionsAfter.Items.Single().ContentHash);
        Assert.Equal(detailBefore!.Version, detailAfter!.Version);
        Assert.Equal(detailBefore.ETag, detailAfter.ETag);
        Assert.Equal(detailBefore.Intent!.ComputeCanonicalHash(), detailAfter.Intent!.ComputeCanonicalHash());
    }

    [Fact]
    public async Task Create_rejects_a_client_overridden_launch_profile()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-placement-owner");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var intent = Intent() with
        {
            Placement = new ElsaPlacementIntent("managed", "eastus", "dedicated", "standard-small", "public", "managed")
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/instances")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceCreateRequest("Altered placement", "altered-placement", intent),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", "create-altered-placement");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("instance.catalog-selection-unavailable", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void Slug_unique_reservation_conflict_maps_to_stable_api_code()
    {
        var code = ManagedElsaInstanceEndpoints.ConflictCode(
            new ElsaInstanceLifecycleConflictException(
                "Instance slug is already in use in this workspace.", ElsaInstanceLifecycleConflictReason.SlugConflict));

        Assert.Equal("instance.slug-conflict", code);
    }

    [Fact]
    public async Task Healthy_bound_instance_is_openable_but_deleting_instance_fails_closed()
    {
        var healthy = Guid.NewGuid();
        var app = await PrepareApplicationAsync([
            Instance(healthy, "Claims runtime", "claims-runtime"),
            Instance(
                Guid.NewGuid(),
                "Deleting runtime",
                "deleting-runtime",
                ElsaDesiredLifecycle.Deleting,
                ElsaObservedLifecycle.Deleting,
                ElsaInstanceHealth.Unknown,
                bound: false),
            Instance(
                Guid.NewGuid(),
                "Unbound healthy runtime",
                "unbound-healthy-runtime",
                bound: false)
        ]);

        var client = app.CreateControlIdentityClient(subject: "managed-owner");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        var response = await client.GetAsync($"/api/workspaces/{workspaceId}/managed-elsa/instances");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("private", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Contains("no-cache", response.Headers.Pragma.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        var instances = await response.Content.ReadFromJsonAsync<List<ManagedElsaInstanceResponse>>(ControlApiTestApplication.JsonOptions);
        Assert.NotNull(instances);
        var openable = Assert.Single(instances!, x => x.InstanceId == healthy);
        Assert.True(openable.CanOpen);
        Assert.Equal("urn:elsa:instance:" + healthy.ToString("D"), openable.Audience);
        Assert.Equal("https://managed.example.test/managed-elsa/handoff/callback", openable.RedirectUri);

        var deleting = Assert.Single(instances, x => x.DesiredLifecycle == ElsaDesiredLifecycle.Deleting);
        Assert.False(deleting.CanOpen);
        Assert.Null(deleting.Audience);
        Assert.Null(deleting.RedirectUri);

        var unbound = Assert.Single(instances, x => x.Slug == "unbound-healthy-runtime");
        Assert.False(unbound.CanOpen);
        Assert.Null(unbound.Audience);
        Assert.Null(unbound.RedirectUri);
    }

    [Fact]
    public async Task Caller_without_workspace_access_cannot_read_managed_instances()
    {
        var app = await PrepareApplicationAsync([Instance(Guid.NewGuid(), "Claims runtime", "claims-runtime")]);
        var owner = app.CreateControlIdentityClient(subject: "managed-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();

        var response = await app.CreateControlIdentityClient(subject: "managed-outsider")
            .GetAsync($"/api/workspaces/{workspaceId}/managed-elsa/instances");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Workspace_reader_without_open_permission_sees_redacted_binding()
    {
        var instanceId = Guid.NewGuid();
        var app = await PrepareApplicationAsync([Instance(instanceId, "Claims runtime", "claims-runtime")]);
        var owner = app.CreateTrustedWorkspaceClient("managed-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await app.AddWorkspaceMemberAsync(workspaceId, "managed-reader", ElsaControl.PackageCatalog.Core.Accounts.WorkspaceRole.Reader);

        var response = await app.CreateTrustedWorkspaceClient("managed-reader")
            .GetAsync($"/api/workspaces/{workspaceId}/managed-elsa/instances");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var instances = await response.Content.ReadFromJsonAsync<List<ManagedElsaInstanceResponse>>(ControlApiTestApplication.JsonOptions);
        var item = Assert.Single(instances!);
        Assert.False(item.CanOpen);
        Assert.Null(item.Audience);
        Assert.Null(item.RedirectUri);
    }

    [Fact]
    public async Task Canonical_create_returns_an_async_operation_and_safe_detail_projection()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-api-owner");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/instances")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceCreateRequest("Claims runtime", "Claims Runtime", Intent()),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", "create-claims-runtime");

        var accepted = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.NotNull(accepted.Headers.Location);
        Assert.Contains("private", accepted.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Contains("no-store", accepted.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var acceptedJson = await accepted.Content.ReadAsStringAsync();
        Assert.DoesNotContain("serializedPlan", acceptedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("providerId", acceptedJson, StringComparison.OrdinalIgnoreCase);
        var acceptedBody = System.Text.Json.JsonSerializer.Deserialize<ManagedElsaInstanceAcceptedResponse>(acceptedJson, ControlApiTestApplication.JsonOptions);
        Assert.NotNull(acceptedBody);
        Assert.Equal(ElsaInstanceOperationAction.Create, acceptedBody!.Operation.Action);
        Assert.Equal(accepted.Headers.Location!.ToString(), acceptedBody.Links["self"]);
        Assert.Equal("instance-unavailable", acceptedBody.Instance.IdentityBindingState);

        var operation = await client.GetControlJsonAsync<ManagedElsaInstanceOperationResponse>(accepted.Headers.Location!.ToString());
        Assert.NotNull(operation);
        Assert.Equal(acceptedBody.Operation.Id, operation!.Id);

        var detail = await client.GetControlJsonAsync<ManagedElsaInstanceResponse>(
            $"/api/workspaces/{workspaceId}/instances/{acceptedBody.Instance.InstanceId}");
        Assert.NotNull(detail);
        Assert.Equal("claims-runtime", detail!.Slug);
        Assert.Equal(detail.ETag, acceptedBody.Instance.ETag);

        var audit = await client.GetControlJsonAsync<ManagedElsaInstanceAuditResponse>(
            $"/api/workspaces/{workspaceId}/instances/{acceptedBody.Instance.InstanceId}/audit");
        var acceptedAudit = Assert.Single(audit!.Items);
        Assert.NotNull(acceptedAudit.ActorAccountId);
        Assert.Null(acceptedAudit.OperatorSubject);
    }

    [Fact]
    public async Task Provider_mutations_return_a_stable_422_when_the_entitlement_is_constrained()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-commercial-denied");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        await SetSubscriptionStateAsync(app, workspaceId, OrganizationSubscriptionState.Constrained);

        var create = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/instances")
        {
            Content = JsonContent.Create(new ManagedElsaInstanceCreateRequest("Denied runtime", "denied-runtime", Intent()),
                options: ControlApiTestApplication.JsonOptions)
        };
        create.Headers.Add("Idempotency-Key", "denied-create-runtime");
        var createResponse = await client.SendAsync(create);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, createResponse.StatusCode);
        Assert.Contains(ElsaInstanceCommercialOperation.LifecycleConstrained, await createResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await SetSubscriptionStateAsync(app, workspaceId, OrganizationSubscriptionState.Active);
        var created = await CreateCanonicalInstanceAsync(client, workspaceId, "denied-patch-runtime");
        await MarkOperationSucceededAsync(app, created.Operation.Id);
        await SetSubscriptionStateAsync(app, workspaceId, OrganizationSubscriptionState.Constrained);
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/workspaces/{workspaceId}/instances/{created.Instance.InstanceId}")
        {
            Content = JsonContent.Create(new ManagedElsaInstancePatchRequest(Name: "Denied rename"),
                options: ControlApiTestApplication.JsonOptions)
        };
        patch.Headers.Add("Idempotency-Key", "denied-rename-runtime");
        patch.Headers.TryAddWithoutValidation("If-Match", created.Instance.ETag);
        var patchResponse = await client.SendAsync(patch);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, patchResponse.StatusCode);
        Assert.Contains(ElsaInstanceCommercialOperation.LifecycleConstrained, await patchResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_canonical_create_reserves_one_collection_idempotency_key_and_replays_exactly()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-concurrent-create");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        const string key = "concurrent-create-runtime";

        Task<HttpResponseMessage> SendAsync(string name, string slug)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/instances")
            {
                Content = JsonContent.Create(new ManagedElsaInstanceCreateRequest(name, slug, Intent()),
                    options: ControlApiTestApplication.JsonOptions)
            };
            request.Headers.Add("Idempotency-Key", key);
            return client.SendAsync(request);
        }

        var responses = await Task.WhenAll(
            SendAsync("Concurrent runtime", "concurrent-runtime"),
            SendAsync("Concurrent runtime", "concurrent-runtime"));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        var accepted = await Task.WhenAll(responses.Select(response =>
            response.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>()));
        Assert.All(accepted, response => Assert.NotNull(response));
        Assert.Single(accepted.Select(response => response!.Instance.InstanceId).Distinct());
        Assert.Single(accepted.Select(response => response!.Operation.Id).Distinct());

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            Assert.Equal(1, await CountRowsAsync(db, "ElsaInstances"));
            Assert.Equal(1, await CountRowsAsync(db, "ElsaInstanceOperations"));
            Assert.Equal(1, await CountRowsAsync(db, "ElsaInstanceLifecycleOutbox"));
        }

        var replay = await SendAsync("Concurrent runtime", "concurrent-runtime");
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var replayBody = await replay.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
        Assert.Equal(accepted[0]!.Instance.InstanceId, replayBody!.Instance.InstanceId);
        Assert.Equal(accepted[0]!.Operation.Id, replayBody.Operation.Id);

        var conflict = await SendAsync("Changed runtime", "changed-runtime");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("instance.idempotency-conflict", await conflict.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canonical_detail_uses_current_identity_seam_for_callback_rotation_and_rejects_stale_binding()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-current-identity");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(client, workspaceId, "identity-rotation-runtime");
        var instanceId = created.Instance.InstanceId;
        var rotationChangedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        Guid organizationId;

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            organizationId = await db.Workspaces.Where(x => x.Id == workspaceId)
                .Select(x => x.OrganizationId)
                .SingleAsync();
            await SetOpenableDeploymentEndpointAsync(db, instanceId, "https://old-managed.example.test");
            var identities = scope.ServiceProvider.GetRequiredService<IManagedElsaInstanceIdentityStore>();
            var bound = await identities.BindAsync(organizationId, workspaceId, instanceId,
                "https://old-managed.example.test", expectedBindingVersion: null, DateTimeOffset.UtcNow);
            Assert.True(bound.Succeeded);
        }

        var original = await client.GetControlJsonAsync<ManagedElsaInstanceResponse>(
            $"/api/workspaces/{workspaceId}/instances/{instanceId}");
        Assert.True(original!.CanOpen);
        Assert.Equal("https://old-managed.example.test/managed-elsa/handoff/callback", original.RedirectUri);
        Assert.Equal(1, original.IdentityBinding!.BindingVersion);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await SetOpenableDeploymentEndpointAsync(db, instanceId, "https://rotated-managed.example.test");
        }

        var stale = await client.GetControlJsonAsync<ManagedElsaInstanceResponse>(
            $"/api/workspaces/{workspaceId}/instances/{instanceId}");
        Assert.False(stale!.CanOpen);
        Assert.Null(stale.Audience);
        Assert.Null(stale.RedirectUri);
        Assert.Null(stale.IdentityBinding);
        Assert.Equal("identity-unavailable", stale.IdentityBindingState);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var identities = scope.ServiceProvider.GetRequiredService<IManagedElsaInstanceIdentityStore>();
            var rotated = await identities.BindAsync(organizationId, workspaceId, instanceId,
                "https://rotated-managed.example.test", expectedBindingVersion: 1, rotationChangedAt);
            Assert.True(rotated.Succeeded);
        }

        var current = await client.GetControlJsonAsync<ManagedElsaInstanceResponse>(
            $"/api/workspaces/{workspaceId}/instances/{instanceId}");
        Assert.True(current!.CanOpen);
        Assert.Equal("urn:elsa:instance:" + instanceId.ToString("D"), current.Audience);
        Assert.Equal("https://rotated-managed.example.test/managed-elsa/handoff/callback", current.RedirectUri);
        Assert.Equal(current.RedirectUri, current.IdentityBinding!.CanonicalCallbackUri);
        Assert.Equal("https://rotated-managed.example.test", current.IdentityBinding.VerifiedEndpointOrigin);
        Assert.Equal(2, current.IdentityBinding.BindingVersion);
        Assert.Equal(rotationChangedAt, current.IdentityBinding.ChangedAt);
        Assert.DoesNotContain("old-managed",
            System.Text.Json.JsonSerializer.Serialize(current, ControlApiTestApplication.JsonOptions),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canonical_list_and_detail_offer_open_only_once_the_provider_configured_the_managed_handoff()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-handoff-gate");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var instanceId = (await CreateCanonicalInstanceAsync(client, workspaceId, "handoff-gate-runtime")).Instance.InstanceId;
        await SetDeploymentAsync(managedHandoff: false);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var organizationId = await db.Workspaces.Where(x => x.Id == workspaceId).Select(x => x.OrganizationId).SingleAsync();
            var bound = await scope.ServiceProvider.GetRequiredService<IManagedElsaInstanceIdentityStore>().BindAsync(
                organizationId, workspaceId, instanceId, "https://managed.example.test", expectedBindingVersion: null, DateTimeOffset.UtcNow);
            Assert.True(bound.Succeeded);
        }

        // A healthy, bound instance whose runtime was deployed without the handoff would answer Open with 404.
        foreach (var unconfigured in await ReadBothProjectionsAsync())
        {
            Assert.False(unconfigured.CanOpen);
            Assert.Null(unconfigured.Audience);
            Assert.Null(unconfigured.RedirectUri);
            Assert.Null(unconfigured.IdentityBinding);
            Assert.Equal("handoff-unavailable", unconfigured.IdentityBindingState);
            Assert.Equal(ManagedElsaInstanceEndpoints.HandoffUnavailableReason, unconfigured.UnavailableReason);
        }

        await SetDeploymentAsync(managedHandoff: true);

        foreach (var configured in await ReadBothProjectionsAsync())
        {
            Assert.True(configured.CanOpen);
            Assert.Equal(ElsaInstanceIdentityBinding.AudienceFor(instanceId), configured.Audience);
            Assert.Equal("https://managed.example.test/managed-elsa/handoff/callback", configured.RedirectUri);
            Assert.Equal("available", configured.IdentityBindingState);
            Assert.Null(configured.UnavailableReason);
        }

        async Task SetDeploymentAsync(bool managedHandoff)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await SetOpenableDeploymentEndpointAsync(scope.ServiceProvider.GetRequiredService<CatalogDbContext>(),
                instanceId, "https://managed.example.test", managedHandoff);
        }

        async Task<ManagedElsaInstanceResponse[]> ReadBothProjectionsAsync()
        {
            var detail = await client.GetControlJsonAsync<ManagedElsaInstanceResponse>(
                $"/api/workspaces/{workspaceId}/instances/{instanceId}");
            var list = await client.GetControlJsonAsync<ManagedElsaInstanceListResponse>(
                $"/api/workspaces/{workspaceId}/instances");
            return [detail!, Assert.Single(list!.Items)];
        }
    }

    [Fact]
    public async Task Canonical_list_handles_large_page_numbers_without_overflowing_has_more()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-api-pagination");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/instances")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceCreateRequest("Claims runtime", "claims-runtime", Intent()),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", "create-for-pagination");
        var accepted = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        var list = await client.GetControlJsonAsync<ManagedElsaInstanceListResponse>(
            $"/api/workspaces/{workspaceId}/instances?page={int.MaxValue}&pageSize=100");

        Assert.NotNull(list);
        Assert.Empty(list!.Items);
        Assert.Equal(1, list.TotalCount);
        Assert.False(list.HasMore);
    }

    [Fact]
    public async Task Canonical_mutations_require_idempotency_and_strong_etags()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-api-preconditions");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        var instanceId = Guid.NewGuid();

        var missingKey = await client.PostControlJsonAsync(
            $"/api/workspaces/{workspaceId}/instances/{instanceId}/operations",
            new ManagedElsaInstanceOperationRequest(ElsaInstanceOperationAction.Start, 1));
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);

        var missingMatch = new HttpRequestMessage(HttpMethod.Patch, $"/api/workspaces/{workspaceId}/instances/{instanceId}")
        {
            Content = JsonContent.Create(new ManagedElsaInstancePatchRequest(Name: "Renamed"), options: ControlApiTestApplication.JsonOptions)
        };
        missingMatch.Headers.Add("Idempotency-Key", "rename-claims-runtime");
        var response = await client.SendAsync(missingMatch);
        Assert.Equal((HttpStatusCode)428, response.StatusCode);

        var operationWithoutMatch = new HttpRequestMessage(HttpMethod.Post,
            $"/api/workspaces/{workspaceId}/instances/{instanceId}/operations")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceOperationRequest(ElsaInstanceOperationAction.Start, 1),
                options: ControlApiTestApplication.JsonOptions)
        };
        operationWithoutMatch.Headers.Add("Idempotency-Key", "start-claims-runtime");
        response = await client.SendAsync(operationWithoutMatch);
        Assert.Equal((HttpStatusCode)428, response.StatusCode);
    }

    [Fact]
    public async Task Canonical_mutations_reject_ambiguous_multi_value_if_match()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-api-multi-if-match");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        var instanceId = Guid.NewGuid();

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/workspaces/{workspaceId}/instances/{instanceId}")
        {
            Content = JsonContent.Create(new ManagedElsaInstancePatchRequest(Name: "Renamed"), options: ControlApiTestApplication.JsonOptions)
        };
        patch.Headers.Add("Idempotency-Key", "rename-multi-if-match");
        patch.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        patch.Headers.TryAddWithoutValidation("If-Match", "\"2\"");
        var response = await client.SendAsync(patch);
        Assert.Equal((HttpStatusCode)428, response.StatusCode);
    }

    [Fact]
    public async Task Canonical_operation_maps_typed_version_conflict_to_precondition_failed()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-version-conflict");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(client, workspaceId, "version-conflict-runtime");

        var response = await SendOperationAsync(client, workspaceId, created.Instance.InstanceId,
            "\"999\"", "version-conflict-start", new(ElsaInstanceOperationAction.Start));

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Contains("instance.version-conflict", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canonical_operation_maps_typed_active_operation_conflict_to_conflict()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-active-operation");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(client, workspaceId, "active-operation-runtime");

        var response = await SendOperationAsync(client, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, "active-operation-reconcile", new(ElsaInstanceOperationAction.Reconcile));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("instance.operation-active", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_api_key_can_discover_the_exact_active_operation()
    {
        var app = await PrepareApplicationAsync([]);
        var customer = app.CreateTrustedWorkspaceClient("admin-recovery-discovery-owner");
        var workspaceId = await customer.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(customer, workspaceId, "admin-recovery-discovery-runtime");
        await MarkOperationRecoveryRequiredAsync(app, created.Operation.Id);
        var path = $"/api/admin/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/operations/current";

        using var anonymous = app.CreateClient();
        using var unauthenticated = await anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var customerIdentity = app.CreateControlIdentityClient();
        using var customerResponse = await customerIdentity.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, customerResponse.StatusCode);

        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        using var response = await admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(created.Instance.ETag, response.Headers.ETag?.Tag);
        var body = (await response.Content.ReadControlJsonAsync<ManagedElsaInstanceOperationResponse>())!;
        Assert.Equal(created.Operation.Id, body.Id);
        Assert.Equal(ElsaInstanceOperationAction.Create, body.Action);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, body.State);
    }

    [Fact]
    public async Task Admin_api_key_can_recover_exact_operation_and_replay_exact_request()
    {
        var app = await PrepareApplicationAsync([]);
        var customer = app.CreateTrustedWorkspaceClient("admin-recovery-owner");
        var workspaceId = await customer.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(customer, workspaceId, "admin-recovery-runtime");
        await MarkOperationRecoveryRequiredAsync(app, created.Operation.Id);

        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        var path = $"/api/admin/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/operations/{created.Operation.Id:D}/recover";
        var request = new AdminManagedElsaRecoveryRequest("operator recovery");

        using var first = await SendAdminRecoveryAsync(admin, path, created.Instance.ETag, "admin-recovery-key", request);
        var firstText = await first.Content.ReadAsStringAsync();
        Assert.True(first.StatusCode == HttpStatusCode.Accepted, firstText);
        var firstBody = (await first.Content.ReadControlJsonAsync<AdminManagedElsaRecoveryResponse>())!;
        Assert.Equal(created.Operation.Id, firstBody.OperationId);
        Assert.Equal(ElsaInstanceOperationState.Queued, firstBody.State);
        Assert.Equal(2, firstBody.AttemptNumber);
        Assert.False(firstBody.Replayed);
        Assert.Equal(path[..path.LastIndexOf("/recover", StringComparison.Ordinal)], firstBody.OperationUrl);

        var rowCounts = await ReadLifecycleRowCountsAsync(app);
        using var replay = await SendAdminRecoveryAsync(admin, path, created.Instance.ETag, "admin-recovery-key", request);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var replayBody = (await replay.Content.ReadControlJsonAsync<AdminManagedElsaRecoveryResponse>())!;
        Assert.Equal(firstBody.OperationId, replayBody.OperationId);
        Assert.Equal(firstBody.AttemptNumber, replayBody.AttemptNumber);
        Assert.True(replayBody.Replayed);
        Assert.Equal(rowCounts, await ReadLifecycleRowCountsAsync(app));

        using var status = await admin.GetAsync(firstBody.OperationUrl);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var statusBody = (await status.Content.ReadControlJsonAsync<ManagedElsaInstanceOperationResponse>())!;
        Assert.Equal(created.Operation.Id, statusBody.Id);
        Assert.Equal(ElsaInstanceOperationState.Queued, statusBody.State);
    }

    [Fact]
    public async Task Admin_can_recover_failed_create_while_delete_waits_for_that_operation()
    {
        var app = await PrepareApplicationAsync([]);
        var customer = app.CreateTrustedWorkspaceClient("admin-recovery-waiting-delete-owner");
        var workspaceId = await customer.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(customer, workspaceId, "admin-recovery-waiting-delete-runtime");
        await MarkOperationRecoveryRequiredAsync(app, created.Operation.Id);

        using var confirmationResponse = await customer.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);

        using var deletion = await SendDeleteAsync(customer, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, "admin-recovery-waiting-delete", confirmation!.ConfirmationId);
        var deletionText = await deletion.Content.ReadAsStringAsync();
        Assert.True(deletion.StatusCode == HttpStatusCode.Accepted, deletionText);
        var deletionBody = (await deletion.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteAcceptedResponse>())!;
        await AssertOperationStateAsync(app, deletionBody.OperationId, ElsaInstanceOperationState.WaitingForPriorOperation);

        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        var currentPath = $"/api/admin/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/operations/current";
        using var current = await admin.GetAsync(currentPath);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var currentBody = (await current.Content.ReadControlJsonAsync<ManagedElsaInstanceOperationResponse>())!;
        Assert.Equal(created.Operation.Id, currentBody.Id);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, currentBody.State);

        var recoveryPath = $"/api/admin/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/operations/{created.Operation.Id:D}/recover";
        using var recovery = await SendAdminRecoveryAsync(admin, recoveryPath, current.Headers.ETag!.Tag,
            "admin-recovery-waiting-delete-resume", new("operator recovery with waiting delete"));
        var recoveryText = await recovery.Content.ReadAsStringAsync();
        Assert.True(recovery.StatusCode == HttpStatusCode.Accepted, recoveryText);
        await AssertOperationStateAsync(app, created.Operation.Id, ElsaInstanceOperationState.Queued);
        await AssertOperationStateAsync(app, deletionBody.OperationId, ElsaInstanceOperationState.WaitingForPriorOperation);
    }

    [Fact]
    public async Task Admin_topology_exposes_safe_complete_nonterminal_operation_graph()
    {
        var app = await PrepareApplicationAsync([]);
        var customer = app.CreateTrustedWorkspaceClient("admin-topology-owner");
        var workspaceId = await customer.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(customer, workspaceId, "admin-topology-runtime");
        await MarkOperationRecoveryRequiredAsync(app, created.Operation.Id);

        using var confirmationResponse = await customer.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);

        using var deletion = await SendDeleteAsync(customer, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, "admin-topology-delete", confirmation!.ConfirmationId);
        var deletionText = await deletion.Content.ReadAsStringAsync();
        Assert.True(deletion.StatusCode == HttpStatusCode.Accepted, deletionText);
        var deletionBody = (await deletion.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteAcceptedResponse>())!;

        const string privateWorker = "private-worker-secret";
        const string privateLeaseHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var quarantinedAt = DateTimeOffset.UtcNow.UtcTicks;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ElsaInstanceOperations
                SET WorkerId = {privateWorker}, LeaseTokenHash = {privateLeaseHash}
                WHERE Id = {created.Operation.Id}
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ElsaInstanceLifecycleOutbox
                SET QuarantinedAt = {quarantinedAt}, QuarantineCode = {"outbox.invalid"}
                WHERE OperationId = {deletionBody.OperationId}
                """);
        }

        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        var path = $"/api/admin/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/operations/topology";
        using var response = await admin.GetAsync(path);
        var responseText = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var topology = System.Text.Json.JsonSerializer.Deserialize<ElsaInstanceLifecycleTopologySnapshot>(
            responseText, ControlApiTestApplication.JsonOptions)!;
        Assert.Equal($"\"{topology.InstanceVersion}\"", response.Headers.ETag?.Tag);
        Assert.Equal(created.Instance.InstanceId, topology.InstanceId);
        Assert.Equal(ElsaDesiredLifecycle.Deleting, topology.DesiredLifecycle);
        Assert.Equal(deletionBody.OperationId, topology.LastOperationId);
        Assert.Collection(topology.Operations,
            create =>
            {
                Assert.Equal(created.Operation.Id, create.Id);
                Assert.Equal(ElsaInstanceOperationAction.Create, create.Action);
                Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, create.State);
                Assert.NotNull(create.Outbox);
                Assert.Null(create.Outbox!.QuarantinedAt);
            },
            delete =>
            {
                Assert.Equal(deletionBody.OperationId, delete.Id);
                Assert.Equal(ElsaInstanceOperationAction.Delete, delete.Action);
                Assert.Equal(ElsaInstanceOperationState.WaitingForPriorOperation, delete.State);
                Assert.NotNull(delete.Outbox);
                Assert.NotNull(delete.Outbox!.QuarantinedAt);
                Assert.Equal("outbox.invalid", delete.Outbox.QuarantineCode);
            });
        Assert.DoesNotContain(privateWorker, responseText, StringComparison.Ordinal);
        Assert.DoesNotContain(privateLeaseHash, responseText, StringComparison.Ordinal);
        Assert.DoesNotContain("requestHash", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lease", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("worker", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evidence", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("providerPayload", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("providerResponse", responseText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Admin_topology_requires_operator_authentication_and_exact_scope()
    {
        var app = await PrepareApplicationAsync([]);
        var customer = app.CreateTrustedWorkspaceClient("admin-topology-scope-owner");
        var workspaceId = await customer.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(customer, workspaceId, "admin-topology-scope-runtime");
        var path = $"/api/admin/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/operations/topology";

        using var anonymous = app.CreateClient();
        using var anonymousResponse = await anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var customerIdentity = app.CreateControlIdentityClient();
        using var customerResponse = await customerIdentity.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, customerResponse.StatusCode);

        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        using var controlAdmin = app.CreateClient();
        app.AddControlSessionCookie(controlAdmin, new Claim("roles", AdminAuthorization.ControlAdminRole));
        using var controlAdminResponse = await controlAdmin.GetAsync(path);
        using var wrongWorkspace = await admin.GetAsync(
            $"/api/admin/workspaces/{Guid.NewGuid():D}/instances/{created.Instance.InstanceId:D}/operations/topology");
        using var wrongInstance = await admin.GetAsync(
            $"/api/admin/workspaces/{workspaceId:D}/instances/{Guid.NewGuid():D}/operations/topology");
        Assert.Equal(HttpStatusCode.OK, controlAdminResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongWorkspace.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongInstance.StatusCode);
    }

    [Fact]
    public async Task Admin_topology_returns_safe_conflict_when_revalidation_detects_drift()
    {
        await using var app = await CreateTopologyTestApplicationAsync<TopologyChangedStore>();
        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");

        using var response = await admin.GetAsync(
            $"/api/admin/workspaces/{Guid.NewGuid():D}/instances/{Guid.NewGuid():D}/operations/topology");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var responseText = await response.Content.ReadAsStringAsync();
        Assert.Contains("instance.topology-changed", responseText, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(ElsaInstanceLifecycleTopologyChangedException), responseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_topology_represents_missing_outbox_authority_explicitly()
    {
        await using var app = await CreateTopologyTestApplicationAsync<MissingOutboxTopologyStore>();
        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        var workspaceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();

        using var response = await admin.GetAsync(
            $"/api/admin/workspaces/{workspaceId:D}/instances/{instanceId:D}/operations/topology");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var topology = await response.Content.ReadControlJsonAsync<ElsaInstanceLifecycleTopologySnapshot>();
        var operation = Assert.Single(topology!.Operations);
        Assert.Equal(instanceId, topology.InstanceId);
        Assert.Equal(ElsaInstanceOperationState.WaitingForPriorOperation, operation.State);
        Assert.Null(operation.Outbox);
    }

    [Fact]
    public async Task Admin_recovery_requires_exact_operation_and_preserves_the_active_operation()
    {
        var app = await PrepareApplicationAsync([]);
        var customer = app.CreateTrustedWorkspaceClient("admin-recovery-operation-binding");
        var workspaceId = await customer.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(customer, workspaceId, "admin-recovery-operation-binding-runtime");
        await MarkOperationRecoveryRequiredAsync(app, created.Operation.Id);

        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        var path = $"/api/admin/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/operations/{Guid.NewGuid():D}/recover";
        using var response = await SendAdminRecoveryAsync(
            admin,
            path,
            created.Instance.ETag,
            "admin-recovery-wrong-operation",
            new AdminManagedElsaRecoveryRequest("wrong operation"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("instance.invalid-state", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(1, await CountOperationsAsync(app));
        await AssertOperationStateAsync(app, created.Operation.Id, ElsaInstanceOperationState.RecoveryRequired);
    }

    [Fact]
    public async Task Admin_recovery_is_scoped_to_the_exact_workspace_and_instance()
    {
        var app = await PrepareApplicationAsync([]);
        var customer = app.CreateTrustedWorkspaceClient("admin-recovery-scope");
        var workspaceId = await customer.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(customer, workspaceId, "admin-recovery-scope-runtime");
        await MarkOperationRecoveryRequiredAsync(app, created.Operation.Id);

        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        var request = new AdminManagedElsaRecoveryRequest("scope check");
        var wrongWorkspacePath = $"/api/admin/workspaces/{Guid.NewGuid():D}/instances/{created.Instance.InstanceId:D}/operations/{created.Operation.Id:D}/recover";
        using var wrongWorkspace = await SendAdminRecoveryAsync(admin, wrongWorkspacePath, created.Instance.ETag, "wrong-workspace", request);
        Assert.Equal(HttpStatusCode.NotFound, wrongWorkspace.StatusCode);

        var wrongInstancePath = $"/api/admin/workspaces/{workspaceId:D}/instances/{Guid.NewGuid():D}/operations/{created.Operation.Id:D}/recover";
        using var wrongInstance = await SendAdminRecoveryAsync(admin, wrongInstancePath, created.Instance.ETag, "wrong-instance", request);
        Assert.Equal(HttpStatusCode.NotFound, wrongInstance.StatusCode);
        await AssertOperationStateAsync(app, created.Operation.Id, ElsaInstanceOperationState.RecoveryRequired);
    }

    [Fact]
    public async Task Admin_recovery_rejects_an_operation_that_is_not_recovery_required()
    {
        var app = await PrepareApplicationAsync([]);
        var customer = app.CreateTrustedWorkspaceClient("admin-recovery-state");
        var workspaceId = await customer.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(customer, workspaceId, "admin-recovery-state-runtime");

        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        var path = $"/api/admin/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/operations/{created.Operation.Id:D}/recover";
        using var response = await SendAdminRecoveryAsync(
            admin,
            path,
            created.Instance.ETag,
            "not-recovery-required",
            new AdminManagedElsaRecoveryRequest("invalid state"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("instance.invalid-state", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await AssertOperationStateAsync(app, created.Operation.Id, ElsaInstanceOperationState.Accepted);
    }

    [Fact]
    public async Task Admin_recovery_requires_operator_authentication_and_strong_preconditions()
    {
        var app = await PrepareApplicationAsync([]);
        var workspaceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var path = $"/api/admin/workspaces/{workspaceId:D}/instances/{instanceId:D}/operations/{operationId:D}/recover";
        var body = new AdminManagedElsaRecoveryRequest("precondition check");

        using var anonymous = app.CreateClient();
        using var unauthenticated = await SendAdminRecoveryAsync(anonymous, path, "\"1\"", "anonymous-key", body);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var customer = app.CreateControlIdentityClient();
        using var customerResponse = await SendAdminRecoveryAsync(customer, path, "\"1\"", "customer-key", body);
        Assert.Equal(HttpStatusCode.Unauthorized, customerResponse.StatusCode);

        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        using var missingIfMatch = await SendAdminRecoveryAsync(admin, path, null, "missing-if-match", body);
        Assert.Equal((HttpStatusCode)428, missingIfMatch.StatusCode);
        Assert.Contains("instance.if-match-required", await missingIfMatch.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var missingIdempotency = await SendAdminRecoveryAsync(admin, path, "\"1\"", null, body);
        Assert.Equal(HttpStatusCode.BadRequest, missingIdempotency.StatusCode);
        Assert.Contains("instance.idempotency-key-required", await missingIdempotency.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ElsaInstanceOperationAction.Start)]
    [InlineData(ElsaInstanceOperationAction.Retry)]
    [InlineData(ElsaInstanceOperationAction.Recover)]
    public async Task Canonical_operation_maps_invalid_request_state_to_stable_conflict(
        ElsaInstanceOperationAction action)
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient($"managed-instance-invalid-{action}");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(client, workspaceId, $"invalid-{action.ToString().ToLowerInvariant()}");
        await MarkOperationSucceededAsync(app, created.Operation.Id);

        var response = await SendOperationAsync(client, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, $"invalid-{action}", new(action));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("instance.invalid-state", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canonical_operation_key_cannot_be_reused_for_a_different_terminal_action()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-cross-action-idempotency");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(client, workspaceId, "cross-action-runtime");
        await MarkOperationSucceededAsync(app, created.Operation.Id);
        var first = await SendOperationAsync(client, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, "shared-operation-key", new(ElsaInstanceOperationAction.Reconcile));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstBody = await first.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
        await MarkOperationSucceededAsync(app, firstBody!.Operation.Id);
        var operationCount = await CountOperationsAsync(app);

        var conflict = await SendOperationAsync(client, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, "shared-operation-key", new(ElsaInstanceOperationAction.Stop));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("instance.idempotency-conflict", await conflict.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(operationCount, await CountOperationsAsync(app));
    }

    [Fact]
    public async Task Canonical_delete_requires_matching_confirmation_and_replays_exact_request()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-delete-owner");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(client, workspaceId, "delete-runtime");

        var missing = await SendOperationAsync(client, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, "delete-runtime", new(ElsaInstanceOperationAction.Delete));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("instance.delete-confirmation-required", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var wrongConfirmation = await CreateConfirmationAsync(
            client, workspaceId, ConfirmationActionType.DeleteManagedInstance, Guid.NewGuid().ToString("D"));
        var wrong = await SendOperationAsync(client, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, "delete-runtime", new(ElsaInstanceOperationAction.Delete, DeleteConfirmationId: wrongConfirmation.Id));
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Contains("instance.delete-confirmation-invalid", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var confirmation = await CreateConfirmationAsync(
            client, workspaceId, ConfirmationActionType.DeleteManagedInstance, created.Instance.InstanceId.ToString("D"));
        var request = new ManagedElsaInstanceOperationRequest(ElsaInstanceOperationAction.Delete, DeleteConfirmationId: confirmation.Id);
        var first = await SendOperationAsync(client, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, "delete-runtime-confirmed", request);
        var replay = await SendOperationAsync(client, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, "delete-runtime-confirmed", request);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var firstBody = await first.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
        var replayBody = await replay.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
        Assert.Equal(firstBody!.Operation.Id, replayBody!.Operation.Id);

        var replacementConfirmation = await CreateConfirmationAsync(
            client, workspaceId, ConfirmationActionType.DeleteManagedInstance, created.Instance.InstanceId.ToString("D"));
        var mismatchedReplay = await SendOperationAsync(client, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, "delete-runtime-confirmed",
            new(ElsaInstanceOperationAction.Delete, DeleteConfirmationId: replacementConfirmation.Id));
        Assert.Equal(HttpStatusCode.Conflict, mismatchedReplay.StatusCode);
        Assert.Contains("instance.idempotency-conflict", await mismatchedReplay.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dedicated_delete_contract_is_idempotent_scoped_and_returns_only_safe_operation_state()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-narrow-delete-owner");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(client, workspaceId, "narrow-delete-runtime");

        using var confirmationResponse = await client.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations",
            content: null);
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.NotNull(confirmation);
        Assert.True(confirmation!.ExpiresAt > DateTimeOffset.UtcNow);
        var confirmationJson = await confirmationResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("confirmedByAccountId", confirmationJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspaceId", confirmationJson, StringComparison.OrdinalIgnoreCase);

        using var first = await SendDeleteAsync(client, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, "narrow-delete-idempotency", confirmation.ConfirmationId);
        using var replay = await SendDeleteAsync(client, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, "narrow-delete-idempotency", confirmation.ConfirmationId);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var firstBody = await first.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteAcceptedResponse>();
        var replayBody = await replay.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteAcceptedResponse>();
        Assert.NotNull(firstBody);
        Assert.Equal(firstBody!.OperationId, replayBody!.OperationId);
        Assert.Equal($"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-operations/{firstBody.OperationId:D}",
            firstBody.OperationUrl);

        var operationCount = await CountOperationsAsync(app);
        var acceptedEtag = first.Headers.ETag?.ToString() ?? created.Instance.ETag;
        using var reusedConfirmation = await SendDeleteAsync(client, workspaceId, created.Instance.InstanceId,
            acceptedEtag, "narrow-delete-reused-confirmation", confirmation.ConfirmationId);
        Assert.Equal(HttpStatusCode.Conflict, reusedConfirmation.StatusCode);
        Assert.Equal(operationCount, await CountOperationsAsync(app));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var now = DateTimeOffset.UtcNow;
            var state = ElsaInstanceOperationState.Succeeded.ToString();
            var failureCode = "provider.private-secret-value";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE ElsaInstances SET DesiredLifecycle = {ElsaDesiredLifecycle.Deleting.ToString()}, ObservedLifecycle = {ElsaObservedLifecycle.Deleted.ToString()}, Health = {ElsaInstanceHealth.Unknown.ToString()}, DeletedAt = {now.UtcTicks}, CurrentDeploymentId = NULL, CurrentDeploymentRevisionId = NULL, CurrentDeploymentEndpointUri = NULL, CurrentDeploymentManagedHandoff = 0, PlacementAssignmentId = NULL, ElsaTenantId = NULL, ElsaTenantAudience = NULL WHERE Id = {created.Instance.InstanceId}");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE ElsaInstanceOperations SET State = {state}, CompletedAt = {now.UtcTicks}, FailureCode = {failureCode} WHERE Id = {firstBody.OperationId}");
        }

        using var status = await client.GetAsync(firstBody.OperationUrl);
        using var wrongScope = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{Guid.NewGuid():D}/delete-operations/{firstBody.OperationId:D}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongScope.StatusCode);
        var statusBody = await status.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteOperationResponse>();
        Assert.NotNull(statusBody);
        Assert.Equal(firstBody.OperationId, statusBody!.OperationId);
        Assert.Equal(ElsaInstanceOperationState.Succeeded, statusBody.State);
        var statusJson = await status.Content.ReadAsStringAsync();
        Assert.DoesNotContain("provider.private-secret-value", statusJson, StringComparison.Ordinal);
        Assert.DoesNotContain("failureCode", statusJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resolvedPlanId", statusJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("providerResponse", statusJson, StringComparison.OrdinalIgnoreCase);

        var recoveryProjection = ManagedElsaInstanceEndpoints.ToDeleteOperationResponse(new ElsaInstanceOperationSummary(
            firstBody.OperationId,
            created.Instance.InstanceId,
            ElsaInstanceOperationAction.Delete,
            ElsaInstanceOperationState.RecoveryRequired,
            created.Instance.Version,
            2,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            "private-revision-reference",
            "private-plan-reference",
            Guid.NewGuid(),
            "provider.private-secret-value",
            ElsaObservedLifecycle.Deleting,
            ElsaInstanceHealth.Unknown));
        var recoveryJson = System.Text.Json.JsonSerializer.Serialize(recoveryProjection, ControlApiTestApplication.JsonOptions);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, recoveryProjection.State);
        Assert.DoesNotContain("provider.private-secret-value", recoveryJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-revision-reference", recoveryJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-plan-reference", recoveryJson, StringComparison.Ordinal);

        var otherOwner = app.CreateTrustedWorkspaceClient("managed-instance-narrow-delete-other-organization");
        var otherWorkspaceId = await otherOwner.GetDefaultWorkspaceIdAsync();
        using var crossWorkspaceConfirmation = await otherOwner.PostAsync(
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        using var crossWorkspaceOperation = await otherOwner.GetAsync(
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-operations/{firstBody.OperationId:D}");
        Assert.Equal(HttpStatusCode.NotFound, crossWorkspaceConfirmation.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossWorkspaceOperation.StatusCode);
    }

    [Fact]
    public async Task Dedicated_delete_resumes_recovery_required_delete_with_fresh_confirmation()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-delete-recovery-owner");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(client, workspaceId, "delete-recovery-runtime");
        await MarkOperationSucceededAsync(app, created.Operation.Id);

        using var firstConfirmationResponse = await client.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations",
            content: null);
        var firstConfirmation = await firstConfirmationResponse.Content
            .ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, firstConfirmationResponse.StatusCode);

        using var firstDelete = await SendDeleteAsync(
            client,
            workspaceId,
            created.Instance.InstanceId,
            created.Instance.ETag,
            "delete-recovery-first",
            firstConfirmation!.ConfirmationId);
        var firstDeleteBody = await firstDelete.Content
            .ReadControlJsonAsync<ManagedElsaInstanceDeleteAcceptedResponse>();
        Assert.Equal(HttpStatusCode.Accepted, firstDelete.StatusCode);
        Assert.NotNull(firstDeleteBody);
        await MarkOperationRecoveryRequiredAsync(app, firstDeleteBody!.OperationId);
        var acceptedEtag = firstDelete.Headers.ETag?.ToString() ?? throw new InvalidOperationException("Delete acceptance ETag is missing.");

        using var originalReplay = await SendDeleteAsync(
            client,
            workspaceId,
            created.Instance.InstanceId,
            created.Instance.ETag,
            "delete-recovery-first",
            firstConfirmation!.ConfirmationId);
        Assert.Equal(HttpStatusCode.Accepted, originalReplay.StatusCode);

        using var reusedConfirmation = await SendDeleteAsync(
            client,
            workspaceId,
            created.Instance.InstanceId,
            acceptedEtag,
            "delete-recovery-reused-confirmation",
            firstConfirmation!.ConfirmationId);
        Assert.Equal(HttpStatusCode.Conflict, reusedConfirmation.StatusCode);

        using var recoveryConfirmationResponse = await client.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations",
            content: null);
        var recoveryConfirmation = await recoveryConfirmationResponse.Content
            .ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, recoveryConfirmationResponse.StatusCode);
        var operationCount = await CountOperationsAsync(app);

        using var recovered = await SendDeleteAsync(
            client,
            workspaceId,
            created.Instance.InstanceId,
            acceptedEtag,
            "delete-recovery-resume",
            recoveryConfirmation!.ConfirmationId);
        Assert.Equal(HttpStatusCode.Accepted, recovered.StatusCode);
        var recoveredBody = await recovered.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteAcceptedResponse>();
        await MarkOperationSucceededAsync(app, recoveredBody!.OperationId);

        using var replay = await SendDeleteAsync(
            client,
            workspaceId,
            created.Instance.InstanceId,
            acceptedEtag,
            "delete-recovery-resume",
            recoveryConfirmation.ConfirmationId);

        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var replayBody = await replay.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteAcceptedResponse>();
        Assert.Equal(firstDeleteBody.OperationId, recoveredBody!.OperationId);
        Assert.Equal(ElsaInstanceOperationState.Queued, recoveredBody.State);
        Assert.Equal(recoveredBody.OperationId, replayBody!.OperationId);
        Assert.Equal(operationCount, await CountOperationsAsync(app));
        Assert.Equal(2, await ReadOperationAttemptAsync(app, recoveredBody.OperationId));
        Assert.True(await IsConfirmationUsedAsync(app, recoveryConfirmation.ConfirmationId));
    }

    [Fact]
    public async Task Dedicated_delete_contract_enforces_permission_confirmation_target_and_current_etag()
    {
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient("managed-instance-narrow-delete-permission-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(owner, workspaceId, "narrow-delete-permission-runtime");

        await app.AddWorkspaceMemberAsync(workspaceId, "managed-instance-narrow-delete-reader", WorkspaceRole.Reader);
        using var readerConfirmation = await app.CreateTrustedWorkspaceClient("managed-instance-narrow-delete-reader")
            .PostAsync($"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        Assert.Equal(HttpStatusCode.Forbidden, readerConfirmation.StatusCode);

        var wrongConfirmation = await CreateConfirmationAsync(
            owner, workspaceId, ConfirmationActionType.DeleteManagedInstance, Guid.NewGuid().ToString("D"));
        using var wrongTarget = await SendDeleteAsync(owner, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, "narrow-delete-wrong-target", wrongConfirmation.Id);
        Assert.Equal(HttpStatusCode.Conflict, wrongTarget.StatusCode);
        Assert.Contains("instance.delete-confirmation-invalid", await wrongTarget.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var confirmationResponse = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);
        using var stale = await SendDeleteAsync(owner, workspaceId, created.Instance.InstanceId,
            $"\"{created.Instance.Version + 1}\"", "narrow-delete-stale-etag", confirmation!.ConfirmationId);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Contains("instance.version-conflict", await stale.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var current = await SendDeleteAsync(owner, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, "narrow-delete-current-etag", confirmation.ConfirmationId);
        Assert.Equal(HttpStatusCode.Accepted, current.StatusCode);
    }

    [Fact]
    public async Task Dedicated_delete_rechecks_workspace_membership_after_confirmation()
    {
        const string subject = "managed-instance-narrow-delete-revoked-owner";
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient(subject);
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(owner, workspaceId, "narrow-delete-revoked-runtime");
        using var confirmationResponse = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var membership = await db.WorkspaceMemberships
                .Include(x => x.Account)
                .ThenInclude(x => x!.ExternalIdentities)
                .SingleAsync(x => x.WorkspaceId == workspaceId &&
                                  x.Account!.ExternalIdentities.Any(identity => identity.Subject == subject));
            db.WorkspaceMemberships.Remove(membership);
            await db.SaveChangesAsync();
        }

        var operationCount = await CountOperationsAsync(app);
        using var deletion = await SendDeleteAsync(owner, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, "narrow-delete-revoked-membership", confirmation!.ConfirmationId);

        Assert.Equal(HttpStatusCode.Forbidden, deletion.StatusCode);
        Assert.Equal(operationCount, await CountOperationsAsync(app));
    }

    [Fact]
    public async Task Canonical_operation_rejects_fields_that_do_not_apply_to_action()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-operation-shape");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var instanceId = Guid.NewGuid();

        var startWithIntent = await SendOperationAsync(client, workspaceId, instanceId, "\"1\"", "start-with-intent",
            new(ElsaInstanceOperationAction.Start, Intent: Intent()));
        var startWithDeleteConfirmation = await SendOperationAsync(client, workspaceId, instanceId, "\"1\"", "start-with-delete-confirmation",
            new(ElsaInstanceOperationAction.Start, DeleteConfirmationId: Guid.NewGuid()));
        var deleteWithName = await SendOperationAsync(client, workspaceId, instanceId, "\"1\"", "delete-with-name",
            new(ElsaInstanceOperationAction.Delete, Name: "ignored", DeleteConfirmationId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, startWithIntent.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, startWithDeleteConfirmation.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, deleteWithName.StatusCode);
        Assert.All([startWithIntent, startWithDeleteConfirmation, deleteWithName], response =>
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType));
    }

    [Fact]
    public async Task Canonical_create_ignores_caller_supplied_instance_id_without_identity_oracle()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-server-identity");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var suppliedId = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/instances")
        {
            Content = JsonContent.Create(new
            {
                name = "Server identity runtime",
                slug = "server-identity-runtime",
                intent = Intent(),
                instanceId = suppliedId
            }, options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", "create-server-identity-runtime");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(body);
        Assert.NotEqual(suppliedId, body!.Instance.InstanceId);
    }

    [Fact]
    public async Task Delete_confirmation_requires_explicit_delete_permission()
    {
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient("managed-instance-delete-permission-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await app.AddWorkspaceMemberAsync(workspaceId, "managed-instance-delete-reader", WorkspaceRole.Reader);

        var response = await app.CreateTrustedWorkspaceClient("managed-instance-delete-reader").PostControlJsonAsync(
            $"/api/workspaces/{workspaceId}/deployments/confirmations",
            new WorkspaceActionConfirmationRequest(
                ConfirmationActionType.DeleteManagedInstance,
                Guid.NewGuid().ToString("D"),
                null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("unsafe/key")]
    [InlineData("\u007funsafe")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Canonical_mutations_reject_unsafe_idempotency_keys_at_api_boundary(string idempotencyKey)
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-unsafe-idempotency");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);

        var create = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/instances")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceCreateRequest("Claims runtime", "claims-runtime", Intent()),
                options: ControlApiTestApplication.JsonOptions)
        };
        create.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        var createResponse = await client.SendAsync(create);

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/workspaces/{workspaceId}/instances/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new ManagedElsaInstancePatchRequest(Name: "Renamed"), options: ControlApiTestApplication.JsonOptions)
        };
        patch.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        patch.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        var patchResponse = await client.SendAsync(patch);

        var operation = new HttpRequestMessage(HttpMethod.Post,
            $"/api/workspaces/{workspaceId}/instances/{Guid.NewGuid()}/operations")
        {
            Content = JsonContent.Create(new ManagedElsaInstanceOperationRequest(ElsaInstanceOperationAction.Start),
                options: ControlApiTestApplication.JsonOptions)
        };
        operation.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        operation.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        var operationResponse = await client.SendAsync(operation);

        Assert.Equal(HttpStatusCode.BadRequest, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, patchResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, operationResponse.StatusCode);
        Assert.Contains("instance.idempotency-key-invalid", await createResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("instance.idempotency-key-invalid", await patchResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("instance.idempotency-key-invalid", await operationResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canonical_create_fails_closed_when_managed_hosting_entitlement_is_missing()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-api-no-entitlement");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/instances")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceCreateRequest("Claims runtime", "claims-runtime", Intent()),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", "create-without-entitlement");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("instance.entitlement-required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canonical_create_rejects_display_names_over_256_characters_as_unprocessable()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-api-long-name");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/instances")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceCreateRequest(new string('n', 257), "claims-runtime", Intent()),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", "create-long-name");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains("instance.shape-invalid", responseBody, StringComparison.Ordinal);
        Assert.Contains("The instance request is invalid.", responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Display name cannot exceed", responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_projection_shows_provisioning_for_an_accepted_create_with_unknown_storage()
    {
        var instanceId = Guid.NewGuid();
        var instance = ElsaInstance.Hydrate(instanceId, Guid.NewGuid(), Guid.NewGuid(), "Claims runtime", "claims-runtime",
            Intent(), ElsaObservedLifecycle.Unknown, ElsaInstanceHealth.Unknown, 4,
            lastOperationId: new ElsaLastOperationId(Guid.NewGuid()));
        var operation = new ElsaInstanceOperationSummary(
            Guid.NewGuid(), instanceId, ElsaInstanceOperationAction.Create, ElsaInstanceOperationState.RecoveryRequired,
            1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, null, null, null, null);

        var response = ManagedElsaInstanceEndpoints.ToResponse(instance, canOpen: true, instance.WorkspaceId, activeOperation: operation);

        Assert.Equal(ElsaObservedLifecycle.Provisioning, response.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Unknown, response.Health);
        Assert.False(response.CanOpen);
        Assert.Equal(ManagedElsaInstanceCustomerProjection.ProvisioningUnavailableReason, response.UnavailableReason);
    }

    [Fact]
    public void Canonical_projection_keeps_stale_ready_health_separate_from_lifecycle()
    {
        var instanceId = Guid.NewGuid();
        var instance = ElsaInstance.Hydrate(instanceId, Guid.NewGuid(), Guid.NewGuid(), "Claims runtime", "claims-runtime",
            Intent(), ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Unknown, 2);

        var response = ManagedElsaInstanceEndpoints.ToResponse(instance, canOpen: true, instance.WorkspaceId);

        Assert.Equal(ElsaObservedLifecycle.Ready, response.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Unknown, response.Health);
        Assert.False(response.CanOpen);
        Assert.Equal(ManagedElsaInstanceCustomerProjection.GenericUnavailableReason, response.UnavailableReason);
    }

    [Fact]
    public void Canonical_projection_explains_genuine_unknown_and_keeps_refresh_recovery()
    {
        var instanceId = Guid.NewGuid();
        var instance = ElsaInstance.Hydrate(instanceId, Guid.NewGuid(), Guid.NewGuid(), "Claims runtime", "claims-runtime",
            Intent(), ElsaObservedLifecycle.Unknown, ElsaInstanceHealth.Unknown, 2);

        var response = ManagedElsaInstanceEndpoints.ToResponse(instance, canOpen: true, instance.WorkspaceId);

        Assert.Equal(ElsaObservedLifecycle.Unknown, response.ObservedLifecycle);
        Assert.Equal(ManagedElsaInstanceCustomerProjection.UnknownUnavailableReason, response.UnavailableReason);
        Assert.Contains("Refresh", response.UnavailableReason, StringComparison.Ordinal);
        Assert.Contains("recover", response.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.CanOpen);
    }

    [Fact]
    public void Canonical_projection_hides_binding_unless_instance_is_running_ready_and_healthy()
    {
        var instanceId = Guid.NewGuid();
        var binding = ElsaInstanceIdentityBinding.Create(instanceId, "https://managed.example.test");
        var instance = ElsaInstance.Hydrate(instanceId, Guid.NewGuid(), Guid.NewGuid(), "Claims runtime", "claims-runtime",
            Intent(), ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Degraded, 2, binding);

        var response = ManagedElsaInstanceEndpoints.ToResponse(instance, canOpen: true, instance.WorkspaceId);

        Assert.False(response.CanOpen);
        Assert.Null(response.Audience);
        Assert.Null(response.RedirectUri);
        Assert.Null(response.IdentityBinding);
        Assert.Equal("instance-unavailable", response.IdentityBindingState);
        Assert.Equal("This instance is not currently available.", response.UnavailableReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Canonical_projection_offers_open_only_for_a_deployment_that_carries_the_managed_handoff(bool managedHandoff)
    {
        var instanceId = Guid.NewGuid();
        const string origin = "https://managed.example.test";
        var instance = ElsaInstance.Hydrate(instanceId, Guid.NewGuid(), Guid.NewGuid(), "Claims runtime", "claims-runtime",
            Intent(), ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy, 2,
            currentDeploymentReference: new ElsaCurrentDeploymentReference("deployment-managed", "attempt-1", origin, managedHandoff));
        var identity = new ManagedElsaInstanceIdentity(instance.OrganizationId, instance.WorkspaceId, instanceId,
            ElsaInstanceIdentityBinding.AudienceFor(instanceId),
            new Uri(ElsaInstanceIdentityBinding.CanonicalizeCallbackUri(origin)), 1, DateTimeOffset.UtcNow);

        var response = ManagedElsaInstanceEndpoints.ToResponse(instance, canOpen: true, instance.WorkspaceId, identity);

        Assert.Equal(managedHandoff, response.CanOpen);
        Assert.Equal(managedHandoff, response.RedirectUri is not null);
        Assert.Equal(managedHandoff, response.IdentityBinding is not null);
        Assert.Equal(managedHandoff ? "available" : "handoff-unavailable", response.IdentityBindingState);
        Assert.Equal(managedHandoff ? null : ManagedElsaInstanceEndpoints.HandoffUnavailableReason, response.UnavailableReason);
    }

    [Fact]
    public void Customer_audit_projection_redacts_operator_subject()
    {
        var audit = new ElsaInstanceAuditEventSummary(Guid.NewGuid(), 1, "instance.updated", Guid.NewGuid(),
            "sha256:sensitive-operator-fingerprint", null, null, null, null, null, null, null, null, null, null,
            DateTimeOffset.UtcNow);

        var response = ManagedElsaInstanceEndpoints.RedactAudit(audit);

        Assert.Null(response.OperatorSubject);
        Assert.Equal(audit.Id, response.Id);
    }

    [Fact]
    public void Lifecycle_worker_poll_interval_is_clamped_to_one_second()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), ElsaInstanceLifecycleHostedService.NormalizePollInterval(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(1), ElsaInstanceLifecycleHostedService.NormalizePollInterval(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(TimeSpan.FromSeconds(3), ElsaInstanceLifecycleHostedService.NormalizePollInterval(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Lifecycle_worker_identity_is_safe_bounded_and_unique_per_hosted_service()
    {
        var first = ElsaInstanceLifecycleHostedService.CreateWorkerId();
        var second = ElsaInstanceLifecycleHostedService.CreateWorkerId();

        Assert.NotEqual(first, second);
        Assert.StartsWith($"api-instance-lifecycle-{Environment.ProcessId}-", first, StringComparison.Ordinal);
        Assert.InRange(first.Length, 1, 256);
        Assert.DoesNotContain(first, char.IsControl);
    }

    [Fact]
    public async Task Instance_from_another_workspace_and_unknown_instance_are_indistinguishable()
    {
        var app = await PrepareApplicationAsync([]);
        var first = app.CreateTrustedWorkspaceClient("managed-instance-owner-a");
        var firstWorkspaceId = await first.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, firstWorkspaceId);
        var create = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{firstWorkspaceId}/instances")
        {
            Content = JsonContent.Create(new ManagedElsaInstanceCreateRequest("Claims runtime", "claims-runtime", Intent()),
                options: ControlApiTestApplication.JsonOptions)
        };
        create.Headers.Add("Idempotency-Key", "create-workspace-a-runtime");
        var accepted = await first.SendAsync(create);
        var body = await accepted.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
        Assert.NotNull(body);

        var second = app.CreateTrustedWorkspaceClient("managed-instance-owner-b");
        var secondWorkspaceId = await second.GetDefaultWorkspaceIdAsync();
        var otherWorkspace = await second.GetAsync($"/api/workspaces/{secondWorkspaceId}/instances/{body!.Instance.InstanceId}");
        var unknown = await second.GetAsync($"/api/workspaces/{secondWorkspaceId}/instances/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, otherWorkspace.StatusCode);
        Assert.Equal(unknown.StatusCode, otherWorkspace.StatusCode);
    }

    [Fact]
    public async Task Operational_health_is_safe_and_does_not_reveal_cross_workspace_existence()
    {
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient("managed-health-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var accepted = await CreateCanonicalInstanceAsync(owner, workspaceId, "managed-health-runtime");
        var instanceId = accepted.Instance.InstanceId;
        Assert.Equal(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/health",
            accepted.Instance.Links["health"]);

        var response = await owner.GetAsync($"/api/workspaces/{workspaceId}/instances/{instanceId}/health");
        var responseJson = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = await response.Content.ReadControlJsonAsync<ManagedElsaInstanceOperationalHealthResponse>();
        Assert.NotNull(health);
        Assert.Equal(ManagedLifecycleOperationalHealthStatus.Unknown, health.Status);
        Assert.NotEqual(default, health.EvaluatedAt);
        Assert.DoesNotContain("managed-health-runtime", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Claims runtime", responseJson, StringComparison.Ordinal);

        var other = app.CreateTrustedWorkspaceClient("managed-health-other-owner");
        var otherWorkspaceId = await other.GetDefaultWorkspaceIdAsync();
        var crossWorkspace = await other.GetAsync(
            $"/api/workspaces/{otherWorkspaceId}/instances/{instanceId}/health");
        var unknown = await other.GetAsync(
            $"/api/workspaces/{otherWorkspaceId}/instances/{Guid.NewGuid()}/health");

        Assert.Equal(HttpStatusCode.NotFound, crossWorkspace.StatusCode);
        Assert.Equal(unknown.StatusCode, crossWorkspace.StatusCode);
    }

    public sealed class Fixture : IAsyncLifetime
    {
        private readonly FakeManagedElsaInstanceCatalog _instanceCatalog = new();
        private readonly CapturingReleaseCatalogStore _releaseCatalog = new();

        internal ControlApiTestApplication Application { get; }

        internal CapturingReleaseCatalogStore ReleaseCatalog => _releaseCatalog;

        public Fixture()
        {
            // Open is offered only while Control's own handoff is enabled (ControlHandoffGatedIdentityStore).
            Application = new ControlApiTestApplication(
                configuration: new Dictionary<string, string?>
                {
                    ["ManagedElsa:Handoff:Enabled"] = "true",
                    ["ManagedElsa:Handoff:Issuer"] = "https://control.test"
                },
                configureServices: services =>
                {
                    services.AddSingleton<IEngineProvisioningModule, TestProvisioningModule>();
                    services.RemoveAll<IManagedElsaInstanceCatalog>();
                    services.AddSingleton<IManagedElsaInstanceCatalog>(_instanceCatalog);
                    services.RemoveAll<IGovernedReleaseCatalogStore>();
                    services.AddSingleton<IGovernedReleaseCatalogStore>(_releaseCatalog);
                });
        }

        internal void Reset(
            IReadOnlyList<ManagedElsaInstanceSummary> instances,
            IReadOnlyList<GovernedReleaseCatalogEntry> releaseEntries)
        {
            _instanceCatalog.SetInstances(instances);
            _releaseCatalog.SetEntries(releaseEntries);
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync() => await ((IAsyncDisposable)Application).DisposeAsync();
    }

    private sealed class TestProvisioningModule : IEngineProvisioningModule
    {
        public string Id => "test";
        public string DisplayName => "Test provider";
    }

    private async Task<ControlApiTestApplication> PrepareApplicationAsync(
        IReadOnlyList<ManagedElsaInstanceSummary> instances,
        IReadOnlyList<GovernedReleaseCatalogEntry>? releaseEntries = null)
    {
        _fixture.Reset(
            instances,
            releaseEntries
            ?? [CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid")]);
        await _fixture.Application.SeedAsync(_ => Task.CompletedTask);
        return _fixture.Application;
    }

    internal sealed class CapturingReleaseCatalogStore : IGovernedReleaseCatalogStore
    {
        private IReadOnlyList<GovernedReleaseCatalogEntry> _entries = [];

        public GovernedReleaseCatalogQuery? Query { get; private set; }
        public List<GovernedReleaseCatalogQuery> Queries { get; } = [];

        public void SetEntries(IReadOnlyList<GovernedReleaseCatalogEntry> entries)
        {
            _entries = entries.ToArray();
            Query = null;
            Queries.Clear();
        }

        public Task<GovernedReleaseCatalogWriteResult> StoreAsync(
            IReadOnlyList<GovernedReleaseCatalogEntry> entries,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<GovernedReleaseCatalogEntry>> QueryAsync(
            GovernedReleaseCatalogQuery query,
            CancellationToken cancellationToken = default)
        {
            Query = query;
            Queries.Add(query);
            return Task.FromResult<IReadOnlyList<GovernedReleaseCatalogEntry>>(_entries
                .Where(entry => query.DistributionId is null || string.Equals(entry.Distribution.Id, query.DistributionId, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.ReleaseLine is null || string.Equals(entry.Distribution.ReleaseLine, query.ReleaseLine, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.ReleaseVersion is null || string.Equals(entry.Distribution.ReleaseVersion, query.ReleaseVersion, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.Channel is null || string.Equals(entry.Distribution.Channel, query.Channel, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.CatalogLifecycle is null || string.Equals(entry.CatalogLifecycle, query.CatalogLifecycle, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.RegistryClass is null || string.Equals(entry.RegistryClass, query.RegistryClass, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.TopologyId is null || string.Equals(entry.Topology.Id, query.TopologyId, StringComparison.OrdinalIgnoreCase))
                .ToArray());
        }
    }

    private static GovernedReleaseCatalogEntry CatalogEntry(
        string distributionId,
        string releaseLine,
        string version,
        string channel,
        string topologyId,
        string catalogLifecycle,
        string registryClass,
        char digestMarker = 'a') => new(
        "1.0",
        $"oci://registry.example.test/releases/manifest@sha256:{new string(digestMarker, 64)}",
        $"sha256:{new string(digestMarker, 64)}",
        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        "https://evidence.example.test/signatures/manifest",
        "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
        registryClass,
        new GovernedReleaseDistribution(
            distributionId, "3", releaseLine, version, channel, "supported", null,
            "https://github.com/valence-works/elsa", "0123456789abcdef", "run-1"),
        new GovernedReleaseTopology(topologyId, "1.0", ["server"], [], [], [], []),
        catalogLifecycle,
        DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

    private static ManagedElsaInstanceSummary Instance(
        Guid instanceId,
        string name,
        string slug,
        ElsaDesiredLifecycle desiredLifecycle = ElsaDesiredLifecycle.Running,
        ElsaObservedLifecycle observedLifecycle = ElsaObservedLifecycle.Ready,
        ElsaInstanceHealth health = ElsaInstanceHealth.Healthy,
        bool bound = true,
        string? audience = null,
        Uri? callbackUri = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            instanceId,
            name,
            slug,
            desiredLifecycle,
            observedLifecycle,
            health,
            bound ? audience ?? "urn:elsa:instance:" + instanceId.ToString("D") : null,
            bound ? callbackUri ?? new Uri("https://managed.example.test/managed-elsa/handoff/callback") : null,
            bound ? 1 : null);

    private static ElsaInstanceIntent Intent() => new(
        new ElsaReleaseIntent("valence-runtime", "3.8", channel: "stable"),
        new ElsaApplicationIntent("combined", "starter",
            new Dictionary<string, ElsaFeatureOverride> { ["replicas"] = ElsaFeatureOverride.FromNumber(3) },
            "approved"),
        new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed"));

    private static ElsaInstanceIntent PreviewIntent(string? manifestDigest = null) => Intent() with
    {
        Release = new ElsaReleaseIntent(
            "preview-runtime", "4.1", "4.1.0-preview.1", "preview",
            previewManifestDigest: manifestDigest)
    };

    private static GovernedReleaseCatalogEntry PreviewCatalogEntry(char digestMarker = 'a') =>
        CatalogEntry("preview-runtime", "4.1", "4.1.0-preview.1", "preview", "combined", "preview", "paid", digestMarker);

    private static string Digest(char marker) => "sha256:" + new string(marker, 64);

    private static async Task EnableManagedHostingAsync(ControlApiTestApplication app, Guid workspaceId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var organizationId = await db.Workspaces.Where(x => x.Id == workspaceId).Select(x => x.OrganizationId).SingleAsync();
        db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = organizationId,
            ManagedHostingEnabled = true,
            MaxSources = 5,
            MaxWorkspaces = 5,
            MaxInstances = int.MaxValue,
            SubscriptionState = OrganizationSubscriptionState.Active
        });
        await db.SaveChangesAsync();
    }

    private static async Task SetSubscriptionStateAsync(
        ControlApiTestApplication app,
        Guid workspaceId,
        OrganizationSubscriptionState state)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var organizationId = await db.Workspaces.Where(x => x.Id == workspaceId).Select(x => x.OrganizationId).SingleAsync();
        var entitlement = await db.OrganizationEntitlementSnapshots.SingleAsync(x => x.OrganizationId == organizationId);
        entitlement.SubscriptionState = state;
        entitlement.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private static Task SetOpenableDeploymentEndpointAsync(
        CatalogDbContext db,
        Guid instanceId,
        string endpointUri,
        bool managedHandoff = true) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstances SET CurrentDeploymentId = {"deployment-managed"}, CurrentDeploymentEndpointUri = {endpointUri}, CurrentDeploymentManagedHandoff = {managedHandoff}, DesiredLifecycle = {ElsaDesiredLifecycle.Running.ToString()}, ObservedLifecycle = {ElsaObservedLifecycle.Ready.ToString()}, Health = {ElsaInstanceHealth.Healthy.ToString()} WHERE Id = {instanceId}");

    private static async Task MarkOperationSucceededAsync(ControlApiTestApplication app, Guid operationId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var completedAtTicks = DateTimeOffset.UtcNow.UtcTicks;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstanceOperations SET State = {ElsaInstanceOperationState.Succeeded.ToString()}, CompletedAt = {completedAtTicks} WHERE Id = {operationId}");
    }

    private static async Task MarkOperationRecoveryRequiredAsync(ControlApiTestApplication app, Guid operationId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ElsaInstanceOperations
            SET State = {ElsaInstanceOperationState.RecoveryRequired.ToString()},
                CompletedAt = NULL,
                FailureCode = {ElsaInstanceProviderReconciliationService.RetrySafeCode},
                ReconciliationRetryEvidenceReference = {"https://provider.example.test/recovery-evidence"},
                ReconciliationRetryEvidenceDigest = {"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
            WHERE Id = {operationId}
            """);
    }

    private static async Task AssertOperationStateAsync(
        ControlApiTestApplication app,
        Guid operationId,
        ElsaInstanceOperationState expectedState)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT CAST(State AS TEXT) FROM ElsaInstanceOperations WHERE Id = @operationId";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@operationId";
        parameter.Value = operationId;
        command.Parameters.Add(parameter);
        var stateText = Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expectedState, Enum.Parse<ElsaInstanceOperationState>(stateText!));
    }

    private static async Task<int> ReadOperationAttemptAsync(ControlApiTestApplication app, Guid operationId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT AttemptNumber FROM ElsaInstanceOperations WHERE Id = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = operationId;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> IsConfirmationUsedAsync(ControlApiTestApplication app, Guid confirmationId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT UsedAt FROM ActionConfirmations WHERE Id = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = confirmationId;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync() is not (null or DBNull);
    }

    private static async Task<int> CountOperationsAsync(ControlApiTestApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ElsaInstanceOperations";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<(int Operations, int Outbox, int Audit)> ReadLifecycleRowCountsAsync(
        ControlApiTestApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return (
            await CountRowsAsync(db, "ElsaInstanceOperations"),
            await CountRowsAsync(db, "ElsaInstanceLifecycleOutbox"),
            await CountRowsAsync(db, "ElsaInstanceAuditEvents"));
    }

    private static async Task<int> CountRowsAsync(CatalogDbContext db, string table)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<ManagedElsaInstanceAcceptedResponse> CreateCanonicalInstanceAsync(
        HttpClient client,
        Guid workspaceId,
        string slug)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/instances")
        {
            Content = JsonContent.Create(new ManagedElsaInstanceCreateRequest("Claims runtime", slug, Intent()),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", $"create-{slug}");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>())!;
    }

    private static Task<HttpResponseMessage> SendCreateRequestAsync(
        HttpClient client,
        Guid workspaceId,
        string name,
        string slug,
        ElsaInstanceIntent intent,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/instances")
        {
            Content = JsonContent.Create(new ManagedElsaInstanceCreateRequest(name, slug, intent),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendOperationAsync(
        HttpClient client,
        Guid workspaceId,
        Guid instanceId,
        string etag,
        string idempotencyKey,
        ManagedElsaInstanceOperationRequest body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/workspaces/{workspaceId}/instances/{instanceId}/operations")
        {
            Content = JsonContent.Create(body, options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendAdminRecoveryAsync(
        HttpClient client,
        string path,
        string? etag,
        string? idempotencyKey,
        AdminManagedElsaRecoveryRequest body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: ControlApiTestApplication.JsonOptions)
        };
        if (etag is not null)
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (idempotencyKey is not null)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
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
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/delete")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceDeleteRequest(confirmationId),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return client.SendAsync(request);
    }

    private static async Task<ControlApiTestApplication> CreateTopologyTestApplicationAsync<TStore>()
        where TStore : class, IManagedElsaInstanceApiStore
    {
        var app = new ControlApiTestApplication(configureServices: services =>
        {
            services.RemoveAll<IManagedElsaInstanceApiStore>();
            services.AddScoped<IManagedElsaInstanceApiStore, TStore>();
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        return app;
    }

    private static async Task<ActionConfirmation> CreateConfirmationAsync(
        HttpClient client,
        Guid workspaceId,
        ConfirmationActionType action,
        string targetId)
    {
        var response = await client.PostControlJsonAsync(
            $"/api/workspaces/{workspaceId}/deployments/confirmations",
            new WorkspaceActionConfirmationRequest(action, targetId, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadControlJsonAsync<ActionConfirmation>())!;
    }

    private sealed class FakeManagedElsaInstanceCatalog : IManagedElsaInstanceCatalog
    {
        private readonly List<ManagedElsaInstanceSummary> _instances = [];

        public void SetInstances(IReadOnlyList<ManagedElsaInstanceSummary> instances)
        {
            _instances.Clear();
            _instances.AddRange(instances);
        }

        public Task<IReadOnlyList<ManagedElsaInstanceSummary>> ListAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManagedElsaInstanceSummary>>(_instances);
    }

    private abstract class TopologyStoreStub : IManagedElsaInstanceApiStore
    {
        public abstract Task<ElsaInstanceLifecycleTopologySnapshot?> GetLifecycleTopologyAsync(
            Guid workspaceId,
            Guid instanceId,
            CancellationToken cancellationToken = default);

        public Task<ElsaInstancePage> ListInstancesAsync(Guid workspaceId, int page, int pageSize, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> SlugExistsAsync(Guid workspaceId, string slug, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ElsaInstanceOperationSummary?> GetOperationAsync(Guid workspaceId, Guid instanceId, Guid operationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, ElsaInstanceOperationSummary>> GetActiveOperationsAsync(
            Guid workspaceId,
            IReadOnlyCollection<Guid> instanceIds,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ElsaInstanceIntentRevisionSummary>> ListRevisionsAsync(Guid workspaceId, Guid instanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ElsaInstanceResolvedPlanSummary?> GetResolvedPlanAsync(Guid workspaceId, Guid instanceId, string planId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ElsaInstanceDeploymentSummary>> ListDeploymentsAsync(Guid workspaceId, Guid instanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ElsaInstanceAuditEventSummary>> ListAuditAsync(Guid workspaceId, Guid instanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TopologyChangedStore : TopologyStoreStub
    {
        public override Task<ElsaInstanceLifecycleTopologySnapshot?> GetLifecycleTopologyAsync(
            Guid workspaceId,
            Guid instanceId,
            CancellationToken cancellationToken = default) =>
            throw new ElsaInstanceLifecycleTopologyChangedException();
    }

    private sealed class MissingOutboxTopologyStore : TopologyStoreStub
    {
        public override Task<ElsaInstanceLifecycleTopologySnapshot?> GetLifecycleTopologyAsync(
            Guid workspaceId,
            Guid instanceId,
            CancellationToken cancellationToken = default)
        {
            var operationId = Guid.NewGuid();
            return Task.FromResult<ElsaInstanceLifecycleTopologySnapshot?>(new(
                instanceId,
                3,
                ElsaDesiredLifecycle.Deleting,
                ElsaObservedLifecycle.Deleting,
                operationId,
                [new(
                    operationId,
                    ElsaInstanceOperationAction.Delete,
                    ElsaInstanceOperationState.WaitingForPriorOperation,
                    2,
                    1,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null)]));
        }
    }
}
