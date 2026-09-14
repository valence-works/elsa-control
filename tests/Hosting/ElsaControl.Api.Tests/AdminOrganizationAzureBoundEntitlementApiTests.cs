using System.Net;
using System.Net.Http.Json;
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

public sealed class AdminOrganizationAzureBoundEntitlementApiTests :
    IClassFixture<AdminOrganizationAzureBoundEntitlementApiTests.Fixture>,
    IAsyncLifetime
{
    private const string Reason = "Guided Azure design partner";
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
    private readonly Fixture _fixture;
    private readonly ControlApiTestApplication _app;
    private readonly HttpClient _operator;
    private readonly Guid _organizationId = Guid.NewGuid();

    public AdminOrganizationAzureBoundEntitlementApiTests(Fixture fixture)
    {
        _fixture = fixture;
        _app = fixture.Application;
        _operator = _app.CreateClient();
        _operator.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
    }

    public async Task InitializeAsync()
    {
        _fixture.Clock.UtcNow = Now;
        await _app.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = _organizationId, Name = "Azure design partner" });
            return Task.CompletedTask;
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Operator_mints_reads_and_revokes_an_active_bound_entitlement()
    {
        await AddActiveBindAsync();

        var put = await _operator.PutControlJsonAsync(Url(), new
        {
            reason = Reason,
            maxInstances = 2,
            expiresAt = Now.AddDays(30).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var minted = await put.Content.ReadControlJsonAsync<AdminAzureBoundEntitlementResponse>();
        Assert.Equal(new AdminAzureBoundEntitlementResponse(
            _organizationId,
            OrganizationAzureBoundEntitlementState.Active,
            2,
            Now.AddDays(30),
            Now), minted);
        Assert.DoesNotContain(Reason, await put.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(minted, await _operator.GetControlJsonAsync<AdminAzureBoundEntitlementResponse>(Url()));

        Assert.True((await EvaluateAsync(ElsaInstanceOperationAction.Create, 1)).Allowed);
        Assert.Equal(
            ElsaInstanceCommercialOperation.InstanceLimitReached,
            (await EvaluateAsync(ElsaInstanceOperationAction.Create, 2)).Code);

        _fixture.Clock.UtcNow = Now.AddHours(1);
        Assert.Equal(HttpStatusCode.NoContent, (await _operator.DeleteAsync(Url())).StatusCode);
        Assert.Equal(
            ElsaInstanceCommercialOperation.EntitlementRequired,
            (await EvaluateAsync(ElsaInstanceOperationAction.UpdateIntent)).Code);
        Assert.True((await EvaluateAsync(ElsaInstanceOperationAction.Stop)).Allowed);

        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(
            BillingProviderNames.AzureBound,
            (await db.OrganizationSubscriptions.AsNoTracking()
                .SingleAsync(x => x.OrganizationId == _organizationId)).Provider);
        Assert.Equal(
            OrganizationSubscriptionState.Deleted,
            (await db.OrganizationSubscriptions.AsNoTracking()
                .SingleAsync(x => x.OrganizationId == _organizationId)).State);
        Assert.Equal(
            2,
            await db.OrganizationAuditRecords.CountAsync(x =>
                x.OrganizationId == _organizationId && x.TargetType == "azure-bound-entitlement"));
    }

    [Fact]
    public async Task Mint_without_an_active_nonempty_bind_is_refused_without_writes()
    {
        var response = await _operator.PutControlJsonAsync(Url(), new
        {
            reason = Reason,
            maxInstances = 1,
            expiresAt = (string?)null
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            ElsaInstanceCommercialOperation.BindingRequired,
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.False(await db.OrganizationSubscriptions.AnyAsync(x => x.OrganizationId == _organizationId));
        Assert.False(await db.OrganizationEntitlementSnapshots.AnyAsync(x => x.OrganizationId == _organizationId));
    }

    [Fact]
    public async Task Anonymous_caller_cannot_read_the_entitlement()
    {
        var response = await _app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(Url());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private string Url() => $"/api/admin/organizations/{_organizationId}/azure-bound-entitlement";

    private async Task AddActiveBindAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        db.OrganizationAzureSubscriptionBinds.Add(new OrganizationAzureSubscriptionBind
        {
            OrganizationId = _organizationId,
            CustomerTenantId = "customer-tenant",
            SubscriptionId = "00000000-0000-0000-0000-000000000435",
            ManagingTenantId = "managing-tenant",
            ManagingPrincipalObjectId = "principal-object",
            ManagingPrincipalClientId = "principal-client",
            RegistrationDefinitionId = "registration-definition",
            State = OrganizationAzureSubscriptionBindState.Active,
            VerifiedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();
    }

    private async Task<ElsaInstanceCommercialGateDecision> EvaluateAsync(
        ElsaInstanceOperationAction action,
        int? activeInstanceCount = null)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IElsaInstanceCommercialGate>()
            .EvaluateAsync(_organizationId, action, activeInstanceCount);
    }

    public sealed class Fixture : IAsyncLifetime
    {
        public Fixture() => Application = new(configureServices: services =>
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
