using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class AccountIdentityLinkMigrationTests : IAsyncDisposable
{
    private const string PreviousSqliteMigration = "20261015190000_AddBillingWebhookSource";
    private const string SqliteMigration = "20261015220000_AddAccountIdentityLinkAudit";
    private const string PreviousSqlServerMigration = "20261015190001_AddBillingWebhookSource";
    private const string SqlServerMigration = "20261015220001_AddAccountIdentityLinkAudit";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CatalogDbContext _sqlite;

    public AccountIdentityLinkMigrationTests()
    {
        _connection.Open();
        _sqlite = new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(_connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);
    }

    [Fact]
    public async Task Sqlite_migration_adds_the_audit_table_and_hub_account_index()
    {
        await _sqlite.Database.MigrateAsync(PreviousSqliteMigration);

        await _sqlite.Database.MigrateAsync();

        Assert.True(await TableExistsAsync("AccountIdentityLinkAuditRecords"));
        Assert.True(await IndexExistsAsync("IX_ExternalIdentities_AccountId_Hub"));
    }

    [Fact]
    public void SqlServer_migration_only_adds_the_audit_table_and_hub_account_index()
    {
        using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,1;Database=ElsaControlMigrationScriptTests;User ID=unused;Password=unused;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);

        Assert.Contains(SqliteMigration, _sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqlServerMigration, db.GetService<IMigrationsAssembly>().Migrations.Keys);
        var script = db.GetService<IMigrator>().GenerateScript(fromMigration: PreviousSqlServerMigration, toMigration: SqlServerMigration);

        Assert.Contains("CREATE TABLE [AccountIdentityLinkAuditRecords]", script, StringComparison.Ordinal);
        Assert.Contains("IX_ExternalIdentities_AccountId_Hub", script, StringComparison.Ordinal);
        Assert.Contains("Kind = 'hub'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE [ExternalIdentities]", script, StringComparison.Ordinal);
        Assert.DoesNotContain("buyer@", script, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _sqlite.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;""";
        command.Parameters.AddWithValue("$name", table);
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
