using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ElsaInstanceOperationReasonClockBackfillMigrationTests
{
    private const string PreviousSqliteMigration = "20261002120000_AddElsaInstanceOperationReasonClock";
    private const string SqliteMigration = "20261003120000_BackfillElsaInstanceOperationReasonEnteredAt";
    private const string PreviousSqlServerMigration = "20261002120001_AddElsaInstanceOperationReasonClock";
    private const string SqlServerMigration = "20261003120001_BackfillElsaInstanceOperationReasonEnteredAt";

    [Fact]
    public void Sqlite_and_sql_server_backfill_migrations_are_discoverable()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var sqlite = CreateSqliteContext(connection);
        using var sqlServer = CreateSqlServerContext();

        Assert.Contains(SqliteMigration, sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqlServerMigration, sqlServer.GetService<IMigrationsAssembly>().Migrations.Keys);
    }

    [Fact]
    public void SqlServer_script_backfills_null_reason_entered_at_from_updated_or_start()
    {
        using var db = CreateSqlServerContext();
        var script = db.GetService<IMigrator>().GenerateScript(
            fromMigration: PreviousSqlServerMigration,
            toMigration: SqlServerMigration,
            options: MigrationsSqlGenerationOptions.Idempotent);

        Assert.Contains("ReasonEnteredAt = COALESCE(UpdatedAt, StartedAt, AcceptedAt)", script, StringComparison.Ordinal);
        Assert.Contains("State = 'RecoveryRequired'", script, StringComparison.Ordinal);
        Assert.Contains("ReasonEnteredAt IS NULL", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sqlite_backfill_stamps_existing_parks_from_updated_at_and_is_idempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateSqliteContext(connection);
        await db.Database.MigrateAsync(PreviousSqliteMigration);

        var parkedId = Guid.NewGuid();
        var stampedId = Guid.NewGuid();
        var acceptedId = Guid.NewGuid();
        var updatedAt = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero).UtcTicks;
        var startedAt = new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.Zero).UtcTicks;
        var acceptedAt = new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero).UtcTicks;
        var alreadyEntered = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero).UtcTicks;
        await InsertOperationAsync(db, parkedId, "RecoveryRequired", updatedAt, startedAt, acceptedAt, reasonEnteredAt: null);
        await InsertOperationAsync(db, stampedId, "RecoveryRequired", updatedAt, startedAt, acceptedAt, reasonEnteredAt: alreadyEntered);
        await InsertOperationAsync(db, acceptedId, "Accepted", updatedAt, startedAt, acceptedAt, reasonEnteredAt: null);

        await db.Database.MigrateAsync(SqliteMigration);

        Assert.Equal(updatedAt, await ReadReasonEnteredAtAsync(db, parkedId));
        Assert.Equal(alreadyEntered, await ReadReasonEnteredAtAsync(db, stampedId));
        Assert.Null(await ReadReasonEnteredAtAsync(db, acceptedId));
    }

    private static async Task InsertOperationAsync(
        CatalogDbContext db,
        Guid id,
        string state,
        long updatedAt,
        long startedAt,
        long acceptedAt,
        long? reasonEnteredAt)
    {
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ElsaInstanceOperations"
                    ("Id", "OrganizationId", "WorkspaceId", "Action", "IdempotencyScope", "IdempotencyKey",
                     "RequestHash", "ExpectedVersion", "State", "AttemptNumber", "AcceptedAt", "StartedAt",
                     "CreatedAt", "UpdatedAt", "LeaseVersion", "ReconciliationVersion", "ReasonEnteredAt")
                VALUES
                    ({id}, {Guid.NewGuid()}, {Guid.NewGuid()}, {"Create"}, {"backfill-scope"}, {id.ToString("N")},
                     {new string('a', 64)}, 1, {state}, 1, {acceptedAt}, {startedAt},
                     {acceptedAt}, {updatedAt}, 0, 0, {reasonEnteredAt});
                """);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        }
    }

    private static Task<long?> ReadReasonEnteredAtAsync(CatalogDbContext db, Guid id) =>
        db.Database.SqlQuery<long?>(
                $"SELECT ReasonEnteredAt AS Value FROM ElsaInstanceOperations WHERE Id = {id}")
            .SingleAsync();

    private static CatalogDbContext CreateSqliteContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(
                CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);

    private static CatalogDbContext CreateSqlServerContext() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                @"Server=(localdb)\MSSQLLocalDB;Initial Catalog=ElsaControlMigrationScriptTests;Integrated Security=True;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(
                    CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);
}
