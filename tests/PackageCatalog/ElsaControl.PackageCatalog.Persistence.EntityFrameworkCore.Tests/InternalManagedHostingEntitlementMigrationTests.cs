using System.Text.RegularExpressions;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class InternalManagedHostingEntitlementMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260906084500_AddAzureDeleteRecoveryAuthority";
    private const string SqliteMigration = "20260911233247_AddInternalManagedHostingEntitlement";
    private const string SqlServerMigration = "20260911233658_AddInternalManagedHostingEntitlement";
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _organizationId = Guid.NewGuid();
    private readonly Guid _snapshotId = Guid.NewGuid();
    private CatalogDbContext _sqlite = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _sqlite = new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(_connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);
    }

    public async Task DisposeAsync()
    {
        await _sqlite.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public void Migrations_are_discoverable_for_both_providers()
    {
        using var sqlServer = CreateSqlServerContext();

        Assert.Contains(SqliteMigration, _sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqlServerMigration, sqlServer.GetService<IMigrationsAssembly>().Migrations.Keys);
    }

    [Fact]
    public async Task Existing_entitlements_stay_non_expiring_and_keep_their_gate_decision()
    {
        await _sqlite.Database.MigrateAsync(PreviousMigration);
        await InsertLegacyEntitlementAsync();

        await _sqlite.Database.MigrateAsync();

        var snapshot = await _sqlite.OrganizationEntitlementSnapshots.AsNoTracking().SingleAsync(x => x.Id == _snapshotId);
        Assert.Null(snapshot.ManagedHostingExpiresAt);
        Assert.True(snapshot.ManagedHostingEnabled);
        Assert.Equal(2, snapshot.MaxInstances);
        var decision = await new EfCoreElsaInstanceCommercialGate(_sqlite)
            .EvaluateAsync(_organizationId, ElsaInstanceOperationAction.Create, activeInstanceCount: 0);
        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Empty_database_migrates_and_round_trips_an_internal_grant()
    {
        await _sqlite.Database.MigrateAsync();
        _sqlite.Organizations.Add(new Organization { Id = _organizationId, Name = "Dogfood" });
        await _sqlite.SaveChangesAsync();
        var expiresAt = Now.AddDays(30).AddTicks(7);

        var result = await new OrganizationBillingStore(_sqlite).GrantInternalEntitlementAsync(
            new(_organizationId, new("Internal dogfood of managed hosting", 1, expiresAt), "operator"), Now);

        Assert.Equal(OrganizationInternalEntitlementOutcome.Granted, result.Outcome);
        _sqlite.ChangeTracker.Clear();
        Assert.Equal(expiresAt, (await _sqlite.OrganizationEntitlementSnapshots.SingleAsync()).ManagedHostingExpiresAt);
    }

    [Fact]
    public async Task Sqlite_down_migration_drops_only_the_expiry_column_and_keeps_rows()
    {
        await _sqlite.Database.MigrateAsync();
        await InsertLegacyEntitlementAsync();

        await _sqlite.Database.MigrateAsync(PreviousMigration);

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) AS Value FROM OrganizationEntitlementSnapshots"));
        Assert.Equal(0, await CountAsync(
            "SELECT COUNT(*) AS Value FROM pragma_table_info('OrganizationEntitlementSnapshots') WHERE name = 'ManagedHostingExpiresAt'"));
    }

    [Fact]
    public void SqlServer_script_adds_a_nullable_tick_column()
    {
        using var db = CreateSqlServerContext();

        var script = db.GetService<IMigrator>().GenerateScript(
            fromMigration: PreviousMigration,
            toMigration: SqlServerMigration,
            options: MigrationsSqlGenerationOptions.Idempotent);

        Assert.Matches(new Regex(@"ALTER TABLE \[OrganizationEntitlementSnapshots\] ADD \[ManagedHostingExpiresAt\] bigint NULL"), script);
        Assert.DoesNotContain("UPDATE", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Down_operations_remove_only_the_expiry_column_for_both_providers()
    {
        using var sqlServer = CreateSqlServerContext();

        foreach (var (db, migrationId) in new[] { (_sqlite, SqliteMigration), (sqlServer, SqlServerMigration) })
        {
            var migration = (Migration)Activator.CreateInstance(db.GetService<IMigrationsAssembly>().Migrations[migrationId])!;
            var drop = Assert.IsType<DropColumnOperation>(Assert.Single(migration.DownOperations));
            Assert.Equal(("OrganizationEntitlementSnapshots", "ManagedHostingExpiresAt"), (drop.Table, drop.Name));
        }
    }

    private async Task InsertLegacyEntitlementAsync()
    {
        await _sqlite.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Organizations (Id, Name, Status, CreatedAt, UpdatedAt)
            VALUES ({_organizationId}, {"Legacy organization"}, {"Active"}, {Now.UtcTicks}, {Now.UtcTicks});
            """);
        await _sqlite.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO OrganizationEntitlementSnapshots
                (Id, OrganizationId, CanCreateCustomSources, MaxSources, MaxWorkspaces, MaxInstances,
                 PrivateFeedsEnabled, ManagedHostingEnabled, DeploymentTargetsEnabled, SubscriptionState,
                 SyncedAt, CreatedAt, UpdatedAt)
            VALUES
                ({_snapshotId}, {_organizationId}, 0, 1, 1, 2, 0, 1, 0, {"Active"},
                 {Now.UtcTicks}, {Now.UtcTicks}, {Now.UtcTicks});
            """);
    }

    private Task<long> CountAsync(string sql) => _sqlite.Database.SqlQueryRaw<long>(sql).SingleAsync();

    private static CatalogDbContext CreateSqlServerContext() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                @"Server=(localdb)\MSSQLLocalDB;Initial Catalog=ElsaControlMigrationScriptTests;Integrated Security=True;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);
}
