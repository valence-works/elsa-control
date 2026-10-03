using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using ElsaControl.Api.Admin.Workspaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Provisioning;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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
    public async Task Managed_and_hosted_onboarding_still_resolves_valence_runtime_from_the_signed_catalog()
    {
        var app = await PrepareApplicationAsync([], [
            CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid"),
            CatalogEntry("valence-runtime", "3.8", "3.8.0-preview.1", "preview", "combined", "preview", "paid", digestMarker: 'f')
        ]);
        var client = app.CreateControlIdentityClient(subject: "managed-hosted-catalog-owner");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);

        var response = await client.GetAsync($"/api/workspaces/{workspaceId}/instances/onboarding-options");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = await response.Content.ReadFromJsonAsync<ManagedElsaInstanceOnboardingOptionsResponse>(
            ControlApiTestApplication.JsonOptions);
        Assert.NotNull(options);
        var release = Assert.Single(options.Releases);
        Assert.Equal("valence-runtime", release.DistributionId);
        Assert.Equal("3.8", release.ReleaseLine);
        Assert.Equal("3.8.4", release.Version);
        Assert.Equal("stable", release.Channel);
        Assert.Equal("combined", release.TopologyId);
        var preview = Assert.Single(options.PreviewReleases!);
        Assert.Equal("valence-runtime", preview.DistributionId);
        Assert.Equal("3.8.0-preview.1", preview.Version);
        Assert.Equal("sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", preview.ManifestDigest);
        Assert.Contains(_fixture.ReleaseCatalog.Queries, query => query.CatalogLifecycle == "supported" && query.RegistryClass == "paid");
        Assert.Contains(_fixture.ReleaseCatalog.Queries, query => query.CatalogLifecycle == "preview" && query.RegistryClass == "paid");
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
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(ElsaInstanceCommercialOperation.LifecycleConstrained, body, StringComparison.Ordinal);
        Assert.Contains(ManagedElsaProvisioningProgressCopy.EntitlementHeldCreate, body, StringComparison.Ordinal);
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
        var createBody = await createResponse.Content.ReadAsStringAsync();
        Assert.Contains(ElsaInstanceCommercialOperation.LifecycleConstrained, createBody, StringComparison.Ordinal);
        Assert.Contains(ManagedElsaProvisioningProgressCopy.EntitlementHeldCreate, createBody, StringComparison.Ordinal);

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
        var patchBody = await patchResponse.Content.ReadAsStringAsync();
        Assert.Contains(ElsaInstanceCommercialOperation.LifecycleConstrained, patchBody, StringComparison.Ordinal);
        Assert.Contains(ManagedElsaProvisioningProgressCopy.EntitlementHeldChange, patchBody, StringComparison.Ordinal);
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
    public async Task Operations_list_is_paged_newest_first_and_matches_the_advertised_link()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-operations-list");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(client, workspaceId, "operations-list-runtime");
        var instanceId = created.Instance.InstanceId;
        Assert.Equal(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/operations",
            created.Instance.Links["operations"]);
        await MarkOperationSucceededAsync(app, created.Operation.Id);
        var middle = await SendOperationAsync(
            client, workspaceId, instanceId, created.Instance.ETag, "operations-list-middle",
            new(ElsaInstanceOperationAction.Reconcile));
        Assert.Equal(HttpStatusCode.Accepted, middle.StatusCode);
        var middleBody = await middle.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
        await MarkOperationSucceededAsync(app, middleBody!.Operation.Id);
        var newest = await SendOperationAsync(
            client, workspaceId, instanceId, middleBody.Instance.ETag, "operations-list-newest",
            new(ElsaInstanceOperationAction.Reconcile));
        Assert.Equal(HttpStatusCode.Accepted, newest.StatusCode);
        var newestBody = await newest.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();

        var firstPage = await client.GetControlJsonAsync<ManagedElsaInstanceOperationListResponse>(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/operations?page=1&pageSize=1");
        var secondPage = await client.GetControlJsonAsync<ManagedElsaInstanceOperationListResponse>(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/operations?page=2&pageSize=1");
        var oversized = await client.GetControlJsonAsync<ManagedElsaInstanceOperationListResponse>(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/operations?page=1&pageSize=1000");

        Assert.NotNull(firstPage);
        Assert.Equal(1, firstPage.Page);
        Assert.Equal(1, firstPage.PageSize);
        Assert.Equal(3, firstPage.TotalCount);
        Assert.True(firstPage.HasMore);
        var newestItem = Assert.Single(firstPage.Items);
        Assert.Equal(newestBody!.Operation.Id, newestItem.Id);
        Assert.Equal(ElsaInstanceOperationAction.Reconcile, newestItem.Action);
        Assert.Equal(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/operations/{newestBody.Operation.Id:D}",
            newestItem.Links["self"]);
        Assert.Equal(middleBody.Operation.Id, Assert.Single(secondPage!.Items).Id);
        Assert.Equal(3, oversized!.Items.Count);
        Assert.Equal(100, oversized.PageSize);
        Assert.Equal(newestBody.Operation.Id, oversized.Items[0].Id);
        Assert.Equal(middleBody.Operation.Id, oversized.Items[1].Id);
        Assert.Equal(created.Operation.Id, oversized.Items[2].Id);
        Assert.False(oversized.HasMore);
    }

    [Fact]
    public async Task Operations_list_requires_workspace_read_access_and_conceals_cross_workspace_instances()
    {
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient("managed-operations-list-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(owner, workspaceId, "operations-list-scope-runtime");
        var instanceId = created.Instance.InstanceId;
        var path = $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/operations";

        using var anonymous = app.CreateClient();
        using var unauthenticated = await anonymous.GetAsync(path);
        using var unknown = await owner.GetAsync($"/api/workspaces/{workspaceId:D}/instances/{Guid.NewGuid():D}/operations");
        var other = app.CreateTrustedWorkspaceClient("managed-operations-list-other");
        var otherWorkspaceId = await other.GetDefaultWorkspaceIdAsync();
        using var crossWorkspace = await other.GetAsync(
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{instanceId:D}/operations");
        using var unknownOther = await other.GetAsync(
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{Guid.NewGuid():D}/operations");

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossWorkspace.StatusCode);
        Assert.Equal(unknownOther.StatusCode, crossWorkspace.StatusCode);
    }

    [Fact]
    public async Task Operations_list_authorization_matches_instance_detail()
    {
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient("managed-operations-auth-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(owner, workspaceId, "operations-auth-runtime");
        var instanceId = created.Instance.InstanceId;
        var operationsPath = $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/operations";
        var detailPath = $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}";

        await app.AddWorkspaceMemberAsync(workspaceId, "managed-operations-auth-reader", WorkspaceRole.Reader);
        using var reader = app.CreateTrustedWorkspaceClient("managed-operations-auth-reader");
        using var readerOperations = await reader.GetAsync(operationsPath);
        using var readerDetail = await reader.GetAsync(detailPath);

        using var outsider = app.CreateControlIdentityClient(subject: "managed-operations-auth-outsider");
        using var outsiderOperations = await outsider.GetAsync(operationsPath);
        using var outsiderDetail = await outsider.GetAsync(detailPath);

        using var bff = app.CreateControlIdentityClient(
            subject: "managed-operations-auth-bff",
            claims: new Dictionary<string, string>
            {
                ["azp"] = "elsa-cloud-lovable-bff",
                ["scp"] = CloudBffDefaults.DefaultScope
            });
        using var bffOperations = await bff.GetAsync(operationsPath);
        using var bffDetail = await bff.GetAsync(detailPath);

        var other = app.CreateTrustedWorkspaceClient("managed-operations-auth-other-organization");
        var otherWorkspaceId = await other.GetDefaultWorkspaceIdAsync();
        Guid ownerOrganizationId;
        Guid otherOrganizationId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            ownerOrganizationId = await db.Workspaces.Where(x => x.Id == workspaceId)
                .Select(x => x.OrganizationId).SingleAsync();
            otherOrganizationId = await db.Workspaces.Where(x => x.Id == otherWorkspaceId)
                .Select(x => x.OrganizationId).SingleAsync();
        }
        using var crossOrgOnOwnerPath = await other.GetAsync(operationsPath);
        using var crossOrgOnOwnPath = await other.GetAsync(
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{instanceId:D}/operations");
        using var crossOrgDetailOnOwnPath = await other.GetAsync(
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{instanceId:D}");

        Assert.Equal(HttpStatusCode.OK, readerOperations.StatusCode);
        Assert.Equal(readerDetail.StatusCode, readerOperations.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, outsiderOperations.StatusCode);
        Assert.Equal(outsiderDetail.StatusCode, outsiderOperations.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, bffOperations.StatusCode);
        Assert.Equal(bffDetail.StatusCode, bffOperations.StatusCode);
        Assert.Contains("cloud-bff.denied", await bffOperations.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, ownerOrganizationId);
        Assert.NotEqual(Guid.Empty, otherOrganizationId);
        Assert.NotEqual(ownerOrganizationId, otherOrganizationId);
        Assert.Equal(HttpStatusCode.Forbidden, crossOrgOnOwnerPath.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossOrgOnOwnPath.StatusCode);
        Assert.Equal(crossOrgDetailOnOwnPath.StatusCode, crossOrgOnOwnPath.StatusCode);
    }

    [Fact]
    public async Task Instance_response_includes_utc_created_and_updated_timestamps()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-timestamps");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(client, workspaceId, "timestamp-runtime");

        var detail = await client.GetControlJsonAsync<ManagedElsaInstanceResponse>(
            $"/api/workspaces/{workspaceId}/instances/{created.Instance.InstanceId}");
        var list = await client.GetControlJsonAsync<ManagedElsaInstanceListResponse>(
            $"/api/workspaces/{workspaceId}/instances");
        var listed = Assert.Single(list!.Items);

        Assert.NotEqual(default, created.Instance.CreatedAt);
        Assert.NotEqual(default, created.Instance.UpdatedAt);
        Assert.Equal(TimeSpan.Zero, created.Instance.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, created.Instance.UpdatedAt.Offset);
        Assert.Equal(created.Instance.CreatedAt, detail!.CreatedAt);
        Assert.Equal(created.Instance.UpdatedAt, detail.UpdatedAt);
        Assert.Equal(created.Instance.CreatedAt, listed.CreatedAt);
        Assert.Equal(created.Instance.UpdatedAt, listed.UpdatedAt);
    }

    [Fact]
    public async Task Legacy_list_includes_utc_created_and_updated_timestamps()
    {
        var createdAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var updatedAt = new DateTimeOffset(2026, 3, 5, 8, 9, 10, TimeSpan.Zero);
        var app = await PrepareApplicationAsync([
            Instance(Guid.NewGuid(), "Claims runtime", "claims-runtime") with
            {
                CreatedAt = createdAt,
                UpdatedAt = updatedAt
            }
        ]);
        var client = app.CreateTrustedWorkspaceClient("managed-legacy-timestamps");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();

        var response = await client.GetAsync($"/api/workspaces/{workspaceId}/managed-elsa/instances");
        var instances = await response.Content.ReadFromJsonAsync<List<ManagedElsaInstanceResponse>>(
            ControlApiTestApplication.JsonOptions);
        var item = Assert.Single(instances!);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(createdAt, item.CreatedAt);
        Assert.Equal(updatedAt, item.UpdatedAt);
        Assert.Equal(TimeSpan.Zero, item.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, item.UpdatedAt.Offset);
        Assert.NotEqual(default, item.CreatedAt);
        Assert.NotEqual(default, item.UpdatedAt);
    }

    [Fact]
    public async Task Audit_list_is_bounded_newest_first_and_rejects_invalid_limits()
    {
        var app = await PrepareApplicationAsync([]);
        var client = app.CreateTrustedWorkspaceClient("managed-instance-audit-limit");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(client, workspaceId, "audit-limit-runtime");
        var instanceId = created.Instance.InstanceId;
        await MarkOperationSucceededAsync(app, created.Operation.Id);
        var etag = created.Instance.ETag;
        for (var index = 0; index < 4; index++)
        {
            var accepted = await SendOperationAsync(
                client, workspaceId, instanceId, etag, $"audit-limit-{index}",
                new(ElsaInstanceOperationAction.Reconcile));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var body = await accepted.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
            etag = body!.Instance.ETag;
            await MarkOperationSucceededAsync(app, body.Operation.Id);
        }

        var defaultPage = await client.GetControlJsonAsync<ManagedElsaInstanceAuditResponse>(
            $"/api/workspaces/{workspaceId}/instances/{instanceId}/audit");
        var limited = await client.GetControlJsonAsync<ManagedElsaInstanceAuditResponse>(
            $"/api/workspaces/{workspaceId}/instances/{instanceId}/audit?limit=2");
        using var maximum = await client.GetAsync(
            $"/api/workspaces/{workspaceId}/instances/{instanceId}/audit?limit=500");
        using var zero = await client.GetAsync(
            $"/api/workspaces/{workspaceId}/instances/{instanceId}/audit?limit=0");
        using var negative = await client.GetAsync(
            $"/api/workspaces/{workspaceId}/instances/{instanceId}/audit?limit=-1");
        using var tooLarge = await client.GetAsync(
            $"/api/workspaces/{workspaceId}/instances/{instanceId}/audit?limit=501");

        Assert.True(defaultPage!.Items.Count >= 5);
        Assert.Equal(2, limited!.Items.Count);
        Assert.True(limited.Items[0].Sequence > limited.Items[1].Sequence);
        Assert.Equal(defaultPage.Items[0].Id, limited.Items[0].Id);
        Assert.Equal(defaultPage.Items[1].Id, limited.Items[1].Id);
        Assert.Equal(HttpStatusCode.OK, maximum.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Contains("instance.audit-limit-invalid", await zero.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_list_requires_workspace_read_access_and_conceals_cross_workspace_instances()
    {
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient("managed-audit-limit-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(owner, workspaceId, "audit-limit-scope-runtime");
        var instanceId = created.Instance.InstanceId;
        var path = $"/api/workspaces/{workspaceId}/instances/{instanceId}/audit?limit=10";

        using var anonymous = app.CreateClient();
        using var unauthenticated = await anonymous.GetAsync(path);
        using var unknown = await owner.GetAsync($"/api/workspaces/{workspaceId}/instances/{Guid.NewGuid()}/audit?limit=10");
        var other = app.CreateTrustedWorkspaceClient("managed-audit-limit-other");
        var otherWorkspaceId = await other.GetDefaultWorkspaceIdAsync();
        using var crossWorkspace = await other.GetAsync(
            $"/api/workspaces/{otherWorkspaceId}/instances/{instanceId}/audit?limit=10");
        using var unknownOther = await other.GetAsync(
            $"/api/workspaces/{otherWorkspaceId}/instances/{Guid.NewGuid()}/audit?limit=10");

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossWorkspace.StatusCode);
        Assert.Equal(unknownOther.StatusCode, crossWorkspace.StatusCode);
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
    public async Task Admin_provider_readout_requires_admin_and_a_concrete_provider()
    {
        var app = await PrepareApplicationAsync([]);
        var customer = app.CreateTrustedWorkspaceClient("provider-readout-owner");
        var workspaceId = await customer.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(customer, workspaceId, "provider-readout-runtime");
        var path = $"/api/admin/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/operations/provider-current";

        using var anonymous = app.CreateClient();
        using var unauthenticated = await anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var customerIdentity = app.CreateControlIdentityClient();
        using var customerResponse = await customerIdentity.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, customerResponse.StatusCode);

        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        using var noProvider = await admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, noProvider.StatusCode);
    }

    [Fact]
    public async Task Admin_provider_readout_follows_the_correlated_delete_assignment()
    {
        var app = await PrepareApplicationAsync([]);
        var topology = await SeedCorrelationInvalidDeleteTopologyAsync(
            app, "admin-provider-delete-readout", retainWorkload: true, assignmentDeleted: false);
        Guid providerOperationId;
        string resourceGroupName;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var store = new AzureProviderOperationStore(db);
            var assignmentId = Guid.Parse(await db.Database.SqlQuery<string>($"""
                SELECT PlacementAssignmentId AS Value FROM ElsaInstances
                WHERE WorkspaceId = {topology.WorkspaceId} AND Id = {topology.InstanceId}
                """).SingleAsync());
            var assignment = (await ((IAzureProviderResourceAssignmentStore)store)
                .GetAsync(topology.WorkspaceId, assignmentId))!;
            resourceGroupName = assignment.ResourceGroupName;
            var operation = await store.CreateOrGetAsync(new AzureProviderOperationRequest(
                topology.WorkspaceId,
                AzureElsaInstanceProvider.WorkloadName(topology.InstanceId),
                AzureProviderOperationAction.Delete,
                AzureProviderOperationValidation.LifecycleIdempotencyKey(topology.OperationId) + ":delete",
                new string('a', 64), new string('b', 64), "3.8.0", "3.8", "combined", "Dedicated",
                "westeurope", "valenceruntimeimages.azurecr.io/runtime-combined",
                "sha256:" + new string('c', 64),
                ProviderScopeFingerprint: new string('a', 64),
                OrganizationId: assignment.OrganizationId,
                InstanceId: topology.InstanceId,
                LifecycleAction: ElsaInstanceOperationAction.Delete,
                ProviderAssignmentId: assignment.Id), DateTimeOffset.UtcNow);
            providerOperationId = operation.Id;
        }

        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        var path = $"/api/admin/workspaces/{topology.WorkspaceId:D}/instances/{topology.InstanceId:D}/operations/provider-current";
        // An uncheckpointed reservation has no assignment authority yet.
        using var unbound = await admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, unbound.StatusCode);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var store = new AzureProviderOperationStore(scope.ServiceProvider.GetRequiredService<CatalogDbContext>());
            var now = DateTimeOffset.UtcNow;
            const string leaseToken = "admin-readout-delete-lease";
            var claimed = (await store.ClaimAsync(topology.WorkspaceId, providerOperationId,
                "admin-readout-worker", leaseToken, TimeSpan.FromMinutes(1), now))!;
            var checkpointed = (await store.CheckpointAsync(topology.WorkspaceId, providerOperationId,
                leaseToken,
                new AzureProviderCheckpoint(
                    AzureProviderOperationPhase.CleanupSubmitted,
                    "cleanup.submitted",
                    "Cleanup submitted.",
                    new AzureProviderResourceReferences(ResourceGroupName: resourceGroupName),
                    null,
                    AzureProviderHealth.Unknown,
                    [],
                    AttemptedStep: AzureProviderRunnerStep.Cleanup),
                now, claimed.Version))!;
            Assert.NotNull(await store.FinalizeAsync(topology.WorkspaceId, providerOperationId,
                leaseToken, AzureProviderOperationStatus.RecoveryRequired, "cleanup.timeout",
                now, checkpointed.Version));
        }

        using var response = await admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadControlJsonAsync<AdminManagedElsaProviderOperationResponse>())!;
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, body.Status);
        Assert.Equal(AzureProviderOperationPhase.CleanupSubmitted, body.Phase);
        Assert.Equal(AzureProviderRunnerStep.Cleanup, body.AttemptedStep);
        Assert.Equal("cleanup.timeout", body.LastTransitionCode);
        Assert.True(body.AssignmentScopeCurrent);
        Assert.True(body.OperationScopeCurrent);
        Assert.True(body.AssignmentPlacementMatchesCurrent);
        Assert.True(body.AssignmentGroupOnly);
        Assert.Equal(AzureProviderHealth.Unknown, body.Health);
        Assert.False(body.EndpointValid);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(providerOperationId.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(topology.InstanceId.ToString("D"), json, StringComparison.OrdinalIgnoreCase);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE AzureProviderOperations
                SET IdempotencyKey = {AzureProviderOperationValidation.LifecycleIdempotencyKey(Guid.NewGuid()) + ":delete"}
                WHERE Id = {providerOperationId}
                """);
        }
        using var wrongLifecycle = await admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, wrongLifecycle.StatusCode);
        var unavailableJson = await wrongLifecycle.Content.ReadAsStringAsync();
        using var unavailable = System.Text.Json.JsonDocument.Parse(unavailableJson);
        Assert.Equal("provider-readout.delete-correlation-mismatch", unavailable.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(providerOperationId.ToString("D"), unavailableJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(topology.InstanceId.ToString("D"), unavailableJson, StringComparison.OrdinalIgnoreCase);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE AzureProviderOperations
                SET IdempotencyKey = {AzureProviderOperationValidation.LifecycleIdempotencyKey(topology.OperationId) + ":delete"}
                WHERE Id = {providerOperationId}
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE AzureProviderResourceAssignments
                SET ProviderScopeFingerprint = {new string('d', 64)}
                WHERE WorkspaceId = {topology.WorkspaceId} AND InstanceId = {topology.InstanceId}
                """);
        }
        using var mismatchedOperationScope = await admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, mismatchedOperationScope.StatusCode);
        var mismatchJson = await mismatchedOperationScope.Content.ReadAsStringAsync();
        using var scopeProblem = System.Text.Json.JsonDocument.Parse(mismatchJson);
        Assert.Equal("provider-readout.operation-correlation-mismatch", scopeProblem.RootElement.GetProperty("code").GetString());

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE AzureProviderOperations SET ProviderScopeFingerprint = {new string('d', 64)}
                WHERE Id = {providerOperationId}
                """);
        }
        using var retainedScope = await admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, retainedScope.StatusCode);
        var retainedBody = (await retainedScope.Content.ReadControlJsonAsync<AdminManagedElsaProviderOperationResponse>())!;
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, retainedBody.Status);
        Assert.False(retainedBody.AssignmentScopeCurrent);
        Assert.False(retainedBody.OperationScopeCurrent);
        Assert.True(retainedBody.AssignmentPlacementMatchesCurrent);
        Assert.True(retainedBody.AssignmentGroupOnly);
        var retainedJson = await retainedScope.Content.ReadAsStringAsync();
        Assert.DoesNotContain(providerOperationId.ToString("D"), retainedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(topology.InstanceId.ToString("D"), retainedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(resourceGroupName, retainedJson, StringComparison.OrdinalIgnoreCase);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE AzureProviderResourceAssignments SET ResourceGroupName = {"unrelated"}
                WHERE WorkspaceId = {topology.WorkspaceId} AND InstanceId = {topology.InstanceId}
                """);
        }
        using var movedPlacement = await admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, movedPlacement.StatusCode);
        var movedBody = (await movedPlacement.Content.ReadControlJsonAsync<AdminManagedElsaProviderOperationResponse>())!;
        Assert.False(movedBody.AssignmentPlacementMatchesCurrent);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var assignmentId = Guid.Parse(await db.Database.SqlQuery<string>($"""
                SELECT PlacementAssignmentId AS Value FROM ElsaInstances
                WHERE WorkspaceId = {topology.WorkspaceId} AND Id = {topology.InstanceId}
                """).SingleAsync());
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE AzureProviderResourceAssignments
                SET ProviderScopeFingerprint = {new string('a', 64)}, ResourceGroupName = {resourceGroupName}
                WHERE Id = {assignmentId}
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO AzureProviderAssignmentRebinds
                    (Id, AssignmentId, WorkspaceId, InstanceId, FromProviderScopeFingerprint,
                     ToProviderScopeFingerprint, TriggeredBy, TriggerOperationId, OccurredAt)
                VALUES ({Guid.NewGuid()}, {assignmentId}, {topology.WorkspaceId}, {topology.InstanceId},
                        {new string('d', 64)}, {new string('a', 64)}, {"test-rebind"},
                        {topology.OperationId}, {DateTimeOffset.UtcNow})
                """);
        }
        using var reboundScope = await admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, reboundScope.StatusCode);
        var reboundBody = (await reboundScope.Content.ReadControlJsonAsync<AdminManagedElsaProviderOperationResponse>())!;
        Assert.True(reboundBody.AssignmentScopeCurrent);
        Assert.False(reboundBody.OperationScopeCurrent);
        Assert.True(reboundBody.AssignmentPlacementMatchesCurrent);
        Assert.True(reboundBody.AssignmentGroupOnly);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE AzureProviderResourceAssignments SET State = {AzureProviderAssignmentState.Deleted}
                WHERE WorkspaceId = {topology.WorkspaceId} AND InstanceId = {topology.InstanceId}
                """);
        }
        using var deletedAssignment = await admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, deletedAssignment.StatusCode);
        var deletedBody = (await deletedAssignment.Content.ReadControlJsonAsync<AdminManagedElsaProviderOperationResponse>())!;
        Assert.Equal(AzureProviderAssignmentState.Deleted, deletedBody.AssignmentState);
        Assert.True(deletedBody.AssignmentPlacementMatchesCurrent);
        Assert.True(deletedBody.AssignmentGroupOnly);
    }

    [Fact]
    public void Admin_provider_readout_contract_contains_only_value_free_status()
    {
        var status = new AdminManagedElsaProviderOperationResponse(
            AzureProviderOperationStatus.RecoveryRequired,
            AzureProviderOperationPhase.FoundationSubmitted,
            AzureProviderRunnerStep.Foundation,
            1,
            0,
            DateTimeOffset.UtcNow,
            "azure.command.timeout",
            ["azure.command.timeout"],
            Health: AzureProviderHealth.Healthy,
            EndpointValid: true);
        var json = System.Text.Json.JsonSerializer.Serialize(status, ControlApiTestApplication.JsonOptions);

        Assert.Contains("azure.command.timeout", json, StringComparison.Ordinal);
        Assert.Contains("\"health\":\"Healthy\"", json, StringComparison.Ordinal);
        Assert.Contains("\"endpointValid\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("resource", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("endpointUri", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://managed.example.test/", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("identity", json, StringComparison.OrdinalIgnoreCase);
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
    public async Task Customer_list_and_detail_keep_delete_outcome_when_an_older_create_is_still_active()
    {
        var app = await PrepareApplicationAsync([]);
        var customer = app.CreateTrustedWorkspaceClient("delete-current-operation-owner");
        var workspaceId = await customer.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(customer, workspaceId, "delete-current-operation-runtime");
        await MarkOperationRecoveryRequiredAsync(app, created.Operation.Id);

        using var confirmationResponse = await customer.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);

        using var deletion = await SendDeleteAsync(customer, workspaceId, created.Instance.InstanceId,
            created.Instance.ETag, "delete-current-operation", confirmation!.ConfirmationId);
        Assert.Equal(HttpStatusCode.Accepted, deletion.StatusCode);
        var accepted = await deletion.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteAcceptedResponse>();
        Assert.NotNull(accepted);

        async Task AssertCustomerOperationAsync(ElsaInstanceOperationState expected)
        {
            using var list = await customer.GetAsync($"/api/workspaces/{workspaceId:D}/instances");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var listBody = await list.Content.ReadControlJsonAsync<ManagedElsaInstanceListResponse>();
            var listed = Assert.Single(listBody!.Items);
            Assert.Equal(ElsaDesiredLifecycle.Deleting, listed.DesiredLifecycle);
            Assert.Equal(accepted!.OperationId, listed.ActiveOperation?.Id);
            Assert.Equal(ElsaInstanceOperationAction.Delete, listed.ActiveOperation?.Action);
            Assert.Equal(expected, listed.ActiveOperation?.State);
            Assert.False(listed.CanOpen);

            using var detail = await customer.GetAsync(
                $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var detailBody = await detail.Content.ReadControlJsonAsync<ManagedElsaInstanceResponse>();
            Assert.Equal(listed.ActiveOperation, detailBody!.ActiveOperation);
        }

        await AssertCustomerOperationAsync(ElsaInstanceOperationState.WaitingForPriorOperation);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE ElsaInstanceOperations SET State = {ElsaInstanceOperationState.Failed.ToString()}, FailureCode = {"provider.private-secret-value"} WHERE Id = {accepted!.OperationId}");
        }

        await AssertCustomerOperationAsync(ElsaInstanceOperationState.Failed);
        using var sanitized = await customer.GetAsync($"/api/workspaces/{workspaceId:D}/instances");
        Assert.DoesNotContain("provider.private-secret-value", await sanitized.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var outsider = app.CreateTrustedWorkspaceClient("delete-current-operation-outsider");
        var otherWorkspaceId = await outsider.GetDefaultWorkspaceIdAsync();
        using var otherList = await outsider.GetAsync($"/api/workspaces/{otherWorkspaceId:D}/instances");
        var otherBody = await otherList.Content.ReadControlJsonAsync<ManagedElsaInstanceListResponse>();
        Assert.Equal(HttpStatusCode.OK, otherList.StatusCode);
        Assert.DoesNotContain(otherBody!.Items, item => item.InstanceId == created.Instance.InstanceId);
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
    public async Task Admin_recover_fails_closed_when_correlation_invalid_delete_still_has_workload_inventory()
    {
        var app = await PrepareApplicationAsync([]);
        var topology = await SeedCorrelationInvalidDeleteTopologyAsync(
            app, "admin-correlation-invalid-retained", retainWorkload: true, assignmentDeleted: false);

        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        var path = $"/api/admin/workspaces/{topology.WorkspaceId:D}/instances/{topology.InstanceId:D}/operations/{topology.OperationId:D}/recover";
        using var response = await SendAdminRecoveryAsync(
            admin, path, $"\"{topology.InstanceVersion}\"", "admin-correlation-invalid-retained",
            new AdminManagedElsaRecoveryRequest("operator recover"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("instance.recovery-authority-unavailable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("instance.invalid-state", body, StringComparison.Ordinal);
        await AssertOperationStateAsync(app, topology.OperationId, ElsaInstanceOperationState.RecoveryRequired);
        Assert.Equal(1, await ReadOperationAttemptAsync(app, topology.OperationId));
    }

    [Fact]
    public async Task Admin_recover_accepts_correlation_invalid_delete_when_assignment_is_confirmed_absent()
    {
        var app = await PrepareApplicationAsync([]);
        var topology = await SeedCorrelationInvalidDeleteTopologyAsync(
            app, "admin-correlation-invalid-absent", retainWorkload: false, assignmentDeleted: true);

        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        var path = $"/api/admin/workspaces/{topology.WorkspaceId:D}/instances/{topology.InstanceId:D}/operations/{topology.OperationId:D}/recover";
        var request = new AdminManagedElsaRecoveryRequest("operator recover");
        using var first = await SendAdminRecoveryAsync(
            admin, path, $"\"{topology.InstanceVersion}\"", "admin-correlation-invalid-absent", request);
        var firstText = await first.Content.ReadAsStringAsync();
        Assert.True(first.StatusCode == HttpStatusCode.Accepted, firstText);
        var firstBody = (await first.Content.ReadControlJsonAsync<AdminManagedElsaRecoveryResponse>())!;
        Assert.Equal(topology.OperationId, firstBody.OperationId);
        Assert.Equal(ElsaInstanceOperationState.Queued, firstBody.State);
        Assert.Equal(2, firstBody.AttemptNumber);
        Assert.False(firstBody.Replayed);

        var rowCounts = await ReadLifecycleRowCountsAsync(app);
        using var replay = await SendAdminRecoveryAsync(
            admin, path, $"\"{topology.InstanceVersion}\"", "admin-correlation-invalid-absent", request);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var replayBody = (await replay.Content.ReadControlJsonAsync<AdminManagedElsaRecoveryResponse>())!;
        Assert.True(replayBody.Replayed);
        Assert.Equal(firstBody.AttemptNumber, replayBody.AttemptNumber);
        Assert.Equal(rowCounts, await ReadLifecycleRowCountsAsync(app));
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
        Assert.Null(recoveryProjection.ReasonCode);
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
    public async Task Instance_mutating_routes_are_classified_and_record_delete_rebase_causes()
    {
        var app = await PrepareApplicationAsync([], [
            CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid"),
            CatalogEntry("valence-runtime", "3.8", "3.8.5", "stable", "combined", "supported", "paid", 'b'),
            CatalogEntry("valence-runtime", "3.9", "3.9.0", "stable", "combined", "supported", "paid", 'c')
        ]);
        using var warmup = await app.CreateClient().GetAsync("/health");
        warmup.EnsureSuccessStatusCode();

        var discovered = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint =>
            {
                var route = endpoint.RoutePattern.RawText;
                return route is not null &&
                       (route.Contains("/instances", StringComparison.Ordinal) ||
                        route.Contains("/operations", StringComparison.Ordinal));
            })
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .Where(method => !string.Equals(method, HttpMethods.Get, StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(method, HttpMethods.Head, StringComparison.OrdinalIgnoreCase))
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}") ?? [])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        // Customer and operator routes must record a mutation that blocks a
        // stale Delete rebase. The staging recovery lever is intentionally
        // separate: it records a system-only Reconcile operation and must not
        // be treated as a customer/operator mutation by the rebase guard.
        var customerOrOperatorRoutes = new[]
        {
            "PATCH /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}",
            "POST /api/admin/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/operations/{operationId:guid}/recover",
            "POST /api/workspaces/{workspaceId:guid}/instances/",
            "POST /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/apply-release",
            "POST /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/delete",
            "POST /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/delete-confirmations",
            "POST /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/operations",
            "POST /api/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/restart"
        };
        var systemOnlyRoutes = new[]
        {
            "POST /api/staging/lifecycle-lever/instances/{instanceId:guid}/recovery-required",
            "POST /api/staging/lifecycle-lever/instances/{instanceId:guid}/reset"
        };
        var classified = customerOrOperatorRoutes
            .Concat(systemOnlyRoutes)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(classified, discovered);
        Assert.Equal(
            systemOnlyRoutes.Order(StringComparer.Ordinal),
            discovered.Where(route => route.Contains("/api/staging/lifecycle-lever/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal));
        Assert.True(ElsaInstanceOperation.IsSystemOnlyLifecycleAction(ElsaInstanceOperationAction.Reconcile));
        Assert.False(ElsaInstanceOperation.IsCustomerOrOperatorMutation(ElsaInstanceOperationAction.Reconcile));

        var owner = app.CreateTrustedWorkspaceClient("managed-instance-mutation-guard");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);

        var created = await CreateReadyInstanceAsync(
            app,
            owner,
            workspaceId,
            "mutation-guard-create-runtime",
            Intent() with
            {
                Release = new ElsaReleaseIntent("valence-runtime", "3.8", requestedVersion: "3.8.4", channel: "stable")
            });
        Assert.True(await CountOperationsAsync(app) >= 1);

        var confirmationVersion = await ReadInstanceVersionAsync(app, created.Instance.InstanceId);
        var confirmationOps = await CountOperationsAsync(app);
        using var confirmationOnly = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        Assert.Equal(HttpStatusCode.OK, confirmationOnly.StatusCode);
        Assert.Equal(confirmationVersion, await ReadInstanceVersionAsync(app, created.Instance.InstanceId));
        Assert.Equal(confirmationOps, await CountOperationsAsync(app));

        await AssertRecordsMutationAsync(app, created.Instance.InstanceId, async () =>
        {
            using var current = await owner.GetAsync(
                $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}");
            current.EnsureSuccessStatusCode();
            using var patch = await owner.SendAsync(CustomerMutation(
                HttpMethod.Patch,
                $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}",
                current.Headers.ETag?.Tag,
                "mutation-guard-rename",
                new ManagedElsaInstancePatchRequest(Name: "Mutation guard renamed")));
            Assert.Equal(HttpStatusCode.Accepted, patch.StatusCode);
            var accepted = await patch.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
            await MarkOperationSucceededAsync(app, accepted!.Operation.Id);
        });

        await AssertRecordsMutationAsync(app, created.Instance.InstanceId, async () =>
        {
            using var current = await owner.GetAsync(
                $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}");
            current.EnsureSuccessStatusCode();
            using var restart = await owner.SendAsync(CustomerMutation(
                HttpMethod.Post,
                $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/restart",
                current.Headers.ETag?.Tag,
                "mutation-guard-restart"));
            Assert.Equal(HttpStatusCode.Accepted, restart.StatusCode);
            var accepted = await restart.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
            await MarkOperationSucceededAsync(app, accepted!.OperationId);
            await MarkInstanceReadyAsync(app, created.Instance.InstanceId);
        });

        await AssertRecordsMutationAsync(app, created.Instance.InstanceId, async () =>
        {
            using var current = await owner.GetAsync(
                $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}");
            current.EnsureSuccessStatusCode();
            using var apply = await owner.SendAsync(CustomerMutation(
                HttpMethod.Post,
                $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/apply-release",
                current.Headers.ETag?.Tag,
                "mutation-guard-apply",
                new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
            Assert.Equal(HttpStatusCode.Accepted, apply.StatusCode);
            var accepted = await apply.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
            await MarkOperationSucceededAsync(app, accepted!.OperationId);
            await MarkInstanceReadyAsync(app, created.Instance.InstanceId);
        });

        foreach (var action in Enum.GetValues<ElsaInstanceOperationAction>())
        {
            if (action is ElsaInstanceOperationAction.Create or ElsaInstanceOperationAction.UpdateIntent)
            {
                using var current = await owner.GetAsync(
                    $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}");
                current.EnsureSuccessStatusCode();
                var opsBefore = await CountOperationsAsync(app);
                using var rejected = await SendOperationAsync(
                    owner, workspaceId, created.Instance.InstanceId, current.Headers.ETag!.Tag,
                    $"mutation-guard-ops-{action}", new(action));
                Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
                Assert.Equal(opsBefore, await CountOperationsAsync(app));
                continue;
            }

            var subject = await CreateReadyInstanceAsync(
                app,
                owner,
                workspaceId,
                $"mutation-guard-ops-{action}-runtime",
                Intent() with
                {
                    Release = new ElsaReleaseIntent("valence-runtime", "3.8", requestedVersion: "3.8.4", channel: "stable")
                });
            if (action == ElsaInstanceOperationAction.Recover)
                await ParkApplyReleaseForRecoverAsync(
                    app, owner, workspaceId, subject, "mutation-guard-ops-recover");
            await AssertRecordsMutationAsync(app, subject.Instance.InstanceId, async () =>
            {
                await InvokeOperationsActionAsync(app, owner, workspaceId, subject, action);
            });
        }

        var recoverInstance = await CreateReadyInstanceAsync(
            app, owner, workspaceId, "mutation-guard-admin-recover-runtime",
            Intent() with
            {
                Release = new ElsaReleaseIntent("valence-runtime", "3.8", requestedVersion: "3.8.4", channel: "stable")
            });
        using var recoverApply = await owner.SendAsync(CustomerMutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{recoverInstance.Instance.InstanceId:D}/apply-release",
            recoverInstance.Instance.ETag,
            "mutation-guard-admin-recover-apply",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        Assert.Equal(HttpStatusCode.Accepted, recoverApply.StatusCode);
        var recoverApplied = await recoverApply.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
        await ParkRecoveryRequiredAsync(app, recoverApplied!.OperationId, "guard");
        using var recoverEtag = await owner.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{recoverInstance.Instance.InstanceId:D}");
        recoverEtag.EnsureSuccessStatusCode();
        await AssertRecordsMutationAsync(app, recoverInstance.Instance.InstanceId, async () =>
        {
            using var admin = app.CreateClient();
            admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
            using var recover = await SendAdminRecoveryAsync(
                admin,
                $"/api/admin/workspaces/{workspaceId:D}/instances/{recoverInstance.Instance.InstanceId:D}/operations/{recoverApplied.OperationId:D}/recover",
                recoverEtag.Headers.ETag?.Tag,
                "mutation-guard-admin-recover",
                new("operator recovery"));
            Assert.Equal(HttpStatusCode.Accepted, recover.StatusCode);
        });

        using var deleteCurrent = await owner.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}");
        deleteCurrent.EnsureSuccessStatusCode();
        using var deleteConfirmation = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var deleteConfirmationBody = await deleteConfirmation.Content
            .ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        await AssertRecordsMutationAsync(app, created.Instance.InstanceId, async () =>
        {
            using var deletion = await SendDeleteAsync(
                owner,
                workspaceId,
                created.Instance.InstanceId,
                deleteCurrent.Headers.ETag!.Tag,
                "mutation-guard-delete",
                deleteConfirmationBody!.ConfirmationId);
            Assert.Equal(HttpStatusCode.Accepted, deletion.StatusCode);
        });
    }

    [Fact]
    public async Task Dedicated_delete_retries_once_when_if_match_lags_the_current_version()
    {
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient("managed-instance-delete-stale-retry");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(owner, workspaceId, "narrow-delete-stale-retry-runtime");
        using var confirmationResponse = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE ElsaInstances SET Version = Version + 1 WHERE Id = {created.Instance.InstanceId}");
        }

        using var deletion = await SendDeleteAsync(
            owner,
            workspaceId,
            created.Instance.InstanceId,
            created.Instance.ETag,
            "narrow-delete-stale-retry",
            confirmation!.ConfirmationId);
        Assert.Equal(HttpStatusCode.Accepted, deletion.StatusCode);
    }

    [Fact]
    public async Task Dedicated_delete_replays_the_original_etag_after_rebased_acceptance_loses_the_response()
    {
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient("managed-instance-delete-rebase-lost-202");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(owner, workspaceId, "narrow-delete-rebase-lost-202-runtime");
        using var confirmationResponse = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE ElsaInstances SET Version = Version + 4 WHERE Id = {created.Instance.InstanceId}");
        }

        using var accepted = await SendDeleteAsync(
            owner,
            workspaceId,
            created.Instance.InstanceId,
            created.Instance.ETag,
            "narrow-delete-rebase-lost-202",
            confirmation!.ConfirmationId);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var acceptedBody = await accepted.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteAcceptedResponse>();
        Assert.NotNull(acceptedBody);

        using var lostResponseReplay = await SendDeleteAsync(
            owner,
            workspaceId,
            created.Instance.InstanceId,
            created.Instance.ETag,
            "narrow-delete-rebase-lost-202",
            confirmation.ConfirmationId);
        Assert.Equal(HttpStatusCode.Accepted, lostResponseReplay.StatusCode);
        var replayBody = await lostResponseReplay.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteAcceptedResponse>();
        Assert.Equal(acceptedBody!.OperationId, replayBody!.OperationId);
        Assert.Equal(2, await CountOperationsAsync(app));
    }

    [Fact]
    public async Task Dedicated_delete_returns_412_when_if_match_lags_a_customer_rename()
    {
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient("managed-instance-delete-after-rename");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(owner, workspaceId, "narrow-delete-after-rename-runtime");
        await MarkOperationSucceededAsync(app, created.Operation.Id);
        var rename = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstancePatchRequest(Name: "Renamed before delete"),
                options: ControlApiTestApplication.JsonOptions)
        };
        rename.Headers.Add("Idempotency-Key", "narrow-delete-after-rename");
        rename.Headers.TryAddWithoutValidation("If-Match", created.Instance.ETag);
        using var renamed = await owner.SendAsync(rename);
        Assert.Equal(HttpStatusCode.Accepted, renamed.StatusCode);

        using var confirmationResponse = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);
        var operationCount = await CountOperationsAsync(app);

        using var deletion = await SendDeleteAsync(
            owner,
            workspaceId,
            created.Instance.InstanceId,
            created.Instance.ETag,
            "narrow-delete-after-rename-delete",
            confirmation!.ConfirmationId);
        Assert.Equal(HttpStatusCode.PreconditionFailed, deletion.StatusCode);
        var renameBody = await deletion.Content.ReadAsStringAsync();
        Assert.Contains(ManagedElsaInstanceEndpoints.ChangedSinceReadCode, renameBody, StringComparison.Ordinal);
        Assert.DoesNotContain(ManagedElsaInstanceEndpoints.SystemOnlyVersionConflictCode, renameBody, StringComparison.Ordinal);
        Assert.Equal(operationCount, await CountOperationsAsync(app));
    }

    [Fact]
    public async Task Dedicated_delete_retries_once_after_reconcile_only_churn()
    {
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient("managed-instance-delete-after-reconcile");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(owner, workspaceId, "narrow-delete-after-reconcile-runtime");
        await MarkOperationSucceededAsync(app, created.Operation.Id);
        var reconcile = await SendOperationAsync(
            owner,
            workspaceId,
            created.Instance.InstanceId,
            created.Instance.ETag,
            "narrow-delete-after-reconcile",
            new(ElsaInstanceOperationAction.Reconcile));
        Assert.Equal(HttpStatusCode.Accepted, reconcile.StatusCode);

        using var confirmationResponse = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);

        using var deletion = await SendDeleteAsync(
            owner,
            workspaceId,
            created.Instance.InstanceId,
            created.Instance.ETag,
            "narrow-delete-after-reconcile-delete",
            confirmation!.ConfirmationId);
        Assert.Equal(HttpStatusCode.Accepted, deletion.StatusCode);
    }

    [Fact]
    public async Task Dedicated_delete_returns_412_when_if_match_lags_a_customer_restart()
    {
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient("managed-instance-delete-after-restart");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateReadyInstanceAsync(app, owner, workspaceId, "narrow-delete-after-restart-runtime");
        using var restart = await owner.SendAsync(CustomerMutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/restart",
            created.Instance.ETag,
            "narrow-delete-after-restart"));
        Assert.Equal(HttpStatusCode.Accepted, restart.StatusCode);

        using var confirmationResponse = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);
        var operationCount = await CountOperationsAsync(app);

        using var deletion = await SendDeleteAsync(
            owner,
            workspaceId,
            created.Instance.InstanceId,
            created.Instance.ETag,
            "narrow-delete-after-restart-delete",
            confirmation!.ConfirmationId);
        Assert.Equal(HttpStatusCode.PreconditionFailed, deletion.StatusCode);
        var restartBody = await deletion.Content.ReadAsStringAsync();
        Assert.Contains(ManagedElsaInstanceEndpoints.ChangedSinceReadCode, restartBody, StringComparison.Ordinal);
        Assert.DoesNotContain(ManagedElsaInstanceEndpoints.SystemOnlyVersionConflictCode, restartBody, StringComparison.Ordinal);
        Assert.Equal(operationCount, await CountOperationsAsync(app));
    }

    [Fact]
    public async Task Dedicated_delete_returns_412_when_if_match_lags_a_customer_apply_release()
    {
        var app = await PrepareApplicationAsync([], [
            CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid"),
            CatalogEntry("valence-runtime", "3.8", "3.8.5", "stable", "combined", "supported", "paid", 'b')
        ]);
        var owner = app.CreateTrustedWorkspaceClient("managed-instance-delete-after-apply");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateReadyInstanceAsync(
            app,
            owner,
            workspaceId,
            "narrow-delete-after-apply-runtime",
            Intent() with
            {
                Release = new ElsaReleaseIntent("valence-runtime", "3.8", requestedVersion: "3.8.4", channel: "stable")
            });
        using var apply = await owner.SendAsync(CustomerMutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/apply-release",
            created.Instance.ETag,
            "narrow-delete-after-apply",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        Assert.Equal(HttpStatusCode.Accepted, apply.StatusCode);

        using var confirmationResponse = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);
        var operationCount = await CountOperationsAsync(app);

        using var deletion = await SendDeleteAsync(
            owner,
            workspaceId,
            created.Instance.InstanceId,
            created.Instance.ETag,
            "narrow-delete-after-apply-delete",
            confirmation!.ConfirmationId);
        Assert.Equal(HttpStatusCode.PreconditionFailed, deletion.StatusCode);
        var applyBody = await deletion.Content.ReadAsStringAsync();
        Assert.Contains(ManagedElsaInstanceEndpoints.ChangedSinceReadCode, applyBody, StringComparison.Ordinal);
        Assert.Contains(ManagedElsaInstanceEndpoints.ChangedSinceReadDetail, applyBody, StringComparison.Ordinal);
        Assert.DoesNotContain(ManagedElsaInstanceEndpoints.SystemOnlyVersionConflictCode, applyBody, StringComparison.Ordinal);
        Assert.Equal(operationCount, await CountOperationsAsync(app));
    }

    [Fact]
    public async Task Dedicated_delete_returns_412_with_exact_ac2_copy_when_if_match_lags_a_customer_change()
    {
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient("managed-instance-delete-ac2-copy");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateReadyInstanceAsync(app, owner, workspaceId, "narrow-delete-ac2-copy-runtime");
        using var restart = await owner.SendAsync(CustomerMutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/restart",
            created.Instance.ETag,
            "narrow-delete-ac2-copy-restart"));
        Assert.Equal(HttpStatusCode.Accepted, restart.StatusCode);

        using var confirmationResponse = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);

        using var deletion = await SendDeleteAsync(
            owner,
            workspaceId,
            created.Instance.InstanceId,
            created.Instance.ETag,
            "narrow-delete-ac2-copy-delete",
            confirmation!.ConfirmationId);
        Assert.Equal(HttpStatusCode.PreconditionFailed, deletion.StatusCode);
        var body = await deletion.Content.ReadAsStringAsync();
        Assert.Contains(ManagedElsaInstanceEndpoints.ChangedSinceReadCode, body, StringComparison.Ordinal);
        Assert.Contains(ManagedElsaInstanceEndpoints.ChangedSinceReadDetail, body, StringComparison.Ordinal);
        Assert.Equal(
            ManagedElsaInstanceEndpoints.ChangedSinceReadDetail,
            ExtractProblemTitle(body));
        Assert.DoesNotContain("try again", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keeps failing", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ManagedElsaInstanceEndpoints.SystemOnlyVersionConflictCode, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dedicated_delete_returns_412_when_if_match_lags_a_member_recover()
    {
        var app = await PrepareApplicationAsync([], [
            CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid"),
            CatalogEntry("valence-runtime", "3.8", "3.8.5", "stable", "combined", "supported", "paid", 'b')
        ]);
        var owner = app.CreateTrustedWorkspaceClient("managed-instance-delete-after-recover-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        await app.AddWorkspaceMemberAsync(workspaceId, "managed-instance-delete-after-recover-member", WorkspaceRole.Owner);
        var member = app.CreateTrustedWorkspaceClient("managed-instance-delete-after-recover-member");
        var created = await CreateReadyInstanceAsync(
            app,
            owner,
            workspaceId,
            "narrow-delete-after-recover-runtime",
            Intent() with
            {
                Release = new ElsaReleaseIntent("valence-runtime", "3.8", requestedVersion: "3.8.4", channel: "stable")
            });
        using var apply = await owner.SendAsync(CustomerMutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/apply-release",
            created.Instance.ETag,
            "narrow-delete-after-recover-apply",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        Assert.Equal(HttpStatusCode.Accepted, apply.StatusCode);
        var applied = await apply.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
        Assert.NotNull(applied);
        await ParkRecoveryRequiredAsync(app, applied.OperationId, "a");

        using var etagResponse = await owner.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}");
        etagResponse.EnsureSuccessStatusCode();
        var capturedETag = etagResponse.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(capturedETag));

        using var recover = await SendOperationAsync(
            member,
            workspaceId,
            created.Instance.InstanceId,
            capturedETag!,
            "narrow-delete-after-recover",
            new(ElsaInstanceOperationAction.Recover));
        var recoverText = await recover.Content.ReadAsStringAsync();
        Assert.True(recover.StatusCode == HttpStatusCode.Accepted, recoverText);

        using var confirmationResponse = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);
        var operationCount = await CountOperationsAsync(app);

        using var deletion = await SendDeleteAsync(
            owner,
            workspaceId,
            created.Instance.InstanceId,
            capturedETag!,
            "narrow-delete-after-recover-delete",
            confirmation!.ConfirmationId);
        Assert.Equal(HttpStatusCode.PreconditionFailed, deletion.StatusCode);
        var deleteBody = await deletion.Content.ReadAsStringAsync();
        Assert.Contains(ManagedElsaInstanceEndpoints.ChangedSinceReadCode, deleteBody, StringComparison.Ordinal);
        Assert.Contains(ManagedElsaInstanceEndpoints.ChangedSinceReadDetail, deleteBody, StringComparison.Ordinal);
        Assert.DoesNotContain(ManagedElsaInstanceEndpoints.SystemOnlyVersionConflictCode, deleteBody, StringComparison.Ordinal);
        Assert.Equal(operationCount, await CountOperationsAsync(app));
    }

    [Fact]
    public async Task Dedicated_delete_retries_once_after_system_auto_resume()
    {
        var app = await PrepareApplicationAsync([], [
            CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid"),
            CatalogEntry("valence-runtime", "3.8", "3.8.5", "stable", "combined", "supported", "paid", 'b')
        ]);
        var owner = app.CreateTrustedWorkspaceClient("managed-instance-delete-after-auto-resume");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateReadyInstanceAsync(
            app,
            owner,
            workspaceId,
            "narrow-delete-after-auto-resume-runtime",
            Intent() with
            {
                Release = new ElsaReleaseIntent("valence-runtime", "3.8", requestedVersion: "3.8.4", channel: "stable")
            });
        using var apply = await owner.SendAsync(CustomerMutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/apply-release",
            created.Instance.ETag,
            "narrow-delete-after-auto-resume-apply",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        Assert.Equal(HttpStatusCode.Accepted, apply.StatusCode);
        var applied = await apply.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
        Assert.NotNull(applied);
        await ParkRecoveryRequiredAsync(app, applied.OperationId, "auto");

        using var etagResponse = await owner.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}");
        etagResponse.EnsureSuccessStatusCode();
        var capturedETag = etagResponse.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(capturedETag));
        var capturedVersion = ReadETagVersion(capturedETag!);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var lifecycle = scope.ServiceProvider.GetRequiredService<ElsaInstanceLifecycleService>();
            var recovered = await lifecycle.RecoverAsync(new ElsaInstanceLifecycleRequest(
                workspaceId,
                created.Instance.InstanceId,
                capturedVersion,
                $"auto-resume.{applied.OperationId:N}.1",
                "auto-resume",
                ActorAccountId: null,
                ExpectedOperationId: applied.OperationId));
            Assert.Null(recovered.Operation.RecoveryExpectedVersion);
        }

        using var confirmationResponse = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);

        using var deletion = await SendDeleteAsync(
            owner,
            workspaceId,
            created.Instance.InstanceId,
            capturedETag!,
            "narrow-delete-after-auto-resume-delete",
            confirmation!.ConfirmationId);
        var deleteText = await deletion.Content.ReadAsStringAsync();
        Assert.True(deletion.StatusCode == HttpStatusCode.Accepted, deleteText);
        Assert.DoesNotContain(ManagedElsaInstanceEndpoints.ChangedSinceReadCode, deleteText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dedicated_delete_returns_412_when_auto_resume_follows_a_member_recover()
    {
        var app = await PrepareApplicationAsync([], [
            CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid"),
            CatalogEntry("valence-runtime", "3.8", "3.8.5", "stable", "combined", "supported", "paid", 'b')
        ]);
        var owner = app.CreateTrustedWorkspaceClient("managed-instance-delete-after-recover-auto-resume-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        await app.AddWorkspaceMemberAsync(
            workspaceId, "managed-instance-delete-after-recover-auto-resume-member", WorkspaceRole.Owner);
        var member = app.CreateTrustedWorkspaceClient("managed-instance-delete-after-recover-auto-resume-member");
        var created = await CreateReadyInstanceAsync(
            app,
            owner,
            workspaceId,
            "narrow-delete-after-recover-auto-resume-runtime",
            Intent() with
            {
                Release = new ElsaReleaseIntent("valence-runtime", "3.8", requestedVersion: "3.8.4", channel: "stable")
            });
        using var apply = await owner.SendAsync(CustomerMutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/apply-release",
            created.Instance.ETag,
            "narrow-delete-after-recover-auto-resume-apply",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        Assert.Equal(HttpStatusCode.Accepted, apply.StatusCode);
        var applied = await apply.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
        Assert.NotNull(applied);
        await ParkRecoveryRequiredAsync(app, applied.OperationId, "mix");

        using var etagResponse = await owner.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}");
        etagResponse.EnsureSuccessStatusCode();
        var capturedETag = etagResponse.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(capturedETag));
        var capturedVersion = ReadETagVersion(capturedETag!);

        using var recover = await SendOperationAsync(
            member,
            workspaceId,
            created.Instance.InstanceId,
            capturedETag!,
            "narrow-delete-after-recover-auto-resume-recover",
            new(ElsaInstanceOperationAction.Recover));
        var recoverText = await recover.Content.ReadAsStringAsync();
        Assert.True(recover.StatusCode == HttpStatusCode.Accepted, recoverText);
        Assert.True(await ReadRecoveryExpectedVersionAsync(app, applied.OperationId) >= capturedVersion);

        await ParkRecoveryRequiredAsync(app, applied.OperationId, "mix-2");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var lifecycle = scope.ServiceProvider.GetRequiredService<ElsaInstanceLifecycleService>();
            var resumed = await lifecycle.RecoverAsync(new ElsaInstanceLifecycleRequest(
                workspaceId,
                created.Instance.InstanceId,
                await ReadInstanceVersionAsync(app, created.Instance.InstanceId),
                $"auto-resume.{applied.OperationId:N}.2",
                "auto-resume",
                ActorAccountId: null,
                ExpectedOperationId: applied.OperationId));
            Assert.True(resumed.Operation.RecoveryExpectedVersion >= capturedVersion);
        }

        Assert.True(await ReadRecoveryExpectedVersionAsync(app, applied.OperationId) >= capturedVersion);
        using var confirmationResponse = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);
        var operationCount = await CountOperationsAsync(app);

        using var deletion = await SendDeleteAsync(
            owner,
            workspaceId,
            created.Instance.InstanceId,
            capturedETag!,
            "narrow-delete-after-recover-auto-resume-delete",
            confirmation!.ConfirmationId);
        Assert.Equal(HttpStatusCode.PreconditionFailed, deletion.StatusCode);
        var deleteBody = await deletion.Content.ReadAsStringAsync();
        Assert.Contains(ManagedElsaInstanceEndpoints.ChangedSinceReadCode, deleteBody, StringComparison.Ordinal);
        Assert.Contains(ManagedElsaInstanceEndpoints.ChangedSinceReadDetail, deleteBody, StringComparison.Ordinal);
        Assert.DoesNotContain(ManagedElsaInstanceEndpoints.SystemOnlyVersionConflictCode, deleteBody, StringComparison.Ordinal);
        Assert.Equal(operationCount, await CountOperationsAsync(app));
    }

    [Fact]
    public async Task Dedicated_delete_returns_412_when_if_match_lags_an_operator_recover()
    {
        var app = await PrepareApplicationAsync([], [
            CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid"),
            CatalogEntry("valence-runtime", "3.8", "3.8.5", "stable", "combined", "supported", "paid", 'b')
        ]);
        var owner = app.CreateTrustedWorkspaceClient("managed-instance-delete-after-operator-recover");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateReadyInstanceAsync(
            app,
            owner,
            workspaceId,
            "narrow-delete-after-operator-recover-runtime",
            Intent() with
            {
                Release = new ElsaReleaseIntent("valence-runtime", "3.8", requestedVersion: "3.8.4", channel: "stable")
            });
        using var apply = await owner.SendAsync(CustomerMutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/apply-release",
            created.Instance.ETag,
            "narrow-delete-after-operator-recover-apply",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        Assert.Equal(HttpStatusCode.Accepted, apply.StatusCode);
        var applied = await apply.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
        Assert.NotNull(applied);
        await ParkRecoveryRequiredAsync(app, applied.OperationId, "op");

        using var etagResponse = await owner.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}");
        etagResponse.EnsureSuccessStatusCode();
        var capturedETag = etagResponse.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(capturedETag));

        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        var recoveryPath =
            $"/api/admin/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/operations/{applied.OperationId:D}/recover";
        using var recover = await SendAdminRecoveryAsync(
            admin, recoveryPath, capturedETag, "narrow-delete-after-operator-recover", new("operator recovery"));
        var recoverText = await recover.Content.ReadAsStringAsync();
        Assert.True(recover.StatusCode == HttpStatusCode.Accepted, recoverText);

        using var confirmationResponse = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);
        var operationCount = await CountOperationsAsync(app);

        using var deletion = await SendDeleteAsync(
            owner,
            workspaceId,
            created.Instance.InstanceId,
            capturedETag!,
            "narrow-delete-after-operator-recover-delete",
            confirmation!.ConfirmationId);
        Assert.Equal(HttpStatusCode.PreconditionFailed, deletion.StatusCode);
        var deleteBody = await deletion.Content.ReadAsStringAsync();
        Assert.Contains(ManagedElsaInstanceEndpoints.ChangedSinceReadCode, deleteBody, StringComparison.Ordinal);
        Assert.Contains(ManagedElsaInstanceEndpoints.ChangedSinceReadDetail, deleteBody, StringComparison.Ordinal);
        Assert.DoesNotContain(ManagedElsaInstanceEndpoints.SystemOnlyVersionConflictCode, deleteBody, StringComparison.Ordinal);
        Assert.Equal(operationCount, await CountOperationsAsync(app));
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
    public void Canonical_projection_shows_recovery_required_for_a_parked_create_with_unknown_storage()
    {
        var instanceId = Guid.NewGuid();
        var instance = ElsaInstance.Hydrate(instanceId, Guid.NewGuid(), Guid.NewGuid(), "Claims runtime", "claims-runtime",
            Intent(), ElsaObservedLifecycle.Unknown, ElsaInstanceHealth.Unknown, 4,
            lastOperationId: new ElsaLastOperationId(Guid.NewGuid()));
        var now = DateTimeOffset.UtcNow;
        var operation = new ElsaInstanceOperationSummary(
            Guid.NewGuid(), instanceId, ElsaInstanceOperationAction.Create, ElsaInstanceOperationState.RecoveryRequired,
            1, 1, now, now, null, null, null, null, null, null, null,
            ReasonEnteredAt: now, RequiresHumanAt: now);

        var response = ManagedElsaInstanceEndpoints.ToResponse(instance, canOpen: true, instance.WorkspaceId, activeOperation: operation);

        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, response.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Unknown, response.Health);
        Assert.False(response.CanOpen);
        Assert.Equal(ManagedElsaInstanceCustomerProjection.RecoveryRequiredUnavailableReason, response.UnavailableReason);
        Assert.Equal("instance.recovery-required", response.UnavailableReasonCode);
        Assert.DoesNotContain("Failed", response.UnavailableReason, StringComparison.Ordinal);
        Assert.NotEqual(ManagedElsaInstanceCustomerProjection.ProvisioningUnavailableReason, response.UnavailableReason);
    }

    [Fact]
    public void Ready_healthy_human_required_park_agrees_on_list_detail_and_overview()
    {
        var (instance, identity, operation) = ReadyHealthyParked(
            ElsaInstanceProviderReconciliationService.AutoResumeExhaustedCode);
        var listed = ManagedElsaInstanceCustomerProjection.Apply(instance, operation);
        var list = ManagedElsaInstanceEndpoints.ToResponse(
            listed, canOpen: true, instance.WorkspaceId, identity, operation);
        var detail = ManagedElsaInstanceEndpoints.ToResponse(
            instance, canOpen: true, instance.WorkspaceId, identity, operation);
        var overview = Overview(instance, identity, operation);

        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, list.ObservedLifecycle);
        Assert.Equal(list.ObservedLifecycle, detail.ObservedLifecycle);
        Assert.Equal(list.ObservedLifecycle, overview.Summary.ObservedLifecycle);
        Assert.False(list.CanOpen);
        Assert.Equal(list.CanOpen, detail.CanOpen);
        Assert.Equal(list.CanOpen, overview.Summary.CanOpen);
        Assert.Equal("instance.recovery-required", overview.Summary.UnavailableReason);
        Assert.Equal(ManagedElsaInstanceCustomerProjection.RecoveryRequiredUnavailableReason, list.UnavailableReason);
        Assert.Equal(overview.Summary.UnavailableReason, list.UnavailableReasonCode);
        Assert.DoesNotContain("Failed", list.UnavailableReason, StringComparison.Ordinal);
        Assert.Equal(ManagedElsaInstanceCustomerProjection.NeedsAttentionLabel,
            ManagedElsaInstanceCustomerProjection.CustomerLabel(list.ObservedLifecycle));
    }

    [Fact]
    public void Ready_healthy_hand_off_park_stays_openable_on_list_detail_and_overview()
    {
        var (instance, identity, operation) = ReadyHealthyParked(
            ManagedElsaReasonCodeCatalog.ProviderSubmissionAccepted);
        var listed = ManagedElsaInstanceCustomerProjection.Apply(instance, operation);
        var list = ManagedElsaInstanceEndpoints.ToResponse(
            listed, canOpen: true, instance.WorkspaceId, identity, operation);
        var detail = ManagedElsaInstanceEndpoints.ToResponse(
            instance, canOpen: true, instance.WorkspaceId, identity, operation);
        var overview = Overview(instance, identity, operation);

        Assert.Equal(ElsaObservedLifecycle.Ready, list.ObservedLifecycle);
        Assert.Equal(list.ObservedLifecycle, detail.ObservedLifecycle);
        Assert.Equal(list.ObservedLifecycle, overview.Summary.ObservedLifecycle);
        Assert.True(list.CanOpen);
        Assert.Equal(list.CanOpen, detail.CanOpen);
        Assert.Equal(list.CanOpen, overview.Summary.CanOpen);
        Assert.Null(overview.Summary.UnavailableReason);
        Assert.Null(list.UnavailableReason);
        Assert.Null(list.UnavailableReasonCode);
        Assert.Null(ManagedElsaInstanceCustomerProjection.CustomerLabel(list.ObservedLifecycle));
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
        Assert.Equal("instance.unavailable", response.UnavailableReasonCode);
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

    [Theory]
    [InlineData("disabled")]
    [InlineData("pending")]
    [InlineData("unacknowledged")]
    public async Task Parked_unknown_create_projects_recovery_required_on_canonical_list_detail_and_overview(
        string alertDelivery)
    {
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient($"parked-unknown-{alertDelivery}-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(owner, workspaceId, $"parked-unknown-{alertDelivery}");
        await ParkHumanRequiredCreateAsync(
            app, created.Instance.InstanceId, created.Operation.Id,
            ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted);
        await SeedAlertOutboxAsync(app, workspaceId, created.Instance.InstanceId, created.Operation.Id, alertDelivery);

        Assert.Equal(ElsaObservedLifecycle.Unknown, await ReadStoredObservedLifecycleAsync(app, created.Instance.InstanceId));

        using var list = await owner.GetAsync($"/api/workspaces/{workspaceId:D}/instances");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listed = Assert.Single((await list.Content.ReadControlJsonAsync<ManagedElsaInstanceListResponse>())!.Items);
        using var detail = await owner.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}");
        var detailBody = await detail.Content.ReadControlJsonAsync<ManagedElsaInstanceResponse>();
        var overview = await owner.GetControlJsonAsync<ManagedElsaInstanceOverviewResponse>(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/overview");

        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, listed.ObservedLifecycle);
        Assert.Equal(listed.ObservedLifecycle, detailBody!.ObservedLifecycle);
        Assert.Equal(listed.ObservedLifecycle, overview!.Summary.ObservedLifecycle);
        Assert.Equal(ManagedElsaInstanceCustomerProjection.RecoveryRequiredUnavailableReason, listed.UnavailableReason);
        Assert.Equal(listed.UnavailableReason, detailBody.UnavailableReason);
        Assert.Equal("instance.recovery-required", listed.UnavailableReasonCode);
        Assert.Equal(listed.UnavailableReasonCode, detailBody.UnavailableReasonCode);
        Assert.Equal(listed.UnavailableReasonCode, overview.Summary.UnavailableReason);
        Assert.DoesNotContain("Valence Works has been alerted", listed.UnavailableReason, StringComparison.Ordinal);
        Assert.DoesNotContain("alerted", listed.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Failed", listed.UnavailableReason, StringComparison.Ordinal);
        Assert.False(listed.CanOpen);

        await app.AddWorkspaceMemberAsync(workspaceId, $"parked-unknown-{alertDelivery}-reader", WorkspaceRole.Reader);
        using var reader = app.CreateTrustedWorkspaceClient($"parked-unknown-{alertDelivery}-reader");
        using var readerList = await reader.GetAsync($"/api/workspaces/{workspaceId:D}/instances");
        var readerListed = Assert.Single((await readerList.Content.ReadControlJsonAsync<ManagedElsaInstanceListResponse>())!.Items);
        Assert.Equal(HttpStatusCode.OK, readerList.StatusCode);
        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, readerListed.ObservedLifecycle);
        Assert.Equal("not-authorized", readerListed.UnavailableReasonCode);
        Assert.Equal("Not authorized to open this instance.", readerListed.UnavailableReason);
        Assert.False(readerListed.CanOpen);

        using var outsider = await app.CreateControlIdentityClient(subject: $"parked-unknown-{alertDelivery}-outsider")
            .GetAsync($"/api/workspaces/{workspaceId:D}/instances");
        Assert.Equal(HttpStatusCode.Forbidden, outsider.StatusCode);
    }

    [Fact]
    public void Legacy_list_keeps_stored_unknown_until_a_summary_is_already_projected()
    {
        var parkedId = Guid.NewGuid();
        var projectedId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var parkedOperation = new ElsaInstanceOperationSummary(
            Guid.NewGuid(), parkedId, ElsaInstanceOperationAction.Create,
            ElsaInstanceOperationState.RecoveryRequired, 1, 1, now, now, null, null, null, null, null, null, null,
            ReasonEnteredAt: now, RequiresHumanAt: now);
        var parkedInstance = ElsaInstance.Hydrate(
            parkedId, Guid.NewGuid(), Guid.NewGuid(), "Parked runtime", "parked-runtime",
            Intent(), ElsaObservedLifecycle.Unknown, ElsaInstanceHealth.Unknown, 4,
            lastOperationId: new ElsaLastOperationId(parkedOperation.Id));
        var storedUnknown = Instance(parkedId, "Parked runtime", "parked-runtime",
            observedLifecycle: ElsaObservedLifecycle.Unknown, health: ElsaInstanceHealth.Unknown, bound: false);
        var alreadyProjected = Instance(projectedId, "Projected runtime", "projected-runtime",
            observedLifecycle: ElsaObservedLifecycle.RecoveryRequired, health: ElsaInstanceHealth.Unknown, bound: false);

        var legacyUnknown = ManagedElsaInstanceEndpoints.ToLegacyResponse(storedUnknown, canOpen: true, controlHandoffEnabled: true);
        var legacyProjected = ManagedElsaInstanceEndpoints.ToLegacyResponse(alreadyProjected, canOpen: true, controlHandoffEnabled: true);
        var canonicalParked = ManagedElsaInstanceEndpoints.ToResponse(
            parkedInstance, canOpen: true, parkedInstance.WorkspaceId, activeOperation: parkedOperation);

        Assert.Equal(ElsaObservedLifecycle.Unknown, legacyUnknown.ObservedLifecycle);
        Assert.Equal(ManagedElsaInstanceCustomerProjection.UnknownUnavailableReason, legacyUnknown.UnavailableReason);
        Assert.Equal("instance.unknown", legacyUnknown.UnavailableReasonCode);
        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, legacyProjected.ObservedLifecycle);
        Assert.Equal(ManagedElsaInstanceCustomerProjection.RecoveryRequiredUnavailableReason, legacyProjected.UnavailableReason);
        Assert.Equal("instance.recovery-required", legacyProjected.UnavailableReasonCode);
        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, canonicalParked.ObservedLifecycle);
        Assert.Equal(ManagedElsaInstanceCustomerProjection.RecoveryRequiredUnavailableReason, canonicalParked.UnavailableReason);
        Assert.DoesNotContain("alerted", canonicalParked.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(legacyUnknown.ObservedLifecycle, canonicalParked.ObservedLifecycle);
    }

    [Fact]
    public async Task Legacy_list_http_returns_stored_unknown_and_already_projected_summaries()
    {
        var unknownId = Guid.NewGuid();
        var projectedId = Guid.NewGuid();
        var app = await PrepareApplicationAsync([
            Instance(unknownId, "Parked runtime", "parked-runtime",
                observedLifecycle: ElsaObservedLifecycle.Unknown, health: ElsaInstanceHealth.Unknown, bound: false),
            Instance(projectedId, "Projected runtime", "projected-runtime",
                observedLifecycle: ElsaObservedLifecycle.RecoveryRequired, health: ElsaInstanceHealth.Unknown, bound: false)
        ]);
        var owner = app.CreateTrustedWorkspaceClient("legacy-parked-topology-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();

        using var response = await owner.GetAsync($"/api/workspaces/{workspaceId:D}/managed-elsa/instances");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var instances = await response.Content.ReadFromJsonAsync<List<ManagedElsaInstanceResponse>>(
            ControlApiTestApplication.JsonOptions);
        var storedUnknown = Assert.Single(instances!, item => item.InstanceId == unknownId);
        var alreadyProjected = Assert.Single(instances!, item => item.InstanceId == projectedId);

        Assert.Equal(ElsaObservedLifecycle.Unknown, storedUnknown.ObservedLifecycle);
        Assert.Equal("instance.unknown", storedUnknown.UnavailableReasonCode);
        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, alreadyProjected.ObservedLifecycle);
        Assert.Equal("instance.recovery-required", alreadyProjected.UnavailableReasonCode);
        Assert.DoesNotContain("alerted", alreadyProjected.UnavailableReason, StringComparison.OrdinalIgnoreCase);

        await app.AddWorkspaceMemberAsync(workspaceId, "legacy-parked-topology-reader", WorkspaceRole.Reader);
        using var reader = await app.CreateTrustedWorkspaceClient("legacy-parked-topology-reader")
            .GetAsync($"/api/workspaces/{workspaceId:D}/managed-elsa/instances");
        var readerUnknown = Assert.Single(
            (await reader.Content.ReadFromJsonAsync<List<ManagedElsaInstanceResponse>>(ControlApiTestApplication.JsonOptions))!,
            item => item.InstanceId == unknownId);
        Assert.Equal("not-authorized", readerUnknown.UnavailableReasonCode);

        using var outsider = await app.CreateControlIdentityClient(subject: "legacy-parked-topology-outsider")
            .GetAsync($"/api/workspaces/{workspaceId:D}/managed-elsa/instances");
        Assert.Equal(HttpStatusCode.Forbidden, outsider.StatusCode);
    }

    [Theory]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionBlockedByOperationInFlight)]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionProviderProgressStale)]
    public async Task Parked_delete_returns_allowlisted_reason_without_azure_inventory(string reasonCode)
    {
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient($"parked-delete-{reasonCode.Split('.')[^1]}-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(owner, workspaceId, $"parked-delete-{reasonCode.Split('.')[^1]}");
        await MarkOperationSucceededAsync(app, created.Operation.Id);

        using var confirmationResponse = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        using var deletion = await SendDeleteAsync(
            owner, workspaceId, created.Instance.InstanceId, created.Instance.ETag,
            $"parked-delete-{reasonCode.Split('.')[^1]}", confirmation!.ConfirmationId);
        var accepted = await deletion.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteAcceptedResponse>();
        Assert.Equal(HttpStatusCode.Accepted, deletion.StatusCode);
        await ParkHumanRequiredDeleteAsync(app, created.Instance.InstanceId, accepted!.OperationId, reasonCode);

        using var status = await owner.GetAsync(accepted.OperationUrl);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var statusJson = await status.Content.ReadAsStringAsync();
        var statusBody = System.Text.Json.JsonSerializer.Deserialize<ManagedElsaInstanceDeleteOperationResponse>(
            statusJson, ControlApiTestApplication.JsonOptions);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, statusBody!.State);
        Assert.Equal(reasonCode, statusBody.ReasonCode);
        Assert.DoesNotContain("/subscriptions/", statusJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resourceGroups", statusJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider.private-secret-value", statusJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Worker heartbeat", statusJson, StringComparison.Ordinal);
        Assert.DoesNotContain("alerted", statusJson, StringComparison.OrdinalIgnoreCase);

        using var list = await owner.GetAsync($"/api/workspaces/{workspaceId:D}/instances");
        var listed = Assert.Single((await list.Content.ReadControlJsonAsync<ManagedElsaInstanceListResponse>())!.Items);
        Assert.Equal(ElsaDesiredLifecycle.Deleting, listed.DesiredLifecycle);
        Assert.Equal(ElsaInstanceOperationAction.Delete, listed.ActiveOperation?.Action);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, listed.ActiveOperation?.State);
        Assert.Equal(ManagedElsaInstanceCustomerProjection.ParkedDeleteUnavailableReason, listed.UnavailableReason);
        Assert.Equal(reasonCode, listed.UnavailableReasonCode);
        Assert.DoesNotContain("alerted", listed.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(listed.CanOpen);

        await app.AddWorkspaceMemberAsync(workspaceId, $"parked-delete-{reasonCode.Split('.')[^1]}-reader", WorkspaceRole.Reader);
        using var readerStatus = await app.CreateTrustedWorkspaceClient($"parked-delete-{reasonCode.Split('.')[^1]}-reader")
            .GetAsync(accepted.OperationUrl);
        Assert.Equal(HttpStatusCode.OK, readerStatus.StatusCode);
        var readerBody = await readerStatus.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteOperationResponse>();
        Assert.Equal(reasonCode, readerBody!.ReasonCode);

        using var readerList = await app.CreateTrustedWorkspaceClient($"parked-delete-{reasonCode.Split('.')[^1]}-reader")
            .GetAsync($"/api/workspaces/{workspaceId:D}/instances");
        var readerListed = Assert.Single((await readerList.Content.ReadControlJsonAsync<ManagedElsaInstanceListResponse>())!.Items);
        Assert.Equal("not-authorized", readerListed.UnavailableReasonCode);

        var outsider = app.CreateTrustedWorkspaceClient($"parked-delete-{reasonCode.Split('.')[^1]}-outsider");
        var otherWorkspaceId = await outsider.GetDefaultWorkspaceIdAsync();
        using var crossWorkspace = await outsider.GetAsync(
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-operations/{accepted.OperationId:D}");
        using var nonMember = await app.CreateControlIdentityClient(subject: $"parked-delete-{reasonCode.Split('.')[^1]}-nonmember")
            .GetAsync(accepted.OperationUrl);
        Assert.Equal(HttpStatusCode.NotFound, crossWorkspace.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, nonMember.StatusCode);
    }

    [Fact]
    public async Task Generic_operation_dto_redacts_unsupported_provider_diagnostics()
    {
        const string unsupportedReason = "provider.reconciliation.unsupported-synthetic";
        const string unsupportedFailure = "provider.private-secret-value";
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient("generic-operation-redact-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(owner, workspaceId, "generic-operation-redact");
        Assert.Null(created.Operation.ReasonCode);
        Assert.Null(created.Operation.FailureCode);
        Assert.DoesNotContain(unsupportedReason, created.Operation.Links["self"], StringComparison.Ordinal);
        await PersistParkedOperationDiagnosticsAsync(
            app, created.Instance.InstanceId, created.Operation.Id, unsupportedReason, unsupportedFailure);

        var detailPath = $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/operations/{created.Operation.Id:D}";
        using var detail = await owner.GetAsync(detailPath);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(created.Instance.ETag, detail.Headers.ETag?.Tag);
        var detailJson = await detail.Content.ReadAsStringAsync();
        var detailBody = System.Text.Json.JsonSerializer.Deserialize<ManagedElsaInstanceOperationResponse>(
            detailJson, ControlApiTestApplication.JsonOptions);
        Assert.Null(detailBody!.ReasonCode);
        Assert.Null(detailBody.FailureCode);
        Assert.DoesNotContain(unsupportedReason, detailJson, StringComparison.Ordinal);
        Assert.DoesNotContain(unsupportedFailure, detailJson, StringComparison.Ordinal);
        Assert.DoesNotContain("/subscriptions/", detailJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resourceGroups", detailJson, StringComparison.OrdinalIgnoreCase);

        using var list = await owner.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/operations");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listJson = await list.Content.ReadAsStringAsync();
        var listed = Assert.Single((await list.Content.ReadControlJsonAsync<ManagedElsaInstanceOperationListResponse>())!.Items);
        Assert.Equal(created.Operation.Id, listed.Id);
        Assert.Null(listed.ReasonCode);
        Assert.Null(listed.FailureCode);
        Assert.DoesNotContain(unsupportedReason, listJson, StringComparison.Ordinal);
        Assert.DoesNotContain(unsupportedFailure, listJson, StringComparison.Ordinal);

        await app.AddWorkspaceMemberAsync(workspaceId, "generic-operation-redact-reader", WorkspaceRole.Reader);
        using var readerDetail = await app.CreateTrustedWorkspaceClient("generic-operation-redact-reader")
            .GetAsync(detailPath);
        Assert.Equal(HttpStatusCode.OK, readerDetail.StatusCode);
        var readerBody = await readerDetail.Content.ReadControlJsonAsync<ManagedElsaInstanceOperationResponse>();
        Assert.Null(readerBody!.ReasonCode);
        Assert.Null(readerBody.FailureCode);

        var outsider = app.CreateTrustedWorkspaceClient("generic-operation-redact-outsider");
        var otherWorkspaceId = await outsider.GetDefaultWorkspaceIdAsync();
        using var crossWorkspace = await outsider.GetAsync(
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{created.Instance.InstanceId:D}/operations/{created.Operation.Id:D}");
        using var nonMember = await app.CreateControlIdentityClient(subject: "generic-operation-redact-nonmember")
            .GetAsync(detailPath);
        Assert.Equal(HttpStatusCode.NotFound, crossWorkspace.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, nonMember.StatusCode);
    }

    [Theory]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionBlockedByOperationInFlight)]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionProviderProgressStale)]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionProviderCleanupPending)]
    [InlineData(ElsaInstanceCommercialOperation.EntitlementRequired)]
    public async Task Generic_operation_dto_keeps_allowlisted_reason_and_failure_codes(string reasonCode)
    {
        var slug = $"generic-operation-allow-{reasonCode.Split('.')[^1]}";
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient($"{slug}-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(owner, workspaceId, slug);
        Assert.Null(created.Operation.ReasonCode);
        Assert.Null(created.Operation.FailureCode);
        await PersistParkedOperationDiagnosticsAsync(
            app, created.Instance.InstanceId, created.Operation.Id, reasonCode, reasonCode);

        var detailPath = $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/operations/{created.Operation.Id:D}";
        using var detail = await owner.GetAsync(detailPath);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailBody = await detail.Content.ReadControlJsonAsync<ManagedElsaInstanceOperationResponse>();
        Assert.Equal(reasonCode, detailBody!.ReasonCode);
        Assert.Equal(reasonCode, detailBody.FailureCode);

        using var list = await owner.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/operations");
        var listed = Assert.Single((await list.Content.ReadControlJsonAsync<ManagedElsaInstanceOperationListResponse>())!.Items);
        Assert.Equal(reasonCode, listed.ReasonCode);
        Assert.Equal(reasonCode, listed.FailureCode);

        await app.AddWorkspaceMemberAsync(workspaceId, $"{slug}-reader", WorkspaceRole.Reader);
        using var readerDetail = await app.CreateTrustedWorkspaceClient($"{slug}-reader").GetAsync(detailPath);
        var readerBody = await readerDetail.Content.ReadControlJsonAsync<ManagedElsaInstanceOperationResponse>();
        Assert.Equal(HttpStatusCode.OK, readerDetail.StatusCode);
        Assert.Equal(reasonCode, readerBody!.ReasonCode);
        Assert.Equal(reasonCode, readerBody.FailureCode);

        var outsider = app.CreateTrustedWorkspaceClient($"{slug}-outsider");
        var otherWorkspaceId = await outsider.GetDefaultWorkspaceIdAsync();
        using var crossWorkspace = await outsider.GetAsync(
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{created.Instance.InstanceId:D}/operations/{created.Operation.Id:D}");
        using var nonMember = await app.CreateControlIdentityClient(subject: $"{slug}-nonmember")
            .GetAsync(detailPath);
        Assert.Equal(HttpStatusCode.NotFound, crossWorkspace.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, nonMember.StatusCode);
    }

    [Fact]
    public void Generic_operation_mapper_allowlists_reason_and_failure_codes()
    {
        var now = DateTimeOffset.UtcNow;
        var workspaceId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var instanceId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var unsupported = new ElsaInstanceOperationSummary(
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            instanceId,
            ElsaInstanceOperationAction.Create,
            ElsaInstanceOperationState.RecoveryRequired,
            1, 1, now, now, null, null, null, null,
            "provider.reconciliation.unsupported-synthetic",
            ElsaObservedLifecycle.Unknown,
            ElsaInstanceHealth.Unknown,
            ReasonCode: "azure.recovery.step-unsupported",
            UpdatedAt: now, ReasonEnteredAt: now, RequiresHumanAt: now);
        var allowlisted = unsupported with
        {
            FailureCode = ManagedElsaReasonCodeCatalog.DeletionProviderCleanupPending,
            ReasonCode = ManagedElsaReasonCodeCatalog.DeletionBlockedByOperationInFlight
        };

        var leaked = ManagedElsaInstanceEndpoints.ToOperationResponse(workspaceId, instanceId, unsupported);
        Assert.Null(leaked.ReasonCode);
        Assert.Null(leaked.FailureCode);

        var kept = ManagedElsaInstanceEndpoints.ToOperationResponse(workspaceId, instanceId, allowlisted);
        Assert.Equal(ManagedElsaReasonCodeCatalog.DeletionBlockedByOperationInFlight, kept.ReasonCode);
        Assert.Equal(ManagedElsaReasonCodeCatalog.DeletionProviderCleanupPending, kept.FailureCode);

        var mixed = ManagedElsaInstanceEndpoints.ToOperationResponse(
            workspaceId, instanceId,
            unsupported with
            {
                FailureCode = ElsaInstanceCommercialOperation.EntitlementRequired,
                ReasonCode = "provider.reconciliation.unsupported-synthetic"
            });
        var acceptedJson = System.Text.Json.JsonSerializer.Serialize(
            new ManagedElsaInstanceAcceptedResponse(
                ManagedElsaInstanceEndpoints.ToResponse(
                    ElsaInstance.Hydrate(instanceId, Guid.NewGuid(), workspaceId, "Claims runtime", "generic-allowlist",
                        Intent(), ElsaObservedLifecycle.Unknown, ElsaInstanceHealth.Unknown, 1,
                        lastOperationId: new ElsaLastOperationId(unsupported.Id)),
                    canOpen: true, workspaceId, activeOperation: unsupported),
                mixed,
                new Dictionary<string, string>
                {
                    ["self"] = $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/operations/{unsupported.Id:D}"
                }),
            ControlApiTestApplication.JsonOptions);
        Assert.Equal(ElsaInstanceCommercialOperation.EntitlementRequired, mixed.FailureCode);
        Assert.Null(mixed.ReasonCode);
        Assert.DoesNotContain("provider.reconciliation.unsupported-synthetic", acceptedJson, StringComparison.Ordinal);
        Assert.Contains(ElsaInstanceCommercialOperation.EntitlementRequired, acceptedJson, StringComparison.Ordinal);
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

    [Fact]
    public async Task Provisioning_progress_reports_a_safe_queued_create_and_conceals_cross_workspace_instances()
    {
        var app = await PrepareApplicationAsync([]);
        var owner = app.CreateTrustedWorkspaceClient("managed-progress-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var accepted = await CreateCanonicalInstanceAsync(owner, workspaceId, "managed-progress-runtime");
        var instanceId = accepted.Instance.InstanceId;

        var response = await owner.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/provisioning-progress");
        var responseJson = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var progress = await response.Content.ReadControlJsonAsync<ManagedElsaProvisioningProgress>();
        Assert.NotNull(progress);
        Assert.Equal(ManagedElsaProvisioningProgressStates.Queued, progress.State);
        Assert.Equal("azure", progress.Provider);
        Assert.Equal(ManagedElsaProvisioningProgressStages.RequestAccepted, progress.CurrentStage);
        Assert.Equal(ManagedElsaProvisioningProgressStages.Ordered, progress.Stages.Select(stage => stage.Code));
        Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Current, progress.Stages[0].Status);
        Assert.All(progress.Stages.Skip(1), stage =>
            Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Pending, stage.Status));
        Assert.Contains(progress.Activity, activity =>
            activity.MessageCode == "request.accepted" &&
            activity.Stage == ManagedElsaProvisioningProgressStages.RequestAccepted);
        Assert.DoesNotContain(workspaceId.ToString("D"), responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(instanceId.ToString("D"), responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("operationId", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("endpoint", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("worker", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fingerprint", responseJson, StringComparison.OrdinalIgnoreCase);

        using var anonymous = app.CreateClient();
        var unauthenticated = await anonymous.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/provisioning-progress");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var other = app.CreateTrustedWorkspaceClient("managed-progress-other-owner");
        var otherWorkspaceId = await other.GetDefaultWorkspaceIdAsync();
        var crossWorkspace = await other.GetAsync(
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{instanceId:D}/provisioning-progress");
        var unknown = await other.GetAsync(
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{Guid.NewGuid():D}/provisioning-progress");

        Assert.Equal(HttpStatusCode.NotFound, crossWorkspace.StatusCode);
        Assert.Equal(unknown.StatusCode, crossWorkspace.StatusCode);
    }

    [Fact]
    public async Task Provisioning_progress_maps_consistency_drift_to_a_safe_retryable_conflict()
    {
        await using var app = new ControlApiTestApplication(configureServices: services =>
        {
            services.RemoveAll<IManagedElsaProvisioningProgressReader>();
            services.AddScoped<IManagedElsaProvisioningProgressReader, TopologyChangedProgressReader>();
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = app.CreateTrustedWorkspaceClient("managed-progress-drift-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();

        var response = await owner.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{Guid.NewGuid():D}/provisioning-progress");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("instance.provisioning-topology-changed", json, StringComparison.Ordinal);
        Assert.DoesNotContain("operation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provisioning_progress_serializes_blocker_and_stale_reason_for_cloud()
    {
        await using var app = new ControlApiTestApplication(configureServices: services =>
        {
            services.RemoveAll<IManagedElsaProvisioningProgressReader>();
            services.AddScoped<IManagedElsaProvisioningProgressReader, BlockerStaleProgressReader>();
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        using var owner = app.CreateTrustedWorkspaceClient("managed-progress-blocker-owner");
        var workspaceId = await owner.GetDefaultWorkspaceIdAsync();

        var response = await owner.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{Guid.NewGuid():D}/provisioning-progress");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"staleReason\":\"blocking-operation-stale\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"blockingOperationId\":\"{BlockerStaleProgressReader.BlockerId:D}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"blockingOperationStage\":\"request-accepted\"", json, StringComparison.Ordinal);
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
                    // The API fixture supplies a durable provider read model without
                    // starting Azure workers or running production authority preflight.
                    services.Remove(services.Single(descriptor =>
                        descriptor.ServiceType == typeof(IHostedService) &&
                        descriptor.ImplementationType == typeof(ManagedAzureProviderConfigurationValidator)));
                    services.AddSingleton(new AzureElsaInstanceProviderOptions
                    {
                        Enabled = true,
                        TemplateFingerprint = new string('b', 64),
                        ProviderScopeFingerprint = new string('a', 64),
                        SubscriptionId = "11111111-1111-1111-1111-111111111111",
                        ResourceGroupNamePrefix = "rg-correlation"
                    });
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

    private static (ElsaInstance Instance, ManagedElsaInstanceIdentity Identity, ElsaInstanceOperationSummary Operation)
        ReadyHealthyParked(string recoveryReason)
    {
        var instanceId = Guid.NewGuid();
        const string origin = "https://managed.example.test";
        var instance = ElsaInstance.Hydrate(
            instanceId, Guid.NewGuid(), Guid.NewGuid(), "Claims runtime", "claims-runtime",
            Intent(), ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy, 2,
            currentDeploymentReference: new ElsaCurrentDeploymentReference(
                "deployment-managed", "attempt-1", origin, true));
        var identity = new ManagedElsaInstanceIdentity(
            instance.OrganizationId, instance.WorkspaceId, instanceId,
            ElsaInstanceIdentityBinding.AudienceFor(instanceId),
            new Uri(ElsaInstanceIdentityBinding.CanonicalizeCallbackUri(origin)), 1, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var operation = new ElsaInstanceOperationSummary(
            Guid.NewGuid(), instanceId, ElsaInstanceOperationAction.UpdateIntent,
            ElsaInstanceOperationState.RecoveryRequired, 1, 1, now, now, null, null, null, null, null, null, null,
            RecoveryReason: recoveryReason, UpdatedAt: now, ReasonEnteredAt: now,
            RequiresHumanAt: ManagedElsaReasonCodeCatalog.RequiresHuman(recoveryReason, now, now) ? now : null);
        return (instance, identity, operation);
    }

    private static ManagedElsaInstanceOverviewResponse Overview(
        ElsaInstance instance,
        ManagedElsaInstanceIdentity identity,
        ElsaInstanceOperationSummary operation) =>
        ManagedElsaInstanceOverviewProjection.ToOverview(
            instance,
            instance.WorkspaceId,
            WorkspaceRole.Owner,
            canOpenPermission: true,
            identity,
            operation,
            lastOperation: null,
            new ManagedLifecycleOperationalHealthResult(
                ManagedLifecycleOperationalHealthStatus.Healthy,
                ManagedLifecycleOperationalHealthDiagnosticCodes.Healthy,
                new string('a', 64),
                DateTimeOffset.UtcNow,
                []),
            new ElsaInstanceCommercialGateDecision(true, "commercial.allowed", "allowed"),
            new ElsaInstanceCommercialGateDecision(true, "commercial.allowed", "allowed"));

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

    private static async Task<CorrelationInvalidDeleteTopology> SeedCorrelationInvalidDeleteTopologyAsync(
        ControlApiTestApplication app,
        string slug,
        bool retainWorkload,
        bool assignmentDeleted)
    {
        var customer = app.CreateTrustedWorkspaceClient($"{slug}-owner");
        var workspaceId = await customer.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        var created = await CreateCanonicalInstanceAsync(customer, workspaceId, slug);
        await MarkOperationSucceededAsync(app, created.Operation.Id);

        Guid organizationId;
        Guid assignmentId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            organizationId = await db.Workspaces.Where(x => x.Id == workspaceId)
                .Select(x => x.OrganizationId)
                .SingleAsync();
            var assignmentStore = (IAzureProviderResourceAssignmentStore)new AzureProviderOperationStore(db);
            var assignment = await assignmentStore.CreateOrGetAsync(
                new(
                    workspaceId,
                    organizationId,
                    created.Instance.InstanceId,
                    new string('a', 64),
                    "11111111-1111-1111-1111-111111111111",
                    "rg-correlation",
                    $"e{created.Instance.InstanceId:N}"[..16],
                    "westeurope"),
                DateTimeOffset.UtcNow);
            assignmentId = assignment.Id;
            var workloadResourceId = retainWorkload
                ? "/subscriptions/retained/resourceGroups/retained/providers/Microsoft.App/containerApps/retained"
                : null;
            var deletedAt = assignmentDeleted ? DateTimeOffset.UtcNow.UtcTicks : (long?)null;
            var state = assignmentDeleted
                ? AzureProviderAssignmentState.Deleted.ToString()
                : AzureProviderAssignmentState.Active.ToString();
            var lastOperationId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE AzureProviderResourceAssignments
                SET State = {state},
                    LastOperationId = {lastOperationId},
                    WorkloadResourceId = {workloadResourceId},
                    DeletedAt = {deletedAt}
                WHERE Id = {assignmentId}
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ElsaInstances
                SET PlacementAssignmentId = {assignmentId.ToString("D")},
                    ObservedLifecycle = {ElsaObservedLifecycle.Unknown.ToString()},
                    Version = Version + 1
                WHERE Id = {created.Instance.InstanceId}
                """);
        }

        using var confirmationResponse = await customer.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations", null);
        var confirmation = await confirmationResponse.Content
            .ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);
        var currentEtag = $"\"{created.Instance.Version + 1}\"";
        using var deletion = await SendDeleteAsync(
            customer, workspaceId, created.Instance.InstanceId, currentEtag, $"{slug}-delete",
            confirmation!.ConfirmationId);
        var deletionText = await deletion.Content.ReadAsStringAsync();
        Assert.True(deletion.StatusCode == HttpStatusCode.Accepted, deletionText);
        var deletionBody = (await deletion.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteAcceptedResponse>())!;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ElsaInstanceOperations
                SET State = {ElsaInstanceOperationState.RecoveryRequired.ToString()},
                    FailureCode = {"deletion.provider-correlation-invalid"},
                    DeletionDiagnosticCode = {"deletion.provider-correlation-invalid"},
                    CompletedAt = NULL
                WHERE Id = {deletionBody.OperationId}
                """);
        }

        int instanceVersion;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT Version FROM ElsaInstances WHERE Id = @id";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@id";
            parameter.Value = created.Instance.InstanceId;
            command.Parameters.Add(parameter);
            instanceVersion = Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        return new(workspaceId, created.Instance.InstanceId, deletionBody.OperationId, instanceVersion);
    }

    private sealed record CorrelationInvalidDeleteTopology(
        Guid WorkspaceId,
        Guid InstanceId,
        Guid OperationId,
        int InstanceVersion);

    private static async Task ParkApplyReleaseForRecoverAsync(
        ControlApiTestApplication app,
        HttpClient owner,
        Guid workspaceId,
        ManagedElsaInstanceAcceptedResponse subject,
        string suffix)
    {
        using var apply = await owner.SendAsync(CustomerMutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{subject.Instance.InstanceId:D}/apply-release",
            subject.Instance.ETag,
            $"{suffix}-apply",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        Assert.Equal(HttpStatusCode.Accepted, apply.StatusCode);
        var applied = await apply.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
        await ParkRecoveryRequiredAsync(app, applied!.OperationId, suffix);
    }

    private static async Task ParkRecoveryRequiredAsync(
        ControlApiTestApplication app,
        Guid operationId,
        string suffix)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var evidence = $"https://evidence.example/retry/delete-after-recover-{suffix}";
        var digest = "sha256:" + new string('a', 64);
        var retrySafe = ElsaInstanceProviderReconciliationService.RetrySafeCode;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ElsaInstanceOperations
            SET State = {ElsaInstanceOperationState.RecoveryRequired.ToString()},
                FailureCode = {retrySafe},
                ReconciliationRetryEvidenceReference = {evidence},
                ReconciliationRetryEvidenceDigest = {digest},
                CompletedAt = NULL
            WHERE Id = {operationId}
            """);
    }

    private static async Task AssertRecordsMutationAsync(
        ControlApiTestApplication app,
        Guid instanceId,
        Func<Task> mutate)
    {
        var versionBefore = await ReadInstanceVersionAsync(app, instanceId);
        var operationsBefore = await CountOperationsForInstanceAsync(app, instanceId);
        await mutate();
        var operationsAfter = await CountOperationsForInstanceAsync(app, instanceId);
        var stamped = await HasRecoveryStampAtOrAfterAsync(app, instanceId, versionBefore);
        Assert.True(
            operationsAfter > operationsBefore || stamped,
            "Mutating instance route must write an operation row or stamp RecoveryExpectedVersion.");
    }

    private static async Task<bool> HasRecoveryStampAtOrAfterAsync(
        ControlApiTestApplication app,
        Guid instanceId,
        int version)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM ElsaInstanceOperations
            WHERE InstanceId = @instanceId AND RecoveryExpectedVersion >= @version
            """;
        var instance = command.CreateParameter();
        instance.ParameterName = "@instanceId";
        instance.Value = instanceId;
        command.Parameters.Add(instance);
        var versionParameter = command.CreateParameter();
        versionParameter.ParameterName = "@version";
        versionParameter.Value = version;
        command.Parameters.Add(versionParameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static async Task InvokeOperationsActionAsync(
        ControlApiTestApplication app,
        HttpClient owner,
        Guid workspaceId,
        ManagedElsaInstanceAcceptedResponse subject,
        ElsaInstanceOperationAction action)
    {
        var instanceId = subject.Instance.InstanceId;
        using var current = await owner.GetAsync($"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}");
        current.EnsureSuccessStatusCode();
        var etag = current.Headers.ETag!.Tag;
        ManagedElsaInstanceOperationRequest body = action switch
        {
            ElsaInstanceOperationAction.ApproveMinorUpgrade => new(
                action,
                Intent: Intent() with
                {
                    Release = new ElsaReleaseIntent("valence-runtime", "3.8", requestedVersion: "3.8.5", channel: "stable")
                }),
            ElsaInstanceOperationAction.MajorMigration => new(
                action,
                Intent: Intent() with
                {
                    Release = new ElsaReleaseIntent("valence-runtime", "3.9", requestedVersion: "3.9.0", channel: "stable")
                }),
            ElsaInstanceOperationAction.Delete => await DeleteOperationRequestAsync(owner, workspaceId, instanceId),
            _ => new(action)
        };

        if (action == ElsaInstanceOperationAction.Start)
            await MarkInstanceStoppedAsync(app, instanceId);
        if (action == ElsaInstanceOperationAction.Retry)
            await MarkInstanceFailedAsync(app, instanceId);

        using var response = await SendOperationAsync(
            owner, workspaceId, instanceId, etag!, $"mutation-guard-ops-{action}", body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{action}: {text}");
    }

    private static async Task<ManagedElsaInstanceOperationRequest> DeleteOperationRequestAsync(
        HttpClient owner,
        Guid workspaceId,
        Guid instanceId)
    {
        using var confirmation = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/delete-confirmations", null);
        var body = await confirmation.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        return new(ElsaInstanceOperationAction.Delete, DeleteConfirmationId: body!.ConfirmationId);
    }

    private static async Task MarkInstanceFailedAsync(ControlApiTestApplication app, Guid instanceId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ElsaInstances
            SET ObservedLifecycle = {ElsaObservedLifecycle.Failed.ToString()},
                Health = {ElsaInstanceHealth.Degraded.ToString()}
            WHERE Id = {instanceId}
            """);
    }

    private static async Task MarkInstanceStoppedAsync(ControlApiTestApplication app, Guid instanceId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ElsaInstances
            SET DesiredLifecycle = {ElsaDesiredLifecycle.Stopped.ToString()},
                ObservedLifecycle = {ElsaObservedLifecycle.Stopped.ToString()},
                Health = {ElsaInstanceHealth.Healthy.ToString()}
            WHERE Id = {instanceId}
            """);
    }

    private static int ReadETagVersion(string etag)
    {
        var value = etag.Trim();
        if (value.Length >= 3 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1];
        return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> ReadInstanceVersionAsync(ControlApiTestApplication app, Guid instanceId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT Version FROM ElsaInstances WHERE Id = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = instanceId;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int?> ReadRecoveryExpectedVersionAsync(ControlApiTestApplication app, Guid operationId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT RecoveryExpectedVersion FROM ElsaInstanceOperations WHERE Id = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = operationId;
        command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ExtractProblemTitle(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("title", out var title)
            ? title.GetString() ?? string.Empty
            : string.Empty;
    }

    private static async Task PersistParkedOperationDiagnosticsAsync(
        ControlApiTestApplication app,
        Guid instanceId,
        Guid operationId,
        string reasonCode,
        string failureCode)
    {
        var ticks = DateTimeOffset.UtcNow.UtcTicks;
        const string azureInventory =
            "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-customer/providers/Microsoft.App/containerApps/app";
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ElsaInstances
            SET ObservedLifecycle = {ElsaObservedLifecycle.Unknown.ToString()}
            WHERE Id = {instanceId}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ElsaInstanceOperations
            SET State = {ElsaInstanceOperationState.RecoveryRequired.ToString()},
                CompletedAt = NULL,
                FailureCode = {failureCode},
                ReconciliationDiagnosticCode = {reasonCode},
                DeletionDiagnosticCode = {reasonCode},
                DeletionEvidenceReference = {azureInventory},
                ReasonEnteredAt = {ticks},
                RequiresHumanAt = {ticks}
            WHERE Id = {operationId}
            """);
    }

    private static async Task ParkHumanRequiredCreateAsync(
        ControlApiTestApplication app,
        Guid instanceId,
        Guid operationId,
        string reasonCode)
    {
        var ticks = DateTimeOffset.UtcNow.UtcTicks;
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ElsaInstances
            SET ObservedLifecycle = {ElsaObservedLifecycle.Unknown.ToString()}
            WHERE Id = {instanceId}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ElsaInstanceOperations
            SET State = {ElsaInstanceOperationState.RecoveryRequired.ToString()},
                CompletedAt = NULL,
                FailureCode = {reasonCode},
                ReconciliationDiagnosticCode = {reasonCode},
                ReasonEnteredAt = {ticks},
                RequiresHumanAt = {ticks}
            WHERE Id = {operationId}
            """);
    }

    private static async Task ParkHumanRequiredDeleteAsync(
        ControlApiTestApplication app,
        Guid instanceId,
        Guid operationId,
        string reasonCode)
    {
        var ticks = DateTimeOffset.UtcNow.UtcTicks;
        const string azureInventory =
            "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-customer/providers/Microsoft.App/containerApps/app";
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ElsaInstances
            SET DesiredLifecycle = {ElsaDesiredLifecycle.Deleting.ToString()},
                ObservedLifecycle = {ElsaObservedLifecycle.Deleting.ToString()}
            WHERE Id = {instanceId}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ElsaInstanceOperations
            SET State = {ElsaInstanceOperationState.RecoveryRequired.ToString()},
                CompletedAt = NULL,
                FailureCode = {"provider.private-secret-value"},
                ReconciliationDiagnosticCode = {reasonCode},
                DeletionDiagnosticCode = {reasonCode},
                DeletionEvidenceReference = {azureInventory},
                ReasonEnteredAt = {ticks},
                RequiresHumanAt = {ticks}
            WHERE Id = {operationId}
            """);
    }

    private static async Task SeedAlertOutboxAsync(
        ControlApiTestApplication app,
        Guid workspaceId,
        Guid instanceId,
        Guid operationId,
        string delivery)
    {
        if (delivery == "disabled")
            return;

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var organizationId = await db.Workspaces.Where(x => x.Id == workspaceId)
            .Select(x => x.OrganizationId)
            .SingleAsync();
        var attempts = delivery == "unacknowledged" ? 3 : 0;
        var createdAt = DateTimeOffset.UtcNow.UtcTicks;
        var id = Guid.NewGuid();
        var dedupe = new string('a', 64);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ElsaInstanceRecoveryRequiredAlertOutbox
            (Id, OrganizationId, WorkspaceId, InstanceId, OperationId, AttemptNumber, DedupeIdentity, CreatedAt, DeliveryAttempts)
            VALUES ({id}, {organizationId}, {workspaceId}, {instanceId}, {operationId}, {1}, {dedupe}, {createdAt}, {attempts})
            """);
    }

    private static async Task<ElsaObservedLifecycle> ReadStoredObservedLifecycleAsync(
        ControlApiTestApplication app,
        Guid instanceId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT CAST(ObservedLifecycle AS TEXT) FROM ElsaInstances WHERE Id = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = instanceId;
        command.Parameters.Add(parameter);
        var value = Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        return Enum.Parse<ElsaObservedLifecycle>(value!);
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

    private static async Task<int> CountOperationsForInstanceAsync(ControlApiTestApplication app, Guid instanceId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ElsaInstanceOperations WHERE InstanceId = @instanceId";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@instanceId";
        parameter.Value = instanceId;
        command.Parameters.Add(parameter);
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

    private static async Task<ManagedElsaInstanceAcceptedResponse> CreateReadyInstanceAsync(
        ControlApiTestApplication app,
        HttpClient client,
        Guid workspaceId,
        string slug,
        ElsaInstanceIntent? intent = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/instances")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceCreateRequest("Claims runtime", slug, intent ?? Intent()),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", $"create-{slug}");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>())!;
        await MarkOperationSucceededAsync(app, created.Operation.Id);
        await MarkInstanceReadyAsync(app, created.Instance.InstanceId);
        return created;
    }

    private static HttpRequestMessage CustomerMutation(
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

        public Task<ElsaInstanceOperationPage> ListOperationsAsync(
            Guid workspaceId, Guid instanceId, int page, int pageSize,
            CancellationToken cancellationToken = default, Guid organizationId = default) =>
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

        public Task<IReadOnlyList<ElsaInstanceAuditEventSummary>> ListAuditAsync(
            Guid workspaceId, Guid instanceId, CancellationToken cancellationToken = default,
            int? limit = null, Guid organizationId = default) =>
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

    private sealed class TopologyChangedProgressReader : IManagedElsaProvisioningProgressReader
    {
        public Task<ManagedElsaProvisioningProgress?> ReadAsync(
            Guid workspaceId,
            Guid instanceId,
            CancellationToken cancellationToken = default) =>
            throw new ElsaInstanceLifecycleTopologyChangedException();
    }

    private sealed class BlockerStaleProgressReader : IManagedElsaProvisioningProgressReader
    {
        public static readonly Guid BlockerId = Guid.Parse("77777777-7777-7777-7777-777777777777");

        public Task<ManagedElsaProvisioningProgress?> ReadAsync(
            Guid workspaceId,
            Guid instanceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ManagedElsaProvisioningProgress?>(new(
                ManagedElsaProvisioningProgressStates.Stale,
                "azure",
                ManagedElsaProvisioningProgressStages.RequestAccepted,
                DateTimeOffset.Parse("2026-09-21T10:00:00Z"),
                DateTimeOffset.Parse("2026-09-21T10:10:00Z"),
                null,
                ManagedElsaProvisioningProgressDiagnostics.RequiresAttention,
                [],
                [],
                BlockerId,
                ManagedElsaProvisioningProgressStages.RequestAccepted,
                ManagedElsaProvisioningProgressStaleReasons.BlockingOperationStale));
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
