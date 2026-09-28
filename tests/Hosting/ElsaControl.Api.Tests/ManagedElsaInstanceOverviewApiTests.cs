using System.Net;
using System.Net.Http.Json;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElsaControl.Api.Tests;

public sealed class ManagedElsaInstanceOverviewApiTests : IClassFixture<ManagedElsaInstanceOverviewApiTests.Fixture>
{
    private static readonly string[] NeverIncludedFields =
    [
        "desiredStateRevisionId",
        "resolvedPlan",
        "planReference",
        "currentDeployment",
        "deploymentRunId",
        "identityBinding",
        "identityBindingState",
        "audience",
        "redirectUri",
        "links",
        "dedupeIdentity",
        "actorAccountId",
        "operatorSubject",
        "requestKeyHash",
        "migrationId",
        "revisionId",
        "runId",
        "planId"
    ];

    private readonly Fixture _fixture;

    public ManagedElsaInstanceOverviewApiTests(Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Overview_is_customer_shaped_with_a_strong_etag_and_omits_never_included_fields()
    {
        var app = await PrepareApplicationAsync();
        var client = app.CreateTrustedWorkspaceClient("overview-owner");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, client, "overview-runtime");

        using var response = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/overview");
        var json = await response.Content.ReadAsStringAsync();
        var overview = JsonSerializer.Deserialize<ManagedElsaInstanceOverviewResponse>(
            json, ControlApiTestApplication.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"\"{created.Instance.Version}\"", response.Headers.ETag?.ToString());
        Assert.NotNull(overview);
        Assert.Equal(created.Instance.InstanceId, overview!.Summary.InstanceId);
        Assert.Equal("overview-runtime", overview.Summary.Slug);
        Assert.Equal(created.Instance.Version, overview.Summary.Version);
        Assert.NotEqual(default, overview.Summary.CreatedAt);
        Assert.False(overview.AllowedActions.Restart.Allowed is false && overview.AllowedActions.Restart.ReasonCode is null);
        Assert.True(overview.AllowedActions.Restart.Allowed);
        Assert.True(overview.AllowedActions.ApplyRelease.Allowed);
        Assert.False(overview.AllowedActions.Open.Allowed);
        Assert.Equal("handoff-unavailable", overview.AllowedActions.Open.ReasonCode);
        AssertNoNeverIncludedFields(json);
    }

    [Fact]
    public async Task Customer_overview_dtos_never_serialize_forbidden_fields()
    {
        var overview = new ManagedElsaInstanceOverviewResponse(
            new ManagedElsaInstanceOverviewSummaryResponse(
                Guid.NewGuid(), "Runtime", "runtime", ElsaDesiredLifecycle.Running,
                ElsaObservedLifecycle.Ready, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 2, false, "not-authorized"),
            new ManagedElsaInstanceOverviewHealthResponse(
                ManagedLifecycleOperationalHealthStatus.Healthy,
                "managed.lifecycle.healthy",
                DateTimeOffset.UtcNow,
                [new ManagedElsaInstanceOverviewAlertResponse("managed.lifecycle.healthy", ManagedLifecycleOperationalHealthAlertSeverity.Warning)]),
            new ManagedElsaInstanceOverviewReleaseResponse("valence-runtime", "3.8", "3.8.4", "stable"),
            new ManagedElsaInstanceOverviewPolicyResponse("automatic-within-minor", "explicit-approval", "explicit-migration"),
            [new ManagedElsaInstanceOverviewComponentResponse("server", "sha256:" + new string('a', 64))],
            new ManagedElsaInstanceOverviewActiveOperationResponse(
                OperationId: Guid.NewGuid(),
                Action: ElsaInstanceOperationAction.Restart,
                State: ElsaInstanceOperationState.Running,
                AcceptedAt: DateTimeOffset.UtcNow,
                StartedAt: DateTimeOffset.UtcNow)
            {
                Progress = new ManagedElsaInstanceOverviewProgressResponse()
            },
            new ManagedElsaInstanceOverviewLastOperationResponse(
                ElsaInstanceOperationAction.Create, ElsaInstanceOperationState.Succeeded, DateTimeOffset.UtcNow, null),
            new ManagedElsaInstanceOverviewAllowedActionsResponse(
                new ManagedElsaInstanceOverviewActionDecisionResponse(true, null),
                new ManagedElsaInstanceOverviewActionDecisionResponse(false, "instance.permission-required"),
                new ManagedElsaInstanceOverviewActionDecisionResponse(false, "not-authorized")));
        var activity = new ManagedElsaInstanceActivityResponse(
            [
                new ManagedElsaInstanceActivityItemResponse(
                    3, "lifecycle.accepted", DateTimeOffset.UtcNow, ElsaInstanceOperationAction.Create,
                    "Pending", "Provisioning", "managed.lifecycle.healthy", "customer")
            ],
            false);
        var releases = new ManagedElsaInstanceAvailableReleasesResponse(
            [new ManagedElsaInstanceAvailableReleaseResponse("3.8", "3.8.5", "stable", false, "patch")]);
        var accepted = new ManagedElsaInstanceOverviewOperationResponse(
            Guid.NewGuid(), ElsaInstanceOperationAction.Restart, ElsaInstanceOperationState.Accepted, DateTimeOffset.UtcNow);

        foreach (var payload in new object[] { overview, activity, releases, accepted })
            AssertNoNeverIncludedFields(JsonSerializer.Serialize(payload, ControlApiTestApplication.JsonOptions));
    }

