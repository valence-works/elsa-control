using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class ManagedElsaHandoffTests
{
    private const string CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string WrongCodeVerifier = "aBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    [Fact]
    public async Task Existing_control_identity_can_issue_and_redeem_one_time_handoff()
    {
        var organizationId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var authorizer = new FakeHandoffAuthorizer(organizationId, instanceId);
        await using var app = CreateApplication(authorizer);
        await app.SeedAsync(_ => Task.CompletedTask);
        var controlExpiresAt = DateTimeOffset.UtcNow.AddMinutes(4);
        var client = app.CreateControlIdentityClient(subject: "handoff-user", expires: controlExpiresAt);

        var issue = await client.PostControlJsonAsync(
            "/api/managed-elsa/handoff/issue",
            new ManagedElsaHandoffIssueRequest(
                organizationId,
                instanceId,
                authorizer.Audience,
                authorizer.RedirectUri.OriginalString,
                authorizer.CodeChallenge));

        Assert.Equal(HttpStatusCode.OK, issue.StatusCode);
        Assert.Contains("no-store", issue.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Contains("no-cache", issue.Headers.Pragma.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        var issued = (await issue.Content.ReadControlJsonAsync<ManagedElsaHandoffIssueResponse>())!;
        Assert.Equal(ManagedElsaHandoffDefaults.TokenType, issued.TokenType);
        Assert.Equal(authorizer.Audience, issued.Audience);
        Assert.Equal(authorizer.RedirectUri.OriginalString, issued.RedirectUri);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.Token);
        var sessionExpiresAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(
            jwt.Claims.Single(x => x.Type == ManagedElsaHandoffDefaults.SessionExpiryClaim).Value,
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(controlExpiresAt.ToUnixTimeSeconds(), sessionExpiresAt.ToUnixTimeSeconds());
        Assert.True(sessionExpiresAt > issued.ExpiresAt);

        var redeem = await app.CreateClient().PostControlJsonAsync(
            "/api/managed-elsa/handoff/redeem",
            new ManagedElsaHandoffRedeemRequest(issued.Token, authorizer.Audience, authorizer.RedirectUri.OriginalString, CodeVerifier));

        Assert.Equal(HttpStatusCode.OK, redeem.StatusCode);
        Assert.Contains("no-store", redeem.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Contains("no-cache", redeem.Headers.Pragma.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        var session = (await redeem.Content.ReadControlJsonAsync<ManagedElsaHandoffRedeemResponse>())!;
        Assert.Equal(authorizer.OrganizationId, session.OrganizationId);
        Assert.Equal(authorizer.InstanceId, session.InstanceId);
        Assert.Contains(ManagedElsaHandoffDefaults.RuntimeSessionScope, session.Scopes);
        Assert.Equal(sessionExpiresAt, session.SessionExpiresAt);
    }

    [Fact]
    public async Task Issued_session_bound_is_capped_by_configured_runtime_maximum()
    {
        var authorizer = new FakeHandoffAuthorizer(Guid.NewGuid(), Guid.NewGuid());
        await using var app = CreateApplication(authorizer, new Dictionary<string, string?>
        {
            [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:RuntimeSessionMaximumLifetime"] = "00:02:00"
        });
        await app.SeedAsync(_ => Task.CompletedTask);

        var response = await app.CreateControlIdentityClient(
                subject: "bounded-handoff-user",
                expires: DateTimeOffset.UtcNow.AddHours(1))
            .PostControlJsonAsync("/api/managed-elsa/handoff/issue", IssueRequest(authorizer));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var issued = (await response.Content.ReadControlJsonAsync<ManagedElsaHandoffIssueResponse>())!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issued.Token);
        var sessionExpiresAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(
            jwt.Claims.Single(x => x.Type == ManagedElsaHandoffDefaults.SessionExpiryClaim).Value,
            System.Globalization.CultureInfo.InvariantCulture));
        var seconds = (sessionExpiresAt - issued.IssuedAt).TotalSeconds;
        Assert.InRange(seconds, 119, 120);
    }

    [Fact]
    public async Task Expiring_control_session_is_rejected_before_handoff_issue()
    {
        var authorizer = new FakeHandoffAuthorizer(Guid.NewGuid(), Guid.NewGuid());
        await using var app = CreateApplication(authorizer);
        await app.SeedAsync(_ => Task.CompletedTask);

        var response = await app.CreateControlIdentityClient(
                subject: "expiring-handoff-user",
                expires: DateTimeOffset.UtcNow.AddSeconds(30))
            .PostControlJsonAsync("/api/managed-elsa/handoff/issue", IssueRequest(authorizer));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void Cookie_session_bound_comes_only_from_authentication_properties()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(2);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Iss, ControlApiTestApplication.TestControlIdentityIssuer),
            new Claim(JwtRegisteredClaimNames.Sub, "cookie-user")
        ], CustomerAuthenticationDefaults.CookieScheme));
        var properties = new AuthenticationProperties { ExpiresUtc = expiresAt };
        var ticket = new AuthenticationTicket(principal, properties, CustomerAuthenticationDefaults.CookieScheme);

        var session = CustomerSessionIdentityReader.ToAuthenticatedControlSession(
            AuthenticateResult.Success(ticket),
            new ControlIdentityOptions { Issuer = ControlApiTestApplication.TestControlIdentityIssuer });
        var missingBound = CustomerSessionIdentityReader.ToAuthenticatedControlSession(
            AuthenticateResult.Success(new AuthenticationTicket(
                principal,
                new AuthenticationProperties(),
                CustomerAuthenticationDefaults.CookieScheme)),
            new ControlIdentityOptions { Issuer = ControlApiTestApplication.TestControlIdentityIssuer });

        Assert.Equal("cookie-user", session?.Identity.Subject);
        Assert.Equal(expiresAt.ToUnixTimeSeconds(), session?.ExpiresAt.ToUnixTimeSeconds());
        Assert.Null(missingBound);
    }

    [Theory]
    [InlineData("Bearer invalid-token")]
    [InlineData(" Basic cookie,   Bearer invalid-token")]
    public async Task Invalid_bearer_does_not_fall_back_to_an_authenticated_cookie_user(string authorization)
    {
        var cookieUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Iss, ControlApiTestApplication.TestControlIdentityIssuer),
            new Claim(JwtRegisteredClaimNames.Sub, "cookie-user")
        ], CustomerAuthenticationDefaults.CookieScheme));
        var context = new DefaultHttpContext
        {
            User = cookieUser,
            RequestServices = new ServiceCollection()
                .AddSingleton<IAuthenticationService>(new FailedAuthenticationService())
                .BuildServiceProvider()
        };
        context.Request.Headers.Authorization = authorization;

        var reader = new ControlIdentityReader(Options.Create(new ControlIdentityOptions
        {
            Issuer = ControlApiTestApplication.TestControlIdentityIssuer
        }));

        Assert.Null(await reader.ReadAsync(context));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("9223372036854775807")]
    public void Bearer_session_bound_rejects_missing_malformed_or_out_of_range_expiry(string? value)
    {
        var claims = value is null ? [] : new[] { new Claim(JwtRegisteredClaimNames.Exp, value) };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ControlIdentityDefaults.Scheme));

        Assert.False(ControlIdentityReader.TryReadBearerExpiry(principal, out _));
    }

    [Fact]
    public async Task Production_wiring_creates_persisted_binding_and_issues_handoff_token()
    {
        await using var app = new ControlApiTestApplication(new Dictionary<string, string?>
        {
            [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:Enabled"] = "true"
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        var client = app.CreateControlIdentityClient(subject: "managed-owner");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        var instanceId = Guid.NewGuid();
        Guid organizationId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var workspace = await db.Workspaces.SingleAsync(x => x.Id == workspaceId);
            organizationId = workspace.OrganizationId;
            db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
            {
                OrganizationId = organizationId,
                ManagedHostingEnabled = true,
                SubscriptionState = OrganizationSubscriptionState.Active,
                MaxInstances = int.MaxValue,
                SyncedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            var lifecycle = new ElsaInstanceLifecycleService(
                new EfCoreElsaInstanceLifecycleStore(db, new EmptyLifecycleResolutionInputSource()));
            await lifecycle.CreateAsync(new ElsaInstanceCreateRequest(
                organizationId,
                workspaceId,
                "Managed Elsa",
                "managed-elsa",
                new ElsaInstanceIntent(
                    new ElsaReleaseIntent("server-studio", "3.10", "3.10.4"),
                    new ElsaApplicationIntent("combined"),
                    new ElsaPlacementIntent(
                        "managed", "westeurope", "dedicated", "standard-small", "public", "managed")),
                "managed-handoff-production-wiring",
                instanceId));
            const string deploymentId = "deployment-managed";
            const string endpointUri = "https://managed.example.test";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE ElsaInstances SET CurrentDeploymentId = {deploymentId}, CurrentDeploymentEndpointUri = {endpointUri}, CurrentDeploymentManagedHandoff = {true}, DesiredLifecycle = {ElsaDesiredLifecycle.Running.ToString()}, ObservedLifecycle = {ElsaObservedLifecycle.Ready.ToString()}, Health = {ElsaInstanceHealth.Healthy.ToString()} WHERE Id = {instanceId}");
            db.ChangeTracker.Clear();
        }

        var audience = ElsaInstanceIdentityBinding.AudienceFor(instanceId);
        var callback = ElsaInstanceIdentityBinding.CanonicalizeCallbackUri("https://managed.example.test");
        var handoffRequest = new ManagedElsaHandoffIssueRequest(
            organizationId, instanceId, audience, callback,
            ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier));
        var unauthorized = await app.CreateControlIdentityClient("managed-outsider")
            .PostControlJsonAsync("/api/managed-elsa/handoff/issue", handoffRequest);
        Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var identities = scope.ServiceProvider.GetRequiredService<IManagedElsaInstanceIdentityStore>();
            Assert.Null(await identities.FindAsync(organizationId, instanceId));
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var identities = new EfCoreManagedElsaInstanceIdentityStore(db);
            var binding = await identities.BindAsync(
                organizationId,
                workspaceId,
                instanceId,
                "https://managed.example.test",
                expectedBindingVersion: null,
                DateTimeOffset.UtcNow);
            Assert.True(binding.Succeeded);
        }

        var response = await client.PostControlJsonAsync(
            "/api/managed-elsa/handoff/issue",
            handoffRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(ElsaDesiredLifecycle.Stopped, ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy, true)]
    [InlineData(ElsaDesiredLifecycle.Running, ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Degraded, true)]
    [InlineData(ElsaDesiredLifecycle.Running, ElsaObservedLifecycle.Failed, ElsaInstanceHealth.Unreachable, true)]
    [InlineData(ElsaDesiredLifecycle.Running, ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy, false)]
    public async Task Direct_issue_requires_a_healthy_currently_running_bound_instance(
        ElsaDesiredLifecycle desiredLifecycle,
        ElsaObservedLifecycle observedLifecycle,
        ElsaInstanceHealth health,
        bool bind)
    {
        var setup = await SeedManagedInstanceAsync(desiredLifecycle, observedLifecycle, health, bind);
        await using var app = setup.App;

        var response = await setup.Client.PostControlJsonAsync(
            "/api/managed-elsa/handoff/issue",
            new ManagedElsaHandoffIssueRequest(
                setup.OrganizationId,
                setup.InstanceId,
                setup.Audience,
                setup.RedirectUri,
                ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Redemption_fails_closed_after_instance_health_transitions_away_from_healthy()
    {
        var setup = await SeedManagedInstanceAsync(
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceHealth.Healthy,
            bind: true);
        await using var app = setup.App;

        var issue = await setup.Client.PostControlJsonAsync(
            "/api/managed-elsa/handoff/issue",
            new ManagedElsaHandoffIssueRequest(
                setup.OrganizationId,
                setup.InstanceId,
                setup.Audience,
                setup.RedirectUri,
                ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier)));
        Assert.Equal(HttpStatusCode.OK, issue.StatusCode);
        var issued = (await issue.Content.ReadControlJsonAsync<ManagedElsaHandoffIssueResponse>())!;

        await SetInstanceStateAsync(app, setup.InstanceId, ElsaDesiredLifecycle.Running, ElsaObservedLifecycle.Degraded, ElsaInstanceHealth.Degraded);

        var redeem = await app.CreateClient().PostControlJsonAsync(
            "/api/managed-elsa/handoff/redeem",
            new ManagedElsaHandoffRedeemRequest(issued.Token, setup.Audience, setup.RedirectUri, CodeVerifier));

        Assert.Equal(HttpStatusCode.Forbidden, redeem.StatusCode);
    }

    [Fact]
    public async Task Issue_and_redemption_fail_closed_when_the_current_deployment_carries_no_managed_handoff()
    {
        var setup = await SeedManagedInstanceAsync(
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceHealth.Healthy,
            bind: true);
        await using var app = setup.App;
        var request = new ManagedElsaHandoffIssueRequest(
            setup.OrganizationId,
            setup.InstanceId,
            setup.Audience,
            setup.RedirectUri,
            ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier));

        var issue = await setup.Client.PostControlJsonAsync("/api/managed-elsa/handoff/issue", request);
        Assert.Equal(HttpStatusCode.OK, issue.StatusCode);
        var issued = (await issue.Content.ReadControlJsonAsync<ManagedElsaHandoffIssueResponse>())!;

        // A redeploy without the handoff leaves the runtime without its endpoints: an already issued code
        // must not become a session, and no further code may be issued for it.
        await SetInstanceStateAsync(app, setup.InstanceId, ElsaDesiredLifecycle.Running, ElsaObservedLifecycle.Ready,
            ElsaInstanceHealth.Healthy, managedHandoff: false);

        var redeem = await app.CreateClient().PostControlJsonAsync(
            "/api/managed-elsa/handoff/redeem",
            new ManagedElsaHandoffRedeemRequest(issued.Token, setup.Audience, setup.RedirectUri, CodeVerifier));
        Assert.Equal(HttpStatusCode.Forbidden, redeem.StatusCode);
        var reissue = await setup.Client.PostControlJsonAsync("/api/managed-elsa/handoff/issue", request);
        Assert.Equal(HttpStatusCode.Forbidden, reissue.StatusCode);
    }

    [Theory]
    [InlineData("instanceId", "codeChallenge")]
    [InlineData("instance_id", "code_challenge")]
    public void Continuation_parser_accepts_runtime_camel_and_snake_query_names(string instanceKey, string challengeKey)
    {
        var instanceId = Guid.NewGuid();
        const string state = "state-value-that-is-long-enough";
        var challenge = ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier);
        var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            [instanceKey] = instanceId.ToString("D"),
            ["state"] = state,
            [challengeKey] = challenge
        });

        Assert.True(ManagedElsaHandoffContinuation.TryParse(query, out var parsed));
        Assert.Equal(instanceId, parsed.InstanceId);
        Assert.Equal(state, parsed.State);
        Assert.Equal(challenge, parsed.CodeChallenge);
    }

    [Fact]
    public void Continuation_parser_leaves_handoff_status_errors_to_the_console()
    {
        var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["instanceId"] = Guid.NewGuid().ToString("D"),
            ["state"] = "state-value-that-is-long-enough",
            ["codeChallenge"] = ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier),
            ["handoff_status"] = "403"
        });

        Assert.False(ManagedElsaHandoffContinuation.TryParse(query, out _));
    }

    [Fact]
    public async Task CamelCase_continuation_get_issues_and_auto_posts_to_the_bound_callback()
    {
        var setup = await SeedManagedInstanceAsync(
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceHealth.Healthy,
            bind: true);
        await using var app = setup.App;
        var challenge = ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier);
        const string state = "state-value-that-is-long-enough";
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = setup.Client.DefaultRequestHeaders.Authorization;

        using var response = await client.GetAsync(ContinuationPath(setup.InstanceId, state, challenge));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(response.Headers.Location);
        Assert.DoesNotContain("/admin/runtimes", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Sign in", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/login", html, StringComparison.Ordinal);
        Assert.Contains("<title>Opening managed Elsa</title>", html, StringComparison.Ordinal);
        var form = ParseAutoSubmitForm(html);
        Assert.Equal(setup.RedirectUri, form.Action);
        Assert.Equal(state, form.State);
        Assert.False(string.IsNullOrWhiteSpace(form.Code));

        var redeem = await app.CreateClient().PostControlJsonAsync(
            "/api/managed-elsa/handoff/redeem",
            new ManagedElsaHandoffRedeemRequest(form.Code, setup.Audience, setup.RedirectUri, CodeVerifier));
        Assert.Equal(HttpStatusCode.OK, redeem.StatusCode);
        var session = (await redeem.Content.ReadControlJsonAsync<ManagedElsaHandoffRedeemResponse>())!;
        Assert.Equal(setup.OrganizationId, session.OrganizationId);
        Assert.Equal(setup.InstanceId, session.InstanceId);
        Assert.Contains(ManagedElsaHandoffDefaults.RuntimeSessionScope, session.Scopes);
    }

    [Fact]
    public async Task Unauthenticated_continuation_returns_to_control_login_not_studio()
    {
        var setup = await SeedManagedInstanceAsync(
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceHealth.Healthy,
            bind: true);
        await using var app = setup.App;
        var challenge = ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier);
        const string state = "state-value-that-is-long-enough";
        var path = ContinuationPath(setup.InstanceId, state, challenge);
        var client = app.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await client.GetAsync(path);
        var location = response.Headers.Location;
        var locationText = location?.ToString() ?? "";
        var loginPath = location is { IsAbsoluteUri: true } absolute
            ? absolute.AbsolutePath
            : locationText.Split('?', 2)[0];

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(ManagedElsaHandoffContinuation.LoginPath, loginPath);
        Assert.Contains(Uri.EscapeDataString(path), locationText, StringComparison.Ordinal);
        Assert.DoesNotContain("/login?", locationText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Continuation_get_denies_an_outsider_without_opening_studio()
    {
        var setup = await SeedManagedInstanceAsync(
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceHealth.Healthy,
            bind: true);
        await using var app = setup.App;
        var challenge = ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier);
        const string state = "state-value-that-is-long-enough";
        var outsider = app.CreateControlIdentityClient("managed-outsider");

        using var response = await outsider.GetAsync(ContinuationPath(setup.InstanceId, state, challenge));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("handoff.denied", document.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("Opening managed Elsa", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/login", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Console_runtimes_route_without_continuation_params_is_not_intercepted()
    {
        var setup = await SeedManagedInstanceAsync(
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceHealth.Healthy,
            bind: true);
        await using var app = setup.App;

        using var response = await setup.Client.GetAsync(ManagedElsaHandoffDefaults.ConsoleContinuationPath);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Elsa Control Console", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Opening managed Elsa", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handoff_status_continuation_is_left_to_the_console()
    {
        var setup = await SeedManagedInstanceAsync(
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceHealth.Healthy,
            bind: true);
        await using var app = setup.App;

        using var response = await setup.Client.GetAsync(
            $"{ManagedElsaHandoffDefaults.ConsoleContinuationPath}?instanceId={setup.InstanceId:D}&handoff_status=403");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Elsa Control Console", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Opening managed Elsa", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Continuation_head_does_not_issue_a_code()
    {
        var setup = await SeedManagedInstanceAsync(
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceHealth.Healthy,
            bind: true);
        await using var app = setup.App;
        var challenge = ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier);
        const string state = "state-value-that-is-long-enough";
        using var request = new HttpRequestMessage(HttpMethod.Head, ContinuationPath(setup.InstanceId, state, challenge));
        request.Headers.Authorization = setup.Client.DefaultRequestHeaders.Authorization;

        using var response = await app.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True((response.Content.Headers.ContentLength ?? 0) == 0);
    }

    private static string ContinuationPath(Guid instanceId, string state, string codeChallenge) =>
        $"{ManagedElsaHandoffDefaults.ConsoleContinuationPath}?instanceId={instanceId:D}&state={state}&codeChallenge={codeChallenge}";

    private static (string Action, string Code, string State) ParseAutoSubmitForm(string html)
    {
        var action = System.Text.RegularExpressions.Regex.Match(html, """<form method="post" action="([^"]+)">""").Groups[1].Value;
        var code = System.Text.RegularExpressions.Regex.Match(html, """name="code" value="([^"]+)"""").Groups[1].Value;
        var state = System.Text.RegularExpressions.Regex.Match(html, """name="state" value="([^"]+)"""").Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(action));
        Assert.False(string.IsNullOrWhiteSpace(code));
        Assert.False(string.IsNullOrWhiteSpace(state));
        return (action, code, state);
    }

    private sealed class EmptyLifecycleResolutionInputSource : IElsaInstanceLifecycleResolutionInputSource
    {
        public Task<ElsaInstanceLifecycleResolutionInput?> GetAsync(
            ElsaInstance instance,
            ElsaInstanceOperation operation,
            CancellationToken cancellationToken = default) => Task.FromResult<ElsaInstanceLifecycleResolutionInput?>(null);
    }

    private sealed class FailedAuthenticationService : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Fail("invalid bearer"));

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task Handoff_endpoint_rejects_cross_organization_target()
    {
        var authorizer = new FakeHandoffAuthorizer(Guid.NewGuid(), Guid.NewGuid());
        await using var app = CreateApplication(authorizer);
        await app.SeedAsync(_ => Task.CompletedTask);
        var client = app.CreateControlIdentityClient(subject: "handoff-user");

        var response = await client.PostControlJsonAsync(
            "/api/managed-elsa/handoff/issue",
            new ManagedElsaHandoffIssueRequest(
                Guid.NewGuid(),
                authorizer.InstanceId,
                authorizer.Audience,
                authorizer.RedirectUri.OriginalString,
                authorizer.CodeChallenge));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Denied_caller_controlled_target_is_a_safe_forbidden_response()
    {
        var authorizer = new FakeHandoffAuthorizer(Guid.NewGuid(), Guid.NewGuid());
        await using var app = CreateApplication(authorizer);
        await app.SeedAsync(_ => Task.CompletedTask);

        var response = await app.CreateControlIdentityClient(subject: "handoff-user").PostControlJsonAsync(
            "/api/managed-elsa/handoff/issue",
            new ManagedElsaHandoffIssueRequest(
                Guid.Empty,
                Guid.Empty,
                "caller-controlled-audience",
                authorizer.RedirectUri.OriginalString,
                authorizer.CodeChallenge));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Replay_is_rejected_atomically()
    {
        using var fixture = CreateFixture();
        var token = fixture.Issue();

        var first = await fixture.RedeemAsync(token);
        var second = await fixture.RedeemAsync(token);

        Assert.True(first.Succeeded);
        Assert.Equal(ManagedElsaHandoffRedeemFailure.Replay, second.Failure);
        Assert.Contains(fixture.Audit.Events, audit => audit.Action == "redeem.succeeded");
        Assert.Contains(fixture.Audit.Events, audit => audit.Action == "redeem.replay_rejected");
    }

    [Fact]
    public async Task Concurrent_redeemers_allow_exactly_one_success()
    {
        using var fixture = CreateFixture();
        var token = fixture.Issue();

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => fixture.RedeemAsync(token)));

        Assert.Single(results, result => result.Succeeded);
        Assert.Equal(31, results.Count(result => result.Failure == ManagedElsaHandoffRedeemFailure.Replay));
        Assert.Equal(31, fixture.Audit.Events.Count(x => x.Action == "redeem.replay_rejected"));
    }

    [Fact]
    public async Task Wrong_audience_is_rejected()
    {
        using var fixture = CreateFixture();

        var result = await fixture.RedeemAsync(fixture.Issue(), expectedAudience: "urn:elsa:instance:other");

        Assert.Equal(ManagedElsaHandoffRedeemFailure.InvalidToken, result.Failure);
    }

    [Fact]
    public async Task Missing_or_wrong_verifier_is_rejected()
    {
        using var fixture = CreateFixture();
        var token = fixture.Issue();

        var missing = await fixture.RedeemAsync(token, codeVerifier: "");
        var wrong = await fixture.RedeemAsync(fixture.Issue(), codeVerifier: WrongCodeVerifier);

        Assert.Equal(ManagedElsaHandoffRedeemFailure.InvalidToken, missing.Failure);
        Assert.Equal(ManagedElsaHandoffRedeemFailure.InvalidToken, wrong.Failure);
        Assert.Equal(2, fixture.Audit.Events.Count(audit => audit.Action == "redeem.invalid"));
    }

    [Fact]
    public async Task Malformed_verifier_is_rejected_before_hashing()
    {
        using var fixture = CreateFixture();

        var result = await fixture.RedeemAsync(fixture.Issue(), codeVerifier: "too-short");

        Assert.Equal(ManagedElsaHandoffRedeemFailure.InvalidToken, result.Failure);
        Assert.Contains(fixture.Audit.Events, audit => audit.Action == "redeem.invalid");
    }

    [Fact]
    public void Issued_token_contains_the_required_type_and_known_key_id()
    {
        using var fixture = CreateFixture();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(fixture.Issue());

        Assert.Equal(ManagedElsaHandoffDefaults.TokenType, jwt.Header.Typ);
        Assert.Equal("prototype", jwt.Header.Kid);
        Assert.Equal(
            fixture.Authorizer.BindingVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            jwt.Claims.Single(x => x.Type == "binding_version").Value);
    }

    [Fact]
    public async Task Wrong_token_type_and_unknown_key_id_are_rejected()
    {
        using var fixture = CreateFixture();
        var wrongType = fixture.IssueWithTokenType("JWT");
        using var unknownKeyRing = ManagedElsaHandoffKeyRing.CreateEphemeral();
        var alternateIssuer = new ManagedElsaHandoffIssuer(
            Options.Create(new ManagedElsaHandoffOptions
            {
                Enabled = true,
                Issuer = "https://cloud.example.test",
                TokenLifetime = TimeSpan.FromMinutes(1)
            }),
            unknownKeyRing,
            fixture.Clock);
        var unknownKey = alternateIssuer.Issue(
            new TrustedWorkspaceIdentity("https://idp.example.test", "subject", "User", "user@example.test"),
            fixture.Request,
            fixture.Authorizer.Authorization,
            fixture.Clock.GetUtcNow().AddHours(1)).Token;

        Assert.Equal(ManagedElsaHandoffRedeemFailure.InvalidToken, (await fixture.RedeemAsync(wrongType)).Failure);
        Assert.Equal(ManagedElsaHandoffRedeemFailure.InvalidToken, (await fixture.RedeemAsync(unknownKey)).Failure);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-10));
        using var fixture = CreateFixture(clock);

        var result = await fixture.RedeemAsync(fixture.Issue());

        Assert.Equal(ManagedElsaHandoffRedeemFailure.InvalidToken, result.Failure);
    }

    [Fact]
    public async Task Missing_or_extended_session_bound_is_rejected()
    {
        using var fixture = CreateFixture();

        var missing = await fixture.RedeemAsync(fixture.IssueWithoutSessionExpiry());
        var extended = await fixture.RedeemAsync(
            fixture.IssueWithSessionExpiry(fixture.Clock.GetUtcNow().AddDays(1)));

        Assert.Equal(ManagedElsaHandoffRedeemFailure.InvalidToken, missing.Failure);
        Assert.Equal(ManagedElsaHandoffRedeemFailure.InvalidToken, extended.Failure);
    }

    [Fact]
    public async Task Revoked_membership_is_checked_again_at_redeem()
    {
        using var fixture = CreateFixture();
        var token = fixture.Issue();
        fixture.Authorizer.IsAuthorized = false;

        var result = await fixture.RedeemAsync(token);

        Assert.Equal(ManagedElsaHandoffRedeemFailure.AuthorizationRevoked, result.Failure);
        Assert.Contains(fixture.Audit.Events, audit => audit.Action == "redeem.authorization_revoked");
    }

    [Fact]
    public async Task Rotated_instance_binding_invalidates_an_issued_handoff()
    {
        using var fixture = CreateFixture();
        var token = fixture.Issue();
        fixture.Authorizer.BindingVersion++;

        var result = await fixture.RedeemAsync(token);

        Assert.Equal(ManagedElsaHandoffRedeemFailure.AuthorizationRevoked, result.Failure);
    }

    [Fact]
    public async Task Legacy_token_without_binding_version_is_bounded_to_version_one()
    {
        using var fixture = CreateFixture();
        fixture.Authorizer.BindingVersion = 1;

        var result = await fixture.RedeemAsync(fixture.IssueWithoutBindingVersion());

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Claims!.BindingVersion);
    }

    [Fact]
    public async Task Redirect_binding_is_exact_and_rejects_a_different_callback()
    {
        using var fixture = CreateFixture();

        var result = await fixture.RedeemAsync(
            fixture.Issue(),
            expectedRedirectUri: new Uri("https://managed.example.test/another-callback"));

        Assert.Equal(ManagedElsaHandoffRedeemFailure.InvalidToken, result.Failure);
    }

    [Fact]
    public void Issue_rejects_uri_normalization_differences()
    {
        using var fixture = CreateFixture();
        var request = fixture.Request with
        {
            RedirectUri = new Uri(fixture.Authorizer.RedirectUri.OriginalString.Replace(
                "https://managed.example.test/",
                "https://managed.example.test:443/"))
        };

        Assert.Throws<InvalidOperationException>(() => fixture.Issue(request));
    }

    [Fact]
    public async Task Redeem_rejects_uri_normalization_differences()
    {
        using var fixture = CreateFixture();
        var normalizedDifferent = new Uri($"https://managed.example.test:443/instances/{fixture.Authorizer.InstanceId:D}/auth/callback");

        var result = await fixture.RedeemAsync(fixture.Issue(), expectedRedirectUri: normalizedDifferent);

        Assert.Equal(ManagedElsaHandoffRedeemFailure.InvalidToken, result.Failure);
    }

    [Fact]
    public void Key_ring_rejects_validation_key_that_duplicates_active_id()
    {
        using var active = RSA.Create(2048);
        using var duplicate = RSA.Create(2048);

        Assert.Throws<ArgumentException>(() => new ManagedElsaHandoffKeyRing(
            "active",
            active,
            [("active", duplicate)]));
    }

    [Fact]
    public void Runtime_session_maximum_must_exceed_the_handoff_code_lifetime()
    {
        using var keyRing = ManagedElsaHandoffKeyRing.CreateEphemeral();
        var options = Options.Create(new ManagedElsaHandoffOptions
        {
            TokenLifetime = TimeSpan.FromMinutes(1),
            RuntimeSessionMaximumLifetime = TimeSpan.FromMinutes(1)
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ManagedElsaHandoffIssuer(options, keyRing, TimeProvider.System));

        Assert.Contains("must exceed TokenLifetime", exception.Message, StringComparison.Ordinal);

        var excessive = Options.Create(new ManagedElsaHandoffOptions
        {
            RuntimeSessionMaximumLifetime = TimeSpan.FromHours(8).Add(TimeSpan.FromSeconds(1))
        });
        Assert.Throws<InvalidOperationException>(() =>
            new ManagedElsaHandoffIssuer(excessive, keyRing, TimeProvider.System));
    }

    [Fact]
    public void Session_bound_at_the_normalized_code_expiry_is_accepted()
    {
        var clock = new TestTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_900));
        using var fixture = CreateFixture(clock);
        var boundary = DateTimeOffset.FromUnixTimeSeconds(
            clock.GetUtcNow().AddMinutes(1).ToUnixTimeSeconds());

        var token = fixture.Issue(boundary);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal(
            boundary.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            jwt.Claims.Single(x => x.Type == ManagedElsaHandoffDefaults.SessionExpiryClaim).Value);
    }

    [Fact]
    public void Configured_key_ring_supports_active_and_previous_key_overlap()
    {
        using var active = RSA.Create(2048);
        using var previous = RSA.Create(2048);
        var options = new ManagedElsaHandoffOptions
        {
            ActiveKeyId = "active-2026-09",
            ActivePrivateKeyPem = active.ExportRSAPrivateKeyPem(),
            PreviousPublicKeys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["previous-2026-08"] = previous.ExportRSAPublicKeyPem()
            }
        };

        using var keyRing = ManagedElsaHandoffKeyRing.CreateConfigured(options);

        Assert.Equal("active-2026-09", keyRing.ActiveKeyId);
        Assert.True(keyRing.ContainsKey("active-2026-09"));
        Assert.True(keyRing.ContainsKey("previous-2026-08"));
    }

    [Fact]
    public async Task Production_configuration_validator_rejects_malformed_signing_key_at_startup()
    {
        var handoffOptions = new ManagedElsaHandoffOptions
        {
            Enabled = true,
            Issuer = "https://cloud.example.test",
            ActiveKeyId = "active-2026-09",
            ActivePrivateKeyPem = "not a pem"
        };
        await using var services = CreateKeyRingServices(handoffOptions);
        var validator = new ManagedElsaHandoffConfigurationValidator(
            new TestHostEnvironment(Environments.Production),
            Options.Create(handoffOptions),
            services);

        await Assert.ThrowsAsync<ArgumentException>(() => validator.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Production_configuration_validator_rejects_malformed_previous_key_at_startup()
    {
        using var active = RSA.Create(2048);
        var handoffOptions = new ManagedElsaHandoffOptions
        {
            Enabled = true,
            Issuer = "https://cloud.example.test",
            ActiveKeyId = "active-2026-09",
            ActivePrivateKeyPem = active.ExportRSAPrivateKeyPem(),
            PreviousPublicKeys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["previous-2026-08"] = "not a pem"
            }
        };
        await using var services = CreateKeyRingServices(handoffOptions);
        var validator = new ManagedElsaHandoffConfigurationValidator(
            new TestHostEnvironment(Environments.Production),
            Options.Create(handoffOptions),
            services);

        await Assert.ThrowsAsync<ArgumentException>(() => validator.StartAsync(CancellationToken.None));
    }

    [Fact]
    public void Key_ring_resolution_rejects_partial_active_key_configuration()
    {
        using var app = new ControlApiTestApplication(new Dictionary<string, string?>
        {
            [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:Enabled"] = "true",
            [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:ActiveKeyId"] = "active-2026-09"
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => app.Services.GetRequiredService<ManagedElsaHandoffKeyRing>());

        Assert.Contains("both key ID and private key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_handoff_with_partial_key_configuration_returns_correlated_unavailable()
    {
        await using var app = new ControlApiTestApplication(new Dictionary<string, string?>
        {
            [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:ActiveKeyId"] = "stray-key-id"
        });
        await app.SeedAsync(_ => Task.CompletedTask);

        var response = await app.CreateControlIdentityClient("disabled-handoff")
            .PostControlJsonAsync("/api/managed-elsa/handoff/issue", new { });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("correlationId").GetString()));
    }

    [Fact]
    public async Task Rate_limit_rejection_has_stable_code_and_correlation()
    {
        var authorizer = new FakeHandoffAuthorizer(Guid.NewGuid(), Guid.NewGuid());
        await using var app = CreateApplication(authorizer);
        await app.SeedAsync(_ => Task.CompletedTask);
        var client = app.CreateControlIdentityClient("rate-limited-handoff");
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 21; attempt++)
        {
            response?.Dispose();
            response = await client.PostControlJsonAsync("/api/managed-elsa/handoff/issue", new { });
        }

        using var finalResponse = response ?? throw new InvalidOperationException("Expected a rate-limit response.");
        Assert.Equal(HttpStatusCode.TooManyRequests, finalResponse.StatusCode);
        using var body = JsonDocument.Parse(await finalResponse.Content.ReadAsStringAsync());
        Assert.Equal("handoff.rate-limited", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("correlationId").GetString()));
    }

    private static ControlApiTestApplication CreateApplication(
        FakeHandoffAuthorizer authorizer,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:Enabled"] = "true"
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
            configuration[key] = value;

        return new ControlApiTestApplication(configuration, services =>
        {
            services.RemoveAll<IManagedElsaHandoffAuthorizer>();
            services.AddSingleton<IManagedElsaHandoffAuthorizer>(authorizer);
        });
    }

    private static async Task<ManagedInstanceSetup> SeedManagedInstanceAsync(
        ElsaDesiredLifecycle desiredLifecycle,
        ElsaObservedLifecycle observedLifecycle,
        ElsaInstanceHealth health,
        bool bind)
    {
        var app = new ControlApiTestApplication(new Dictionary<string, string?>
        {
            [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:Enabled"] = "true"
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        var client = app.CreateControlIdentityClient($"managed-health-{Guid.NewGuid():N}");
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        var instanceId = Guid.NewGuid();
        Guid organizationId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var workspace = await db.Workspaces.SingleAsync(x => x.Id == workspaceId);
            organizationId = workspace.OrganizationId;
            db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
            {
                OrganizationId = organizationId,
                ManagedHostingEnabled = true,
                SubscriptionState = OrganizationSubscriptionState.Active,
                MaxInstances = int.MaxValue,
                SyncedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            var lifecycle = new ElsaInstanceLifecycleService(
                new EfCoreElsaInstanceLifecycleStore(db, new EmptyLifecycleResolutionInputSource()));
            await lifecycle.CreateAsync(new ElsaInstanceCreateRequest(
                organizationId,
                workspaceId,
                "Managed Elsa",
                $"managed-elsa-{instanceId:N}",
                new ElsaInstanceIntent(
                    new ElsaReleaseIntent("server-studio", "3.10", "3.10.4"),
                    new ElsaApplicationIntent("combined"),
                    new ElsaPlacementIntent(
                        "managed", "westeurope", "dedicated", "standard-small", "public", "managed")),
                $"managed-health-{instanceId:N}",
                instanceId));
            await SetInstanceStateAsync(db, instanceId, desiredLifecycle, observedLifecycle, health);
        }

        var audience = ElsaInstanceIdentityBinding.AudienceFor(instanceId);
        var redirectUri = ElsaInstanceIdentityBinding.CanonicalizeCallbackUri("https://managed.example.test");
        if (bind)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var identities = scope.ServiceProvider.GetRequiredService<IManagedElsaInstanceIdentityStore>();
            var result = await identities.BindAsync(
                organizationId,
                workspaceId,
                instanceId,
                "https://managed.example.test",
                expectedBindingVersion: null,
                DateTimeOffset.UtcNow);
            if (!result.Succeeded)
                throw new InvalidOperationException("Test instance binding could not be created.");
        }

        return new(app, client, organizationId, workspaceId, instanceId, audience, redirectUri);
    }

    private static async Task SetInstanceStateAsync(
        ControlApiTestApplication app,
        Guid instanceId,
        ElsaDesiredLifecycle desiredLifecycle,
        ElsaObservedLifecycle observedLifecycle,
        ElsaInstanceHealth health,
        bool managedHandoff = true)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await SetInstanceStateAsync(db, instanceId, desiredLifecycle, observedLifecycle, health, managedHandoff);
    }

    private static Task SetInstanceStateAsync(
        CatalogDbContext db,
        Guid instanceId,
        ElsaDesiredLifecycle desiredLifecycle,
        ElsaObservedLifecycle observedLifecycle,
        ElsaInstanceHealth health,
        bool managedHandoff = true) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstances SET DesiredLifecycle = {desiredLifecycle.ToString()}, ObservedLifecycle = {observedLifecycle.ToString()}, Health = {health.ToString()}, CurrentDeploymentEndpointUri = {"https://managed.example.test"}, CurrentDeploymentManagedHandoff = {managedHandoff} WHERE Id = {instanceId}");

    private sealed record ManagedInstanceSetup(
        ControlApiTestApplication App,
        HttpClient Client,
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid InstanceId,
        string Audience,
        string RedirectUri);

    private static ManagedElsaHandoffIssueRequest IssueRequest(FakeHandoffAuthorizer authorizer) => new(
        authorizer.OrganizationId,
        authorizer.InstanceId,
        authorizer.Audience,
        authorizer.RedirectUri.OriginalString,
        authorizer.CodeChallenge);

    private static ServiceProvider CreateKeyRingServices(ManagedElsaHandoffOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => ManagedElsaHandoffKeyRing.CreateConfigured(options));
        return services.BuildServiceProvider();
    }

    private static HandoffFixture CreateFixture(TestTimeProvider? clock = null)
    {
        clock ??= new TestTimeProvider(DateTimeOffset.UtcNow);
        var authorizer = new FakeHandoffAuthorizer(Guid.NewGuid(), Guid.NewGuid());
        var options = Options.Create(new ManagedElsaHandoffOptions
        {
            Enabled = true,
            Issuer = "https://cloud.example.test",
            TokenLifetime = TimeSpan.FromMinutes(1)
        });
        var keyRing = ManagedElsaHandoffKeyRing.CreateEphemeral();
        var replayStore = new InMemoryManagedElsaHandoffReplayStore(clock);
        var audit = new RecordingAuditSink();
        var issuer = new ManagedElsaHandoffIssuer(options, keyRing, clock);
        var redeemer = new ManagedElsaHandoffRedeemer(options, keyRing, replayStore, authorizer, clock, audit);
        return new HandoffFixture(clock, authorizer, issuer, redeemer, keyRing, audit);
    }

    private sealed class HandoffFixture(
        TestTimeProvider clock,
        FakeHandoffAuthorizer authorizer,
        ManagedElsaHandoffIssuer issuer,
        ManagedElsaHandoffRedeemer redeemer,
        ManagedElsaHandoffKeyRing keyRing,
        RecordingAuditSink audit) : IDisposable
    {
        public TestTimeProvider Clock { get; } = clock;
        public FakeHandoffAuthorizer Authorizer { get; } = authorizer;
        private ManagedElsaHandoffIssuer Issuer { get; } = issuer;
        private ManagedElsaHandoffRedeemer Redeemer { get; } = redeemer;
        private ManagedElsaHandoffKeyRing KeyRing { get; } = keyRing;
        public RecordingAuditSink Audit { get; } = audit;

        public ManagedElsaHandoffRequest Request => new(
            Authorizer.OrganizationId,
            Authorizer.InstanceId,
            Authorizer.Audience,
            Authorizer.RedirectUri,
            Authorizer.CodeChallenge);

        public string Issue() => Issuer.Issue(
            new TrustedWorkspaceIdentity("https://idp.example.test", "subject", "User", "user@example.test"),
            Request,
            Authorizer.Authorization,
            Clock.GetUtcNow().AddHours(1)).Token;

        public string Issue(ManagedElsaHandoffRequest request) => Issuer.Issue(
            new TrustedWorkspaceIdentity("https://idp.example.test", "subject", "User", "user@example.test"),
            request,
            Authorizer.Authorization,
            Clock.GetUtcNow().AddHours(1)).Token;

        public string Issue(DateTimeOffset sessionExpiresAt) => Issuer.Issue(
            new TrustedWorkspaceIdentity("https://idp.example.test", "subject", "User", "user@example.test"),
            Request,
            Authorizer.Authorization,
            sessionExpiresAt).Token;

        public string IssueWithTokenType(string tokenType)
            => RewriteToken(Issue(), claims => claims, tokenType);

        public string IssueWithoutBindingVersion()
            => RewriteToken(Issue(), claims => claims.Where(claim => claim.Type != "binding_version"));

        public string IssueWithoutSessionExpiry() =>
            RewriteToken(Issue(), claims => claims.Where(claim =>
                claim.Type != ManagedElsaHandoffDefaults.SessionExpiryClaim));

        public string IssueWithSessionExpiry(DateTimeOffset expiresAt) =>
            RewriteToken(Issue(), claims => claims
                .Where(claim => claim.Type != ManagedElsaHandoffDefaults.SessionExpiryClaim)
                .Append(new Claim(
                    ManagedElsaHandoffDefaults.SessionExpiryClaim,
                    expiresAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))));

        private string RewriteToken(
            string token,
            Func<IEnumerable<Claim>, IEnumerable<Claim>> rewriteClaims,
            string tokenType = ManagedElsaHandoffDefaults.TokenType)
        {
            var handler = new JwtSecurityTokenHandler();
            var source = handler.ReadJwtToken(token);
            return handler.WriteToken(handler.CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
            {
                Issuer = source.Issuer,
                Audience = source.Audiences.Single(),
                Subject = new ClaimsIdentity(rewriteClaims(source.Claims)),
                IssuedAt = source.ValidFrom,
                NotBefore = source.ValidFrom,
                Expires = source.ValidTo,
                TokenType = tokenType,
                SigningCredentials = KeyRing.ActiveSigningCredentials
            }));
        }

        public Task<ManagedElsaHandoffRedeemResult> RedeemAsync(
            string token,
            string? expectedAudience = null,
            Uri? expectedRedirectUri = null,
            string? codeVerifier = null) =>
            Redeemer.RedeemAsync(
                token,
                expectedAudience ?? Authorizer.Audience,
                expectedRedirectUri ?? Authorizer.RedirectUri,
                codeVerifier ?? CodeVerifier);

        public void Dispose() => KeyRing.Dispose();
    }

    private sealed class FakeHandoffAuthorizer(Guid organizationId, Guid instanceId) : IManagedElsaHandoffAuthorizer
    {
        public Guid OrganizationId { get; } = organizationId;
        public Guid InstanceId { get; } = instanceId;
        public Guid AccountId { get; } = Guid.NewGuid();
        public string Audience { get; } = $"urn:elsa:instance:{instanceId:D}";
        public Uri RedirectUri { get; } = new($"https://managed.example.test/instances/{instanceId:D}/auth/callback");
        public string CodeChallenge { get; } = ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier);
        public int BindingVersion { get; set; } = 7;
        public bool IsAuthorized { get; set; } = true;

        public ManagedElsaHandoffAuthorization Authorization => new(
            AccountId,
            OrganizationId,
            InstanceId,
            Audience,
            RedirectUri,
            CodeChallenge,
            new HashSet<string>([ManagedElsaHandoffDefaults.RuntimeSessionScope], StringComparer.Ordinal),
            BindingVersion);

        public ValueTask<ManagedElsaHandoffAuthorization?> AuthorizeAsync(
            TrustedWorkspaceIdentity identity,
            ManagedElsaHandoffRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ManagedElsaHandoffAuthorization?>(
                IsAuthorized &&
                request.OrganizationId == OrganizationId &&
                request.InstanceId == InstanceId &&
                request.Audience == Audience &&
                request.RedirectUri == RedirectUri
                    ? Authorization
                    : null);

        public ValueTask<bool> IsStillAuthorizedAsync(
            ManagedElsaHandoffClaims claims,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(IsAuthorized && claims.OrganizationId == OrganizationId && claims.InstanceId == InstanceId &&
                                 claims.BindingVersion == BindingVersion);
    }

    private sealed class RecordingAuditSink : IManagedElsaHandoffAuditSink
    {
        public ConcurrentQueue<ManagedElsaHandoffAuditEvent> Events { get; } = new();

        public ValueTask RecordAsync(ManagedElsaHandoffAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Enqueue(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = nameof(ManagedElsaHandoffTests);
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
