using System.Collections.Frozen;
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
    private static readonly FrozenDictionary<string, FrozenSet<string>> ExactCustomerDtoProperties =
        new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
        {
            ["overview"] = Frozen(["summary", "health", "release", "policy", "components", "activeOperation", "lastOperation", "allowedActions"]),
            ["overview.summary"] = Frozen(["instanceId", "name", "slug", "desiredLifecycle", "observedLifecycle", "createdAt", "updatedAt", "version", "canOpen", "unavailableReason"]),
            ["overview.health"] = Frozen(["status", "diagnosticCode", "evaluatedAt", "alerts"]),
            ["overview.health.alerts"] = Frozen(["code", "severity"]),
            ["overview.release"] = Frozen(["distributionId", "releaseLine", "version", "channel"]),
            ["overview.policy"] = Frozen(["patchUpdates", "minorUpdates", "majorMigrations"]),
            ["overview.components"] = Frozen(["componentId", "digest"]),
            ["overview.activeOperation"] = Frozen(["operationId", "action", "state", "acceptedAt", "startedAt", "progress"]),
            ["overview.activeOperation.progress"] = Frozen(["phase", "attemptedStep", "attemptNumber", "attemptStartedAt"]),
            ["overview.lastOperation"] = Frozen(["action", "state", "completedAt", "failureCode"]),
            ["overview.allowedActions"] = Frozen(["restart", "applyRelease", "open"]),
            ["overview.allowedActions.restart"] = Frozen(["allowed", "reasonCode"]),
            ["overview.allowedActions.applyRelease"] = Frozen(["allowed", "reasonCode"]),
            ["overview.allowedActions.open"] = Frozen(["allowed", "reasonCode"]),
            ["activity"] = Frozen(["items", "hasMore"]),
            ["activity.items"] = Frozen(["sequence", "eventType", "occurredAt", "action", "priorState", "newState", "diagnosticCode", "actorKind"]),
            ["releases"] = Frozen(["items"]),
            ["releases.items"] = Frozen(["releaseLine", "version", "channel", "changeKind"]),
            ["accepted"] = Frozen(["operationId", "action", "state", "acceptedAt"])
        }.ToFrozenDictionary(StringComparer.Ordinal);

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
        AssertExactCustomerDtoShape(json, "overview");
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
            [new ManagedElsaInstanceAvailableReleaseResponse("3.8", "3.8.5", "stable", "patch")]);
        var accepted = new ManagedElsaInstanceOverviewOperationResponse(
            Guid.NewGuid(), ElsaInstanceOperationAction.Restart, ElsaInstanceOperationState.Accepted, DateTimeOffset.UtcNow);

        AssertExactCustomerDtoShape(JsonSerializer.Serialize(overview, ControlApiTestApplication.JsonOptions), "overview");
        AssertExactCustomerDtoShape(JsonSerializer.Serialize(activity, ControlApiTestApplication.JsonOptions), "activity");
        AssertExactCustomerDtoShape(JsonSerializer.Serialize(releases, ControlApiTestApplication.JsonOptions), "releases");
        AssertExactCustomerDtoShape(JsonSerializer.Serialize(accepted, ControlApiTestApplication.JsonOptions), "accepted");
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
        using var nonMember = await other.GetAsync(path);

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
        Assert.Equal(HttpStatusCode.Forbidden, nonMember.StatusCode);
    }

    [Fact]
    public async Task Available_releases_lists_same_major_supported_paid_rows_newest_first()
    {
        var app = await PrepareApplicationAsync();
        var client = app.CreateTrustedWorkspaceClient("overview-releases-owner");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, client, "overview-releases-runtime");
        _fixture.ReleaseCatalog.SetEntries(
        [
            CatalogEntry("valence-runtime", "3.8", "3.8.3", "stable", "combined", "supported", "paid", '0'),
            CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid", 'a'),
            CatalogEntry("valence-runtime", "3.8", "3.8.5", "stable", "combined", "supported", "paid", 'b'),
            CatalogEntry("valence-runtime", "3.9", "3.9.0", "stable", "combined", "supported", "paid", 'c'),
            CatalogEntry("valence-runtime", "3.7", "3.7.9", "stable", "combined", "supported", "paid", 'g'),
            CatalogEntry("valence-runtime", "4.0", "4.0.0", "stable", "combined", "supported", "paid", 'd'),
            CatalogEntry("valence-runtime", "3.8", "3.8.6-preview.1", "preview", "combined", "preview", "paid", 'e'),
            CatalogEntry("valence-runtime", "3.8", "3.8.7", "preview", "combined", "supported", "paid", 'f')
        ]);

        var releases = await client.GetControlJsonAsync<ManagedElsaInstanceAvailableReleasesResponse>(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/available-releases");

        Assert.NotNull(releases);
        Assert.Equal(["3.9.0", "3.8.5"], releases!.Items.Select(item => item.Version).ToArray());
        Assert.Equal("minor", Assert.Single(releases.Items, item => item.Version == "3.9.0").ChangeKind);
        Assert.Equal("patch", Assert.Single(releases.Items, item => item.Version == "3.8.5").ChangeKind);
        Assert.DoesNotContain(releases.Items, item => item.Version == "3.8.4");
        Assert.DoesNotContain(releases.Items, item => item.Version == "3.8.3");
        Assert.DoesNotContain(releases.Items, item => item.Version.StartsWith("3.7.", StringComparison.Ordinal));
        Assert.DoesNotContain(releases.Items, item => item.Version.StartsWith("4.", StringComparison.Ordinal));
        Assert.DoesNotContain(releases.Items, item => item.Version.Contains("preview", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(releases.Items, item => item.Version == "3.8.7");
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
        AssertExactCustomerDtoShape(JsonSerializer.Serialize(defaultPage, ControlApiTestApplication.JsonOptions), "activity");
    }

    [Fact]
    public void Activity_diagnostic_codes_use_an_explicit_allowlist()
    {
        Assert.Null(ManagedElsaInstanceOverviewEndpoints.CustomerActivityDiagnosticCode(
            ElsaInstanceProviderReconciliationService.ConvergedCode));
        Assert.Null(ManagedElsaInstanceOverviewEndpoints.CustomerActivityDiagnosticCode(
            ManagedLifecycleOperationalHealthDiagnosticCodes.Healthy));
        Assert.Null(ManagedElsaInstanceOverviewEndpoints.CustomerActivityDiagnosticCode("provider.internal.mystery"));
        Assert.Null(ManagedElsaInstanceOverviewEndpoints.CustomerActivityDiagnosticCode(null));
        Assert.Equal(
            ManagedLifecycleOperationalHealthDiagnosticCodes.OperationFailed,
            ManagedElsaInstanceOverviewEndpoints.CustomerActivityDiagnosticCode(
                ManagedLifecycleOperationalHealthDiagnosticCodes.OperationFailed));
        Assert.Equal(
            ManagedLifecycleOperationalHealthDiagnosticCodes.Failed,
            ManagedElsaInstanceOverviewEndpoints.CustomerActivityDiagnosticCode(
                ElsaInstanceProviderReconciliationService.FailedCode));
        Assert.Equal(
            ElsaInstanceCommercialOperation.EntitlementRequired,
            ManagedElsaInstanceOverviewEndpoints.CustomerActivityDiagnosticCode(
                ElsaInstanceCommercialOperation.EntitlementRequired));
    }

    [Fact]
    public async Task Activity_maps_reconcile_and_entitlement_events_without_false_alarms()
    {
        var app = await PrepareApplicationAsync();
        var client = app.CreateTrustedWorkspaceClient("overview-activity-codes-owner");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, client, "overview-activity-codes-runtime");
        await SeedAuditAsync(
            app, workspaceId, created.Instance.InstanceId, 10_001, "lifecycle.reconciled",
            ElsaInstanceProviderReconciliationService.ConvergedCode);
        await SeedAuditAsync(
            app, workspaceId, created.Instance.InstanceId, 10_002, "lifecycle.failed",
            ManagedLifecycleOperationalHealthDiagnosticCodes.OperationFailed);
        await SeedAuditAsync(
            app, workspaceId, created.Instance.InstanceId, 10_003, "lifecycle.reconciled",
            "provider.internal.mystery");
        await SeedAuditAsync(
            app, workspaceId, created.Instance.InstanceId, 10_004, "lifecycle.entitlement-held",
            ElsaInstanceCommercialOperation.EntitlementRequired);
        await SeedAuditAsync(
            app, workspaceId, created.Instance.InstanceId, 10_005, "lifecycle.entitlement-resumed",
            "instance.entitlement-restored");

        var activity = await client.GetControlJsonAsync<ManagedElsaInstanceActivityResponse>(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/activity");

        Assert.NotNull(activity);
        Assert.Null(Assert.Single(activity!.Items, item => item.Sequence == 10_001).DiagnosticCode);
        Assert.Equal(
            ManagedLifecycleOperationalHealthDiagnosticCodes.OperationFailed,
            Assert.Single(activity.Items, item => item.Sequence == 10_002).DiagnosticCode);
        Assert.Null(Assert.Single(activity.Items, item => item.Sequence == 10_003).DiagnosticCode);
        Assert.DoesNotContain(activity.Items, item => item.DiagnosticCode == ManagedElsaInstanceOverviewEndpoints.RequiresAttentionCode);
        Assert.Equal(
            ElsaInstanceCommercialOperation.EntitlementRequired,
            Assert.Single(activity.Items, item => item.EventType == "lifecycle.entitlement-held").DiagnosticCode);
        Assert.Null(Assert.Single(activity.Items, item => item.EventType == "lifecycle.entitlement-resumed").DiagnosticCode);
    }

    public static TheoryData<ElsaObservedLifecycle, ElsaDesiredLifecycle, bool, string?> CustomerMutationAvailabilityCases =>
        new()
        {
            { ElsaObservedLifecycle.Ready, ElsaDesiredLifecycle.Running, true, null },
            { ElsaObservedLifecycle.Degraded, ElsaDesiredLifecycle.Running, true, null },
            { ElsaObservedLifecycle.Stopped, ElsaDesiredLifecycle.Stopped, true, null },
            { ElsaObservedLifecycle.Failed, ElsaDesiredLifecycle.Running, false, ManagedElsaInstanceOverviewEndpoints.InstanceFailedCode },
            { ElsaObservedLifecycle.Provisioning, ElsaDesiredLifecycle.Running, false, ManagedElsaInstanceOverviewEndpoints.InstanceProvisioningCode },
            { ElsaObservedLifecycle.Pending, ElsaDesiredLifecycle.Running, false, ManagedElsaInstanceOverviewEndpoints.InstanceProvisioningCode },
            { ElsaObservedLifecycle.Updating, ElsaDesiredLifecycle.Running, false, ManagedElsaInstanceOverviewEndpoints.InstanceProvisioningCode },
            { ElsaObservedLifecycle.Stopping, ElsaDesiredLifecycle.Running, false, ManagedElsaInstanceOverviewEndpoints.InstanceProvisioningCode },
            { ElsaObservedLifecycle.Deleting, ElsaDesiredLifecycle.Deleting, false, ManagedElsaInstanceOverviewEndpoints.InstanceDeletingCode },
            { ElsaObservedLifecycle.Ready, ElsaDesiredLifecycle.Deleting, false, ManagedElsaInstanceOverviewEndpoints.InstanceDeletingCode },
            { ElsaObservedLifecycle.Deleted, ElsaDesiredLifecycle.Deleting, false, ManagedElsaInstanceOverviewEndpoints.InstanceDeletingCode },
            { ElsaObservedLifecycle.Unknown, ElsaDesiredLifecycle.Running, false, ManagedElsaInstanceOverviewEndpoints.InstanceUnknownCode }
        };

    [Theory]
    [MemberData(nameof(CustomerMutationAvailabilityCases))]
    public async Task Allowed_actions_match_the_route_gates_for_each_lifecycle_state(
        ElsaObservedLifecycle observed,
        ElsaDesiredLifecycle desired,
        bool allowed,
        string? reasonCode)
    {
        var app = await PrepareApplicationAsync();
        var client = app.CreateTrustedWorkspaceClient("overview-state-owner");
        var slug = $"st-{observed:D}-{desired:D}-{Guid.NewGuid():N}"[..32].TrimEnd('-');
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, client, slug);
        _fixture.ReleaseCatalog.SetEntries(
        [
            CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid", 'a'),
            CatalogEntry("valence-runtime", "3.8", "3.8.5", "stable", "combined", "supported", "paid", 'b')
        ]);
        await SetLifecycleAsync(app, created.Instance.InstanceId, observed, desired);

        var overview = await client.GetControlJsonAsync<ManagedElsaInstanceOverviewResponse>(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/overview");
        using var restart = await client.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/restart",
            created.Instance.ETag,
            $"state-restart-{slug}"));
        using var apply = await client.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/apply-release",
            created.Instance.ETag,
            $"state-apply-{slug}",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));

        Assert.NotNull(overview);
        Assert.Equal(allowed, overview!.AllowedActions.Restart.Allowed);
        Assert.Equal(allowed, overview.AllowedActions.ApplyRelease.Allowed);
        Assert.Equal(reasonCode, overview.AllowedActions.Restart.ReasonCode);
        Assert.Equal(reasonCode, overview.AllowedActions.ApplyRelease.ReasonCode);
        if (allowed)
        {
            Assert.Equal(HttpStatusCode.Accepted, restart.StatusCode);
            return;
        }

        Assert.Equal(HttpStatusCode.Conflict, restart.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, apply.StatusCode);
        Assert.Contains(reasonCode!, await restart.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains(reasonCode!, await apply.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mutations_require_idempotency_and_strong_etags_and_deny_readers()
    {
        var app = await PrepareApplicationAsync();
        var owner = app.CreateTrustedWorkspaceClient("overview-mutate-owner");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, owner, "overview-mutate-runtime");
        _fixture.ReleaseCatalog.SetEntries(
        [
            CatalogEntry("valence-runtime", "3.8", "3.8.3", "stable", "combined", "supported", "paid", '0'),
            CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid", 'a'),
            CatalogEntry("valence-runtime", "3.8", "3.8.5", "stable", "combined", "supported", "paid", 'b')
        ]);
        await app.AddWorkspaceMemberAsync(workspaceId, "overview-mutate-reader", WorkspaceRole.Reader);
        using var reader = app.CreateTrustedWorkspaceClient("overview-mutate-reader");
        var restartPath = $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/restart";
        var applyPath = $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/apply-release";

        using var missingKey = await owner.SendAsync(Mutation(HttpMethod.Post, restartPath, created.Instance.ETag, null));
        using var invalidRestartKey = await owner.SendAsync(Mutation(HttpMethod.Post, restartPath, created.Instance.ETag, "not a valid key"));
        using var invalidApplyKey = await owner.SendAsync(Mutation(
            HttpMethod.Post, applyPath, created.Instance.ETag, "not a valid key",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        using var missingMatch = await owner.SendAsync(Mutation(HttpMethod.Post, restartPath, null, "overview-restart"));
        using var applyMissingMatch = await owner.SendAsync(Mutation(
            HttpMethod.Post, applyPath, null, "overview-apply-match",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        using var stale = await owner.SendAsync(Mutation(HttpMethod.Post, restartPath, "\"999\"", "overview-stale"));
        using var applyStale = await owner.SendAsync(Mutation(
            HttpMethod.Post, applyPath, "\"999\"", "overview-apply-stale",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        using var readerRestart = await reader.SendAsync(Mutation(HttpMethod.Post, restartPath, created.Instance.ETag, "overview-reader-restart"));
        using var readerApply = await reader.SendAsync(Mutation(
            HttpMethod.Post, applyPath, created.Instance.ETag, "overview-reader-apply",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        using var unknownRelease = await owner.SendAsync(Mutation(
            HttpMethod.Post, applyPath, created.Instance.ETag, "overview-unknown-release",
            new ManagedElsaInstanceApplyReleaseRequest("9.9.9")));
        using var currentRelease = await owner.SendAsync(Mutation(
            HttpMethod.Post, applyPath, created.Instance.ETag, "overview-current-release",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.4")));
        using var olderRelease = await owner.SendAsync(Mutation(
            HttpMethod.Post, applyPath, created.Instance.ETag, "overview-older-release",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.3")));
        using var extraField = await owner.SendAsync(Mutation(
            HttpMethod.Post, applyPath, created.Instance.ETag, "overview-extra-field",
            new Dictionary<string, string> { ["version"] = "3.8.5", ["channel"] = "preview" }));
        using var emptyObject = await owner.SendAsync(Mutation(
            HttpMethod.Post, applyPath, created.Instance.ETag, "overview-empty-object",
            new Dictionary<string, string>()));
        using var missingBody = await owner.SendAsync(Mutation(HttpMethod.Post, applyPath, created.Instance.ETag, "overview-missing-body"));

        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidRestartKey.StatusCode);
        Assert.Contains("instance.idempotency-key-invalid", await invalidRestartKey.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, invalidApplyKey.StatusCode);
        Assert.Contains("instance.idempotency-key-invalid", await invalidApplyKey.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal((HttpStatusCode)428, missingMatch.StatusCode);
        Assert.Equal((HttpStatusCode)428, applyMissingMatch.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, applyStale.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, readerRestart.StatusCode);
        Assert.Contains("Workspace role does not allow", await readerRestart.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, readerApply.StatusCode);
        Assert.Contains("Workspace role does not allow", await readerApply.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownRelease.StatusCode);
        Assert.Contains("instance.release-not-available", await unknownRelease.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, currentRelease.StatusCode);
        Assert.Contains("instance.release-already-current", await currentRelease.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, olderRelease.StatusCode);
        Assert.Contains("instance.release-not-available", await olderRelease.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, extraField.StatusCode);
        Assert.Contains("request.unknown-field", await extraField.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, emptyObject.StatusCode);
        Assert.Contains("instance.apply-release-invalid", await emptyObject.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, missingBody.StatusCode);
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
        Assert.Equal(ElsaInstanceOperationAction.ApproveMinorUpgrade, firstBody!.Action);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(firstBody.OperationId, replayBody!.OperationId);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("instance.idempotency-conflict", await conflict.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        AssertExactCustomerDtoShape(JsonSerializer.Serialize(firstBody, ControlApiTestApplication.JsonOptions), "accepted");
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
            HttpMethod.Post, restartPath, "\"999\"", "overview-source-admin-restart"));
        var replayBody = await replay.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
        _fixture.ReleaseCatalog.SetEntries(
        [
            CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid", 'a'),
            CatalogEntry("valence-runtime", "3.8", "3.8.5", "stable", "combined", "supported", "paid", 'b')
        ]);
        using var conflict = await sourceAdmin.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/apply-release",
            created.Instance.ETag,
            "overview-source-admin-restart",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        using var crossOrg = await other.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{created.Instance.InstanceId:D}/restart",
            created.Instance.ETag,
            "overview-cross-org-restart"));
        using var crossOrgApply = await other.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{created.Instance.InstanceId:D}/apply-release",
            created.Instance.ETag,
            "overview-cross-org-apply",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));

        Assert.Equal(HttpStatusCode.Accepted, restart.StatusCode);
        Assert.Equal(ElsaInstanceOperationAction.Restart, body!.Action);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(body.OperationId, replayBody!.OperationId);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("instance.idempotency-conflict", await conflict.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, crossOrg.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossOrgApply.StatusCode);
    }

    [Fact]
    public async Task Minor_apply_replays_the_same_operation_and_a_restart_key_cannot_apply()
    {
        var app = await PrepareApplicationAsync();
        var client = app.CreateTrustedWorkspaceClient("overview-minor-owner");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, client, "overview-minor-runtime");
        _fixture.ReleaseCatalog.SetEntries(
        [
            CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid", 'a'),
            CatalogEntry("valence-runtime", "3.9", "3.9.0", "stable", "combined", "supported", "paid", 'c')
        ]);
        var applyPath = $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/apply-release";
        var restartPath = $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/restart";

        using var first = await client.SendAsync(Mutation(
            HttpMethod.Post, applyPath, created.Instance.ETag, "overview-minor-key",
            new ManagedElsaInstanceApplyReleaseRequest("3.9.0")));
        var firstBody = await first.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
        using var replay = await client.SendAsync(Mutation(
            HttpMethod.Post, applyPath, "\"999\"", "overview-minor-key",
            new ManagedElsaInstanceApplyReleaseRequest("3.9.0")));
        var replayBody = await replay.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();

        var (otherWorkspaceId, otherCreated) = await CreateReadyInstanceAsync(app, client, "overview-restart-key-runtime");
        using var restart = await client.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{otherCreated.Instance.InstanceId:D}/restart",
            otherCreated.Instance.ETag,
            "overview-shared-key"));
        var restartBody = await restart.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>();
        _fixture.ReleaseCatalog.SetEntries(
        [
            CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid", 'a'),
            CatalogEntry("valence-runtime", "3.8", "3.8.5", "stable", "combined", "supported", "paid", 'b')
        ]);
        using var applyWithRestartKey = await client.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{otherWorkspaceId:D}/instances/{otherCreated.Instance.InstanceId:D}/apply-release",
            otherCreated.Instance.ETag,
            "overview-shared-key",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(ElsaInstanceOperationAction.ApproveMinorUpgrade, firstBody!.Action);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(firstBody.OperationId, replayBody!.OperationId);
        Assert.Equal(HttpStatusCode.Accepted, restart.StatusCode);
        Assert.Equal(ElsaInstanceOperationAction.Restart, restartBody!.Action);
        Assert.Equal(HttpStatusCode.Conflict, applyWithRestartKey.StatusCode);
        Assert.Contains("instance.idempotency-conflict", await applyWithRestartKey.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Last_operation_is_the_most_recent_completed_operation()
    {
        var app = await PrepareApplicationAsync();
        var client = app.CreateTrustedWorkspaceClient("overview-last-op-owner");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, client, "overview-last-op-runtime");
        using var restart = await client.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/restart",
            created.Instance.ETag,
            "overview-last-op-restart"));
        Assert.Equal(HttpStatusCode.Accepted, restart.StatusCode);

        var overview = await client.GetControlJsonAsync<ManagedElsaInstanceOverviewResponse>(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/overview");

        Assert.NotNull(overview!.ActiveOperation);
        Assert.Equal(ElsaInstanceOperationAction.Restart, overview.ActiveOperation!.Action);
        Assert.NotNull(overview.LastOperation);
        Assert.Equal(ElsaInstanceOperationAction.Create, overview.LastOperation!.Action);
        Assert.Equal(ElsaInstanceOperationState.Succeeded, overview.LastOperation.State);
        Assert.NotNull(overview.LastOperation.CompletedAt);
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

        await MarkOperationSucceededAsync(app, created!.Operation.Id);
        await MarkInstanceReadyAsync(app, created.Instance.InstanceId);
        catalog.SetEntries(
        [
            CatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid"),
            CatalogEntry("valence-runtime", "3.8", "3.8.5", "stable", "combined", "supported", "paid", 'b')
        ]);

        using var overview = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/overview");
        using var restart = await client.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/restart",
            created.Instance.ETag,
            "bff-overview-restart"));
        using var apply = await client.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/apply-release",
            created.Instance.ETag,
            "bff-overview-restart",
            new ManagedElsaInstanceApplyReleaseRequest("3.8.5")));
        using var operatorDetail = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}");
        using var operatorAudit = await client.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/audit");

        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        var body = await overview.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewResponse>();
        Assert.Equal(created.Instance.InstanceId, body!.Summary.InstanceId);
        Assert.True(overview.Headers.ETag?.ToString()?.StartsWith('\"'));
        Assert.Equal(HttpStatusCode.Accepted, restart.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, apply.StatusCode);
        Assert.Contains("instance.idempotency-conflict", await apply.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, operatorDetail.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, operatorAudit.StatusCode);
        Assert.Contains("cloud-bff.denied", await operatorDetail.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        AssertExactCustomerDtoShape(await overview.Content.ReadAsStringAsync(), "overview");
        AssertExactCustomerDtoShape(
            JsonSerializer.Serialize(
                (await restart.Content.ReadControlJsonAsync<ManagedElsaInstanceOverviewOperationResponse>())!,
                ControlApiTestApplication.JsonOptions),
            "accepted");
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

    private static async Task SetLifecycleAsync(
        ControlApiTestApplication app,
        Guid instanceId,
        ElsaObservedLifecycle observed,
        ElsaDesiredLifecycle desired)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        long? deletedAt = observed == ElsaObservedLifecycle.Deleted ? DateTimeOffset.UtcNow.UtcTicks : null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ElsaInstances
            SET DesiredLifecycle = {desired.ToString()},
                ObservedLifecycle = {observed.ToString()},
                DeletedAt = {deletedAt}
            WHERE Id = {instanceId}
            """);
    }

    private static async Task SeedAuditAsync(
        ControlApiTestApplication app,
        Guid workspaceId,
        Guid instanceId,
        long sequence,
        string eventType,
        string? diagnosticCode)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var organizationId = await db.Workspaces.Where(x => x.Id == workspaceId)
            .Select(x => x.OrganizationId)
            .SingleAsync();
        var occurredAt = DateTimeOffset.UtcNow.UtcTicks;
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ElsaInstanceAuditEvents
                (Id, OrganizationId, WorkspaceId, InstanceId, Sequence, EventType, DiagnosticCode, OccurredAt)
            VALUES
                ({id}, {organizationId}, {workspaceId}, {instanceId}, {sequence}, {eventType}, {diagnosticCode}, {occurredAt})
            """);
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

    private static FrozenSet<string> Frozen(params string[] values) =>
        values.ToFrozenSet(StringComparer.Ordinal);

    private static void AssertExactCustomerDtoShape(string json, string rootName)
    {
        using var document = JsonDocument.Parse(json);
        AssertExactCustomerDtoShape(document.RootElement, rootName);
    }

    private static void AssertExactCustomerDtoShape(JsonElement element, string path)
    {
        if (!ExactCustomerDtoProperties.TryGetValue(path, out var expected))
            Assert.Fail($"No exact customer DTO property set registered for '{path}'.");

        if (element.ValueKind == JsonValueKind.Null)
            return;

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                AssertExactCustomerDtoShape(item, path);
            return;
        }

        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        var actual = element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
        foreach (var property in element.EnumerateObject())
        {
            var childPath = $"{path}.{property.Name}";
            if (ExactCustomerDtoProperties.ContainsKey(childPath))
                AssertExactCustomerDtoShape(property.Value, childPath);
        }
    }

    private static ElsaInstanceIntent Intent() => new(
        new ElsaReleaseIntent("valence-runtime", "3.8", requestedVersion: "3.8.4", channel: "stable"),
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
