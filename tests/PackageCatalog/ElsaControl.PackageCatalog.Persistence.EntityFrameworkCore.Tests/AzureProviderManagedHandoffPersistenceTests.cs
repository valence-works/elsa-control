using ElsaControl.Deployment.Azure;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

/// <summary>
/// Whether a deployment configures the runtime's managed handoff is part of the provider projection a worker
/// restores after a restart and of its request hash: a restored plan configures exactly what was admitted, a
/// tampered flag never deploys, and operations retained before the handoff existed restore without it.
/// </summary>
public sealed class AzureProviderManagedHandoffPersistenceTests : IAsyncDisposable
{
    private const string PreviousSqliteMigration = "20260912132420_AddAzureProviderOperationCapacity";
    private const string PreviousSqlServerMigration = "20260912132436_AddAzureProviderOperationCapacity";
    private const string SqlServerMigration = "20260912144320_AddManagedHandoffApplication";
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly CatalogDbContext _db;

    public AzureProviderManagedHandoffPersistenceTests()
    {
        _connection.Open();
        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(_connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restored_plan_configures_exactly_the_admitted_managed_handoff(bool managedHandoff)
    {
        var store = await MigratedStoreAsync();
        var operation = await store.CreateOrGetAsync(Request(managedHandoff), Now);

        var retained = await store.GetAsync(_workspaceId, operation.Id);

        Assert.Equal(managedHandoff, retained!.ManagedHandoff);
        Assert.Equal(managedHandoff, new PersistedAzureProviderPlanSource().Resolve(retained)?.ManagedHandoff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_flipped_managed_handoff_column_leaves_the_operation_unrestorable(bool admitted)
    {
        var store = await MigratedStoreAsync();
        var operation = await store.CreateOrGetAsync(Request(admitted), Now);

        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE AzureProviderOperations SET ManagedHandoff = {!admitted} WHERE Id = {operation.Id}");

        Assert.Null(new PersistedAzureProviderPlanSource().Resolve((await store.GetAsync(_workspaceId, operation.Id))!));
    }

    [Fact]
    public async Task Sqlite_operation_retained_before_the_migration_restores_without_the_handoff()
    {
        await _db.Database.MigrateAsync(PreviousSqliteMigration);
        _db.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "Retained handoff workspace" });
        await _db.SaveChangesAsync();
        var operationId = await AzureProviderOperationCapacityMigrationTests.InsertRetainedOperationAsync(_db, _workspaceId);
        await _db.Database.MigrateAsync();

        var retained = await new AzureProviderOperationStore(_db).GetAsync(_workspaceId, operationId);

        Assert.False(retained!.ManagedHandoff);
        Assert.False(retained.PersistedMetadataInvalid);
        Assert.False(new PersistedAzureProviderPlanSource().Resolve(retained)!.ManagedHandoff);
    }

    [Fact]
    public void SqlServer_adds_false_default_handoff_columns_without_rewriting_retained_rows()
    {
        using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                @"Server=(localdb)\MSSQLLocalDB;Initial Catalog=ElsaControlMigrationScriptTests;Integrated Security=True;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);

        var script = db.GetService<IMigrator>().GenerateScript(fromMigration: PreviousSqlServerMigration, toMigration: SqlServerMigration);

        Assert.Contains("ALTER TABLE [AzureProviderOperations] ADD [ManagedHandoff] bit NOT NULL DEFAULT CAST(0 AS bit);", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE [ElsaInstances] ADD [CurrentDeploymentManagedHandoff] bit NOT NULL DEFAULT CAST(0 AS bit);", script, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE [", script, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<AzureProviderOperationStore> MigratedStoreAsync()
    {
        await _db.Database.MigrateAsync();
        _db.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "Azure handoff workspace" });
        await _db.SaveChangesAsync();
        return new AzureProviderOperationStore(_db);
    }

    private AzureProviderOperationRequest Request(bool managedHandoff) =>
        AzureProviderOperationCapacityPersistenceTests.RestorableRequest(_workspaceId) with
        {
            Capacity = new AzureWorkloadCapacity(1, 1, 500, 1024),
            ManagedHandoff = managedHandoff
        };
}
