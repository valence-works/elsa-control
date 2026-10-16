using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ElsaControl.Api.Admin.Accounts;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Cloud;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Api.Tests;

public sealed class CloudAccountLinkApiTests
{
    private const string HubIssuer = "https://hub-account.test/auth/v1";
    private const string CloudIssuer = "https://cloud-account.test/auth/v1";
    private const string HubSubject = "c0a80101-0000-4000-8000-000000000001";
    private const string OtherHubSubject = "c0a80101-0000-4000-8000-000000000002";
    private const string CloudSubject = "b6f73e84-5b47-4f4d-ad52-e3c4c9a815c6";
    private const string OtherCloudSubject = "d4e9a1c2-7f18-4b60-9c33-2a8e5f0d1479";

    [Fact]
    public async Task Linked_hub_session_sees_the_cloud_organizations_and_engines()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        var owned = await BootstrapAsync(cloud);
        var instanceId = await SeedOwnedInstanceAsync(app, owned.OrganizationId, owned.WorkspaceId);
        using var hub = CreateLinkClient(app, HubIssuer, HubSubject, CloudIssuer, CloudSubject);

        using var linked = await hub.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());
        using var hubOnly = CreateHubClient(app, HubSubject);
        using var organizations = await hubOnly.GetAsync("/api/me/organizations");
        using var workspaces = await hubOnly.GetAsync("/api/me/workspaces");
        using var instances = await hubOnly.GetAsync($"/api/workspaces/{owned.WorkspaceId:D}/instances");

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var body = await linked.Content.ReadFromJsonAsync<CloudAccountLinkResponse>(ControlApiTestApplication.JsonOptions);
        Assert.False(body!.AlreadyLinked);
        Assert.Equal(HttpStatusCode.OK, organizations.StatusCode);
        var me = await organizations.Content.ReadControlJsonAsync<MeWorkspacesResponse>();
        Assert.Contains(me!.Organizations, organization => organization.Id == owned.OrganizationId);
        Assert.Equal(body.AccountId, me.Account.Id);
        var workspaceList = await workspaces.Content.ReadControlJsonAsync<MeWorkspacesResponse>();
        Assert.Contains(workspaceList!.Workspaces, workspace => workspace.Id == owned.WorkspaceId);
        var listed = await instances.Content.ReadControlJsonAsync<ManagedElsaInstanceListResponse>();
        Assert.Contains(listed!.Items, item => item.InstanceId == instanceId);
    }

    [Fact]
    public async Task Cloud_session_keeps_working_after_the_link()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        var owned = await BootstrapAsync(cloud);
        using var hub = CreateLinkClient(app, HubIssuer, HubSubject, CloudIssuer, CloudSubject);

        using var linked = await hub.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());
        using var bootstrap = await cloud.PostAsJsonAsync("/api/cloud/bootstrap", new CloudBootstrapRequest());
        using var organizations = await cloud.GetAsync("/api/me/organizations");
        using var cloudOnly = await cloud.GetAsync($"/api/workspaces/{owned.WorkspaceId:D}/external-engine-connections");

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        var again = await bootstrap.Content.ReadFromJsonAsync<CloudBootstrapResponse>(ControlApiTestApplication.JsonOptions);
        Assert.Equal(owned, again);
        Assert.Equal(HttpStatusCode.OK, organizations.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cloudOnly.StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Linking_without_both_sessions_is_refused(bool includeHub, bool includeCloud)
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        await BootstrapAsync(cloud);
        using var client = includeHub && includeCloud
            ? CreateLinkClient(app, HubIssuer, HubSubject, CloudIssuer, CloudSubject)
            : includeHub
                ? CreateHubClient(app, HubSubject)
                : includeCloud
                    ? CreateCloudClient(app, CloudSubject)
                    : app.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertProblemAsync(response, CloudAccountLinkEndpoints.SessionsRequiredCode);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.DoesNotContain(await db.ExternalIdentities.ToListAsync(), identity => identity.Kind == ExternalIdentityKinds.Hub);
        Assert.Empty(await db.AccountIdentityLinkAuditRecords.ToListAsync());
    }

    [Fact]
    public async Task Same_email_without_both_sessions_never_links()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        using var hub = CreateHubClient(app, HubSubject);
        var cloudOwned = await BootstrapAsync(cloud);
        var hubOwned = await BootstrapAsync(hub);

        using var cloudOnly = await cloud.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());
        using var hubOnly = await hub.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, cloudOnly.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, hubOnly.StatusCode);
        Assert.NotEqual(cloudOwned.OrganizationId, hubOwned.OrganizationId);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var accounts = await db.Accounts.ToListAsync();
        Assert.Equal(2, accounts.Count);
        Assert.All(accounts, account => Assert.Equal("buyer@example.test", account.Email));
        Assert.Equal(2, await db.ExternalIdentities.CountAsync());
        Assert.Empty(await db.AccountIdentityLinkAuditRecords.ToListAsync());
    }

    [Fact]
    public async Task Linking_twice_is_idempotent()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        await BootstrapAsync(cloud);
        using var hub = CreateLinkClient(app, HubIssuer, HubSubject, CloudIssuer, CloudSubject);

        using var first = await hub.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());
        using var second = await hub.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<CloudAccountLinkResponse>(ControlApiTestApplication.JsonOptions);
        var secondBody = await second.Content.ReadFromJsonAsync<CloudAccountLinkResponse>(ControlApiTestApplication.JsonOptions);
        Assert.False(firstBody!.AlreadyLinked);
        Assert.True(secondBody!.AlreadyLinked);
        Assert.Equal(firstBody.AccountId, secondBody.AccountId);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Single(await db.AccountIdentityLinkAuditRecords.ToListAsync());
    }

    [Fact]
    public async Task Conflicting_cloud_account_hub_identity_is_refused_and_changes_nothing()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        await BootstrapAsync(cloud);
        using var firstHub = CreateLinkClient(app, HubIssuer, HubSubject, CloudIssuer, CloudSubject);
        using var first = await firstHub.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());
        using var otherHub = CreateLinkClient(app, HubIssuer, OtherHubSubject, CloudIssuer, CloudSubject);

        using var conflict = await otherHub.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await AssertProblemAsync(conflict, CloudAccountLinkEndpoints.AccountAlreadyLinkedCode);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Single(await db.ExternalIdentities.Where(identity => identity.Kind == ExternalIdentityKinds.Hub).ToListAsync());
        Assert.Single(await db.AccountIdentityLinkAuditRecords.ToListAsync());
    }

    [Fact]
    public async Task Hub_account_with_engines_is_refused_and_changes_nothing()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        using var hub = CreateHubClient(app, HubSubject);
        var cloudOwned = await BootstrapAsync(cloud);
        var hubOwned = await BootstrapAsync(hub);
        await SeedOwnedInstanceAsync(app, hubOwned.OrganizationId, hubOwned.WorkspaceId);
        using var linker = CreateLinkClient(app, HubIssuer, HubSubject, CloudIssuer, CloudSubject);

        using var conflict = await linker.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await AssertProblemAsync(conflict, CloudAccountLinkEndpoints.HubAccountHasEnginesCode);
        using var hubOrgs = await hub.GetAsync("/api/me/organizations");
        var hubMe = await hubOrgs.Content.ReadControlJsonAsync<MeWorkspacesResponse>();
        Assert.Contains(hubMe!.Organizations, organization => organization.Id == hubOwned.OrganizationId);
        Assert.DoesNotContain(hubMe.Organizations, organization => organization.Id == cloudOwned.OrganizationId);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Empty(await db.AccountIdentityLinkAuditRecords.ToListAsync());
        Assert.Equal(2, await db.Accounts.CountAsync());
    }

    [Fact]
    public async Task Cloud_account_that_has_not_bootstrapped_is_refused()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var linker = CreateLinkClient(app, HubIssuer, HubSubject, CloudIssuer, CloudSubject);

        using var response = await linker.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertProblemAsync(response, CloudAccountLinkEndpoints.CloudAccountMissingCode);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Empty(await db.Accounts.ToListAsync());
        Assert.Empty(await db.AccountIdentityLinkAuditRecords.ToListAsync());
    }

    [Fact]
    public async Task Support_unlink_is_audited_without_personal_data()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        await BootstrapAsync(cloud);
        using var hub = CreateLinkClient(app, HubIssuer, HubSubject, CloudIssuer, CloudSubject);
        using var linked = await hub.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());
        var accountId = (await linked.Content.ReadFromJsonAsync<CloudAccountLinkResponse>(ControlApiTestApplication.JsonOptions))!.AccountId;
        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");

        using var unlink = await admin.DeleteAsync($"/api/admin/accounts/{accountId:D}/hub-link");
        using var audit = await admin.GetAsync($"/api/admin/accounts/{accountId:D}/hub-link/audit");

        Assert.Equal(HttpStatusCode.NoContent, unlink.StatusCode);
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        var history = await audit.Content.ReadFromJsonAsync<AdminAccountHubLinkAuditResponse>(ControlApiTestApplication.JsonOptions);
        Assert.Equal(accountId, history!.AccountId);
        Assert.Equal(["Linked", "Unlinked"], history.Items.Select(item => item.Action).ToArray());
        Assert.Equal("Account", history.Items[0].ActorKind);
        Assert.Equal(accountId, history.Items[0].ActorAccountId);
        Assert.Equal("Operator", history.Items[1].ActorKind);
        Assert.Null(history.Items[1].ActorAccountId);
        Assert.All(history.Items, item => Assert.Null(item.SourceAccountId));
        var json = await audit.Content.ReadAsStringAsync();
        Assert.DoesNotContain("buyer@example.test", json, StringComparison.Ordinal);
        Assert.DoesNotContain(HubSubject, json, StringComparison.Ordinal);
        Assert.DoesNotContain(CloudSubject, json, StringComparison.Ordinal);
        using var hubOnly = CreateHubClient(app, HubSubject);
        using var afterUnlink = await hubOnly.GetAsync("/api/me/organizations");
        var hubMe = await afterUnlink.Content.ReadControlJsonAsync<MeWorkspacesResponse>();
        Assert.NotEqual(accountId, hubMe!.Account.Id);
    }

    [Fact]
    public async Task Same_token_in_both_slots_is_unauthorized_and_writes_nothing()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        await BootstrapAsync(cloud);
        var token = CreateAccountToken(CloudIssuer, CloudSubject);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            CloudAccountLinkDefaults.HubAuthorizationHeader,
            $"Bearer {token}");

        using var response = await client.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertProblemAsync(response, CloudAccountLinkEndpoints.SessionsRequiredCode);
        await AssertNoHubLinkAsync(app);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cross_scheme_companion_token_is_unauthorized_and_writes_nothing(bool hubPrimary)
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        await BootstrapAsync(cloud);
        using var client = hubPrimary
            ? CreateLinkClient(app, HubIssuer, HubSubject, HubIssuer, OtherHubSubject, CloudAccountLinkDefaults.CloudAuthorizationHeader)
            : CreateLinkClient(app, CloudIssuer, CloudSubject, CloudIssuer, CloudSubject, CloudAccountLinkDefaults.HubAuthorizationHeader);

        using var response = await client.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertProblemAsync(response, CloudAccountLinkEndpoints.SessionsRequiredCode);
        await AssertNoHubLinkAsync(app);
    }

    [Fact]
    public async Task Anonymous_companion_token_is_unauthorized_and_writes_nothing()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        await BootstrapAsync(cloud);
        using var client = CreateIssuerClient(app, CloudIssuer, CloudSubject);
        var anonymousHub = TestWorkspaceIdentity.CreateToken(
            HubSubject,
            HubIssuer,
            "authenticated",
            claims: new Dictionary<string, string>
            {
                ["role"] = "authenticated",
                ["email"] = "buyer@example.test",
                ["is_anonymous"] = "true"
            });
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            CloudAccountLinkDefaults.HubAuthorizationHeader,
            $"Bearer {anonymousHub}");

        using var response = await client.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertProblemAsync(response, CloudAccountLinkEndpoints.SessionsRequiredCode);
        await AssertNoHubLinkAsync(app);
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/audit")]
    [InlineData("DELETE", "")]
    public async Task Non_admin_is_forbidden_on_admin_hub_link_routes(string method, string suffix)
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        await BootstrapAsync(cloud);
        using var hub = CreateLinkClient(app, HubIssuer, HubSubject, CloudIssuer, CloudSubject);
        using var linked = await hub.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());
        var accountId = (await linked.Content.ReadFromJsonAsync<CloudAccountLinkResponse>(ControlApiTestApplication.JsonOptions))!.AccountId;
        using var customer = app.CreateClient();
        app.AddControlSessionCookie(customer);

        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            $"/api/admin/accounts/{accountId:D}/hub-link{suffix}");
        using var response = await customer.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.True(await db.ExternalIdentities.AnyAsync(identity => identity.Kind == ExternalIdentityKinds.Hub));
    }

    [Fact]
    public async Task Already_linked_hub_identity_is_not_moved_to_a_different_cloud_account()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var firstCloud = CreateCloudClient(app, CloudSubject);
        var firstOwned = await BootstrapAsync(firstCloud);
        using var firstLink = CreateLinkClient(app, HubIssuer, HubSubject, CloudIssuer, CloudSubject);
        using var linked = await firstLink.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());
        using var secondCloud = CreateCloudClient(app, OtherCloudSubject);
        var secondOwned = await BootstrapAsync(secondCloud);
        using var secondLink = CreateLinkClient(app, HubIssuer, HubSubject, CloudIssuer, OtherCloudSubject);

        using var conflict = await secondLink.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await AssertProblemAsync(conflict, CloudAccountLinkEndpoints.AccountAlreadyLinkedCode);
        using var hubOnly = CreateHubClient(app, HubSubject);
        using var organizations = await hubOnly.GetAsync("/api/me/organizations");
        var hubMe = await organizations.Content.ReadControlJsonAsync<MeWorkspacesResponse>();
        Assert.Contains(hubMe!.Organizations, organization => organization.Id == firstOwned.OrganizationId);
        Assert.DoesNotContain(hubMe.Organizations, organization => organization.Id == secondOwned.OrganizationId);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Single(await db.AccountIdentityLinkAuditRecords.ToListAsync());
        Assert.Single(await db.ExternalIdentities.Where(identity => identity.Kind == ExternalIdentityKinds.Hub).ToListAsync());
        Assert.Equal(
            (await db.ExternalIdentities.SingleAsync(identity => identity.Subject == CloudSubject)).AccountId,
            (await db.ExternalIdentities.SingleAsync(identity => identity.Kind == ExternalIdentityKinds.Hub)).AccountId);
    }

    [Fact]
    public async Task Hub_only_source_account_moves_onto_the_cloud_account_and_is_audited()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        using var hub = CreateHubClient(app, HubSubject);
        var cloudOwned = await BootstrapAsync(cloud);
        await BootstrapAsync(hub);
        using var hubMeBefore = await hub.GetAsync("/api/me/organizations");
        var hubAccountId = (await hubMeBefore.Content.ReadControlJsonAsync<MeWorkspacesResponse>())!.Account.Id;
        using var linker = CreateLinkClient(app, HubIssuer, HubSubject, CloudIssuer, CloudSubject);

        using var linked = await linker.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());
        var body = await linked.Content.ReadFromJsonAsync<CloudAccountLinkResponse>(ControlApiTestApplication.JsonOptions);
        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        using var audit = await admin.GetAsync($"/api/admin/accounts/{body!.AccountId:D}/hub-link/audit");
        using var organizations = await hub.GetAsync("/api/me/organizations");

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.False(body.AlreadyLinked);
        var cloudAccountId = body.AccountId;
        var history = await audit.Content.ReadFromJsonAsync<AdminAccountHubLinkAuditResponse>(ControlApiTestApplication.JsonOptions);
        var item = Assert.Single(history!.Items);
        Assert.Equal("Linked", item.Action);
        Assert.Equal("Account", item.ActorKind);
        Assert.Equal(cloudAccountId, item.ActorAccountId);
        Assert.Equal(hubAccountId, item.SourceAccountId);
        var hubMe = await organizations.Content.ReadControlJsonAsync<MeWorkspacesResponse>();
        Assert.Equal(cloudAccountId, hubMe!.Account.Id);
        Assert.Contains(hubMe.Organizations, organization => organization.Id == cloudOwned.OrganizationId);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.DoesNotContain(
            await db.AccountIdentityLinkAuditRecords.ToListAsync(),
            record => record.Action == AccountIdentityLinkAction.Unlinked);
    }

    [Fact]
    public async Task Hub_account_with_extra_workspace_memberships_returns_has_state()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        using var hub = CreateHubClient(app, HubSubject);
        await BootstrapAsync(cloud);
        var hubOwned = await BootstrapAsync(hub);
        await SeedExtraWorkspaceMembershipAsync(app, hubOwned);
        using var linker = CreateLinkClient(app, HubIssuer, HubSubject, CloudIssuer, CloudSubject);

        using var conflict = await linker.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await AssertProblemAsync(conflict, CloudAccountLinkEndpoints.HubAccountHasStateCode);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var hubAccountId = await db.OrganizationMemberships
            .Where(membership => membership.OrganizationId == hubOwned.OrganizationId)
            .Select(membership => membership.AccountId)
            .SingleAsync();
        Assert.Empty(await db.AccountIdentityLinkAuditRecords.ToListAsync());
        Assert.Equal(2, await db.Accounts.CountAsync());
        Assert.Equal(hubAccountId, (await db.ExternalIdentities.SingleAsync(identity => identity.Subject == HubSubject)).AccountId);
    }

    [Fact]
    public async Task Hub_account_with_other_org_memberships_returns_has_state()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        using var hub = CreateHubClient(app, HubSubject);
        await BootstrapAsync(cloud);
        var hubOwned = await BootstrapAsync(hub);
        await SeedExtraOrganizationMembershipAsync(app, hubOwned);
        using var linker = CreateLinkClient(app, HubIssuer, HubSubject, CloudIssuer, CloudSubject);

        using var conflict = await linker.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await AssertProblemAsync(conflict, CloudAccountLinkEndpoints.HubAccountHasStateCode);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Empty(await db.AccountIdentityLinkAuditRecords.ToListAsync());
    }

    [Fact]
    public async Task Hub_account_with_a_hub_sourced_subscription_returns_has_state()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        using var hub = CreateHubClient(app, HubSubject);
        await BootstrapAsync(cloud);
        var hubOwned = await BootstrapAsync(hub);
        await SeedHubSourcedSubscriptionAsync(app, hubOwned.OrganizationId);
        using var linker = CreateLinkClient(app, HubIssuer, HubSubject, CloudIssuer, CloudSubject);

        using var conflict = await linker.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await AssertProblemAsync(conflict, CloudAccountLinkEndpoints.HubAccountHasStateCode);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Empty(await db.AccountIdentityLinkAuditRecords.ToListAsync());
    }

    [Fact]
    public async Task Cloud_authorization_header_plus_hub_bearer_also_links()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var cloud = CreateCloudClient(app, CloudSubject);
        var owned = await BootstrapAsync(cloud);
        using var linker = CreateLinkClient(app, CloudIssuer, CloudSubject, companionIssuer: HubIssuer, companionSubject: HubSubject, companionHeader: CloudAccountLinkDefaults.HubAuthorizationHeader);

        using var linked = await linker.PostAsJsonAsync("/api/cloud/account-links", new CloudAccountLinkRequest());
        using var hubOnly = CreateHubClient(app, HubSubject);
        using var organizations = await hubOnly.GetAsync("/api/me/organizations");

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var me = await organizations.Content.ReadControlJsonAsync<MeWorkspacesResponse>();
        Assert.Contains(me!.Organizations, organization => organization.Id == owned.OrganizationId);
    }

    private static ControlApiTestApplication CreateApplication() => new(new Dictionary<string, string?>
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

    private static HttpClient CreateLinkClient(
        ControlApiTestApplication app,
        string primaryIssuer,
        string primarySubject,
        string companionIssuer,
        string companionSubject,
        string? companionHeader = null)
    {
        var client = CreateIssuerClient(app, primaryIssuer, primarySubject);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            companionHeader ?? CloudAccountLinkDefaults.CloudAuthorizationHeader,
            $"Bearer {CreateAccountToken(companionIssuer, companionSubject)}");
        return client;
    }

    private static HttpClient CreateIssuerClient(ControlApiTestApplication app, string issuer, string subject)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateAccountToken(issuer, subject));
        return client;
    }

    private static string CreateAccountToken(string issuer, string subject) =>
        TestWorkspaceIdentity.CreateToken(
            subject,
            issuer,
            "authenticated",
            claims: new Dictionary<string, string> { ["role"] = "authenticated", ["email"] = "buyer@example.test" });

    private static async Task<CloudBootstrapResponse> BootstrapAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/cloud/bootstrap", new CloudBootstrapRequest());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CloudBootstrapResponse>(ControlApiTestApplication.JsonOptions);
        Assert.NotNull(body);
        return body;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, string code)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }

    private static async Task AssertNoHubLinkAsync(ControlApiTestApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.DoesNotContain(await db.ExternalIdentities.ToListAsync(), identity => identity.Kind == ExternalIdentityKinds.Hub);
        Assert.Empty(await db.AccountIdentityLinkAuditRecords.ToListAsync());
    }

    private static async Task SeedExtraWorkspaceMembershipAsync(
        ControlApiTestApplication app,
        CloudBootstrapResponse owned)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var accountId = await db.OrganizationMemberships
            .Where(membership => membership.OrganizationId == owned.OrganizationId)
            .Select(membership => membership.AccountId)
            .SingleAsync();
        var workspace = new ElsaControl.PackageCatalog.Core.Accounts.Workspace
        {
            OrganizationId = owned.OrganizationId,
            Name = "extra-workspace",
            Kind = WorkspaceKind.Shared
        };
        db.Workspaces.Add(workspace);
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            AccountId = accountId,
            Workspace = workspace,
            Role = WorkspaceRole.Owner
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedExtraOrganizationMembershipAsync(
        ControlApiTestApplication app,
        CloudBootstrapResponse owned)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var accountId = await db.OrganizationMemberships
            .Where(membership => membership.OrganizationId == owned.OrganizationId)
            .Select(membership => membership.AccountId)
            .SingleAsync();
        var organization = new Organization { Name = "extra-org", CreatedByAccountId = accountId };
        db.Organizations.Add(organization);
        db.OrganizationMemberships.Add(new OrganizationMembership
        {
            AccountId = accountId,
            Organization = organization,
            Role = OrganizationRole.Member
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedHubSourcedSubscriptionAsync(ControlApiTestApplication app, Guid organizationId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var subscription = OrganizationSubscriptionLifecycle.CreateTrial(
            organizationId,
            BillingProviderNames.Stripe,
            DateTimeOffset.UtcNow);
        subscription.WebhookSource = BillingWebhookSources.Hub;
        db.OrganizationSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
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
            Name = "Cloud runtime",
            Slug = "cloud-runtime",
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
