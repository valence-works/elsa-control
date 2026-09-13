using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ElsaInstanceProvisioningContextMigrationTests : IAsyncLifetime
{
    private const string PreviousSqliteMigration = "20260913021000_AddAzureProviderAssignmentRebinds";
    private const string PreviousSqlServerMigration = "20260913021010_AddAzureProviderAssignmentRebinds";
    private const string SqliteMigration = "20260913021623_AddElsaInstanceProvisioningContext";
    private const string SqlServerMigration = "20260913021756_AddElsaInstanceProvisioningContext";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private CatalogDbContext _sqlite = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _sqlite = new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(_connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
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
    public async Task Sqlite_migration_adds_a_false_legacy_marker_and_immutable_snapshot_triggers()
    {
        await _sqlite.Database.MigrateAsync();

        var markerDefault = await _sqlite.Database
            .SqlQueryRaw<string>("SELECT dflt_value AS Value FROM pragma_table_info('ElsaInstances') WHERE name = 'RequiresProvisioningContext'")
            .SingleAsync();
        Assert.Equal("0", markerDefault);
        Assert.Equal(0, await _sqlite.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM ElsaInstanceProvisioningContexts").SingleAsync());

        var triggers = await _sqlite.Database
            .SqlQueryRaw<SqliteTrigger>("SELECT tbl_name AS \"Table\", name AS \"Name\" FROM sqlite_master WHERE type = 'trigger'")
            .ToListAsync();
        Assert.Contains(triggers, x => x.Table == "ElsaInstanceProvisioningContexts" &&
            x.Name == "TR_ElsaInstanceProvisioningContexts_AppendOnly_Update");
        Assert.Contains(triggers, x => x.Table == "ElsaInstanceProvisioningContexts" &&
            x.Name == "TR_ElsaInstanceProvisioningContexts_AppendOnly_Delete");
    }

    [Fact]
    public async Task Sqlite_migration_can_roll_back_and_reapply_without_losing_managed_binding_guards()
    {
        var migrator = _sqlite.GetService<IMigrator>();
        await migrator.MigrateAsync(SqliteMigration);
        await migrator.MigrateAsync(PreviousSqliteMigration);
        Assert.DoesNotContain(SqliteMigration, await _sqlite.Database.GetAppliedMigrationsAsync());

        await migrator.MigrateAsync(SqliteMigration);
        Assert.Contains(SqliteMigration, await _sqlite.Database.GetAppliedMigrationsAsync());
        var guards = await _sqlite.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type = 'trigger'").ToListAsync();
        Assert.Contains("TR_DeploymentEnvironments_ManagedInstanceBinding_Update", guards);
        Assert.Contains("TR_ElsaInstanceProvisioningContexts_AppendOnly_Update", guards);
    }

    [Fact]
    public void SqlServer_migration_adds_the_marker_table_and_append_only_guard()
    {
        using var db = CreateSqlServerContext();
        var script = db.GetService<IMigrator>().GenerateScript(
            fromMigration: PreviousSqlServerMigration,
            toMigration: SqlServerMigration,
            options: MigrationsSqlGenerationOptions.Idempotent);

        Assert.Contains("ALTER TABLE [ElsaInstances] ADD [RequiresProvisioningContext] bit NOT NULL DEFAULT CAST(0 AS bit);", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [ElsaInstanceProvisioningContexts]", script, StringComparison.Ordinal);
        Assert.Contains("EXEC(N'CREATE TRIGGER dbo.TR_ElsaInstanceProvisioningContexts_AppendOnly", script, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE [ElsaInstances]", script, StringComparison.Ordinal);
    }

    private static CatalogDbContext CreateSqlServerContext() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,1;Database=ElsaControlMigrationScriptTests;User ID=unused;Password=unused;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);

    private sealed record SqliteTrigger(string Table, string Name);
}
