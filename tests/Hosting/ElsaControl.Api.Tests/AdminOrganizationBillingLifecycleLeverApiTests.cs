using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElsaControl.Api.Admin.Organizations;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.OrganizationBilling;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElsaControl.Api.Tests;

public sealed class AdminOrganizationBillingLifecycleLeverApiTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AllowlistedOrganizationId = Guid.Parse("20000000-0000-0000-0000-000000000022");
    private static readonly Guid OtherOrganizationId = Guid.Parse("20000000-0000-0000-0000-000000000099");
    private const string BffClientId = "elsa-cloud-bff";
    private const string BffScope = CloudBffDefaults.DefaultScope;

    [Fact]
    public async Task Flag_off_refuses_even_for_a_synthetic_org_and_writes_nothing()
    {
        await using var app = CreateApp(enabled: false);
        var organizationId = await SeedPastDueAsync(app, synthetic: true);

        var response = await PostAsync(Operator(app), organizationId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(StagingBillingLifecycleLeverDefaults.DisabledCode, await ProblemCodeAsync(response));
        await AssertLeverDidNotRunAsync(app, organizationId, OrganizationSubscriptionState.PastDue);
    }

    [Fact]
    public async Task Synthetic_org_moves_grace_and_runs_the_normal_advancer()
    {
        await using var app = CreateApp(enabled: true);
        var organizationId = await SeedPastDueAsync(app, synthetic: true);

        var response = await PostAsync(Operator(app), organizationId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadControlJsonAsync<AdminBillingLifecycleDeadlineAdvanceResponse>();
        Assert.NotNull(body);
        Assert.Equal(organizationId, body.OrganizationId);
        Assert.Equal(OrganizationBillingLifecycleDeadline.GraceEndsAt, body.Deadline);
        Assert.Equal(OrganizationSubscriptionState.PastDue, body.PreviousState);
        Assert.Equal(OrganizationSubscriptionState.Constrained, body.CurrentState);
        Assert.True(body.Advanced);
        Assert.True(body.NoticeCreated);
        Assert.Equal(Now, body.DeadlineAt);
        await AssertAdvancedAsync(app, organizationId, OrganizationSubscriptionState.Constrained);
        await AssertLeverAuditAsync(app, organizationId);
        await AssertNoEngineWritesAsync(app);
    }

    [Fact]
    public async Task Allowlisted_org_moves_grace_and_runs_the_normal_advancer()
    {
        await using var app = CreateApp(enabled: true, allowlisted: AllowlistedOrganizationId);
        var organizationId = await SeedPastDueAsync(app, synthetic: false, organizationId: AllowlistedOrganizationId);

        var response = await PostAsync(Operator(app), organizationId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadControlJsonAsync<AdminBillingLifecycleDeadlineAdvanceResponse>();
        Assert.Equal(OrganizationSubscriptionState.Constrained, body!.CurrentState);
        await AssertAdvancedAsync(app, organizationId, OrganizationSubscriptionState.Constrained);
        await AssertLeverAuditAsync(app, organizationId);
    }

    [Fact]
    public async Task Non_allowlisted_org_is_refused_without_writes()
    {
        await using var app = CreateApp(enabled: true, allowlisted: AllowlistedOrganizationId);
        var organizationId = await SeedPastDueAsync(app, synthetic: false, organizationId: OtherOrganizationId);

        var response = await PostAsync(Operator(app), organizationId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(StagingBillingLifecycleLeverDefaults.OrganizationNotAllowedCode, await ProblemCodeAsync(response));
        await AssertLeverDidNotRunAsync(app, organizationId, OrganizationSubscriptionState.PastDue);
    }

    [Fact]
    public async Task Cloud_bff_caller_is_denied_and_the_route_is_not_allowlisted()
    {
        await using var app = CreateApp(
            enabled: true,
            additionalConfiguration: new Dictionary<string, string?>
            {
                [$"{CloudBffOptions.ConfigurationSection}:Enabled"] = "true",
                [$"{CloudBffOptions.ConfigurationSection}:ClientId"] = BffClientId,
                [$"{CloudBffOptions.ConfigurationSection}:Scope"] = BffScope
            });
        var organizationId = await SeedPastDueAsync(app, synthetic: true);
        var bff = app.CreateControlIdentityClient(
            subject: "bff-user",
            claims: new Dictionary<string, string> { ["azp"] = BffClientId, ["scp"] = BffScope });

        var response = await PostAsync(bff, organizationId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("cloud-bff.denied", await ProblemCodeAsync(response));
        await AssertLeverDidNotRunAsync(app, organizationId, OrganizationSubscriptionState.PastDue);

        var allowed = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<CloudBffAllowedEndpointMetadata>() is not null)
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}") ?? []);
        Assert.DoesNotContain(
            "POST /api/admin/organizations/{organizationId:guid}/billing/lifecycle-deadline/advance",
            allowed,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task Anonymous_and_customer_callers_are_denied_without_writes()
    {
        await using var app = CreateApp(enabled: true);
        var organizationId = await SeedPastDueAsync(app, synthetic: true);

        var anonymous = await PostAsync(app.CreateClient(), organizationId);
        var customer = app.CreateClient(new() { AllowAutoRedirect = false });
        app.AddControlSessionCookie(customer, subject: "customer-user", expiresUtc: Now.AddHours(2));
        customer.DefaultRequestHeaders.Add("Origin", "http://localhost");
        var customerResponse = await PostAsync(customer, organizationId);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, customerResponse.StatusCode);
        await AssertLeverDidNotRunAsync(app, organizationId, OrganizationSubscriptionState.PastDue);
    }

    [Fact]
    public async Task Constrained_deadline_is_refused_while_the_subscription_is_still_in_grace()
    {
        await using var app = CreateApp(enabled: true);
        var organizationId = await SeedPastDueAsync(app, synthetic: true);

        var response = await PostAsync(
            Operator(app),
            organizationId,
            new AdminBillingLifecycleDeadlineAdvanceRequest(OrganizationBillingLifecycleDeadline.ConstrainedAt));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(StagingBillingLifecycleLeverDefaults.DeadlineNotApplicableCode, await ProblemCodeAsync(response));
        await AssertLeverDidNotRunAsync(app, organizationId, OrganizationSubscriptionState.PastDue);
    }

    private static ControlApiTestApplication CreateApp(
        bool enabled,
        Guid? allowlisted = null,
        IReadOnlyDictionary<string, string?>? additionalConfiguration = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            [$"{StagingBillingLifecycleLeverOptions.ConfigurationSection}:Enabled"] = enabled ? "true" : "false",
            [$"{StagingBillingLifecycleLeverOptions.ConfigurationSection}:SyntheticCustomerReference"] = "synthetic"
        };
        if (allowlisted is { } organizationId)
            configuration[$"{StagingBillingLifecycleLeverOptions.ConfigurationSection}:AllowedOrganizationIds:0"] = organizationId.ToString("D");
        if (additionalConfiguration is not null)
        {
            foreach (var (key, value) in additionalConfiguration)
                configuration[key] = value;
        }

        return new ControlApiTestApplication(configuration, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        });
    }

    private static HttpClient Operator(ControlApiTestApplication app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        Guid organizationId,
        AdminBillingLifecycleDeadlineAdvanceRequest? request = null) =>
        client.PostAsJsonAsync(
            $"/api/admin/organizations/{organizationId:D}/billing/lifecycle-deadline/advance",
            request ?? new AdminBillingLifecycleDeadlineAdvanceRequest(OrganizationBillingLifecycleDeadline.GraceEndsAt),
            ControlApiTestApplication.JsonOptions);

    private static async Task<Guid> SeedPastDueAsync(
        ControlApiTestApplication app,
        bool synthetic,
        Guid? organizationId = null)
    {
        var id = organizationId ?? Guid.NewGuid();
        await app.SeedAsync(async db =>
        {
            db.Organizations.Add(new Organization
            {
                Id = id,
                Name = "Harness org",
                CustomerReference = synthetic ? "synthetic" : null
            });
            await db.SaveChangesAsync();
            var store = new OrganizationBillingStore(db);
            await store.StartTrialAsync(id, BillingProviderNames.Stripe, Now.AddDays(-14));
            await store.ConsumeAsync(
                new BillingProviderEvent(
                    id,
                    BillingProviderNames.Stripe,
                    "evt_past_due",
                    "customer.subscription.updated",
                    OrganizationSubscriptionState.PastDue,
                    Now,
                    "sha256:" + new string('a', 64),
                    "cus_harness",
                    "sub_harness"),
                Now);
        });
        return id;
    }

    private static async Task AssertAdvancedAsync(
        ControlApiTestApplication app,
        Guid organizationId,
        OrganizationSubscriptionState expected)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var subscription = await db.OrganizationSubscriptions.AsNoTracking().SingleAsync(x => x.OrganizationId == organizationId);
        var entitlement = await db.OrganizationEntitlementSnapshots.AsNoTracking().SingleAsync(x => x.OrganizationId == organizationId);
        var notices = await db.OrganizationBillingLifecycleNotices.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .Select(x => x.Kind)
            .ToListAsync();
        var lifecycleAudits = await db.OrganizationAuditRecords.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Action == OrganizationAuditAction.SubscriptionChanged)
            .ToListAsync();

        Assert.Equal(expected, subscription.State);
        Assert.Equal(expected, entitlement.SubscriptionState);
        Assert.Contains(OrganizationBillingLifecycleNoticeKind.ConstraintStarted, notices);
        Assert.Contains(lifecycleAudits, audit => audit.Summary.Contains("Constrained", StringComparison.Ordinal));
        Assert.True(subscription.GraceEndsAt <= Now);
        Assert.Equal(subscription.GraceEndsAt, subscription.ConstrainedAt);
    }

    private static async Task AssertLeverAuditAsync(ControlApiTestApplication app, Guid organizationId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var audit = await db.OrganizationAuditRecords.AsNoTracking().SingleAsync(x =>
            x.OrganizationId == organizationId &&
            x.Action == OrganizationAuditAction.BillingLifecycleDeadlineMoved);
        Assert.Equal("subscription", audit.TargetType);
        Assert.Equal(OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject("api-key"), audit.OperatorSubject);
        Assert.Contains("GraceEndsAt", audit.Summary, StringComparison.Ordinal);
    }

    private static async Task AssertLeverDidNotRunAsync(
        ControlApiTestApplication app,
        Guid organizationId,
        OrganizationSubscriptionState expected)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var subscription = await db.OrganizationSubscriptions.AsNoTracking().SingleAsync(x => x.OrganizationId == organizationId);
        Assert.Equal(expected, subscription.State);
        Assert.Equal(Now.AddDays(7), subscription.GraceEndsAt);
        Assert.Null(subscription.ConstrainedAt);
        Assert.Empty(await db.OrganizationAuditRecords.AsNoTracking()
            .Where(x => x.Action == OrganizationAuditAction.BillingLifecycleDeadlineMoved)
            .ToListAsync());
        Assert.Empty(await db.OrganizationBillingLifecycleNotices.AsNoTracking()
            .Where(x => x.Kind == OrganizationBillingLifecycleNoticeKind.ConstraintStarted)
            .ToListAsync());
    }

    private static async Task AssertNoEngineWritesAsync(ControlApiTestApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(0, await CountAsync(db, "ElsaInstances"));
        Assert.Equal(0, await CountAsync(db, "ElsaInstanceOperations"));
        Assert.Equal(0, await CountAsync(db, "DeploymentRuns"));
    }

    private static async Task<int> CountAsync(CatalogDbContext db, string table)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
