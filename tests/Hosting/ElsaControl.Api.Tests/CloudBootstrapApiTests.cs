using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Cloud;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CatalogWorkspace = ElsaControl.PackageCatalog.Core.Accounts.Workspace;

namespace ElsaControl.Api.Tests;

public sealed class CloudBootstrapApiTests : IClassFixture<DefaultControlApiTestApplicationFixture>
{
    private readonly ControlApiTestApplication _app;

    public CloudBootstrapApiTests(DefaultControlApiTestApplicationFixture fixture) => _app = fixture.Application;

    [Fact]
    public async Task New_customer_mints_an_organization_and_default_workspace()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var client = _app.CreateControlIdentityClient(subject: "new-customer");

        var response = await PostBootstrapAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            ["organizationId", "workspaceId"],
            document.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.True(Guid.TryParseExact(document.RootElement.GetProperty("organizationId").GetString(), "D", out _));
        Assert.True(Guid.TryParseExact(document.RootElement.GetProperty("workspaceId").GetString(), "D", out _));
        var bootstrap = JsonSerializer.Deserialize<CloudBootstrapResponse>(json, ControlApiTestApplication.JsonOptions);
        Assert.NotNull(bootstrap);
        Assert.NotEqual(Guid.Empty, bootstrap.OrganizationId);
        Assert.NotEqual(Guid.Empty, bootstrap.WorkspaceId);

        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(1, await db.Accounts.CountAsync());
        Assert.Equal(1, await db.Organizations.CountAsync());
        Assert.Equal(1, await db.Workspaces.CountAsync());
        var organizationMembership = await db.OrganizationMemberships.SingleAsync();
        var workspaceMembership = await db.WorkspaceMemberships.SingleAsync();
        Assert.Equal(bootstrap.OrganizationId, organizationMembership.OrganizationId);
        Assert.Equal(OrganizationRole.Owner, organizationMembership.Role);
        Assert.Equal(bootstrap.WorkspaceId, workspaceMembership.WorkspaceId);
        Assert.Equal(WorkspaceRole.Owner, workspaceMembership.Role);
    }

    [Fact]
    public async Task Existing_customer_cookie_resolves_the_linked_organization_and_workspace()
    {
        var organizationId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        await _app.SeedAsync(db => SeedCustomerAsync(db, "existing-customer", organizationId, workspaceId));
        var client = _app.CreateClient();
        _app.AddControlSessionCookie(
            client,
            subject: "existing-customer",
            expiresUtc: DateTimeOffset.UtcNow.AddHours(2));

        var response = await PostBootstrapAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bootstrap = await response.Content.ReadFromJsonAsync<CloudBootstrapResponse>();
        Assert.NotNull(bootstrap);
        Assert.Equal(organizationId, bootstrap.OrganizationId);
        Assert.Equal(workspaceId, bootstrap.WorkspaceId);
    }

    [Fact]
    public async Task Organization_member_without_workspace_access_fails_closed()
    {
        await _app.SeedAsync(db => SeedCustomerAsync(
            db,
            "organization-member",
            Guid.NewGuid(),
            Guid.NewGuid(),
            grantWorkspaceAccess: false));
        var client = _app.CreateControlIdentityClient(subject: "organization-member");

        var response = await PostBootstrapAsync(client);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        Assert.Equal("cloud.bootstrap.workspace-unavailable", problem?["code"].GetString());
    }

    [Fact]
    public async Task Unauthenticated_customer_is_rejected_without_minting_catalog_rows()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var client = _app.CreateClient();

        var response = await PostBootstrapAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Empty(db.Accounts);
        Assert.Empty(db.Organizations);
        Assert.Empty(db.Workspaces);
    }

    [Fact]
    public async Task Foreign_organization_attachment_is_rejected_without_minting_a_customer()
    {
        var foreignOrganizationId = Guid.NewGuid();
        await _app.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization
            {
                Id = foreignOrganizationId,
                Name = "Foreign organization"
            });
            return Task.CompletedTask;
        });
        var client = _app.CreateControlIdentityClient(subject: "new-customer");

        var response = await client.PostAsJsonAsync(
            "/api/cloud/bootstrap",
            new { organizationId = foreignOrganizationId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Empty(db.Accounts);
        Assert.Equal(1, await db.Organizations.CountAsync());
        Assert.Empty(db.Workspaces);
    }

    [Fact]
    public async Task Repeated_bootstrap_is_idempotent_for_the_same_principal()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var client = _app.CreateControlIdentityClient(subject: "repeat-customer");

        var firstResponse = await PostBootstrapAsync(client);
        var secondResponse = await PostBootstrapAsync(client);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<CloudBootstrapResponse>();
        var second = await secondResponse.Content.ReadFromJsonAsync<CloudBootstrapResponse>();
        Assert.Equal(first, second);

        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(1, await db.Accounts.CountAsync());
        Assert.Equal(1, await db.ExternalIdentities.CountAsync());
        Assert.Equal(1, await db.Organizations.CountAsync());
        Assert.Equal(1, await db.Workspaces.CountAsync());
    }

    [Fact]
    public async Task Admin_api_key_is_not_customer_authentication()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");

        var response = await PostBootstrapAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Trusted_headers_are_not_customer_authentication()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Add(TrustedHeaderWorkspaceIdentityReader.IssuerHeader, "https://trusted.example.test");
        client.DefaultRequestHeaders.Add(TrustedHeaderWorkspaceIdentityReader.SubjectHeader, "trusted-customer");

        var response = await PostBootstrapAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_bearer_does_not_fall_back_to_a_customer_cookie()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var client = _app.CreateClient();
        _app.AddControlSessionCookie(
            client,
            subject: "cookie-customer",
            expiresUtc: DateTimeOffset.UtcNow.AddHours(2));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            "not-a-valid-token");

        var response = await PostBootstrapAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static Task<HttpResponseMessage> PostBootstrapAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/cloud/bootstrap", new { });

    private static Task SeedCustomerAsync(
        CatalogDbContext db,
        string subject,
        Guid organizationId,
        Guid workspaceId,
        bool grantWorkspaceAccess = true)
    {
        var account = new Account
        {
            DisplayName = "Existing Customer",
            Email = "existing@example.test"
        };
        var organization = new Organization
        {
            Id = organizationId,
            Name = "Existing organization",
            CreatedByAccountId = account.Id
        };
        var workspace = new CatalogWorkspace
        {
            Id = workspaceId,
            Name = "Existing workspace",
            Kind = WorkspaceKind.Personal,
            Organization = organization
        };
        var organizationMembership = new OrganizationMembership
        {
            Account = account,
            Organization = organization,
            Role = OrganizationRole.Owner
        };
        var workspaceMembership = new WorkspaceMembership
        {
            Account = account,
            Workspace = workspace,
            Role = WorkspaceRole.Owner
        };
        account.ExternalIdentities.Add(new ExternalIdentity
        {
            Account = account,
            Issuer = ControlApiTestApplication.TestControlIdentityIssuer,
            Subject = subject,
            DisplayName = account.DisplayName,
            Email = account.Email
        });
        account.OrganizationMemberships.Add(organizationMembership);
        if (grantWorkspaceAccess)
        {
            account.Memberships.Add(workspaceMembership);
            workspace.Memberships.Add(workspaceMembership);
        }
        organization.Memberships.Add(organizationMembership);
        organization.Workspaces.Add(workspace);
        db.Accounts.Add(account);
        return Task.CompletedTask;
    }
}
