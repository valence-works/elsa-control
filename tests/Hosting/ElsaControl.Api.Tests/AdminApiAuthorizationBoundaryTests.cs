using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using ElsaControl.Api.Authentication;

namespace ElsaControl.Api.Tests;

public sealed class AdminApiAuthorizationBoundaryTests : IClassFixture<DefaultControlApiTestApplicationFixture>
{
    private readonly ControlApiTestApplication _app;

    public AdminApiAuthorizationBoundaryTests(DefaultControlApiTestApplicationFixture fixture) =>
        _app = fixture.Application;

    public static TheoryData<string> RepresentativeAdminGetRoutes => new()
    {
        "/api/admin/sources",
        "/api/admin/application",
        "/api/admin/packages",
        "/api/admin/sync-runs"
    };

    [Theory]
    [MemberData(nameof(RepresentativeAdminGetRoutes))]
    public async Task Unauthenticated_request_is_unauthorized(string path)
    {
        await _app.SeedAsync(_ => Task.CompletedTask);

        var response = await _app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(RepresentativeAdminGetRoutes))]
    public async Task Customer_session_without_control_admin_is_forbidden(string path)
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var client = _app.CreateClient(new() { AllowAutoRedirect = false });
        _app.AddControlSessionCookie(client);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(RepresentativeAdminGetRoutes))]
    public async Task Control_admin_role_claim_authorizes_console_admin_reads(string path)
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var client = _app.CreateClient(new() { AllowAutoRedirect = false });
        _app.AddControlSessionCookie(client, new Claim("roles", AdminAuthorization.ControlAdminRole));

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(RepresentativeAdminGetRoutes))]
    public async Task Admin_api_key_still_authorizes(string path)
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Customer_session_without_control_admin_cannot_admit_releases()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var client = CreateCustomerSessionClient();

        var response = await client.PostAsJsonAsync("/api/admin/release-catalog/manifests", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Customer_session_without_control_admin_cannot_write_workspace_entitlements()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var client = CreateCustomerSessionClient();

        var response = await client.PutAsJsonAsync(
            $"/api/admin/workspaces/{Guid.NewGuid()}/entitlements",
            new
            {
                canCreateCustomSources = false,
                maxSources = 1,
                maxPackagesIndexed = 1,
                maxVersionsPerPackage = 1,
                maxSyncsPerDay = 1,
                privateFeedsEnabled = false
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Customer_session_without_control_admin_cannot_read_internal_entitlement()
    {
        await _app.SeedAsync(_ => Task.CompletedTask);
        var client = CreateCustomerSessionClient();

        var response = await client.GetAsync($"/api/admin/organizations/{Guid.NewGuid()}/internal-entitlement");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("roles")]
    [InlineData("http://schemas.microsoft.com/ws/2008/06/identity/claims/role")]
    public void HasControlAdminRole_accepts_known_role_claim_types(string claimType)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(claimType, AdminAuthorization.ControlAdminRole)]));

        Assert.True(AdminAuthorization.HasControlAdminRole(principal));
    }

    [Fact]
    public void HasControlAdminRole_rejects_other_roles_and_missing_claims()
    {
        var otherRole = new ClaimsPrincipal(new ClaimsIdentity([new Claim("roles", "customer")]));
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False(AdminAuthorization.HasControlAdminRole(otherRole));
        Assert.False(AdminAuthorization.HasControlAdminRole(anonymous));
    }

    private HttpClient CreateCustomerSessionClient()
    {
        var client = _app.CreateClient(new() { AllowAutoRedirect = false });
        _app.AddControlSessionCookie(client);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        return client;
    }
}
