using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Cloud;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Api.Tests;

public sealed class CloudAccountIdentityTests
{
    private const string Issuer = "https://cloud-account.test/auth/v1";

    [Fact]
    public async Task Google_or_email_cloud_session_bootstraps_one_workspace_without_an_entra_token()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateCloudClient(app);

        using var first = await client.PostAsJsonAsync("/api/cloud/bootstrap", new CloudBootstrapRequest());
        using var second = await client.PostAsJsonAsync("/api/cloud/bootstrap", new CloudBootstrapRequest());

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstWorkspace = await first.Content.ReadFromJsonAsync<CloudBootstrapResponse>();
        var secondWorkspace = await second.Content.ReadFromJsonAsync<CloudBootstrapResponse>();
        Assert.Equal(firstWorkspace, secondWorkspace);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Single(await database.Accounts.ToListAsync());
    }

    [Fact]
    public async Task Cloud_session_is_limited_to_the_existing_customer_allowlist()
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateCloudClient(app);

        using var allowed = await client.GetAsync("/api/me/workspaces");
        using var denied = await client.GetAsync("/api/admin/application");

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("cloud-bff.denied", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://foreign.test/auth/v1", "authenticated", "authenticated")]
    [InlineData(Issuer, "wrong-audience", "authenticated")]
    [InlineData(Issuer, "authenticated", "anon")]
    public async Task Foreign_or_non_user_token_never_provisions_a_control_account(
        string issuer, string audience, string role)
    {
        await using var app = CreateApplication();
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = CreateCloudClient(app, issuer, audience, role);

        using var response = await client.PostAsJsonAsync("/api/cloud/bootstrap", new CloudBootstrapRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Empty(await database.Accounts.ToListAsync());
    }

    private static ControlApiTestApplication CreateApplication() => new(new Dictionary<string, string?>
    {
        [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Enabled"] = "true",
        [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Issuer"] = Issuer,
        [$"{CloudAccountIdentityDefaults.ConfigurationSection}:Audience"] = "authenticated",
        [$"{CloudAccountIdentityDefaults.ConfigurationSection}:TestSigningKey"] = ControlApiTestApplication.TestControlIdentitySigningKey
    });

    private static HttpClient CreateCloudClient(
        ControlApiTestApplication app,
        string issuer = Issuer,
        string audience = "authenticated",
        string role = "authenticated")
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestWorkspaceIdentity.CreateToken(
            "b6f73e84-5b47-4f4d-ad52-e3c4c9a815c6", issuer, audience,
            claims: new Dictionary<string, string> { ["role"] = role, ["email"] = "buyer@example.test" }));
        return client;
    }
}
