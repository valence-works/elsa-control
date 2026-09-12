using System.Text.RegularExpressions;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class AzureDeleteRecoveryAuthorityMigrationTests
{
    private const string PreviousSqliteMigration = "20260906005158_AddAzureProviderRecoveryObservations";
    private const string PreviousSqlServerMigration = "20260906005212_AddAzureProviderRecoveryObservations";
    private const string MigrationId = "20260906084500_AddAzureDeleteRecoveryAuthority";

    [Fact]
    public void Sqlite_migration_is_discoverable()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = CreateSqliteContext(connection);

        Assert.Contains(MigrationId, db.GetService<IMigrationsAssembly>().Migrations.Keys);
    }

    [Fact]
    public void SqlServer_migration_is_discoverable()
    {
        using var db = CreateSqlServerContext();

        Assert.Contains(MigrationId, db.GetService<IMigrationsAssembly>().Migrations.Keys);
    }

    [Fact]
    public void SqlServer_script_uses_a_valid_nullable_authority_column_type_and_retains_recovery_guard()
    {
        using var db = CreateSqlServerContext();

        var script = db.GetService<IMigrator>().GenerateScript(
            fromMigration: PreviousSqlServerMigration,
            toMigration: MigrationId,
            options: MigrationsSqlGenerationOptions.Idempotent);

        var column = Regex.Match(
            script,
            @"ADD\s+\[AzureDeleteRecoveryAuthority\]\s+(?<type>nvarchar\((?:max|[0-9]+)\))\s+NULL",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.True(column.Success, "The generated SQL did not add a nullable authority column.");

        var sqlType = column.Groups["type"].Value;
        Assert.DoesNotContain("nvarchar(4096)", sqlType, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(sqlType, "nvarchar(max)", StringComparison.OrdinalIgnoreCase))
            Assert.InRange(int.Parse(sqlType[8..^1]), 1, 4000);

        var fullScript = db.GetService<IMigrator>().GenerateScript(
            fromMigration: null,
            toMigration: MigrationId,
            options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("TR_ElsaInstanceRecoveryRequests_AppendOnly", fullScript, StringComparison.Ordinal);
        Assert.Contains("INSTEAD OF UPDATE, DELETE", fullScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sqlite_forward_migration_keeps_legacy_recovery_authority_null_and_readable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateSqliteContext(connection);

        await db.Database.MigrateAsync(PreviousSqliteMigration);
        var rowId = await InsertLegacyRecoveryRequestAsync(db);
        await db.Database.MigrateAsync();

        var authority = await db.Database.SqlQuery<string?>(
                $"SELECT AzureDeleteRecoveryAuthority AS Value FROM ElsaInstanceRecoveryRequests WHERE Id = {rowId}")
            .SingleAsync();

        Assert.Null(authority);
    }

    [Fact]
    public async Task Sqlite_recovery_ledger_remains_append_only_for_the_new_authority_column()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateSqliteContext(connection);

        await db.Database.MigrateAsync();
        var rowId = await InsertLegacyRecoveryRequestAsync(db);

        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ElsaInstanceRecoveryRequests
            SET AzureDeleteRecoveryAuthority = {"tampered"}
            WHERE Id = {rowId}
            """));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM ElsaInstanceRecoveryRequests
            WHERE Id = {rowId}
            """));

        var count = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM ElsaInstanceRecoveryRequests").SingleAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public void SqlServer_down_operation_removes_only_the_new_authority_column()
    {
        using var db = CreateSqlServerContext();
        var migrationType = db.GetService<IMigrationsAssembly>().Migrations[MigrationId];
        var migration = (Migration)Activator.CreateInstance(migrationType)!;

        var drops = migration.DownOperations.OfType<DropColumnOperation>().ToArray();
        var drop = Assert.Single(drops);
        Assert.Equal("ElsaInstanceRecoveryRequests", drop.Table);
        Assert.Equal("AzureDeleteRecoveryAuthority", drop.Name);
        Assert.DoesNotContain(migration.DownOperations, operation => operation is DropTableOperation);
    }

    [Fact]
    public async Task Sqlite_down_migration_preserves_the_ledger_and_append_only_guards()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateSqliteContext(connection);
        await db.Database.MigrateAsync();
        var rowId = await InsertLegacyRecoveryRequestAsync(db);

        await db.Database.MigrateAsync(PreviousSqliteMigration);

        Assert.Equal(1, await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM ElsaInstanceRecoveryRequests").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM pragma_table_info('ElsaInstanceRecoveryRequests') WHERE name = 'AzureDeleteRecoveryAuthority'").SingleAsync());
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM ElsaInstanceRecoveryRequests WHERE Id = {rowId}
            """));
    }

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

    private static async Task<Guid> InsertLegacyRecoveryRequestAsync(CatalogDbContext db)
    {
        var rowId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.UtcTicks;
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ElsaInstanceRecoveryRequests
                    (Id, OrganizationId, WorkspaceId, InstanceId, OperationId, AttemptNumber,
                     IdempotencyScope, IdempotencyKey, RequestHash,
                     RecoveryObservationReference, RecoveryObservationDigest,
                     ObservedLifecycleAttemptNumber, ObservedInstanceVersion, AcceptedAt, CreatedAt)
                VALUES
                    ({rowId}, {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, 2,
                     {"legacy-scope"}, {"legacy-key"}, {new string('a', 64)},
                     NULL, NULL, NULL, NULL, {now}, {now})
                """);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        }

        return rowId;
    }
}
