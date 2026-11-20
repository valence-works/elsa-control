using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ProviderSubmissionInFlightMigrationTests : IAsyncDisposable
{
    private const string PreviousSqliteMigration = "20261115060000_AddStripeEngineQuantityAndBillingOperatorAlerts";
    private const string SqliteMigration = "20261120120000_AddElsaInstanceOperationProviderSubmissionInFlight";
    private const string PreviousSqlServerMigration = "20261115060001_AddStripeEngineQuantityAndBillingOperatorAlerts";
    private const string SqlServerMigration = "20261120120001_AddElsaInstanceOperationProviderSubmissionInFlight";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CatalogDbContext _sqlite;

    public ProviderSubmissionInFlightMigrationTests()
    {
        _connection.Open();
        _sqlite = new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(_connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);
    }

    [Fact]
    public async Task Sqlite_migration_adds_nullable_submission_in_flight_column()
    {
        await _sqlite.Database.MigrateAsync(PreviousSqliteMigration);
        await _sqlite.Database.MigrateAsync();

        _sqlite.ChangeTracker.Clear();
        var columns = await _sqlite.Database.SqlQueryRaw<string>("""
            SELECT name AS "Value" FROM pragma_table_info('ElsaInstanceOperations')
            """).ToListAsync();
        Assert.Contains("ProviderSubmissionInFlightAt", columns);
        var nullable = await _sqlite.Database.SqlQueryRaw<int>("""
            SELECT "notnull" AS "Value" FROM pragma_table_info('ElsaInstanceOperations')
            WHERE name = 'ProviderSubmissionInFlightAt'
            """).SingleAsync();
        Assert.Equal(0, nullable);
    }

    [Fact]
    public void SqlServer_migration_adds_nullable_submission_in_flight_column()
    {
        using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,1;Database=ElsaControlMigrationScriptTests;User ID=unused;Password=unused;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);

        Assert.Contains(SqliteMigration, _sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqlServerMigration, db.GetService<IMigrationsAssembly>().Migrations.Keys);
        var script = db.GetService<IMigrator>().GenerateScript(fromMigration: PreviousSqlServerMigration, toMigration: SqlServerMigration);

        Assert.Contains("ALTER TABLE [ElsaInstanceOperations] ADD [ProviderSubmissionInFlightAt] bigint NULL;", script, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT NULL", script, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _sqlite.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
