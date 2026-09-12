using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ElsaControl.Api.Admin.Organizations;
using ElsaControl.Api.Authentication;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElsaControl.Api.Tests;

public sealed class AdminOrganizationInternalEntitlementApiTests :
    IClassFixture<AdminOrganizationInternalEntitlementApiTests.Fixture>,
    IAsyncLifetime
{
    private const string Reason = "Internal dogfood of managed hosting";
    private const string EchoMarker = "echo-marker";
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly Fixture _fixture;
    private readonly ControlApiTestApplication _app;
    private readonly HttpClient _operator;
    private readonly Guid _organizationId = Guid.NewGuid();

    public AdminOrganizationInternalEntitlementApiTests(Fixture fixture)
    {
        _fixture = fixture;
        _app = fixture.Application;
        _operator = _app.CreateClient();
        _operator.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
    }

    public static TheoryData<string?, int?, string?, string> InvalidGrants => new()
    {
        { null, 2, Iso(Now.AddDays(30)), "reason" },
        { "ab", 2, Iso(Now.AddDays(30)), "reason" },
        { EchoMarker + new string('r', 190), 2, Iso(Now.AddDays(30)), "reason" },
        { EchoMarker + "\u0007bell", 2, Iso(Now.AddDays(30)), "reason" },
        { Reason, null, Iso(Now.AddDays(30)), "maxInstances" },
        { Reason, 0, Iso(Now.AddDays(30)), "maxInstances" },
        { Reason, 4, Iso(Now.AddDays(30)), "maxInstances" },
        { Reason, 2, Iso(Now.AddMinutes(-1)), "expiresAt" },
        { Reason, 2, Iso(Now.AddDays(91)), "expiresAt" },
        { Reason, 2, "2026-10-12T10:00:00", "expiresAt" },
        { Reason, 2, EchoMarker, "expiresAt" }
    };

    public static TheoryData<string, string, HttpStatusCode> NonOperatorCalls
    {
        get
        {
            var data = new TheoryData<string, string, HttpStatusCode>();
            foreach (var method in new[] { "GET", "PUT", "DELETE" })
            {
                data.Add("anonymous", method, HttpStatusCode.Unauthorized);
                data.Add("customer-bearer", method, HttpStatusCode.Unauthorized);
                data.Add("customer-session", method, HttpStatusCode.Forbidden);
            }

            return data;
        }
    }

    public async Task InitializeAsync()
    {
        _fixture.Clock.UtcNow = Now;
        await _app.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = _organizationId, Name = "Valence dogfood" });
            return Task.CompletedTask;
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Operator_grants_reads_and_revokes_an_audited_internal_entitlement()
    {
        var put = await PutAsync(Grant());

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var granted = await put.Content.ReadControlJsonAsync<AdminInternalEntitlementResponse>();
        Assert.Equal(new AdminInternalEntitlementResponse(_organizationId, OrganizationInternalEntitlementState.Active, 2, Now.AddDays(30), Now), granted);
        Assert.DoesNotContain(Reason, await put.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(granted, await _operator.GetControlJsonAsync<AdminInternalEntitlementResponse>(Url()));

        _fixture.Clock.UtcNow = Now.AddHours(1);
        var delete = await _operator.DeleteAsync(Url());

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(OrganizationInternalEntitlementState.Revoked, (await _operator.GetControlJsonAsync<AdminInternalEntitlementResponse>(Url()))!.State);
        var audits = await ReadAsync(db => db.OrganizationAuditRecords.AsNoTracking()
            .Where(x => x.OrganizationId == _organizationId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync());
        var operatorFingerprint = OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject("api-key");
        Assert.Collection(
            audits,
            audit =>
            {
                Assert.Equal("internal-entitlement", audit.TargetType);
                Assert.Equal(
                    $"Internal managed-hosting entitlement granted (max instances 2, expires 2026-10-12T10:00:00.0000000Z). Reason: {Reason}",
                    audit.Summary);
                Assert.Equal(operatorFingerprint, audit.OperatorSubject);
            },
            audit =>
            {
                Assert.Equal("Internal managed-hosting entitlement revoked.", audit.Summary);
                Assert.Equal(operatorFingerprint, audit.OperatorSubject);
            });
    }

    [Fact]
    public async Task Granted_entitlement_passes_the_gate_within_its_cap_until_expiry_without_a_background_job()
    {
        await PutAsync(Grant(days: 7));

        Assert.True((await EvaluateAsync(ElsaInstanceOperationAction.Create, activeInstanceCount: 1)).Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.InstanceLimitReached, (await EvaluateAsync(ElsaInstanceOperationAction.Create, activeInstanceCount: 2)).Code);

        _fixture.Clock.UtcNow = Now.AddDays(7);

        await AssertCreateAndUpdateDeniedAsync(ElsaInstanceCommercialOperation.EntitlementExpired);
        await AssertSafeExitsAllowedAsync();
        Assert.Equal(OrganizationInternalEntitlementState.Expired, (await _operator.GetControlJsonAsync<AdminInternalEntitlementResponse>(Url()))!.State);
    }

    [Fact]
    public async Task Revoked_entitlement_denies_create_and_update_but_keeps_safe_exits()
    {
        await PutAsync(Grant());
        await _operator.DeleteAsync(Url());

        await AssertCreateAndUpdateDeniedAsync(ElsaInstanceCommercialOperation.EntitlementRequired);
        await AssertSafeExitsAllowedAsync();
    }

    [Fact]
    public async Task Control_admin_session_is_authorized_and_audited_by_its_session_subject()
    {
        var client = CreateCaller("customer-session", new Claim("role", AdminAuthorization.ControlAdminRole));

        var response = await PutAsync(Grant(), client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var audit = await ReadAsync(db => db.OrganizationAuditRecords.AsNoTracking().SingleAsync(x => x.OrganizationId == _organizationId));
        Assert.Equal(OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject("admin-user"), audit.OperatorSubject);
    }

    [Theory]
    [MemberData(nameof(NonOperatorCalls))]
    public async Task Non_operator_callers_are_denied_and_nothing_is_written(string caller, string method, HttpStatusCode expected)
    {
        var response = await SendAsync(CreateCaller(caller), method, _organizationId);

        Assert.Equal(expected, response.StatusCode);
        await AssertNothingWrittenAsync();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Unknown_organization_is_not_found(string method)
    {
        var response = await SendAsync(_operator, method, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("organization.not-found", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Revoke_without_a_grant_is_not_found()
    {
        var response = await _operator.DeleteAsync(Url());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("internal-entitlement.not-granted", await ProblemCodeAsync(response));
        await AssertNothingWrittenAsync();
    }

    [Theory]
    [MemberData(nameof(InvalidGrants))]
    public async Task Invalid_grants_return_validation_problems_without_echo(string? reason, int? maxInstances, string? expiresAt, string field)
    {
        var response = await PutAsync(new { reason, maxInstances, expiresAt });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("internal-entitlement.invalid", problem.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Array, problem.GetProperty("errors").GetProperty(field).ValueKind);
        Assert.DoesNotContain(EchoMarker, body, StringComparison.Ordinal);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task Malformed_body_returns_a_problem_without_echo()
    {
        using var content = new StringContent(
            $$"""{"reason":"{{EchoMarker}}","maxInstances":"two","expiresAt":"{{Iso(Now.AddDays(30))}}"}""",
            Encoding.UTF8,
            "application/json");

        var response = await _operator.PutAsync(Url(), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(EchoMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task Stripe_owned_organization_refuses_grant_and_revoke_without_writes()
    {
        await ReadAsync(db => new OrganizationBillingStore(db).StartTrialAsync(_organizationId, BillingProviderNames.Stripe, Now.AddDays(-1)));
        var before = await ReadBillingStateAsync();

        var put = await PutAsync(Grant());
        var delete = await _operator.DeleteAsync(Url());
        var status = await _operator.GetControlJsonAsync<AdminInternalEntitlementResponse>(Url());

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal("internal-entitlement.commercial-subscription", await ProblemCodeAsync(put));
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("internal-entitlement.commercial-subscription", await ProblemCodeAsync(delete));
        Assert.Equal(new AdminInternalEntitlementResponse(_organizationId, OrganizationInternalEntitlementState.CommercialSubscription, null, null, null), status);
        Assert.Equal(before, await ReadBillingStateAsync());
        await AssertCreateAndUpdateDeniedAsync(ElsaInstanceCommercialOperation.EntitlementRequired);
    }

    private string Url(Guid? organizationId = null) =>
        $"/api/admin/organizations/{organizationId ?? _organizationId}/internal-entitlement";

    private static object Grant(int days = 30) => new { reason = Reason, maxInstances = 2, expiresAt = Iso(Now.AddDays(days)) };

    private Task<HttpResponseMessage> PutAsync(object body, HttpClient? client = null) =>
        (client ?? _operator).PutControlJsonAsync(Url(), body);

    private Task<HttpResponseMessage> SendAsync(HttpClient client, string method, Guid organizationId) => method switch
    {
        "GET" => client.GetAsync(Url(organizationId)),
        "PUT" => client.PutControlJsonAsync(Url(organizationId), Grant()),
        _ => client.DeleteAsync(Url(organizationId))
    };

    private HttpClient CreateCaller(string caller, params Claim[] sessionClaims)
    {
        if (caller == "customer-bearer")
            return _app.CreateControlIdentityClient(subject: "customer-user");

        var client = _app.CreateClient(new() { AllowAutoRedirect = false });
        if (caller == "customer-session")
        {
            _app.AddControlSessionCookie(client, sessionClaims);
            client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        }

        return client;
    }

    private async Task<ElsaInstanceCommercialGateDecision> EvaluateAsync(ElsaInstanceOperationAction action, int? activeInstanceCount = null)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IElsaInstanceCommercialGate>()
            .EvaluateAsync(_organizationId, action, activeInstanceCount);
    }

    private async Task AssertCreateAndUpdateDeniedAsync(string expectedCode)
    {
        foreach (var action in new[] { ElsaInstanceOperationAction.Create, ElsaInstanceOperationAction.UpdateIntent })
        {
            var decision = await EvaluateAsync(action, activeInstanceCount: 0);
            Assert.False(decision.Allowed);
            Assert.Equal(expectedCode, decision.Code);
        }
    }

    private async Task AssertSafeExitsAllowedAsync()
    {
        Assert.True((await EvaluateAsync(ElsaInstanceOperationAction.Stop)).Allowed);
        Assert.True((await EvaluateAsync(ElsaInstanceOperationAction.Delete)).Allowed);
    }

    private async Task AssertNothingWrittenAsync() =>
        Assert.Equal((0, 0, 0), await ReadAsync(async db => (
            await db.OrganizationSubscriptions.CountAsync(),
            await db.OrganizationEntitlementSnapshots.CountAsync(),
            await db.OrganizationAuditRecords.CountAsync(x => x.TargetType == "internal-entitlement"))));

    private Task<string> ReadBillingStateAsync() => ReadAsync(async db => JsonSerializer.Serialize(new
    {
        Subscriptions = await db.OrganizationSubscriptions.AsNoTracking().ToListAsync(),
        Entitlements = await db.OrganizationEntitlementSnapshots.AsNoTracking().ToListAsync(),
        Audits = await db.OrganizationAuditRecords.AsNoTracking().ToListAsync()
    }));

    private async Task<T> ReadAsync<T>(Func<CatalogDbContext, Task<T>> read)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<CatalogDbContext>());
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    public sealed class Fixture : IAsyncLifetime
    {
        public Fixture() =>
            Application = new(configureServices: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            });

        internal MutableTimeProvider Clock { get; } = new(Now);

        internal ControlApiTestApplication Application { get; }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync() => await ((IAsyncDisposable)Application).DisposeAsync();
    }

    internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
