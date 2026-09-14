using System.Data.Common;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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

    [Fact]
    public async Task Store_detaches_a_failed_insert_before_the_same_context_is_reused()
    {
        await _db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectSpecificAzureBind
            BEFORE INSERT ON OrganizationAzureSubscriptionBinds
            WHEN NEW.SubscriptionId = 'ffffffff-ffff-ffff-ffff-ffffffffffff'
            BEGIN
                SELECT RAISE(ABORT, 'forced bind conflict');
            END;
            """);
        var store = new OrganizationAzureSubscriptionBindStore(_db);

        var rejected = await store.CreatePendingAsync(CreateBind(
            "ffffffff-ffff-ffff-ffff-ffffffffffff",
            OrganizationAzureSubscriptionBindState.PendingConsent));

        Assert.Equal(OrganizationAzureSubscriptionBindFailure.BindInFlight, rejected.Failure);
        Assert.False(_db.ChangeTracker.HasChanges());
        await _db.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectSpecificAzureBind");
        var accepted = await store.CreatePendingAsync(CreateBind(
            "dddddddd-dddd-dddd-dddd-dddddddddddd",
            OrganizationAzureSubscriptionBindState.PendingConsent));
        Assert.True(accepted.Succeeded);
    }

    [Fact]
    public async Task Store_rejects_completion_from_a_superseded_verification_lease()
    {
        var store = new OrganizationAzureSubscriptionBindStore(_db);
        var created = await store.CreatePendingAsync(CreateBind(
            "cccccccc-cccc-cccc-cccc-cccccccccccc",
            OrganizationAzureSubscriptionBindState.PendingConsent));
        var bindId = created.Bind!.Id;
        var firstLease = await store.TransitionAsync(new(
            OrganizationId,
            bindId,
            OrganizationAzureSubscriptionBindState.PendingConsent,
            OrganizationAzureSubscriptionBindState.Verifying,
            Now.AddMinutes(1),
            ExpectedUpdatedAt: Now));
        Assert.True(firstLease.Succeeded);
        var replacementLease = await store.TransitionAsync(new(
            OrganizationId,
            bindId,
            OrganizationAzureSubscriptionBindState.Verifying,
            OrganizationAzureSubscriptionBindState.Verifying,
            Now.AddMinutes(2),
            ExpectedUpdatedAt: Now.AddMinutes(1)));
        Assert.True(replacementLease.Succeeded);

        var staleCompletion = await store.TransitionAsync(new(
            OrganizationId,
            bindId,
            OrganizationAzureSubscriptionBindState.Verifying,
            OrganizationAzureSubscriptionBindState.Active,
            Now.AddMinutes(3),
            ExpectedUpdatedAt: Now.AddMinutes(1),
            VerifiedAt: Now.AddMinutes(3)));

        Assert.Equal(OrganizationAzureSubscriptionBindFailure.InvalidState, staleCompletion.Failure);
        Assert.Equal(OrganizationAzureSubscriptionBindState.Verifying, (await store.GetAsync(OrganizationId, bindId))!.State);
    }

    [Fact]
    public async Task Lost_commit_acknowledgement_after_create_returns_the_committed_bind()
    {
        var bind = CreateBind("cccccccc-cccc-cccc-cccc-cccccccccccc", OrganizationAzureSubscriptionBindState.PendingConsent);
        var acknowledgement = new FollowUpBindTransitionLostCommitInterceptor(
            bind.Id, OrganizationAzureSubscriptionBindState.Verifying, Now.AddMinutes(1));
        await using var db = new CatalogDbContext(LostAckOptions(acknowledgement));

        var result = await new OrganizationAzureSubscriptionBindStore(db).CreatePendingAsync(bind);

        Assert.True(result.Succeeded);
        Assert.Equal(bind.Id, result.Bind!.Id);
        Assert.Equal(1, acknowledgement.Committed);
        _db.ChangeTracker.Clear();
        Assert.Equal(bind.Id, (await _db.OrganizationAzureSubscriptionBinds.SingleAsync()).Id);
        Assert.Equal(OrganizationAzureSubscriptionBindState.Verifying,
            (await _db.OrganizationAzureSubscriptionBinds.SingleAsync()).State);
        Assert.Single(await _db.OrganizationAuditRecords.Where(x =>
            x.Action == OrganizationAuditAction.AzureSubscriptionBindChanged).ToListAsync());
    }

    [Fact]
    public async Task Lost_commit_acknowledgement_after_transition_returns_the_committed_bind()
    {
        var created = await new OrganizationAzureSubscriptionBindStore(_db)
            .CreatePendingAsync(CreateBind("cccccccc-cccc-cccc-cccc-cccccccccccc", OrganizationAzureSubscriptionBindState.PendingConsent));
        Assert.True(created.Succeeded);
        _db.ChangeTracker.Clear();
        var acknowledgement = new FollowUpBindTransitionLostCommitInterceptor(
            created.Bind!.Id, OrganizationAzureSubscriptionBindState.Active, Now.AddMinutes(2));
        await using var db = new CatalogDbContext(LostAckOptions(acknowledgement));

        var result = await new OrganizationAzureSubscriptionBindStore(db).TransitionAsync(new(
            OrganizationId,
            created.Bind.Id,
            OrganizationAzureSubscriptionBindState.PendingConsent,
            OrganizationAzureSubscriptionBindState.Verifying,
            Now.AddMinutes(1)));

        Assert.True(result.Succeeded);
        Assert.Equal(OrganizationAzureSubscriptionBindState.Verifying, result.Bind!.State);
        Assert.Equal(1, acknowledgement.Committed);
        _db.ChangeTracker.Clear();
        Assert.Equal(OrganizationAzureSubscriptionBindState.Active,
            (await _db.OrganizationAzureSubscriptionBinds.SingleAsync()).State);
        Assert.Equal(2, await _db.OrganizationAuditRecords.CountAsync(x =>
            x.Action == OrganizationAuditAction.AzureSubscriptionBindChanged));
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

    private DbContextOptions<CatalogDbContext> LostAckOptions(DbTransactionInterceptor acknowledgement) =>
        new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(
                _connection,
                sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly),
                isTransient: exception => exception is LostCommitAcknowledgementException)
            .AddInterceptors(acknowledgement)
            .Options;

    private sealed class FollowUpBindTransitionLostCommitInterceptor(
        Guid bindId,
        OrganizationAzureSubscriptionBindState nextState,
        DateTimeOffset changedAt) : DbTransactionInterceptor
    {
        private int _remainingFailures = 1;
        private int _committed;

        public int Committed => Volatile.Read(ref _committed);

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _committed);
            if (Interlocked.Decrement(ref _remainingFailures) >= 0)
            {
                await using var command = eventData.Context!.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                    UPDATE OrganizationAzureSubscriptionBinds
                    SET State = $state, UpdatedAt = $updatedAt
                    WHERE Id = $id
                    """;
                command.Parameters.Add(new SqliteParameter("$state", nextState.ToString()));
                command.Parameters.Add(new SqliteParameter("$updatedAt", changedAt.UtcTicks));
                command.Parameters.Add(new SqliteParameter("$id", bindId));
                Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
                throw new LostCommitAcknowledgementException();
            }

            await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
