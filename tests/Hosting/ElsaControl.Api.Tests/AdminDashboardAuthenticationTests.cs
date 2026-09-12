using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using ElsaControl.Api.Admin.Sources;
using ElsaControl.Api.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace ElsaControl.Api.Tests;

public sealed class AdminDashboardAuthenticationTests : IClassFixture<DefaultControlApiTestApplicationFixture>
{
    private readonly ControlApiTestApplication _app;

    public AdminDashboardAuthenticationTests(DefaultControlApiTestApplicationFixture fixture) => _app = fixture.Application;

    [Fact]
    public async Task Dashboard_route_renders_local_console_shell_without_starting_oidc_challenge()
    {
        var app = _app;
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/overview");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains("/api/auth/login?returnUrl=%2Fadmin%2Foverview", (await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Dashboard_root_serves_console_shell_without_redirect()
    {
        var app = _app;
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains("/api/auth/login?returnUrl=%2Fadmin%2Foverview", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Dashboard_root_serves_console_shell_when_forwarded_as_path_base()
    {
        var app = _app;
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(ControlApiTestApplication.TestPathBaseHeader, "/admin");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains("/api/auth/login?returnUrl=%2Fadmin%2Foverview", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Dashboard_shell_renders_local_sign_in_fallback_without_starting_oidc_challenge()
    {
        var app = _app;
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains("/api/auth/login?returnUrl=%2Fadmin%2Foverview", (await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Missing_dashboard_asset_remains_not_found()
    {
        var app = _app;
        var response = await app.CreateClient(new() { AllowAutoRedirect = false })
            .GetAsync("/admin/assets/index.js");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_route_proxies_to_development_console_when_configured()
    {
        await using var console = await DevelopmentConsoleStub.StartAsync();
        await using var app = new ControlApiTestApplication(new Dictionary<string, string?>
        {
            [AdminDashboardAuthenticationDefaults.DevelopmentUrlConfigurationKey] = console.Url
        });

        var response = await app.CreateClient(new() { AllowAutoRedirect = false })
            .GetAsync("/admin/overview?tab=workspaces");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/admin/overview?tab=workspaces", (await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Legacy_login_path_redirects_to_control_sign_in()
    {
        var app = _app;
        var response = await app.CreateClient(new() { AllowAutoRedirect = false })
            .GetAsync($"{AdminDashboardAuthenticationDefaults.LoginPath}?returnUrl=/admin/sources");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"{CustomerAuthenticationDefaults.LoginPath}?returnUrl=%2Fadmin%2Fsources", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Legacy_login_path_ignores_unsafe_return_url()
    {
        var app = _app;
        var response = await app.CreateClient(new() { AllowAutoRedirect = false })
            .GetAsync($"{AdminDashboardAuthenticationDefaults.LoginPath}?returnUrl=https://evil.example/admin");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"{CustomerAuthenticationDefaults.LoginPath}?returnUrl={Uri.EscapeDataString(AdminDashboardAuthenticationDefaults.DefaultReturnPath)}", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Legacy_login_path_ignores_logout_return_url()
    {
        var app = _app;
        var response = await app.CreateClient(new() { AllowAutoRedirect = false })
            .GetAsync($"{AdminDashboardAuthenticationDefaults.LoginPath}?returnUrl={AdminDashboardAuthenticationDefaults.LogoutPath}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"{CustomerAuthenticationDefaults.LoginPath}?returnUrl={Uri.EscapeDataString(AdminDashboardAuthenticationDefaults.DefaultReturnPath)}", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Legacy_logout_post_preserves_post_method_for_customer_logout()
    {
        var app = _app;
        var response = await app.CreateClient(new() { AllowAutoRedirect = false })
            .PostAsync(AdminDashboardAuthenticationDefaults.LogoutPath, content: null);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal($"{CustomerAuthenticationDefaults.LogoutPath}?returnUrl={Uri.EscapeDataString(AdminDashboardAuthenticationDefaults.DefaultReturnPath)}", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Api_key_authorizes_admin_api_for_machine_access()
    {
        var app = _app;
        await app.SeedAsync(_ => Task.CompletedTask);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");

        var sources = await client.GetControlJsonAsync<List<AdminSourceResponse>>("/api/admin/sources");

        Assert.NotNull(sources);
    }

    [Fact]
    public async Task Control_admin_session_authorizes_admin_api()
    {
        var app = _app;
        await app.SeedAsync(_ => Task.CompletedTask);
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        app.AddControlSessionCookie(client, new Claim("role", AdminAuthorization.ControlAdminRole));

        var response = await client.GetAsync("/api/admin/sources");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Control_session_without_admin_role_is_forbidden_for_admin_api()
    {
        var app = _app;
        await app.SeedAsync(_ => Task.CompletedTask);
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        app.AddControlSessionCookie(client);

        var response = await client.GetAsync("/api/admin/sources");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Configured_local_customer_session_can_authorize_admin_api()
    {
        await using var app = new ControlApiTestApplication(new Dictionary<string, string?>
        {
            ["Authentication:Admin:AllowAuthenticatedCustomerSession"] = "true"
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        app.AddControlSessionCookie(client);

        var response = await client.GetAsync("/api/admin/sources");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Configured_production_customer_session_can_authorize_admin_api()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCatalogAuthorization();
        services.AddSingleton<IOptions<AdminAuthorizationOptions>>(Options.Create(new AdminAuthorizationOptions
        {
            AllowAuthenticatedCustomerSession = true
        }));
        services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment(Environments.Production));
        await using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "admin-user")],
            CustomerAuthenticationDefaults.CookieScheme));

        var result = await authorization.AuthorizeAsync(principal, resource: null, AdminAuthorization.Policy);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Control_admin_api_mutation_rejects_cross_origin_request()
    {
        var app = _app;
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        app.AddControlSessionCookie(client, new Claim("role", AdminAuthorization.ControlAdminRole));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/sync/packages/Elsa.Workflows");
        request.Headers.Add(HeaderNames.Origin, "https://evil.example");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Control_admin_api_mutation_accepts_same_origin_request()
    {
        var app = _app;
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        app.AddControlSessionCookie(client, new Claim("role", AdminAuthorization.ControlAdminRole));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/sync/packages/Elsa.Workflows");
        request.Headers.Add(HeaderNames.Origin, "http://localhost");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Control_admin_api_mutation_accepts_same_origin_referer_fallback()
    {
        var app = _app;
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        app.AddControlSessionCookie(client, new Claim("role", AdminAuthorization.ControlAdminRole));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/sync/packages/Elsa.Workflows");
        request.Headers.Referrer = new Uri("http://localhost/admin/overview");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Control_admin_api_mutation_rejects_missing_origin_and_referer()
    {
        var app = _app;
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        app.AddControlSessionCookie(client, new Claim("role", AdminAuthorization.ControlAdminRole));

        var response = await client.PostAsync("/api/admin/sync/packages/Elsa.Workflows", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_api_mutation_fails_closed_without_api_key_or_valid_control_session()
    {
        var app = _app;
        var client = app.CreateClient(new() { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/sync/packages/Elsa.Workflows");
        request.Headers.Add(HeaderNames.Origin, "http://localhost");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Same_origin_validation_uses_effective_request_host()
    {
        var app = _app;
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        app.AddControlSessionCookie(client, new Claim("role", AdminAuthorization.ControlAdminRole));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/sync/packages/Elsa.Workflows");
        request.Headers.Host = "catalog.example";
        request.Headers.Add(HeaderNames.Origin, "http://catalog.example");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Same_origin_validation_uses_forwarded_scheme()
    {
        var app = _app;
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        app.AddControlSessionCookie(client, new Claim("role", AdminAuthorization.ControlAdminRole));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/sync/packages/Elsa.Workflows");
        request.Headers.Host = "catalog.example";
        request.Headers.Add(HeaderNames.Origin, "https://catalog.example");
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Same_origin_validation_uses_forwarded_host()
    {
        var app = _app;
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        app.AddControlSessionCookie(client, new Claim("role", AdminAuthorization.ControlAdminRole));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/sync/packages/Elsa.Workflows");
        request.Headers.Host = "internal.example";
        request.Headers.Add(HeaderNames.Origin, "https://catalog.example");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "catalog.example");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Api_key_authenticated_admin_api_mutation_bypasses_browser_origin_check()
    {
        var app = _app;
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/sync/packages/Elsa.Workflows");
        request.Headers.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        request.Headers.Add(HeaderNames.Origin, "https://evil.example");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Public_endpoint_remains_anonymous()
    {
        var app = _app;
        var response = await app.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class TestWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = nameof(AdminDashboardAuthenticationTests);
        public string EnvironmentName { get; set; } = environmentName;
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class DevelopmentConsoleStub(WebApplication app, string url) : IAsyncDisposable
    {
        private readonly WebApplication _app = app;

        public string Url { get; } = url;

        public static async Task<DevelopmentConsoleStub> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.MapGet("/admin/{*path}", (HttpRequest request) =>
                Results.Text($"{request.Path}{request.QueryString}", "text/plain"));
            await app.StartAsync();

            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!;
            return new DevelopmentConsoleStub(app, addresses.Addresses.Single());
        }

        public async ValueTask DisposeAsync() =>
            await _app.DisposeAsync();
    }
}
