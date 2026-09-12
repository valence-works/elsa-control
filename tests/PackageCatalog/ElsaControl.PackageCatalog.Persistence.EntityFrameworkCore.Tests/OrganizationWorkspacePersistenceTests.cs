using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class OrganizationWorkspacePersistenceTests
{
    private const string PreviousMigration = "20260529203445_AddArtifactDesiredStateReferences";

    [Fact]
    public async Task Migration_backfills_organization_for_existing_workspace()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var workspaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        await using (var db = CreateDbContext(connection))
        {
            await db.Database.MigrateAsync(PreviousMigration);
            await SeedLegacyWorkspaceAsync(db, workspaceId, accountId);
            await db.Database.MigrateAsync();
        }

        await using (var db = CreateDbContext(connection))
        {
            var workspace = await db.Workspaces.SingleAsync(x => x.Id == workspaceId);
            Assert.Equal(workspaceId, workspace.OrganizationId);
            Assert.Equal(1, (await db.Organizations.CountAsync(x => x.Id == workspaceId)));
            Assert.Equal(1, await db.OrganizationMemberships.CountAsync(x =>
                x.OrganizationId == workspaceId &&
                x.AccountId == accountId &&
                x.Role == OrganizationRole.Owner));
        }
    }

    private static CatalogDbContext CreateDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        return new CatalogDbContext(options);
    }

    private static async Task SeedLegacyWorkspaceAsync(CatalogDbContext db, Guid workspaceId, Guid accountId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Accounts (Id, DisplayName, Email, CreatedAt, UpdatedAt)
            VALUES ({accountId}, 'Legacy Owner', 'legacy@example.test', {now}, {now});
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Workspaces (Id, Name, Kind, CreatedAt, UpdatedAt, SoftDeletedAt)
            VALUES ({workspaceId}, 'Legacy Workspace', {2}, {now}, {now}, NULL);
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO WorkspaceMemberships (Id, WorkspaceId, AccountId, Role, CreatedAt, UpdatedAt)
            VALUES ({Guid.NewGuid()}, {workspaceId}, {accountId}, {2}, {now}, {now});
            """);
    }
}
