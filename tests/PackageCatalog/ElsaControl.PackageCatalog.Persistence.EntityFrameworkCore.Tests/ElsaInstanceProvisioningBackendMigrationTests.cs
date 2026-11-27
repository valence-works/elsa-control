using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ElsaInstanceProvisioningBackendMigrationTests : IAsyncLifetime
{
    private const string PreviousSqliteMigration = "20261126180000_AddElsaInstanceOperationFailureClass";
    private const string PreviousSqlServerMigration = "20261126180001_AddElsaInstanceOperationFailureClass";
    private const string SqliteMigration = "20261126223000_AddElsaInstanceProvisioningBackend";
    private const string SqlServerMigration = "20261126223001_AddElsaInstanceProvisioningBackend";

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
    public async Task Sqlite_existing_rows_default_to_bicep()
    {
        await _sqlite.Database.MigrateAsync(PreviousSqliteMigration);
        var workspace = new Workspace { Name = "Legacy backend workspace" };
        _sqlite.Workspaces.Add(workspace);
        await _sqlite.SaveChangesAsync();
        var instance = ElsaInstancePersistenceTests.NewInstance(workspace.OrganizationId, workspace.Id);
        await _sqlite.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ElsaInstances
                (Id, OrganizationId, WorkspaceId, Name, Slug, DistributionId,
                 ReleaseLine, Channel, PatchUpdates, MinorUpdates, MajorMigrations,
                 TopologyId, FeatureOverridesJson, TargetMode, RequiresProvisioningContext,
                 RegionCode, IsolationProfile, CapacityProfile, NetworkOutcome, DomainOutcome,
                 DesiredLifecycle, ObservedLifecycle, Health, Version, CreatedAt, UpdatedAt,
                 CurrentDeploymentManagedHandoff, CurrentDeploymentStudioGrants)
            VALUES
                ({instance.Id}, {instance.OrganizationId}, {instance.WorkspaceId}, {instance.Name}, {instance.Slug}, {instance.DistributionId},
                 {instance.ReleaseLine}, {instance.Channel}, {instance.PatchUpdates}, {instance.MinorUpdates}, {instance.MajorMigrations},
                 {instance.TopologyId}, {instance.FeatureOverridesJson}, {instance.TargetMode}, {false},
                 {instance.RegionCode}, {instance.IsolationProfile}, {instance.CapacityProfile}, {instance.NetworkOutcome}, {instance.DomainOutcome},
                 {instance.DesiredLifecycle.ToString()}, {instance.ObservedLifecycle.ToString()}, {instance.Health.ToString()},
                 {instance.Version}, {instance.CreatedAt.UtcTicks}, {instance.UpdatedAt.UtcTicks},
                 {false}, {false})
            """);

        await _sqlite.Database.MigrateAsync(SqliteMigration);
        var backend = await _sqlite.Database
            .SqlQueryRaw<string>("SELECT ProvisioningBackend AS Value FROM ElsaInstances")
            .SingleAsync();
        Assert.Equal(ElsaControl.Deployment.Azure.ElsaInstanceProvisioningBackendText.Bicep, backend);

        var defaultValue = await _sqlite.Database
            .SqlQueryRaw<string>("SELECT dflt_value AS Value FROM pragma_table_info('ElsaInstances') WHERE name = 'ProvisioningBackend'")
            .SingleAsync();
        Assert.Contains("bicep", defaultValue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlServer_migration_adds_the_owner_column_defaulting_to_bicep()
    {
        using var db = CreateSqlServerContext();
        var script = db.GetService<IMigrator>().GenerateScript(
            fromMigration: PreviousSqlServerMigration,
            toMigration: SqlServerMigration,
            options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("[ProvisioningBackend]", script, StringComparison.Ordinal);
        Assert.Contains("bicep", script, StringComparison.Ordinal);
        Assert.Contains("ElsaInstances", script, StringComparison.Ordinal);
    }

    private static CatalogDbContext CreateSqlServerContext() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,1;Database=ElsaControlMigrationScriptTests;User ID=unused;Password=unused;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);
}
