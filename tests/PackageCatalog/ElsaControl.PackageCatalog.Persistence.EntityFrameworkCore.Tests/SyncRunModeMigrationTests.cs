using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class SyncRunModeMigrationTests
{
    private const string PreviousMigration = "20260906084500_AddAzureDeleteRecoveryAuthority";
    private const string SqlServerMigration = "20260912020301_AddSyncRunMode";

    [Fact]
    public async Task Sqlite_classifies_runs_written_without_a_mode_as_verification_runs()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);

        await db.Database.MigrateAsync(PreviousMigration);
        var recordedBeforeModes = await InsertRunWithoutModeAsync(db);
        await db.Database.MigrateAsync();
        var writtenByOlderBinary = await InsertRunWithoutModeAsync(db);

        var modes = await db.SyncRuns.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Mode);
        Assert.Equal(SyncRunMode.Verification, modes[recordedBeforeModes]);
        Assert.Equal(SyncRunMode.Verification, modes[writtenByOlderBinary]);
    }

    [Fact]
    public void SqlServer_defaults_runs_written_without_a_mode_to_verification_runs()
    {
        using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                @"Server=(localdb)\MSSQLLocalDB;Initial Catalog=ElsaControlMigrationScriptTests;Integrated Security=True;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);

        var script = db.GetService<IMigrator>().GenerateScript(fromMigration: PreviousMigration, toMigration: SqlServerMigration);

        Assert.Contains($"ALTER TABLE [SyncRuns] ADD [Mode] int NOT NULL DEFAULT {(int)SyncRunMode.Verification};", script, StringComparison.Ordinal);
    }

    private static async Task<Guid> InsertRunWithoutModeAsync(CatalogDbContext db)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.UtcTicks;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "SyncRuns" ("Id", "Trigger", "Status", "StartedAt", "CompletedAt", "SummaryCountersJson")
            VALUES ({id}, {(int)SyncRunTrigger.ManualAll}, {(int)SyncRunStatus.Completed}, {now}, {now}, {"{}"})
            """);
        return id;
    }
}
