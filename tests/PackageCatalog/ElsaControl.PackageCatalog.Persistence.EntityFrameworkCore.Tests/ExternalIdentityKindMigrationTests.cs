using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ExternalIdentityKindMigrationTests : IAsyncDisposable
{
    private const string PreviousSqliteMigration = "20261004230000_AddBillingProviderEventReplayFacts";
    private const string SqliteMigration = "20261015180000_AddExternalIdentityKind";
    private const string PreviousSqlServerMigration = "20261004230001_AddBillingProviderEventReplayFacts";
    private const string SqlServerMigration = "20261015180001_AddExternalIdentityKind";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CatalogDbContext _sqlite;

    public ExternalIdentityKindMigrationTests()
    {
        _connection.Open();
        _sqlite = new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(_connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);
    }

    [Fact]
    public async Task Sqlite_migration_leaves_existing_identities_without_a_kind()
    {
        await _sqlite.Database.MigrateAsync(PreviousSqliteMigration);
        var accountId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await _sqlite.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Accounts" ("Id", "DisplayName", "Email", "CreatedAt", "UpdatedAt")
            VALUES ({accountId}, {"Existing"}, {"existing@example.test"}, {now}, {now});
            """);
        await _sqlite.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ExternalIdentities" ("Id", "AccountId", "Issuer", "Subject", "LastSeenAt", "CreatedAt", "UpdatedAt")
            VALUES ({identityId}, {accountId}, {"https://cloud-account.test/auth/v1"}, {"b6f73e84-5b47-4f4d-ad52-e3c4c9a815c6"}, {now}, {now}, {now});
            """);

        await _sqlite.Database.MigrateAsync();

        _sqlite.ChangeTracker.Clear();
        var identity = await _sqlite.ExternalIdentities.SingleAsync();
        Assert.Null(identity.Kind);
        Assert.Equal("https://cloud-account.test/auth/v1", identity.Issuer);
    }

    [Fact]
    public void SqlServer_migration_only_adds_the_kind_column()
    {
        using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,1;Database=ElsaControlMigrationScriptTests;User ID=unused;Password=unused;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);

        Assert.Contains(SqliteMigration, _sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqlServerMigration, db.GetService<IMigrationsAssembly>().Migrations.Keys);
        var script = db.GetService<IMigrator>().GenerateScript(fromMigration: PreviousSqlServerMigration, toMigration: SqlServerMigration);

        Assert.Contains("ALTER TABLE [ExternalIdentities] ADD [Kind] nvarchar(32) NULL;", script, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE [ExternalIdentities]", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE [ExternalIdentities]", script, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _sqlite.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
