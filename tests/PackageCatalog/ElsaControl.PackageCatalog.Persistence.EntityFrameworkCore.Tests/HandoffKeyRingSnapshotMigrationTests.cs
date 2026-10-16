using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class HandoffKeyRingSnapshotMigrationTests : IAsyncDisposable
{
    private const string PreviousSqliteMigration = "20261015220000_AddAccountIdentityLinkAudit";
    private const string SqliteMigration = "20261016040000_AddHandoffKeyRingSnapshotIds";
    private const string PreviousSqlServerMigration = "20261015220001_AddAccountIdentityLinkAudit";
    private const string SqlServerMigration = "20261016040001_AddHandoffKeyRingSnapshotIds";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CatalogDbContext _sqlite;

    public HandoffKeyRingSnapshotMigrationTests()
    {
        _connection.Open();
        _sqlite = new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(_connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);
    }

    [Fact]
    public async Task Sqlite_migration_adds_key_ring_snapshot_columns_and_indexes()
    {
        await _sqlite.Database.MigrateAsync(PreviousSqliteMigration);

        await _sqlite.Database.MigrateAsync();

        Assert.True(await ColumnExistsAsync("KeyRingSnapshotId"));
        Assert.True(await ColumnExistsAsync("KeyRingPreviousSnapshotId"));
        Assert.True(await IndexExistsAsync("IX_ManagedElsaHandoffAuditEvents_KeyRingSnapshotId"));
        Assert.True(await IndexExistsAsync("IX_ManagedElsaHandoffAuditEvents_KeyRingPreviousSnapshot"));
    }

    [Fact]
    public void SqlServer_migration_only_adds_key_ring_snapshot_columns_and_indexes()
    {
        using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,1;Database=ElsaControlMigrationScriptTests;User ID=unused;Password=unused;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);

        Assert.Contains(SqliteMigration, _sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqlServerMigration, db.GetService<IMigrationsAssembly>().Migrations.Keys);
        var script = db.GetService<IMigrator>().GenerateScript(fromMigration: PreviousSqlServerMigration, toMigration: SqlServerMigration);

        Assert.Contains("KeyRingSnapshotId", script, StringComparison.Ordinal);
        Assert.Contains("KeyRingPreviousSnapshotId", script, StringComparison.Ordinal);
        Assert.Contains("IX_ManagedElsaHandoffAuditEvents_KeyRingSnapshotId", script, StringComparison.Ordinal);
        Assert.Contains("IX_ManagedElsaHandoffAuditEvents_KeyRingPreviousSnapshot", script, StringComparison.Ordinal);
        Assert.Contains("IS NOT NULL", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\bLIKE\b", script);
        Assert.DoesNotContain("DROP TABLE [ManagedElsaHandoffAuditEvents]", script, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN PUBLIC", script, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _sqlite.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<bool> ColumnExistsAsync(string column)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """SELECT COUNT(*) FROM pragma_table_info('ManagedElsaHandoffAuditEvents') WHERE name = $name;""";
        command.Parameters.AddWithValue("$name", column);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private async Task<bool> IndexExistsAsync(string index)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;""";
        command.Parameters.AddWithValue("$name", index);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }
}
