using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Cloud;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace ElsaControl.Api.Tests;

public sealed class HubAccountIdentityTests
{
    private const string HubIssuer = "https://hub-account.test/auth/v1";
    private const string CloudIssuer = "https://cloud-account.test/auth/v1";
    private const string HubSubject = "c0a80101-0000-4000-8000-000000000001";
    private const string CloudSubject = "b6f73e84-5b47-4f4d-ad52-e3c4c9a815c6";

    [Fact]
    public async Task Hub_path_off_leaves_existing_auth_unchanged()
    {
        await using var app = new ControlApiTestApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var hubClient = CreateClient(app, HubIssuer, HubSubject);
        using var controlClient = app.CreateControlIdentityClient(subject: "control-user");

        using var hubDenied = await hubClient.PostAsJsonAsync("/api/cloud/bootstrap", new CloudBootstrapRequest());
        using var controlAllowed = await controlClient.PostAsJsonAsync("/api/cloud/bootstrap", new CloudBootstrapRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, hubDenied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, controlAllowed.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Single(await database.Accounts.ToListAsync());
        Assert.DoesNotContain(await database.ExternalIdentities.ToListAsync(),
            identity => identity.Kind == ExternalIdentityKinds.Hub);
    }

    [Fact]
    public async Task Valid_hub_session_bootstraps_a_personal_organization_and_workspace()
    {
        await using var app = CreateHubApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateClient(app, HubIssuer, HubSubject);

        using var first = await client.PostAsJsonAsync("/api/cloud/bootstrap", new CloudBootstrapRequest());
        using var second = await client.PostAsJsonAsync("/api/cloud/bootstrap", new CloudBootstrapRequest());

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstWorkspace = await first.Content.ReadFromJsonAsync<CloudBootstrapResponse>();
        var secondWorkspace = await second.Content.ReadFromJsonAsync<CloudBootstrapResponse>();
        Assert.Equal(firstWorkspace, secondWorkspace);
        Assert.NotEqual(Guid.Empty, firstWorkspace!.OrganizationId);
        Assert.NotEqual(Guid.Empty, firstWorkspace.WorkspaceId);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var identity = Assert.Single(await database.ExternalIdentities.ToListAsync());
        Assert.Equal((HubIssuer, HubSubject, ExternalIdentityKinds.Hub), (identity.Issuer, identity.Subject, identity.Kind));
        Assert.Single(await database.Accounts.ToListAsync());
        Assert.Single(await database.Workspaces.ToListAsync(), workspace => workspace.Kind == WorkspaceKind.Personal);
    }

    [Fact]
    public async Task Control_identity_still_bootstraps_when_the_hub_path_is_on()
    {
        await using var app = CreateHubApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = app.CreateControlIdentityClient(subject: "entra-user");

        using var response = await client.PostAsJsonAsync("/api/cloud/bootstrap", new CloudBootstrapRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var identity = Assert.Single(await database.ExternalIdentities.ToListAsync());
        Assert.NotEqual(ExternalIdentityKinds.Hub, identity.Kind);
        Assert.Equal(ControlApiTestApplication.TestControlIdentityIssuer, identity.Issuer);
    }

    [Theory]
    [InlineData("https://foreign.test/auth/v1", "authenticated", "authenticated")]
    [InlineData(HubIssuer, "wrong-audience", "authenticated")]
    [InlineData(HubIssuer, "authenticated", "anon")]
    public async Task Foreign_or_non_user_hub_token_never_provisions_a_control_account(
        string issuer, string audience, string role)
    {
        await using var app = CreateHubApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateClient(app, issuer, HubSubject, audience, role);

        using var response = await client.PostAsJsonAsync("/api/cloud/bootstrap", new CloudBootstrapRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Empty(await database.Accounts.ToListAsync());
    }

    [Fact]
    public async Task Expired_hub_session_is_refused()
    {
        await using var app = CreateHubApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateClient(
            app, HubIssuer, HubSubject, expires: DateTimeOffset.UtcNow.AddMinutes(-5));

        using var response = await client.PostAsJsonAsync("/api/cloud/bootstrap", new CloudBootstrapRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Empty(await database.Accounts.ToListAsync());
    }

    [Fact]
    public async Task Hub_and_cloud_users_with_the_same_email_stay_separate_accounts()
    {
        await using var app = CreateHubAndCloudApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var hubClient = CreateClient(app, HubIssuer, HubSubject);
        using var cloudClient = CreateClient(app, CloudIssuer, CloudSubject);

        using var hub = await hubClient.PostAsJsonAsync("/api/cloud/bootstrap", new CloudBootstrapRequest());
        using var cloud = await cloudClient.PostAsJsonAsync("/api/cloud/bootstrap", new CloudBootstrapRequest());

        Assert.Equal(HttpStatusCode.OK, hub.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cloud.StatusCode);
        var hubWorkspace = await hub.Content.ReadFromJsonAsync<CloudBootstrapResponse>();
        var cloudWorkspace = await cloud.Content.ReadFromJsonAsync<CloudBootstrapResponse>();
        Assert.NotEqual(hubWorkspace!.OrganizationId, cloudWorkspace!.OrganizationId);
        Assert.NotEqual(hubWorkspace.WorkspaceId, cloudWorkspace.WorkspaceId);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var accounts = await database.Accounts.ToListAsync();
        Assert.Equal(2, accounts.Count);
        Assert.All(accounts, account => Assert.Equal("buyer@example.test", account.Email));
        var identities = await database.ExternalIdentities.ToListAsync();
        Assert.Equal(2, identities.Count);
        Assert.Contains(identities, identity => identity.Kind == ExternalIdentityKinds.Hub && identity.Issuer == HubIssuer);
        Assert.Contains(identities, identity => identity.Kind != ExternalIdentityKinds.Hub && identity.Issuer == CloudIssuer);
    }

    [Fact]
    public void Incomplete_hub_bearer_is_selected_so_it_cannot_fall_through()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer not-a-jwt";

        Assert.True(HubAccountTokenSelector.Matches(context, new HubAccountIdentityOptions
        {
            Enabled = true,
            Issuer = "",
            Audience = ""
        }));
        Assert.False(HubAccountTokenSelector.Matches(context, new HubAccountIdentityOptions()));
    }

    [Fact]
    public void Hub_jwt_validation_never_skips_issuer_or_audience()
    {
        var options = new JwtBearerOptions();
        HubAccountJwtBearer.Configure(options, new HubAccountIdentityOptions { Enabled = true, Issuer = "", Audience = "" });

        Assert.True(options.TokenValidationParameters.ValidateIssuer);
        Assert.True(options.TokenValidationParameters.ValidateAudience);
        Assert.True(string.IsNullOrEmpty(options.TokenValidationParameters.ValidIssuer));
        Assert.True(string.IsNullOrEmpty(options.TokenValidationParameters.ValidAudience));
    }

    private static ControlApiTestApplication CreateHubApplication() => new(new Dictionary<string, string?>
    {
        [$"{HubAccountIdentityDefaults.ConfigurationSection}:Enabled"] = "true",
        [$"{HubAccountIdentityDefaults.ConfigurationSection}:Issuer"] = HubIssuer,
        [$"{HubAccountIdentityDefaults.ConfigurationSection}:Audience"] = "authenticated",
        [$"{HubAccountIdentityDefaults.ConfigurationSection}:TestSigningKey"] = ControlApiTestApplication.TestControlIdentitySigningKey
    });

    private static ControlApiTestApplication CreateHubAndCloudApplication() => new(new Dictionary<string, string?>
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

    private static HttpClient CreateClient(
        ControlApiTestApplication app,
        string issuer,
        string subject,
        string audience = "authenticated",
        string role = "authenticated",
        DateTimeOffset? expires = null)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestWorkspaceIdentity.CreateToken(
            subject, issuer, audience, expires,
            claims: new Dictionary<string, string> { ["role"] = role, ["email"] = "buyer@example.test" }));
        return client;
    }
}