    [Fact]
    public async Task Overview_authorization_conceals_cross_workspace_and_organization_instances()
    {
        var app = await PrepareApplicationAsync();
        var owner = app.CreateTrustedWorkspaceClient("overview-scope-owner");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, owner, "overview-scope-runtime");
        var instanceId = created.Instance.InstanceId;
        var path = $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/overview";

        await app.AddWorkspaceMemberAsync(workspaceId, "overview-scope-reader", WorkspaceRole.Reader);
        using var reader = app.CreateTrustedWorkspaceClient("overview-scope-reader");
        using var readerOverview = await reader.GetAsync(path);
        var readerBody = await readerOverview.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewResponse>();

        var other = app.CreateTrustedWorkspaceClient("overview-scope-other-organization");
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
        using var unknown = await owner.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{Guid.NewGuid():D}/overview");
        using var crossWorkspaceOverview = await other.GetAsync(
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{instanceId:D}/overview");
        using var crossWorkspaceReleases = await other.GetAsync(
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{instanceId:D}/available-releases");
        using var crossWorkspaceActivity = await other.GetAsync(
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{instanceId:D}/activity");

        Assert.Equal(HttpStatusCode.OK, readerOverview.StatusCode);
        Assert.NotNull(readerBody);
        Assert.False(readerBody!.AllowedActions.Restart.Allowed);
        Assert.Equal("instance.permission-required", readerBody.AllowedActions.Restart.ReasonCode);
        Assert.False(readerBody.AllowedActions.ApplyRelease.Allowed);
        Assert.Equal("instance.permission-required", readerBody.AllowedActions.ApplyRelease.ReasonCode);
        Assert.False(readerBody.AllowedActions.Open.Allowed);
        Assert.Equal("not-authorized", readerBody.AllowedActions.Open.ReasonCode);
        Assert.NotEqual(Guid.Empty, ownerOrganizationId);
        Assert.NotEqual(ownerOrganizationId, otherOrganizationId);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossWorkspaceOverview.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossWorkspaceReleases.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossWorkspaceActivity.StatusCode);
    }

    [Fact]
    public async Task Available_releases_lists_same_major_supported_paid_rows_newest_first()
    {
        var app = await PrepareApplicationAsync();
        var client = app.CreateTrustedWorkspaceClient("overview-releases-owner");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, client, "overview-releases-runtime");
        _fixture.ReleaseCatalog.SetEntries(
        [
            CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid", 'a'),
            CatalogEntry("valence-runtime", "3.8", "3.8.5", "stable", "combined", "supported", "paid", 'b'),
            CatalogEntry("valence-runtime", "3.9", "3.9.0", "stable", "combined", "supported", "paid", 'c'),
            CatalogEntry("valence-runtime", "4.0", "4.0.0", "stable", "combined", "supported", "paid", 'd'),
            CatalogEntry("valence-runtime", "3.8", "3.8.6-preview.1", "preview", "combined", "preview", "paid", 'e')
        ]);

        var releases = await client.GetControlJsonAsync<ManagedElsaInstanceAvailableReleasesResponse>(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/available-releases");

        Assert.NotNull(releases);
        Assert.Equal(["3.9.0", "3.8.5", "3.8.4"], releases!.Items.Select(item => item.Version).ToArray());
        Assert.Equal("minor", Assert.Single(releases.Items, item => item.Version == "3.9.0").ChangeKind);
        Assert.Equal("patch", Assert.Single(releases.Items, item => item.Version == "3.8.5").ChangeKind);
        Assert.DoesNotContain(releases.Items, item => item.Version.StartsWith("4.", StringComparison.Ordinal));
        Assert.DoesNotContain(releases.Items, item => item.Version.Contains("preview", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Activity_reuses_audit_ordering_and_rejects_invalid_limits()
    {
        var app = await PrepareApplicationAsync();
        var client = app.CreateTrustedWorkspaceClient("overview-activity-owner");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, client, "overview-activity-runtime");
        await SeedReconcileAsync(app, client, workspaceId, created, "overview-activity-extra");

        var defaultPage = await client.GetControlJsonAsync<ManagedElsaInstanceActivityResponse>(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/activity");
        var limited = await client.GetControlJsonAsync<ManagedElsaInstanceActivityResponse>(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/activity?limit=1");
        using var zero = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/activity?limit=0");
        using var tooLarge = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/activity?limit=101");

        Assert.NotNull(defaultPage);
        Assert.True(defaultPage!.Items.Count >= 1);
        Assert.Single(limited!.Items);
        Assert.True(limited.HasMore);
        Assert.Equal(defaultPage.Items[0].Sequence, limited.Items[0].Sequence);
        Assert.Contains(defaultPage.Items, item => item.ActorKind is "customer" or "system" or "support");
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Contains("instance.activity-limit-invalid", await zero.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        AssertNoNeverIncludedFields(JsonSerializer.Serialize(defaultPage, ControlApiTestApplication.JsonOptions));
    }

    [Fact]
    public async Task Mutations_require_idempotency_and_strong_etags_and_deny_readers()
    {
        var app = await PrepareApplicationAsync();
        var owner = app.CreateTrustedWorkspaceClient("overview-mutate-owner");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, owner, "overview-mutate-runtime");
        await app.AddWorkspaceMemberAsync(workspaceId, "overview-mutate-reader", WorkspaceRole.Reader);
        using var reader = app.CreateTrustedWorkspaceClient("overview-mutate-reader");
        var restartPath = $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/restart";
        var applyPath = $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/apply-release";

        using var missingKey = await owner.SendAsync(Mutation(HttpMethod.Post, restartPath, created.Instance.ETag, null));
        using var missingMatch = await owner.SendAsync(Mutation(HttpMethod.Post, restartPath, null, "overview-restart"));
        using var stale = await owner.SendAsync(Mutation(HttpMethod.Post, restartPath, "\"999\"", "overview-stale"));
        using var readerRestart = await reader.SendAsync(Mutation(HttpMethod.Post, restartPath, created.Instance.ETag, "overview-reader-restart"));
        using var readerApply = await reader.SendAsync(Mutation(
            HttpMethod.Post, applyPath, created.Instance.ETag, "overview-reader-apply",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        using var unknownRelease = await owner.SendAsync(Mutation(
            HttpMethod.Post, applyPath, created.Instance.ETag, "overview-unknown-release",
            new ManagedElsaInstanceApplyReleaseRequest("9.9.9")));

        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        Assert.Equal((HttpStatusCode)428, missingMatch.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, readerRestart.StatusCode);
        Assert.Contains("Workspace role does not allow", await readerRestart.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, readerApply.StatusCode);
        Assert.Contains("Workspace role does not allow", await readerApply.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownRelease.StatusCode);
        Assert.Contains("instance.release-not-available", await unknownRelease.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var readerOverview = await reader.GetControlJsonAsync<ManagedElsaInstanceOverviewResponse>(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/overview");
        Assert.False(readerOverview!.AllowedActions.Restart.Allowed);
        Assert.Equal("instance.permission-required", readerOverview.AllowedActions.Restart.ReasonCode);
    }

    [Fact]
    public async Task Restart_and_apply_replay_the_same_operation_and_conflict_on_a_different_request()
    {
        var app = await PrepareApplicationAsync();
        var client = app.CreateTrustedWorkspaceClient("overview-idempotency-owner");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, client, "overview-idempotency-runtime");
        _fixture.ReleaseCatalog.SetEntries(
        [
            CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid", 'a'),
            CatalogEntry("valence-runtime", "3.8", "3.8.5", "stable", "combined", "supported", "paid", 'b'),
            CatalogEntry("valence-runtime", "3.8", "3.8.6", "stable", "combined", "supported", "paid", 'c'),
            CatalogEntry("valence-runtime", "3.9", "3.9.0", "stable", "combined", "supported", "paid", 'd')
        ]);
        var applyPath = $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/apply-release";

        using var first = await client.SendAsync(Mutation(
            HttpMethod.Post, applyPath, created.Instance.ETag, "overview-apply-key",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        var firstBody = await first.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
        using var replay = await client.SendAsync(Mutation(
            HttpMethod.Post, applyPath, created.Instance.ETag, "overview-apply-key",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        var replayBody = await replay.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
        using var conflict = await client.SendAsync(Mutation(
            HttpMethod.Post, applyPath, created.Instance.ETag, "overview-apply-key",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.6")));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.NotNull(firstBody);
        Assert.Equal(ElsaInstanceOperationAction.UpdateIntent, firstBody!.Action);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(firstBody.OperationId, replayBody!.OperationId);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("instance.idempotency-conflict", await conflict.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        AssertNoNeverIncludedFields(JsonSerializer.Serialize(firstBody, ControlApiTestApplication.JsonOptions));
    }

    [Fact]
    public async Task Source_admin_may_restart_and_cross_organization_mutations_are_concealed()
    {
        var app = await PrepareApplicationAsync();
        var owner = app.CreateTrustedWorkspaceClient("overview-source-admin-owner");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, owner, "overview-source-admin-runtime");
        await app.AddWorkspaceMemberAsync(workspaceId, "overview-source-admin", WorkspaceRole.SourceAdmin);
        using var sourceAdmin = app.CreateTrustedWorkspaceClient("overview-source-admin");
        var other = app.CreateTrustedWorkspaceClient("overview-source-admin-other-organization");
        var otherWorkspaceId = await other.GetDefaultWorkspaceIdAsync();

        var restartPath = $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/restart";
        using var restart = await sourceAdmin.SendAsync(Mutation(
            HttpMethod.Post, restartPath, created.Instance.ETag, "overview-source-admin-restart"));
        var body = await restart.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
        using var replay = await sourceAdmin.SendAsync(Mutation(
            HttpMethod.Post, restartPath, created.Instance.ETag, "overview-source-admin-restart"));
        var replayBody = await replay.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
        using var conflict = await sourceAdmin.SendAsync(Mutation(
            HttpMethod.Post, restartPath, "\"999\"", "overview-source-admin-restart"));
        using var crossOrg = await other.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{created.Instance.InstanceId:D}/restart",
            created.Instance.ETag,
            "overview-cross-org-restart"));

        Assert.Equal(HttpStatusCode.Accepted, restart.StatusCode);
        Assert.Equal(ElsaInstanceOperationAction.Restart, body!.Action);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(body.OperationId, replayBody!.OperationId);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("instance.idempotency-conflict", await conflict.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, crossOrg.StatusCode);
    }

    [Fact]
    public async Task Configured_bff_token_can_read_overview_and_is_still_denied_on_operator_routes()
    {
        var catalog = new MutableReleaseCatalogStore();
        catalog.SetEntries([CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid")]);
        await using var app = new ControlApiTestApplication(
            new Dictionary<string, string?>
            {
                [$"{CloudBffOptions.ConfigurationSection}:Enabled"] = "true",
                [$"{CloudBffOptions.ConfigurationSection}:ClientId"] = "elsa-cloud-lovable-bff",
                [$"{CloudBffOptions.ConfigurationSection}:Scope"] = CloudBffDefaults.DefaultScope
            },
            services =>
            {
                services.AddSingleton<IEngineProvisioningModule, TestProvisioningModule>();
                services.RemoveAll<IGovernedReleaseCatalogStore>();
                services.AddSingleton<IGovernedReleaseCatalogStore>(catalog);
            });
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = app.CreateControlIdentityClient(
            subject: "overview-bff-user",
            claims: new Dictionary<string, string>
            {
                ["azp"] = "elsa-cloud-lovable-bff",
                ["scp"] = CloudBffDefaults.DefaultScope
            });
        var workspaceId = (await client.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces"))!
            .Workspaces.Single().Id;
        await EnableManagedHostingAsync(app, workspaceId);
        using var create = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId:D}/instances")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceCreateRequest("BFF overview", "bff-overview-runtime", Intent()),
                options: ControlApiTestApplication.JsonOptions)
        };
        create.Headers.Add("Idempotency-Key", "bff-overview-create");
        using var createdResponse = await client.SendAsync(create);
        var created = await createdResponse.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
        Assert.Equal(HttpStatusCode.Accepted, createdResponse.StatusCode);

        using var overview = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created!.Instance.InstanceId:D}/overview");
        using var operatorDetail = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}");
        using var operatorAudit = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/audit");

        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        var body = await overview.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewResponse>();
        Assert.Equal(created.Instance.InstanceId, body!.Summary.InstanceId);
        Assert.True(overview.Headers.ETag?.ToString()?.StartsWith('\"'));
        Assert.Equal(HttpStatusCode.Forbidden, operatorDetail.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, operatorAudit.StatusCode);
        Assert.Contains("cloud-bff.denied", await operatorDetail.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        AssertNoNeverIncludedFields(await overview.Content.ReadAsStringAsync());
    }

    private async Task<ControlApiTestApplication> PrepareApplicationAsync()
    {
        _fixture.ReleaseCatalog.SetEntries(
            [CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid")]);
        await _fixture.Application.SeedAsync(_ => Task.CompletedTask);
        return _fixture.Application;
    }

    private static async Task<(Guid WorkspaceId, ManagedElsaInstanceAcceptedResponse Created)> CreateReadyInstanceAsync(
        ControlApiTestApplication app,
        HttpClient client,
        string slug)
    {
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId:D}/instances")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceCreateRequest("Claims runtime", slug, Intent()),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", $"create-{slug}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>())!;
        await MarkOperationSucceededAsync(app, created.Operation.Id);
        await MarkInstanceReadyAsync(app, created.Instance.InstanceId);
        return (workspaceId, created);
    }

    private static async Task SeedReconcileAsync(
        ControlApiTestApplication app,
        HttpClient client,
        Guid workspaceId,
        ManagedElsaInstanceAcceptedResponse created,
        string key)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/operations")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceOperationRequest(ElsaInstanceOperationAction.Reconcile),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.TryAddWithoutValidation("If-Match", created.Instance.ETag);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>();
        await MarkOperationSucceededAsync(app, body!.Operation.Id);
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

    private static async Task EnableManagedHostingAsync(ControlApiTestApplication app, Guid workspaceId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var organizationId = await db.Workspaces.Where(x => x.Id == workspaceId)
            .Select(x => x.OrganizationId).SingleAsync();
        if (!await db.OrganizationEntitlementSnapshots.AnyAsync(x => x.OrganizationId == organizationId))
        {
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

    private static void AssertNoNeverIncludedFields(string json)
    {
        using var document = JsonDocument.Parse(json);
        AssertNoNeverIncludedFields(document.RootElement);
    }

    private static void AssertNoNeverIncludedFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                AssertNoNeverIncludedFields(item);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in element.EnumerateObject())
        {
            if (NeverIncludedFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                Assert.Fail($"Customer DTO serialized forbidden field '{property.Name}'.");
            if (property.NameEquals("summary") && property.Value.ValueKind == JsonValueKind.String)
                Assert.Fail("Customer activity DTO serialized free-text summary.");
            AssertNoNeverIncludedFields(property.Value);
        }
    }

    private static ElsaInstanceIntent Intent() => new(
        new ElsaReleaseIntent("valence-runtime", "3.8", channel: "stable"),
        new ElsaApplicationIntent("combined", "starter",
            new Dictionary<string, ElsaFeatureOverride> { ["replicas"] = ElsaFeatureOverride.FromNumber(3) },
            "approved"),
        new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed"));

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

    public sealed class Fixture : IAsyncLifetime
    {
        public Fixture()
        {
            ReleaseCatalog = new MutableReleaseCatalogStore();
            Application = new ControlApiTestApplication(configureServices: services =>
            {
                services.AddSingleton<IEngineProvisioningModule, TestProvisioningModule>();
                services.RemoveAll<IGovernedReleaseCatalogStore>();
                services.AddSingleton<IGovernedReleaseCatalogStore>(ReleaseCatalog);
            });
        }

        internal ControlApiTestApplication Application { get; }

        internal MutableReleaseCatalogStore ReleaseCatalog { get; }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync() => await ((IAsyncDisposable)Application).DisposeAsync();
    }

    internal sealed class MutableReleaseCatalogStore : IGovernedReleaseCatalogStore
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

    private sealed class TestProvisioningModule : IEngineProvisioningModule
    {
        public string Id => "test";
        public string DisplayName => "Test provider";
    }
}
