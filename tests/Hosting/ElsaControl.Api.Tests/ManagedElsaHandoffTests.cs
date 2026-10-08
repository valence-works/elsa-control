using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

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
        Assert.Equal([ManagedElsaRuntimePermissionMapping.StructuredLogsRead], session.RuntimePermissions);
        Assert.Equal(sessionExpiresAt, session.SessionExpiresAt);
    }

    [Fact]
    public async Task Unsigned_studio_access_payload_tampering_returns_401()
    {
        var authorizer = new FakeHandoffAuthorizer(Guid.NewGuid(), Guid.NewGuid());
        await using var app = CreateApplication(authorizer);
        await app.SeedAsync(_ => Task.CompletedTask);
        var client = app.CreateControlIdentityClient("unsigned-studio-access-tamper");
        var issue = await client.PostControlJsonAsync(
            "/api/managed-elsa/handoff/issue",
            IssueRequest(authorizer));
        Assert.Equal(HttpStatusCode.OK, issue.StatusCode);
        var issued = (await issue.Content.ReadControlJsonAsync<ManagedElsaHandoffIssueResponse>())!;

        var segments = issued.Token.Split('.');
        var payload = JsonNode.Parse(Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(segments[1])))!;
        payload[ManagedElsaHandoffDefaults.StudioAccessClaim] = ManagedElsaRuntimePermissionMapping.StudioAccessFull;
        segments[1] = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var unsignedTamper = string.Join('.', segments);
        var redeem = await app.CreateClient().PostControlJsonAsync(
            "/api/managed-elsa/handoff/redeem",
            new ManagedElsaHandoffRedeemRequest(
                unsignedTamper,
                authorizer.Audience,
                authorizer.RedirectUri.OriginalString,
                CodeVerifier));

        Assert.Equal(HttpStatusCode.Unauthorized, redeem.StatusCode);
    }

    public enum Grants { None, StructuredLogs, Studio }

    [Theory]
    [InlineData(WorkspaceRole.Owner, OrganizationRole.Member, Grants.Studio, ManagedElsaRuntimePermissionMapping.StudioAccessFull)]
    [InlineData(WorkspaceRole.Owner, OrganizationRole.Owner, Grants.Studio, ManagedElsaRuntimePermissionMapping.StudioAccessFull)]
    [InlineData(WorkspaceRole.Reader, OrganizationRole.Administrator, Grants.StructuredLogs, ManagedElsaRuntimePermissionMapping.StudioAccessRoleLimited)]
    [InlineData(WorkspaceRole.Reader, OrganizationRole.Owner, Grants.StructuredLogs, ManagedElsaRuntimePermissionMapping.StudioAccessRoleLimited)]
    [InlineData(WorkspaceRole.SourceAdmin, OrganizationRole.Administrator, Grants.StructuredLogs, ManagedElsaRuntimePermissionMapping.StudioAccessRoleLimited)]
    [InlineData(WorkspaceRole.Reader, OrganizationRole.Member, Grants.None, ManagedElsaRuntimePermissionMapping.StudioAccessRoleLimited)]
    [InlineData(WorkspaceRole.SourceAdmin, OrganizationRole.Member, Grants.None, ManagedElsaRuntimePermissionMapping.StudioAccessRoleLimited)]
    public void Workspace_role_sets_the_runtime_permission_ceiling(
        WorkspaceRole workspaceRole,
        OrganizationRole organizationRole,
        Grants onStudioImage,
        string expectedStudioAccess)
    {
        var access = new WorkspaceAccess(Guid.NewGuid(), Guid.NewGuid(), workspaceRole, Guid.NewGuid(), organizationRole);
        var onLegacyImage = onStudioImage == Grants.Studio ? Grants.StructuredLogs : onStudioImage;

        foreach (var studioGrantsSupported in new[] { false, true })
        {
            var expectedGrants = studioGrantsSupported ? onStudioImage : onLegacyImage;
            var expectedAccess = workspaceRole is WorkspaceRole.Owner && !studioGrantsSupported
                ? ManagedElsaRuntimePermissionMapping.StudioAccessDeploymentLimited
                : expectedStudioAccess;

            Assert.Equal(Permissions(expectedGrants), ManagedElsaRuntimePermissionMapping.For(access, studioGrantsSupported).Order());
            Assert.Equal(expectedAccess, ManagedElsaRuntimePermissionMapping.StudioAccessFor(workspaceRole, studioGrantsSupported));
        }
    }

    private static IEnumerable<string> Permissions(Grants grants) => (grants switch
    {
        Grants.Studio => ManagedElsaRuntimePermissions.StudioGrantsV1,
        Grants.StructuredLogs => [ManagedElsaRuntimePermissionMapping.StructuredLogsRead],
        _ => []
    }).Order();

    [Theory]
    [InlineData("*")]
    [InlineData("read:*")]
    [InlineData("read:workflows")]
    [InlineData("READ:DASHBOARD")]
    [InlineData("read:dashboard ")]
    [InlineData("read:workflow-instances")]
    [InlineData("delete:workflow-definitions")]
    public void Handoff_issuer_rejects_wildcard_and_unknown_runtime_grants(string permission)
    {
        using var fixture = CreateFixture();
        fixture.Authorizer.RuntimePermissions = new HashSet<string>([permission], StringComparer.Ordinal);

        Assert.Throws<InvalidOperationException>(() => fixture.Issue());
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
        }), Options.Create(new CloudAccountIdentityOptions()));

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_wiring_issues_only_grants_supported_by_current_deployment(bool studioGrantsSupported)
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
        string workspaceName;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var workspace = await db.Workspaces.SingleAsync(x => x.Id == workspaceId);
            organizationId = workspace.OrganizationId;
            workspaceName = workspace.Name;
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
            var studioGrantsDeploymentId = studioGrantsSupported ? deploymentId : null;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE ElsaInstances SET CurrentDeploymentId = {deploymentId}, CurrentDeploymentEndpointUri = {endpointUri}, CurrentDeploymentManagedHandoff = {true}, CurrentDeploymentStudioGrants = {studioGrantsSupported}, CurrentDeploymentStudioGrantsDeploymentId = {studioGrantsDeploymentId}, DesiredLifecycle = {ElsaDesiredLifecycle.Running.ToString()}, ObservedLifecycle = {ElsaObservedLifecycle.Ready.ToString()}, Health = {ElsaInstanceHealth.Healthy.ToString()} WHERE Id = {instanceId}");
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
        var issued = (await response.Content.ReadControlJsonAsync<ManagedElsaHandoffIssueResponse>())!;
        var token = new JwtSecurityTokenHandler().ReadJwtToken(issued.Token);
        Assert.Equal(
            studioGrantsSupported
                ? ManagedElsaRuntimePermissionMapping.StudioAccessFull
                : ManagedElsaRuntimePermissionMapping.StudioAccessDeploymentLimited,
            token.Claims.Single(claim => claim.Type == ManagedElsaHandoffDefaults.StudioAccessClaim).Value);
        Assert.Equal(
            workspaceName,
            token.Claims.Single(claim => claim.Type == ManagedElsaHandoffDefaults.WorkspaceNameClaim).Value);
        var grants = token.Claims
            .Where(claim => claim.Type == ManagedElsaHandoffDefaults.RuntimePermissionClaim)
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);
        if (studioGrantsSupported)
        {
            Assert.Equal(ManagedElsaRuntimePermissionMapping.AllowedPermissions.Count, grants.Count);
            Assert.All(ManagedElsaRuntimePermissionMapping.AllowedPermissions, permission => Assert.Contains(permission, grants));
        }
        else
            Assert.Equal([ManagedElsaRuntimePermissionMapping.StructuredLogsRead], grants);

        var redeemedResponse = await app.CreateClient().PostControlJsonAsync(
            "/api/managed-elsa/handoff/redeem",
            new ManagedElsaHandoffRedeemRequest(
                issued.Token,
                audience,
                callback,
                CodeVerifier));

        Assert.Equal(HttpStatusCode.OK, redeemedResponse.StatusCode);
        var redeemed = (await redeemedResponse.Content.ReadControlJsonAsync<ManagedElsaHandoffRedeemResponse>())!;
        Assert.Equal(
            studioGrantsSupported
                ? ManagedElsaRuntimePermissionMapping.StudioAccessFull
                : ManagedElsaRuntimePermissionMapping.StudioAccessDeploymentLimited,
            redeemed.StudioAccess);
        Assert.Equal(workspaceName, redeemed.WorkspaceName);
        Assert.Equal(grants.Order(StringComparer.Ordinal), redeemed.RuntimePermissions.Order(StringComparer.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(redeemed.SupportReference));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var audit = await db.ManagedElsaHandoffAuditEvents
                .Where(entry => entry.Action == "redeem.succeeded")
                .OrderByDescending(entry => entry.OccurredAt)
                .FirstAsync();
            Assert.Equal(audit.CorrelationId, redeemed.SupportReference);
        }
    }

    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Owner)]
    public async Task Workspace_reader_with_instance_open_never_receives_studio_permissions(OrganizationRole organizationRole)
    {
        var setup = await SeedManagedInstanceAsync(
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceHealth.Healthy,
            bind: true);
        await using var app = setup.App;
        const string readerSubject = "managed-reader-with-open";
        Guid readerAccountId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var workspace = await db.Workspaces.SingleAsync(x => x.Id == setup.WorkspaceId);
            var readerAccount = new Account
            {
                DisplayName = readerSubject,
                Email = $"{readerSubject}@example.test"
            };
            readerAccount.ExternalIdentities.Add(new ExternalIdentity
            {
                Account = readerAccount,
                Issuer = ControlApiTestApplication.TestControlIdentityIssuer,
                Subject = readerSubject,
                DisplayName = readerSubject,
                Email = readerAccount.Email
            });
            readerAccount.OrganizationMemberships.Add(new OrganizationMembership
            {
                Account = readerAccount,
                OrganizationId = workspace.OrganizationId,
                Role = organizationRole
            });
            readerAccount.Memberships.Add(new WorkspaceMembership
            {
                Account = readerAccount,
                Workspace = workspace,
                Role = WorkspaceRole.Reader
            });
            db.Accounts.Add(readerAccount);
            await db.SaveChangesAsync();
            readerAccountId = readerAccount.Id;

            // Exercise the strongest deployed Studio capability set; a workspace Reader must still receive none of
            // its dashboard or designer grants, whatever their organization role.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE ElsaInstances SET CurrentDeploymentId = {"deployment-managed"}, CurrentDeploymentStudioGrants = {true}, CurrentDeploymentStudioGrantsDeploymentId = {"deployment-managed"} WHERE Id = {setup.InstanceId}");
        }

        using var reader = app.CreateControlIdentityClient(readerSubject);
        var readerContext = await reader.GetControlJsonAsync<MeWorkspacesResponse>("/api/me/workspaces");
        var readerWorkspace = Assert.Single(readerContext!.Workspaces);
        Assert.Equal(setup.WorkspaceId, readerWorkspace.Id);
        Assert.Equal(WorkspaceRole.Reader, readerWorkspace.Role);
        Assert.Equal(organizationRole, readerWorkspace.OrganizationRole);

        var grantsUri = $"/api/workspaces/{setup.WorkspaceId:D}/permissions/grants";
        var grant = await setup.Client.PostControlJsonAsync(
            grantsUri,
            new WorkspacePermissionGrantRequest(readerAccountId, ManagedElsaInstancePermissions.Open));
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        var grants = await setup.Client.GetControlJsonAsync<WorkspacePermissionGrantsResponse>(
            $"{grantsUri}?accountId={readerAccountId:D}");
        var activeGrants = grants!.Items.Where(x => x.RevokedAt is null).ToArray();
        Assert.Equal([ManagedElsaInstancePermissions.Open], activeGrants.Select(x => x.Permission));

        var request = new ManagedElsaHandoffIssueRequest(
            setup.OrganizationId,
            setup.InstanceId,
            setup.Audience,
            setup.RedirectUri,
            ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier));
        var issue = await reader.PostControlJsonAsync("/api/managed-elsa/handoff/issue", request);
        Assert.Equal(HttpStatusCode.OK, issue.StatusCode);
        var issued = (await issue.Content.ReadControlJsonAsync<ManagedElsaHandoffIssueResponse>())!;
        Assert.Equal(
            ManagedElsaRuntimePermissionMapping.StudioAccessRoleLimited,
            new JwtSecurityTokenHandler().ReadJwtToken(issued.Token).Claims
                .Single(claim => claim.Type == ManagedElsaHandoffDefaults.StudioAccessClaim).Value);
        var redeem = await app.CreateClient().PostControlJsonAsync(
            "/api/managed-elsa/handoff/redeem",
            new ManagedElsaHandoffRedeemRequest(
                issued.Token,
                setup.Audience,
                setup.RedirectUri,
                CodeVerifier));

        Assert.Equal(HttpStatusCode.OK, redeem.StatusCode);
        var session = (await redeem.Content.ReadControlJsonAsync<ManagedElsaHandoffRedeemResponse>())!;
        Assert.Equal(readerAccountId, session.AccountId);
        Assert.Equal(setup.OrganizationId, session.OrganizationId);
        Assert.Equal(setup.InstanceId, session.InstanceId);
        Assert.Equal(ManagedElsaRuntimePermissionMapping.StudioAccessRoleLimited, session.StudioAccess);
        // Organization owners and administrators keep the Structured Logs read they have always had, and nothing more.
        Assert.Equal(
            organizationRole is OrganizationRole.Administrator or OrganizationRole.Owner
                ? [ManagedElsaRuntimePermissionMapping.StructuredLogsRead]
                : [],
            session.RuntimePermissions);

        var pendingIssue = await reader.PostControlJsonAsync("/api/managed-elsa/handoff/issue", request);
        Assert.Equal(HttpStatusCode.OK, pendingIssue.StatusCode);
        var pendingToken = (await pendingIssue.Content.ReadControlJsonAsync<ManagedElsaHandoffIssueResponse>())!.Token;
        pendingToken = RewriteSignedStudioAccess(
            pendingToken,
            app.Services.GetRequiredService<ManagedElsaHandoffKeyRing>(),
            ManagedElsaRuntimePermissionMapping.StudioAccessFull);

        var revoke = await setup.Client.PostControlJsonAsync(
            $"/api/workspaces/{setup.WorkspaceId:D}/permissions/revocations",
            new WorkspacePermissionRevokeRequest(readerAccountId, ManagedElsaInstancePermissions.Open));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.True((await revoke.Content.ReadControlJsonAsync<RevokeWorkspacePermissionResult>())!.Changed);

        var deniedRedeem = await app.CreateClient().PostControlJsonAsync(
            "/api/managed-elsa/handoff/redeem",
            new ManagedElsaHandoffRedeemRequest(
                pendingToken,
                setup.Audience,
                setup.RedirectUri,
                CodeVerifier));
        Assert.Equal(HttpStatusCode.Forbidden, deniedRedeem.StatusCode);

        var deniedIssue = await reader.PostControlJsonAsync("/api/managed-elsa/handoff/issue", request);
        Assert.Equal(HttpStatusCode.Forbidden, deniedIssue.StatusCode);
    }

    public enum RedemptionChange { OwnerDemotedToReader, EngineBackOnLegacyImage }

    [Theory]
    [InlineData(RedemptionChange.OwnerDemotedToReader)]
    [InlineData(RedemptionChange.EngineBackOnLegacyImage)]
    public async Task Studio_grant_token_is_denied_when_the_grant_no_longer_holds_at_redemption(RedemptionChange change)
    {
        var setup = await SeedManagedInstanceAsync(
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceHealth.Healthy,
            bind: true);
        await using var app = setup.App;
        await ExecuteSqlAsync(app, db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstances SET CurrentDeploymentId = {"deployment-managed"}, CurrentDeploymentStudioGrants = {true}, CurrentDeploymentStudioGrantsDeploymentId = {"deployment-managed"} WHERE Id = {setup.InstanceId}"));
        var request = new ManagedElsaHandoffIssueRequest(
            setup.OrganizationId,
            setup.InstanceId,
            setup.Audience,
            setup.RedirectUri,
            ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier));
        var issue = await setup.Client.PostControlJsonAsync("/api/managed-elsa/handoff/issue", request);
        Assert.Equal(HttpStatusCode.OK, issue.StatusCode);
        var token = (await issue.Content.ReadControlJsonAsync<ManagedElsaHandoffIssueResponse>())!.Token;
        Assert.Equal(
            ManagedElsaRuntimePermissions.StudioGrantsV1.Count,
            new JwtSecurityTokenHandler().ReadJwtToken(token).Claims
                .Count(claim => claim.Type == ManagedElsaHandoffDefaults.RuntimePermissionClaim));

        await ExecuteSqlAsync(app, db => change == RedemptionChange.OwnerDemotedToReader
            ? db.WorkspaceMemberships
                .Where(x => x.WorkspaceId == setup.WorkspaceId && x.Account.ExternalIdentities.Any(identity => identity.Subject == setup.Subject))
                .ExecuteUpdateAsync(x => x.SetProperty(membership => membership.Role, WorkspaceRole.Reader))
            : db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE ElsaInstances SET CurrentDeploymentStudioGrants = {false} WHERE Id = {setup.InstanceId}"));

        var redeem = await app.CreateClient().PostControlJsonAsync(
            "/api/managed-elsa/handoff/redeem",
            new ManagedElsaHandoffRedeemRequest(token, setup.Audience, setup.RedirectUri, CodeVerifier));

        Assert.Equal(HttpStatusCode.Forbidden, redeem.StatusCode);
    }

    private static async Task ExecuteSqlAsync(ControlApiTestApplication app, Func<CatalogDbContext, Task> change)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await change(scope.ServiceProvider.GetRequiredService<CatalogDbContext>());
    }

    private static string RewriteSignedStudioAccess(
        string token,
        ManagedElsaHandoffKeyRing keyRing,
        string studioAccess)
    {
        var handler = new JwtSecurityTokenHandler();
        var source = handler.ReadJwtToken(token);
        var claims = source.Claims
            .Where(claim => claim.Type != ManagedElsaHandoffDefaults.StudioAccessClaim)
            .Append(new Claim(ManagedElsaHandoffDefaults.StudioAccessClaim, studioAccess));
        return handler.WriteToken(handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = source.Issuer,
            Audience = source.Audiences.Single(),
            Subject = new ClaimsIdentity(claims),
            IssuedAt = source.ValidFrom,
            NotBefore = source.ValidFrom,
            Expires = source.ValidTo,
            TokenType = source.Header.Typ,
            SigningCredentials = keyRing.ActiveSigningCredentials
        }));
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
    public void Cloud_continuation_redirect_preserves_only_the_runtime_handoff_values()
    {
        var instanceId = Guid.NewGuid();
        const string state = "state-value-that-is-long-enough";
        var challenge = ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier);

        var redirect = ManagedElsaHandoffContinuation.CloudContinuationRedirect(
            "https://elsacloud.app/dashboard",
            new ManagedElsaHandoffContinuationRequest(instanceId, state, challenge));

        Assert.Equal(
            $"https://elsacloud.app/dashboard?handoff=1&instanceId={instanceId:D}&state={state}&codeChallenge={challenge}",
            redirect);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://elsacloud.app/dashboard")]
    [InlineData("https://user@elsacloud.app/dashboard")]
    [InlineData("https://elsacloud.app/dashboard?unexpected=1")]
    [InlineData("https://elsacloud.app/dashboard#fragment")]
    public void Cloud_continuation_redirect_rejects_unsafe_configuration(string? configuredUrl)
    {
        var request = new ManagedElsaHandoffContinuationRequest(
            Guid.NewGuid(),
            "state-value-that-is-long-enough",
            ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier));

        Assert.Null(ManagedElsaHandoffContinuation.CloudContinuationRedirect(configuredUrl, request));
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
        Assert.EndsWith("/managed-elsa/handoff/callback", form.Action, StringComparison.Ordinal);
        Assert.DoesNotContain("/authentication/external/callback", form.Action, StringComparison.Ordinal);
        Assert.DoesNotContain("/login", form.Action, StringComparison.Ordinal);
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
    public async Task Unauthenticated_continuation_returns_to_the_configured_cloud_dashboard()
    {
        var setup = await SeedManagedInstanceAsync(
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceHealth.Healthy,
            bind: true,
            cloudContinuationUrl: "https://elsacloud.app/dashboard");
        await using var app = setup.App;
        var challenge = ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier);
        const string state = "state-value-that-is-long-enough";

        using var response = await app.CreateClient(new() { AllowAutoRedirect = false })
            .GetAsync(ContinuationPath(setup.InstanceId, state, challenge));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            $"https://elsacloud.app/dashboard?handoff=1&instanceId={setup.InstanceId:D}&state={state}&codeChallenge={challenge}",
            response.Headers.Location?.OriginalString);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cookie_continuation_issues_and_auto_posts_to_the_bound_callback_not_studio_login()
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
        app.AddControlSessionCookie(client, subject: setup.Subject, expiresUtc: DateTimeOffset.UtcNow.AddHours(2));

        using var response = await client.GetAsync(ContinuationPath(setup.InstanceId, state, challenge));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.DoesNotContain("Sign in", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/login", html, StringComparison.Ordinal);
        var form = ParseAutoSubmitForm(html);
        Assert.Equal(setup.RedirectUri, form.Action);
        Assert.EndsWith("/managed-elsa/handoff/callback", form.Action, StringComparison.Ordinal);
        Assert.DoesNotContain("/authentication/external/callback", form.Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cookie_continuation_without_ticket_expiry_returns_to_control_login_not_studio()
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
        app.AddControlSessionCookie(client, subject: "lifetime-less-cookie", expiresUtc: null);

        using var response = await client.GetAsync(path);
        var locationText = response.Headers.Location?.ToString() ?? "";
        var loginPath = locationText.Split('?', 2)[0];

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(ManagedElsaHandoffContinuation.LoginPath, loginPath);
        Assert.NotEqual("/login", loginPath);
        Assert.Contains(Uri.EscapeDataString(path), locationText, StringComparison.Ordinal);
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
        Assert.NotEqual("/login", loginPath);
        Assert.Contains(Uri.EscapeDataString(path), locationText, StringComparison.Ordinal);
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
        var action = System.Text.RegularExpressions.Regex.Match(html, "<form method=\"post\" action=\"([^\"]+)\">").Groups[1].Value;
        var code = System.Text.RegularExpressions.Regex.Match(html, "name=\"code\" value=\"([^\"]+)\"").Groups[1].Value;
        var state = System.Text.RegularExpressions.Regex.Match(html, "name=\"state\" value=\"([^\"]+)\"").Groups[1].Value;
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
        Assert.Equal(ManagedElsaRuntimePermissionMapping.StructuredLogsRead,
            jwt.Claims.Single(x => x.Type == ManagedElsaHandoffDefaults.RuntimePermissionClaim).Value);
    }

    [Fact]
    public async Task Optional_display_claims_never_change_authorization_results()
    {
        using var fixture = CreateFixture();
        var expectedPermissions = fixture.Authorizer.RuntimePermissions.Order(StringComparer.Ordinal).ToArray();
        var scenarios = new[]
        {
            (fixture.IssueWithoutDisplayMetadata(), (string?)null, (string?)null),
            (fixture.IssueWithDisplayClaims(
                new Claim(ManagedElsaHandoffDefaults.StudioAccessClaim, "future-value"),
                new Claim(ManagedElsaHandoffDefaults.WorkspaceNameClaim, " \t")), (string?)null, (string?)null),
            (fixture.IssueWithDisplayClaims(
                new Claim(ManagedElsaHandoffDefaults.StudioAccessClaim, ManagedElsaRuntimePermissionMapping.StudioAccessFull),
                new Claim(ManagedElsaHandoffDefaults.StudioAccessClaim, ManagedElsaRuntimePermissionMapping.StudioAccessRoleLimited),
                new Claim(ManagedElsaHandoffDefaults.WorkspaceNameClaim, "Workspace A"),
                new Claim(ManagedElsaHandoffDefaults.WorkspaceNameClaim, "Workspace B")), (string?)null, (string?)null),
            (fixture.IssueWithDisplayClaims(
                new Claim(ManagedElsaHandoffDefaults.StudioAccessClaim, "true", ClaimValueTypes.Boolean),
                new Claim(ManagedElsaHandoffDefaults.WorkspaceNameClaim, "Workspace\nA")), (string?)null, (string?)null),
            (fixture.IssueWithDisplayClaims(
                new Claim(ManagedElsaHandoffDefaults.StudioAccessClaim, ManagedElsaRuntimePermissionMapping.StudioAccessFull),
                new Claim(ManagedElsaHandoffDefaults.WorkspaceNameClaim, "Workspace A")),
                ManagedElsaRuntimePermissionMapping.StudioAccessFull,
                "Workspace A")
        };

        foreach (var (token, expectedStudioAccess, expectedWorkspaceName) in scenarios)
        {
            var result = await fixture.RedeemAsync(token);

            Assert.True(result.Succeeded);
            var claims = result.Claims!;
            Assert.Equal(expectedPermissions, claims.RuntimePermissions.Order(StringComparer.Ordinal));
            Assert.Equal(expectedStudioAccess, claims.StudioAccess);
            Assert.Equal(expectedWorkspaceName, claims.WorkspaceName);
        }

        fixture.Authorizer.StudioAccess = "unknown-value";
        fixture.Authorizer.WorkspaceName = new string('w', 257);
        var tokenWithInvalidIssueMetadata = fixture.Issue();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokenWithInvalidIssueMetadata);
        Assert.DoesNotContain(jwt.Claims, claim => claim.Type == ManagedElsaHandoffDefaults.StudioAccessClaim);
        Assert.DoesNotContain(jwt.Claims, claim => claim.Type == ManagedElsaHandoffDefaults.WorkspaceNameClaim);
        var invalidIssueMetadataResult = await fixture.RedeemAsync(tokenWithInvalidIssueMetadata);
        Assert.True(invalidIssueMetadataResult.Succeeded);
        Assert.Equal(expectedPermissions, invalidIssueMetadataResult.Claims!.RuntimePermissions.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("read:workflows")]
    public async Task Handoff_redemption_rejects_wildcard_and_unknown_runtime_grants(string permission)
    {
        using var fixture = CreateFixture();

        var result = await fixture.RedeemAsync(fixture.IssueWithRuntimePermissions(permission));

        Assert.Equal(ManagedElsaHandoffRedeemFailure.InvalidToken, result.Failure);
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
        bool bind,
        string? cloudContinuationUrl = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:Enabled"] = "true"
        };
        if (cloudContinuationUrl is not null)
            configuration[$"{ManagedElsaHandoffDefaults.ConfigurationSection}:CloudContinuationUrl"] = cloudContinuationUrl;
        var app = new ControlApiTestApplication(configuration);
        await app.SeedAsync(_ => Task.CompletedTask);
        var subject = $"managed-health-{Guid.NewGuid():N}";
        var client = app.CreateControlIdentityClient(subject);
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

        return new(app, client, subject, organizationId, workspaceId, instanceId, audience, redirectUri);
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
        string Subject,
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

        public string IssueWithRuntimePermissions(params string[] permissions) =>
            RewriteToken(Issue(), claims => claims
                .Where(claim => claim.Type != ManagedElsaHandoffDefaults.RuntimePermissionClaim)
                .Concat(permissions.Select(permission => new Claim(
                    ManagedElsaHandoffDefaults.RuntimePermissionClaim,
                    permission))));

        public string IssueWithoutDisplayMetadata() =>
            IssueWithDisplayClaims([]);

        public string IssueWithDisplayClaims(params Claim[] displayClaims) =>
            RewriteToken(Issue(), claims => claims
                .Where(claim => claim.Type != ManagedElsaHandoffDefaults.StudioAccessClaim &&
                                claim.Type != ManagedElsaHandoffDefaults.WorkspaceNameClaim)
                .Concat(displayClaims));

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
        public string? StudioAccess { get; set; } = ManagedElsaRuntimePermissionMapping.StudioAccessRoleLimited;
        public string? WorkspaceName { get; set; } = "Trusted workspace";
        public IReadOnlySet<string> RuntimePermissions { get; set; } = new HashSet<string>(
            [ManagedElsaRuntimePermissionMapping.StructuredLogsRead], StringComparer.Ordinal);

        public ManagedElsaHandoffAuthorization Authorization => new(
            AccountId,
            OrganizationId,
            InstanceId,
            Audience,
            RedirectUri,
            CodeChallenge,
            new HashSet<string>([ManagedElsaHandoffDefaults.RuntimeSessionScope], StringComparer.Ordinal),
            BindingVersion,
            RuntimePermissions)
        {
            StudioAccess = StudioAccess,
            WorkspaceName = WorkspaceName
        };

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
