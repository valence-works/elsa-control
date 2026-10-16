using System.Net;
using System.Text.Json.Nodes;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Cloud;
using ElsaControl.Api.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Api.Tests;

/// <summary>
/// <c>GET /api/me/workspace-memberships</c> lists the caller's organizations and workspaces
/// without provisioning anything: no account, organization, workspace or last-seen write.
/// </summary>
public sealed class WorkspaceMembershipsReadTests : IAsyncLifetime
{
    private const string MembershipsPath = "/api/me/workspace-memberships";
    private const string HubIssuer = "https://hub-account.test/auth/v1";
    private const string CloudIssuer = "https://cloud-account.test/auth/v1";
    private const string Subject = "d1b20202-0000-4000-8000-000000000001";
    private const string OtherSubject = "d1b20202-0000-4000-8000-000000000002";

    private readonly ControlApiTestApplication _app = new(new Dictionary<string, string?>
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

    public Task InitializeAsync() => _app.SeedAsync(_ => Task.CompletedTask);

    public async Task DisposeAsync() => await ((IAsyncDisposable)_app).DisposeAsync();

    [Theory]
    [InlineData(HubIssuer)]
    [InlineData(CloudIssuer)]
    public async Task No_account_caller_gets_an_empty_context_and_no_rows_are_written(string issuer)
    {
        using var client = SessionClient(issuer, Subject);
        var before = await CountRowsAsync();

        using var response = await client.GetAsync(MembershipsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadControlJsonAsync<MeWorkspaceMembershipsResponse>();
        Assert.NotNull(body);
        Assert.Null(body.Account);
        Assert.Empty(body.Workspaces);
        Assert.Empty(body.Organizations);
        Assert.Equal(before, await CountRowsAsync());
        Assert.Equal(RowCounts.Empty, before);
    }

    [Theory]
    [InlineData(HubIssuer)]
    [InlineData(CloudIssuer)]
    public async Task Existing_account_gets_exactly_its_context_and_no_rows_change(string issuer)
    {
        var bootstrap = await BootstrapAsync(SessionClient(issuer, Subject));
        var before = await SnapshotAsync(issuer, Subject);

        // A changed email claim would be written back by the provisioning read.
        using var client = SessionClient(issuer, Subject, email: "renamed@example.test");
        var body = await client.GetControlJsonAsync<MeWorkspaceMembershipsResponse>(MembershipsPath);

        Assert.NotNull(body);
        Assert.Equal(before.Identity.AccountId, body.Account?.Id);
        Assert.Equal("buyer@example.test", body.Account?.Email);
        var workspace = Assert.Single(body.Workspaces);
        Assert.Equal((bootstrap.WorkspaceId, bootstrap.OrganizationId, WorkspaceRole.Owner), (workspace.Id, workspace.OrganizationId, workspace.Role));
        var organization = Assert.Single(body.Organizations);
        Assert.Equal((bootstrap.OrganizationId, OrganizationRole.Owner), (organization.Id, organization.Role));
        Assert.Equal(before, await SnapshotAsync(issuer, Subject));
    }

    [Fact]
    public async Task Identity_never_sees_another_identitys_account_organizations_or_workspaces()
    {
        var owned = await BootstrapAsync(SessionClient(HubIssuer, Subject));
        var other = await BootstrapAsync(SessionClient(HubIssuer, OtherSubject));

        var ownerView = await SessionClient(HubIssuer, Subject).GetControlJsonAsync<MeWorkspaceMembershipsResponse>(MembershipsPath);
        var otherView = await SessionClient(HubIssuer, OtherSubject).GetControlJsonAsync<MeWorkspaceMembershipsResponse>(MembershipsPath);
        var cloudView = await SessionClient(CloudIssuer, Subject).GetControlJsonAsync<MeWorkspaceMembershipsResponse>(MembershipsPath);

        Assert.Equal(owned.WorkspaceId, Assert.Single(ownerView!.Workspaces).Id);
        Assert.Equal(owned.OrganizationId, Assert.Single(ownerView.Organizations).Id);
        Assert.Equal(other.WorkspaceId, Assert.Single(otherView!.Workspaces).Id);
        Assert.Equal(other.OrganizationId, Assert.Single(otherView.Organizations).Id);
        Assert.NotEqual(ownerView.Account!.Id, otherView.Account!.Id);
        // The same subject under another issuer is a different identity with no account.
        Assert.Null(cloudView!.Account);
        Assert.Empty(cloudView.Workspaces);
        Assert.Empty(cloudView.Organizations);
    }

    [Fact]
    public async Task Missing_identity_gets_the_existing_unauthorized_response()
    {
        using var client = _app.CreateClient();

        using var memberships = await client.GetAsync(MembershipsPath);
        using var workspaces = await client.GetAsync("/api/me/workspaces");

        Assert.Equal(HttpStatusCode.Unauthorized, memberships.StatusCode);
        Assert.Equal(workspaces.StatusCode, memberships.StatusCode);
        Assert.Equal(await ReadProblemWithoutTraceIdAsync(workspaces), await ReadProblemWithoutTraceIdAsync(memberships));
    }

    // The trace id differs per request; everything else in the problem body must match.
    private static async Task<string> ReadProblemWithoutTraceIdAsync(HttpResponseMessage response)
    {
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        problem.Remove("traceId");
        return problem.ToJsonString();
    }

    private HttpClient SessionClient(string issuer, string subject, string email = "buyer@example.test") =>
        _app.CreateControlIdentityClient(subject, issuer, "authenticated",
            claims: new Dictionary<string, string> { ["role"] = "authenticated", ["email"] = email });

    private static async Task<CloudBootstrapResponse> BootstrapAsync(HttpClient client)
    {
        using (client)
        {
            using var response = await client.PostControlJsonAsync("/api/cloud/bootstrap", new CloudBootstrapRequest());
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadControlJsonAsync<CloudBootstrapResponse>())!;
        }
    }

    private Task<RowCounts> CountRowsAsync() =>
        QueryAsync(async db => new RowCounts(
            await db.Accounts.CountAsync(),
            await db.ExternalIdentities.CountAsync(),
            await db.Organizations.CountAsync(),
            await db.OrganizationMemberships.CountAsync(),
            await db.Workspaces.CountAsync(),
            await db.WorkspaceMemberships.CountAsync()));

    private Task<Snapshot> SnapshotAsync(string issuer, string subject) =>
        QueryAsync(async db =>
        {
            var identity = await db.ExternalIdentities.AsNoTracking()
                .SingleAsync(x => x.Issuer == issuer && x.Subject == subject);
            var account = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == identity.AccountId);
            return new Snapshot(
                await CountRowsAsync(),
                new IdentityState(identity.AccountId, identity.DisplayName, identity.Email, identity.LastSeenAt, identity.UpdatedAt),
                new AccountState(account.DisplayName, account.Email, account.UpdatedAt));
        });

    private async Task<T> QueryAsync<T>(Func<CatalogDbContext, Task<T>> query)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<CatalogDbContext>());
    }

    private sealed record RowCounts(
        int Accounts,
        int ExternalIdentities,
        int Organizations,
        int OrganizationMemberships,
        int Workspaces,
        int WorkspaceMemberships)
    {
        public static RowCounts Empty { get; } = new(0, 0, 0, 0, 0, 0);
    }

    private sealed record IdentityState(Guid AccountId, string? DisplayName, string? Email, DateTimeOffset LastSeenAt, DateTimeOffset UpdatedAt);

    private sealed record AccountState(string? DisplayName, string? Email, DateTimeOffset UpdatedAt);

    private sealed record Snapshot(RowCounts Rows, IdentityState Identity, AccountState Account);
}
