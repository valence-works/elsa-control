using System.Text.Json;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class OrganizationInternalEntitlementPersistenceTests : IAsyncLifetime
{
    private const string Reason = "Internal dogfood of managed hosting";
    private const string OperatorSubject = "operator-subject-a";
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrganizationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private CatalogDbContext _db = null!;
    private OrganizationBillingStore _store = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = CreateDb(_connection);
        await _db.Database.EnsureCreatedAsync();
        await SeedOrganizationAsync(_db);
        _store = new OrganizationBillingStore(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Grant_records_an_internal_subscription_and_owns_only_the_managed_hosting_fields()
    {
        _db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = OrganizationId,
            CanCreateCustomSources = true,
            MaxSources = 17,
            MaxWorkspaces = 4,
            PrivateFeedsEnabled = true,
            DeploymentTargetsEnabled = true,
            CreatedAt = Now.AddDays(-1),
            UpdatedAt = Now.AddDays(-1),
            SyncedAt = Now.AddDays(-1)
        });
        await _db.SaveChangesAsync();

        var result = await GrantAsync();

        Assert.Equal(OrganizationInternalEntitlementOutcome.Granted, result.Outcome);
        Assert.Equal(new OrganizationInternalEntitlementStatus(OrganizationId, OrganizationInternalEntitlementState.Active, 2, Now.AddDays(30), Now), result.Status);
        var persisted = await ReadAsync();
        var subscription = Assert.Single(persisted.Subscriptions);
        Assert.Equal(BillingProviderNames.Internal, subscription.Provider);
        Assert.Equal(OrganizationSubscriptionState.Active, subscription.State);
        Assert.Equal(Now, subscription.ActivatedAt);
        Assert.Null(subscription.ProviderCustomerReference);
        var entitlement = Assert.Single(persisted.Entitlements);
        Assert.True(entitlement.ManagedHostingEnabled);
        Assert.Equal(2, entitlement.MaxInstances);
        Assert.Equal(Now.AddDays(30), entitlement.ManagedHostingExpiresAt);
        Assert.Equal(OrganizationSubscriptionState.Active, entitlement.SubscriptionState);
        Assert.Equal(subscription.Id, entitlement.SubscriptionId);
        Assert.True(entitlement.CanCreateCustomSources);
        Assert.Equal(17, entitlement.MaxSources);
        Assert.Equal(4, entitlement.MaxWorkspaces);
        Assert.True(entitlement.PrivateFeedsEnabled);
        Assert.True(entitlement.DeploymentTargetsEnabled);
        var audit = Assert.Single(persisted.Audits);
        Assert.Equal(OrganizationAuditAction.EntitlementChanged, audit.Action);
        Assert.Equal("internal-entitlement", audit.TargetType);
        Assert.Equal(subscription.Id.ToString("D"), audit.TargetId);
        Assert.Equal(
            $"Internal managed-hosting entitlement granted (max instances 2, expires 2026-10-12T10:00:00.0000000Z). Reason: {Reason}",
            audit.Summary);
        Assert.Equal(OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject(OperatorSubject), audit.OperatorSubject);
    }

    [Fact]
    public async Task Regrant_updates_cap_and_expiry_on_the_same_subscription_idempotently_and_audits_each_grant()
    {
        await GrantAsync(maxInstances: 1);

        var regranted = await GrantAsync(maxInstances: 3, days: 60, at: Now.AddHours(1));
        var persistedAfterRegrant = await ReadAsync();
        var replayed = await GrantAsync(maxInstances: 3, days: 60, at: Now.AddHours(1));

        Assert.Equal(OrganizationInternalEntitlementOutcome.Regranted, regranted.Outcome);
        Assert.Equal(OrganizationInternalEntitlementOutcome.Regranted, replayed.Outcome);
        Assert.Equal(regranted.Status, replayed.Status);
        var persisted = await ReadAsync();
        Assert.Single(persisted.Subscriptions);
        Assert.Equal(Json(persistedAfterRegrant.Subscriptions), Json(persisted.Subscriptions));
        Assert.Equal(Json(persistedAfterRegrant.Entitlements), Json(persisted.Entitlements));
        var entitlement = Assert.Single(persisted.Entitlements);
        Assert.Equal(3, entitlement.MaxInstances);
        Assert.Equal(Now.AddHours(1).AddDays(60), entitlement.ManagedHostingExpiresAt);
        Assert.Collection(
            persisted.Audits,
            audit => Assert.StartsWith("Internal managed-hosting entitlement granted (max instances 1", audit.Summary),
            audit => Assert.StartsWith("Internal managed-hosting entitlement re-granted (max instances 3", audit.Summary),
            audit => Assert.StartsWith("Internal managed-hosting entitlement re-granted (max instances 3", audit.Summary));
    }

    [Fact]
    public async Task Revoke_disables_and_expires_the_grant_once_and_leaves_the_subscription_untouched()
    {
        await GrantAsync();
        var subscriptionBefore = Json((await ReadAsync()).Subscriptions);

        var revoked = await RevokeAsync(Now.AddHours(1));
        var repeated = await RevokeAsync(Now.AddHours(2));

        Assert.Equal(OrganizationInternalEntitlementOutcome.Revoked, revoked.Outcome);
        Assert.Equal(OrganizationInternalEntitlementOutcome.Unchanged, repeated.Outcome);
        Assert.Equal(OrganizationInternalEntitlementState.Revoked, revoked.Status!.State);
        var persisted = await ReadAsync();
        Assert.Equal(subscriptionBefore, Json(persisted.Subscriptions));
        var entitlement = Assert.Single(persisted.Entitlements);
        Assert.False(entitlement.ManagedHostingEnabled);
        Assert.Equal(Now.AddHours(1), entitlement.ManagedHostingExpiresAt);
        Assert.Equal(OrganizationSubscriptionState.Active, entitlement.SubscriptionState);
        Assert.Equal(2, persisted.Audits.Count);
        var audit = persisted.Audits[1];
        Assert.Equal("Internal managed-hosting entitlement revoked.", audit.Summary);
        Assert.Equal(OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject(OperatorSubject), audit.OperatorSubject);
        Assert.Equal(OrganizationInternalEntitlementState.Revoked, (await GetAsync(Now.AddHours(3))).Status!.State);
    }

    [Fact]
    public async Task Regrant_after_revoke_restores_managed_hosting_on_the_same_subscription()
    {
        await GrantAsync();
        await RevokeAsync(Now.AddHours(1));

        var result = await GrantAsync(maxInstances: 1, days: 10, at: Now.AddHours(2));

        Assert.Equal(OrganizationInternalEntitlementOutcome.Regranted, result.Outcome);
        Assert.Equal(OrganizationInternalEntitlementState.Active, result.Status!.State);
        var persisted = await ReadAsync();
        Assert.Single(persisted.Subscriptions);
        Assert.True(Assert.Single(persisted.Entitlements).ManagedHostingEnabled);
    }

    [Fact]
    public async Task Status_reports_expiry_from_the_supplied_clock_without_any_background_job()
    {
        await GrantAsync(days: 1);

        Assert.Equal(OrganizationInternalEntitlementState.Active, (await GetAsync(Now.AddDays(1).AddTicks(-1))).Status!.State);
        Assert.Equal(OrganizationInternalEntitlementState.Expired, (await GetAsync(Now.AddDays(1))).Status!.State);
    }

    [Fact]
    public async Task Revoke_without_a_grant_is_reported_and_writes_nothing()
    {
        var result = await RevokeAsync();

        Assert.Equal(OrganizationInternalEntitlementOutcome.NotGranted, result.Outcome);
        Assert.Equal(OrganizationInternalEntitlementState.None, result.Status!.State);
        AssertNothingWritten(await ReadAsync());
    }

    [Fact]
    public async Task Unknown_organization_is_reported_without_writes()
    {
        var unknown = Guid.NewGuid();

        Assert.Equal(OrganizationInternalEntitlementOutcome.OrganizationNotFound, (await GrantAsync(organizationId: unknown)).Outcome);
        Assert.Equal(OrganizationInternalEntitlementOutcome.OrganizationNotFound, (await RevokeAsync(organizationId: unknown)).Outcome);
        Assert.Equal(OrganizationInternalEntitlementOutcome.OrganizationNotFound, (await _store.GetInternalEntitlementAsync(unknown, Now)).Outcome);
        AssertNothingWritten(await ReadAsync());
    }

    [Fact]
    public async Task Provider_owned_subscription_refuses_grant_and_revoke_and_nothing_is_written()
    {
        await _store.StartTrialAsync(OrganizationId, BillingProviderNames.Stripe, Now.AddDays(-1));
        var before = await ReadAsync();

        var grant = await GrantAsync();
        var revoke = await RevokeAsync();
        var status = await GetAsync(Now);

        Assert.Equal(OrganizationInternalEntitlementOutcome.CommercialSubscriptionExists, grant.Outcome);
        Assert.Equal(OrganizationInternalEntitlementOutcome.CommercialSubscriptionExists, revoke.Outcome);
        Assert.Equal(new OrganizationInternalEntitlementStatus(OrganizationId, OrganizationInternalEntitlementState.CommercialSubscription, null, null, null), status.Status);
        var after = await ReadAsync();
        Assert.Equal(Json(before), Json(after));
        var entitlement = Assert.Single(after.Entitlements);
        Assert.False(entitlement.ManagedHostingEnabled);
        Assert.Null(entitlement.ManagedHostingExpiresAt);
        Assert.Equal(BillingProviderNames.Stripe, Assert.Single(after.Subscriptions).Provider);
    }

    [Fact]
    public async Task Lifecycle_projected_without_a_subscription_row_is_treated_as_provider_owned()
    {
        _db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = OrganizationId,
            SubscriptionState = OrganizationSubscriptionState.Active,
            CreatedAt = Now,
            UpdatedAt = Now,
            SyncedAt = Now
        });
        await _db.SaveChangesAsync();
        var before = await ReadAsync();

        var result = await GrantAsync();

        Assert.Equal(OrganizationInternalEntitlementOutcome.CommercialSubscriptionExists, result.Outcome);
        Assert.Equal(Json(before), Json(await ReadAsync()));
    }

    [Fact]
    public async Task Provider_checkout_and_webhooks_cannot_take_over_an_internal_grant()
    {
        await GrantAsync();
        var before = await ReadAsync();

        await Assert.ThrowsAsync<BillingProviderEventConflictException>(() =>
            _store.StartTrialAsync(OrganizationId, BillingProviderNames.Stripe, Now.AddHours(1)));
        _db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<BillingProviderEventConflictException>(() =>
            _store.ConsumeAsync(StripeEvent(OrganizationSubscriptionState.Suspended), Now.AddHours(1)));

        var after = await ReadAsync();
        Assert.Equal(Json(before.Subscriptions), Json(after.Subscriptions));
        Assert.Equal(Json(before.Entitlements), Json(after.Entitlements));
        Assert.Empty(await _db.BillingProviderEvents.ToListAsync());
    }

    [Theory]
    [InlineData("internal")]
    [InlineData("INTERNAL")]
    public async Task Provider_pipelines_cannot_claim_the_reserved_internal_provider(string provider)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.StartTrialAsync(OrganizationId, provider, Now));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.ConsumeAsync(StripeEvent(OrganizationSubscriptionState.Active) with { Provider = provider }, Now));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.RecordUnknownAsync(StripeEvent(null) with { Provider = provider }, Now));

        AssertNothingWritten(await ReadAsync());
        Assert.Empty(await _db.BillingProviderEvents.ToListAsync());
    }

    [Fact]
    public async Task Closed_internal_subscription_cannot_be_regranted_but_can_still_be_revoked()
    {
        await GrantAsync();
        await _store.RequestDeletionAsync(OrganizationId, Now.AddHours(1));
        _db.ChangeTracker.Clear();
        var before = await ReadAsync();

        var regrant = await GrantAsync(at: Now.AddHours(2));

        Assert.Equal(OrganizationInternalEntitlementOutcome.SubscriptionClosed, regrant.Outcome);
        Assert.Equal(OrganizationInternalEntitlementState.Closed, regrant.Status!.State);
        Assert.Equal(Json(before), Json(await ReadAsync()));
        Assert.Equal(OrganizationInternalEntitlementOutcome.Revoked, (await RevokeAsync(Now.AddHours(3))).Outcome);
        Assert.False(Assert.Single((await ReadAsync()).Entitlements).ManagedHostingEnabled);
    }

    [Theory]
    [InlineData(4, 30)]
    [InlineData(2, 91)]
    [InlineData(2, -1)]
    public async Task Store_rejects_out_of_bounds_terms_at_the_persistence_boundary(int maxInstances, int days)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => GrantAsync(maxInstances, days));

        AssertNothingWritten(await ReadAsync());
    }

    [Fact]
    public async Task Grant_and_revoke_run_under_a_retrying_execution_strategy()
    {
        // SQL Server's retrying strategy rejects user transactions opened outside it.
        await using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection, sqlite => sqlite.ExecutionStrategy(dependencies => new RetryingExecutionStrategy(dependencies)))
            .Options);
        var store = new OrganizationBillingStore(db);

        Assert.Equal(OrganizationInternalEntitlementOutcome.Granted, (await store.GrantInternalEntitlementAsync(Grant(), Now)).Outcome);
        Assert.Equal(OrganizationInternalEntitlementOutcome.Revoked, (await store.RevokeInternalEntitlementAsync(OrganizationId, OperatorSubject, Now.AddHours(1))).Outcome);
    }

    [Fact]
    public async Task Concurrent_grants_converge_on_one_subscription_and_one_snapshot()
    {
        await using var shared = await SharedDatabase.CreateAsync();
        var first = await shared.OpenStoreAsync();
        var second = await shared.OpenStoreAsync();

        var results = await Task.WhenAll(
            first.GrantInternalEntitlementAsync(Grant(maxInstances: 1), Now),
            second.GrantInternalEntitlementAsync(Grant(maxInstances: 3), Now));

        Assert.Equal(
            [OrganizationInternalEntitlementOutcome.Granted, OrganizationInternalEntitlementOutcome.Regranted],
            results.Select(x => x.Outcome).Order());
        var persisted = await shared.ReadAsync();
        Assert.Single(persisted.Subscriptions);
        var lastWriter = results.Single(x => x.Outcome == OrganizationInternalEntitlementOutcome.Regranted);
        Assert.Equal(lastWriter.Status!.MaxInstances, Assert.Single(persisted.Entitlements).MaxInstances);
        Assert.Equal(2, persisted.Audits.Count);
    }

    [Fact]
    public async Task Concurrent_grant_and_provider_checkout_leave_exactly_one_owner()
    {
        await using var shared = await SharedDatabase.CreateAsync();
        var grantStore = await shared.OpenStoreAsync();
        var checkoutStore = await shared.OpenStoreAsync();

        var grantTask = CaptureAsync(() => grantStore.GrantInternalEntitlementAsync(Grant(), Now));
        var checkoutTask = CaptureAsync(() => checkoutStore.StartTrialAsync(OrganizationId, BillingProviderNames.Stripe, Now));
        await Task.WhenAll(grantTask, checkoutTask);
        var (grant, checkout) = (await grantTask, await checkoutTask);

        var persisted = await shared.ReadAsync();
        var subscription = Assert.Single(persisted.Subscriptions);
        var entitlement = Assert.Single(persisted.Entitlements);
        if (subscription.Provider == BillingProviderNames.Stripe)
        {
            // Checkout won: the grant must be refused, never layered over it.
            Assert.Null(checkout.Error);
            Assert.Equal(OrganizationInternalEntitlementOutcome.CommercialSubscriptionExists, grant.Value!.Outcome);
            Assert.False(entitlement.ManagedHostingEnabled);
            Assert.Null(entitlement.ManagedHostingExpiresAt);
            Assert.DoesNotContain(persisted.Audits, x => x.TargetType == "internal-entitlement");
        }
        else
        {
            // Grant won: checkout must fail loudly rather than attach to it.
            Assert.Equal(BillingProviderNames.Internal, subscription.Provider);
            Assert.Equal(OrganizationInternalEntitlementOutcome.Granted, grant.Value!.Outcome);
            Assert.IsType<BillingProviderEventConflictException>(checkout.Error);
            Assert.True(entitlement.ManagedHostingEnabled);
        }
    }

    private Task<OrganizationInternalEntitlementResult> GrantAsync(
        int maxInstances = 2,
        int days = 30,
        DateTimeOffset? at = null,
        Guid? organizationId = null)
    {
        var now = at ?? Now;
        return _store.GrantInternalEntitlementAsync(Grant(maxInstances, now.AddDays(days), organizationId), now);
    }

    private Task<OrganizationInternalEntitlementResult> RevokeAsync(DateTimeOffset? at = null, Guid? organizationId = null) =>
        _store.RevokeInternalEntitlementAsync(organizationId ?? OrganizationId, OperatorSubject, at ?? Now);

    private Task<OrganizationInternalEntitlementResult> GetAsync(DateTimeOffset at) =>
        _store.GetInternalEntitlementAsync(OrganizationId, at);

    private Task<Persisted> ReadAsync()
    {
        _db.ChangeTracker.Clear();
        return Persisted.ReadAsync(_db);
    }

    private static OrganizationInternalEntitlementGrant Grant(int maxInstances = 2, DateTimeOffset? expiresAt = null, Guid? organizationId = null) =>
        new(organizationId ?? OrganizationId, new(Reason, maxInstances, expiresAt ?? Now.AddDays(30)), OperatorSubject);

    private static BillingProviderEvent StripeEvent(OrganizationSubscriptionState? state) =>
        new(OrganizationId, BillingProviderNames.Stripe, "evt_takeover", "customer.subscription.updated", state, Now.AddHours(1), "sha256:" + new string('c', 64));

    private static void AssertNothingWritten(Persisted persisted)
    {
        Assert.Empty(persisted.Subscriptions);
        Assert.Empty(persisted.Entitlements);
        Assert.Empty(persisted.Audits);
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static async Task<(T? Value, Exception? Error)> CaptureAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return (await action(), null);
        }
        catch (Exception error)
        {
            return (default, error);
        }
    }

    private static async Task SeedOrganizationAsync(CatalogDbContext db)
    {
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Valence dogfood" });
        await db.SaveChangesAsync();
    }

    private static CatalogDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite(connection).Options);

    private sealed class RetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, 1, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => false;
    }

    private sealed record Persisted(
        List<OrganizationSubscription> Subscriptions,
        List<OrganizationEntitlementSnapshot> Entitlements,
        List<OrganizationAuditRecord> Audits)
    {
        public static async Task<Persisted> ReadAsync(CatalogDbContext db) => new(
            await db.OrganizationSubscriptions.AsNoTracking().ToListAsync(),
            await db.OrganizationEntitlementSnapshots.AsNoTracking().ToListAsync(),
            await db.OrganizationAuditRecords.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync());
    }

    /// <summary>Shared-cache SQLite database with one connection per concurrent store.</summary>
    private sealed class SharedDatabase : IAsyncDisposable
    {
        private readonly string _connectionString =
            $"Data Source=file:internal-entitlement-{Guid.NewGuid():N}?mode=memory&cache=shared;Default Timeout=5";
        private readonly List<IAsyncDisposable> _resources = [];
        private CatalogDbContext _anchor = null!;

        public static async Task<SharedDatabase> CreateAsync()
        {
            var database = new SharedDatabase();
            database._anchor = await database.OpenAsync();
            await database._anchor.Database.EnsureCreatedAsync();
            await SeedOrganizationAsync(database._anchor);
            return database;
        }

        public async Task<OrganizationBillingStore> OpenStoreAsync() => new(await OpenAsync());

        public Task<Persisted> ReadAsync()
        {
            _anchor.ChangeTracker.Clear();
            return Persisted.ReadAsync(_anchor);
        }

        public async ValueTask DisposeAsync()
        {
            for (var index = _resources.Count - 1; index >= 0; index--)
                await _resources[index].DisposeAsync();
        }

        private async Task<CatalogDbContext> OpenAsync()
        {
            var connection = new SqliteConnection(_connectionString);
            _resources.Add(connection);
            await connection.OpenAsync();
            var db = CreateDb(connection);
            _resources.Add(db);
            return db;
        }
    }
}
