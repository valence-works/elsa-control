using System.Net;
using System.Security.Claims;
using ElsaControl.Api.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace ElsaControl.Api.Tests;

public sealed class CustomerAuthenticationTests : IClassFixture<DefaultControlApiTestApplicationFixture>
{
    private readonly ControlApiTestApplication _app;

    public CustomerAuthenticationTests(DefaultControlApiTestApplicationFixture fixture) => _app = fixture.Application;

    [Fact]
    public async Task Session_reports_login_disabled_when_customer_oidc_is_not_configured()
    {
        await using var app = new ControlApiTestApplication(new Dictionary<string, string?>
        {
            [$"{ControlIdentityDefaults.ConfigurationSection}:Authority"] = "",
            [$"{ControlIdentityDefaults.ConfigurationSection}:ClientId"] = "",
            [TrustedHeaderWorkspaceIdentityReader.EnabledConfigurationKey] = "false"
        });

        var response = await app.CreateClient().GetControlJsonAsync<CustomerAuthSessionResponse>(CustomerAuthenticationDefaults.SessionPath);

        Assert.False(response!.LoginEnabled);
        Assert.False(response.Authenticated);
        Assert.Equal(CustomerAuthenticationDefaults.LoginPath, response.LoginPath);
        Assert.Equal(CustomerAuthenticationDefaults.LogoutPath, response.LogoutPath);
    }

    [Fact]
    public async Task Session_reports_control_admin_cookie_as_admin()
    {
        var client = _app.CreateClient();
        _app.AddControlSessionCookie(client, new Claim("role", AdminAuthorization.ControlAdminRole));

        var response = await client.GetControlJsonAsync<CustomerAuthSessionResponse>(CustomerAuthenticationDefaults.SessionPath);

        Assert.True(response!.Authenticated);
        Assert.True(response.IsAdmin);
    }

    [Fact]
    public async Task Session_reports_ordinary_customer_cookie_as_non_admin()
    {
        var client = _app.CreateClient();
        _app.AddControlSessionCookie(client, new Claim("role", "customer"));

        var response = await client.GetControlJsonAsync<CustomerAuthSessionResponse>(CustomerAuthenticationDefaults.SessionPath);

        Assert.True(response!.Authenticated);
        Assert.False(response.IsAdmin);
    }

    [Fact]
    public async Task Login_fails_closed_when_customer_oidc_is_not_configured()
    {
        await using var app = new ControlApiTestApplication(new Dictionary<string, string?>
        {
            [$"{ControlIdentityDefaults.ConfigurationSection}:Authority"] = "",
            [$"{ControlIdentityDefaults.ConfigurationSection}:ClientId"] = "",
            [TrustedHeaderWorkspaceIdentityReader.EnabledConfigurationKey] = "false"
        });

        var response = await app.CreateClient(new() { AllowAutoRedirect = false })
            .GetAsync($"{CustomerAuthenticationDefaults.LoginPath}?returnUrl=/admin/runtime-builder");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData(null, CustomerAuthenticationDefaults.DefaultReturnPath)]
    [InlineData("", CustomerAuthenticationDefaults.DefaultReturnPath)]
    [InlineData("/admin/runtime-builder", "/admin/runtime-builder")]
    [InlineData("relative/path", CustomerAuthenticationDefaults.DefaultReturnPath)]
    [InlineData("//evil.example/admin", CustomerAuthenticationDefaults.DefaultReturnPath)]
    [InlineData("\\\\evil.example\\admin", CustomerAuthenticationDefaults.DefaultReturnPath)]
    [InlineData("https://evil.example/admin", CustomerAuthenticationDefaults.DefaultReturnPath)]
    [InlineData("/admin/login", CustomerAuthenticationDefaults.DefaultReturnPath)]
    [InlineData("/admin/logout", CustomerAuthenticationDefaults.DefaultReturnPath)]
    [InlineData("/api/auth/login", CustomerAuthenticationDefaults.DefaultReturnPath)]
    [InlineData("/api/auth/logout", CustomerAuthenticationDefaults.DefaultReturnPath)]
    [InlineData("/api/auth/sign-in", CustomerAuthenticationDefaults.DefaultReturnPath)]
    [InlineData("/api/auth/sign-out", CustomerAuthenticationDefaults.DefaultReturnPath)]
    [InlineData("/api/auth/callback", CustomerAuthenticationDefaults.DefaultReturnPath)]
    public void Safe_return_url_accepts_only_root_relative_paths(string? returnUrl, string expected)
    {
        Assert.Equal(expected, CustomerAuthEndpoints.GetSafeReturnUrl(returnUrl));
    }

    [Fact]
    public void Customer_session_cookie_is_separate_from_operator_cookie()
    {
        var app = _app;
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();

        var customer = options.Get(CustomerAuthenticationDefaults.CookieScheme);
        var admin = options.Get(AdminDashboardAuthenticationDefaults.Scheme);

        Assert.Equal(CustomerAuthenticationDefaults.CookieName, customer.Cookie.Name);
        Assert.NotEqual(admin.Cookie.Name, customer.Cookie.Name);
        Assert.True(customer.Cookie.HttpOnly);
        Assert.Equal(CustomerAuthenticationDefaults.SessionLifetime, customer.ExpireTimeSpan);
        Assert.True(customer.SlidingExpiration);
    }

    [Fact]
    public void Customer_oidc_challenge_uses_callback_scoped_cookies_without_pushed_authorization()
    {
        var options = new OpenIdConnectOptions();
        CustomerOidcOptionsConfigurator.Configure(options, new ControlIdentityOptions
        {
            Authority = "https://identity.example/realms/elsa-control",
            ClientId = "elsa-control-console",
            RedirectUri = CustomerAuthenticationDefaults.CallbackPath
        });

        Assert.Equal(PushedAuthorizationBehavior.Disable, options.PushedAuthorizationBehavior);
        Assert.Equal(CustomerAuthenticationDefaults.CallbackPath, options.CorrelationCookie.Path);
        Assert.Equal(CustomerAuthenticationDefaults.CallbackPath, options.NonceCookie.Path);
    }

    [Fact]
    public async Task Logout_rejects_cross_site_post()
    {
        var app = _app;
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, CustomerAuthenticationDefaults.LogoutPath);
        request.Headers.Add(HeaderNames.Origin, "https://evil.example");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Logout_accepts_same_origin_post()
    {
        var app = _app;
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, CustomerAuthenticationDefaults.LogoutPath);
        request.Headers.Add(HeaderNames.Origin, "http://localhost");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
