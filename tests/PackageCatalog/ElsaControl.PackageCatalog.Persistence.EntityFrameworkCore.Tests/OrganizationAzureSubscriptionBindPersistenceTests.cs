using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class OrganizationAzureSubscriptionBindPersistenceTests : IAsyncLifetime
{
    private static readonly Guid OrganizationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private CatalogDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(_connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);
        await _db.Database.MigrateAsync();
        _db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Azure customer" });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Sqlite_migration_creates_the_active_and_inflight_filtered_policy_indexes()
    {
        var indexes = await _db.Database.SqlQueryRaw<string>("""
            SELECT name AS Value
            FROM sqlite_master
            WHERE type = 'index' AND tbl_name = 'OrganizationAzureSubscriptionBinds'
            """).ToListAsync();

        Assert.Contains("IX_OrganizationAzureSubscriptionBinds_OneActive", indexes);
        Assert.Contains("IX_OrganizationAzureSubscriptionBinds_OneInFlight", indexes);
    }

    [Fact]
    public async Task Sqlite_allows_one_active_and_one_inflight_row_but_rejects_duplicates()
    {
        _db.OrganizationAzureSubscriptionBinds.AddRange(
            CreateBind("cccccccc-cccc-cccc-cccc-cccccccccccc", OrganizationAzureSubscriptionBindState.Active),
            CreateBind("dddddddd-dddd-dddd-dddd-dddddddddddd", OrganizationAzureSubscriptionBindState.PendingConsent),
            CreateBind("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", OrganizationAzureSubscriptionBindState.Unbound));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        _db.OrganizationAzureSubscriptionBinds.Add(CreateBind("ffffffff-ffff-ffff-ffff-ffffffffffff", OrganizationAzureSubscriptionBindState.Active));
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Store_persists_verification_fingerprint_and_unbind_then_allows_a_new_subscription()
    {
        var store = new OrganizationAzureSubscriptionBindStore(_db);
        var created = await store.CreatePendingAsync(CreateBind("cccccccc-cccc-cccc-cccc-cccccccccccc", OrganizationAzureSubscriptionBindState.PendingConsent));
        Assert.True(created.Succeeded);
        var bindId = created.Bind!.Id;

        var verifying = await store.TransitionAsync(new(
            OrganizationId,
            bindId,
            OrganizationAzureSubscriptionBindState.PendingConsent,
            OrganizationAzureSubscriptionBindState.Verifying,
            Now));
        Assert.True(verifying.Succeeded);
        var active = await store.TransitionAsync(new(
            OrganizationId,
            bindId,
            OrganizationAzureSubscriptionBindState.Verifying,
            OrganizationAzureSubscriptionBindState.Active,
            Now.AddMinutes(1),
            VerifiedAt: Now.AddMinutes(1),
            LastPreflightCode: "azure.lighthouse.observation-succeeded",
            RegistrationDefinitionFingerprint: "observed-fingerprint"));
        Assert.True(active.Succeeded);

        var persisted = await store.GetAsync(OrganizationId, bindId);
        Assert.Equal(OrganizationAzureSubscriptionBindState.Active, persisted!.State);
        Assert.Equal("observed-fingerprint", persisted.RegistrationDefinitionFingerprint);
        Assert.Equal("azure.lighthouse.observation-succeeded", persisted.LastPreflightCode);
        Assert.Equal(Now.AddMinutes(1), persisted.VerifiedAt);

        var unbound = await store.TransitionAsync(new(
            OrganizationId,
            bindId,
            OrganizationAzureSubscriptionBindState.Active,
            OrganizationAzureSubscriptionBindState.Unbound,
            Now.AddMinutes(2),
            UnbindReason: "customer requested removal"));
        Assert.True(unbound.Succeeded);

        var sameSubscription = await store.CreatePendingAsync(CreateBind("cccccccc-cccc-cccc-cccc-cccccccccccc", OrganizationAzureSubscriptionBindState.PendingConsent));
        Assert.Equal(OrganizationAzureSubscriptionBindFailure.SubscriptionMustChange, sameSubscription.Failure);

        var newSubscription = await store.CreatePendingAsync(CreateBind("dddddddd-dddd-dddd-dddd-dddddddddddd", OrganizationAzureSubscriptionBindState.PendingConsent));
        Assert.True(newSubscription.Succeeded);
        Assert.Equal(OrganizationAzureSubscriptionBindState.PendingConsent, (await store.GetLatestAsync(OrganizationId))!.State);
        Assert.Equal("dddddddd-dddd-dddd-dddd-dddddddddddd", (await store.GetLatestAsync(OrganizationId))!.SubscriptionId);
    }

    [Fact]
    public async Task Store_rejects_a_second_inflight_row_until_the_first_is_unbound()
    {
        var store = new OrganizationAzureSubscriptionBindStore(_db);
        var first = await store.CreatePendingAsync(CreateBind("cccccccc-cccc-cccc-cccc-cccccccccccc", OrganizationAzureSubscriptionBindState.PendingConsent));
        Assert.True(first.Succeeded);

        var second = await store.CreatePendingAsync(CreateBind("dddddddd-dddd-dddd-dddd-dddddddddddd", OrganizationAzureSubscriptionBindState.PendingConsent));

        Assert.Equal(OrganizationAzureSubscriptionBindFailure.BindInFlight, second.Failure);
        var unbound = await store.TransitionAsync(new(
            OrganizationId,
            first.Bind!.Id,
            OrganizationAzureSubscriptionBindState.PendingConsent,
            OrganizationAzureSubscriptionBindState.Unbound,
            Now,
            UnbindReason: "cancelled"));
        Assert.True(unbound.Succeeded);
        Assert.True((await store.CreatePendingAsync(CreateBind("dddddddd-dddd-dddd-dddd-dddddddddddd", OrganizationAzureSubscriptionBindState.PendingConsent))).Succeeded);
    }

    private static OrganizationAzureSubscriptionBind CreateBind(
        string subscriptionId,
        OrganizationAzureSubscriptionBindState state) =>
        new()
        {
            OrganizationId = OrganizationId,
            CustomerTenantId = "11111111-1111-1111-1111-111111111111",
            SubscriptionId = subscriptionId,
            ManagingTenantId = "22222222-2222-2222-2222-222222222222",
            ManagingPrincipalObjectId = "33333333-3333-3333-3333-333333333333",
            ManagingPrincipalClientId = "44444444-4444-4444-4444-444444444444",
            RegistrationDefinitionId = "/subscriptions/" + subscriptionId + "/providers/Microsoft.ManagedServices/registrationDefinitions/9f8cf4c0-1f7a-4c7b-9c7b-e5f26a2d8bd9",
            RegistrationDefinitionFingerprint = "recorded-fingerprint",
            State = state,
            CreatedAt = Now,
            UpdatedAt = Now
        };

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
