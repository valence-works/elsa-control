using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

/// <summary>
/// The tenant binding is additive: existing organizations stay unbound and untouched.
/// </summary>
public sealed class OrganizationIdentityBindingMigrationTests : IAsyncDisposable
{
    private const string PreviousSqliteMigration = "20260913021623_AddElsaInstanceProvisioningContext";
    private const string PreviousSqlServerMigration = "20260913021756_AddElsaInstanceProvisioningContext";
    private const string SqlServerMigration = "20260913224131_AddOrganizationIdentityBindings";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CatalogDbContext _sqlite;

    public OrganizationIdentityBindingMigrationTests()
    {
        _connection.Open();
        _sqlite = new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(_connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);
    }

    [Fact]
    public async Task Sqlite_migration_leaves_existing_organizations_unbound()
    {
        await _sqlite.Database.MigrateAsync(PreviousSqliteMigration);
        var existing = new Organization { Name = "Existing Stripe organization" };
        _sqlite.Organizations.Add(existing);
        await _sqlite.SaveChangesAsync();

        await _sqlite.Database.MigrateAsync();

        _sqlite.ChangeTracker.Clear();
        Assert.Equal("Existing Stripe organization", (await _sqlite.Organizations.SingleAsync(x => x.Id == existing.Id)).Name);
        Assert.Equal(0, await _sqlite.OrganizationIdentityBindings.CountAsync());
    }

    [Fact]
    public void SqlServer_migration_only_adds_the_binding_table_and_its_unique_tenant_index()
    {
        using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,1;Database=ElsaControlMigrationScriptTests;User ID=unused;Password=unused;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);

        var script = db.GetService<IMigrator>().GenerateScript(fromMigration: PreviousSqlServerMigration, toMigration: SqlServerMigration);

        Assert.Contains("CREATE TABLE [OrganizationIdentityBindings]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX [IX_OrganizationIdentityBindings_EntraTenantId] ON [OrganizationIdentityBindings] ([EntraTenantId]);", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE [Organizations]", script, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE [Organizations]", script, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _sqlite.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
