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
        Assert.All(history.Items, item =>
        {
            Assert.Equal(accountId, item.ActorAccountId);
            Assert.Null(item.SourceAccountId);
        });
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
