using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class WorkspacePermissionMigrationTests
{
    private const string PreviousMigration = "20260607190000_AddDeploymentSecretStores";

    [Fact]
    public async Task Latest_migration_supports_audited_permission_grant_and_revoke()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        await using var db = new CatalogDbContext(options);
        await db.Database.MigrateAsync();

        var account = new Account { DisplayName = "Owner", Email = "owner@example.test" };
        var workspace = new Workspace { Name = "Permission Migration" };
        workspace.Memberships.Add(new WorkspaceMembership { Workspace = workspace, Account = account, Role = WorkspaceRole.Owner });
        db.Accounts.Add(account);
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var store = new DeploymentWorkspaceStore(db);

        var grant = await store.GrantPermissionAsync(
            workspace.Id,
            new GrantWorkspacePermissionRequest(account.Id, WorkspaceDeploymentPermissions.Read, account.Id));
        await store.RevokePermissionAsync(
            workspace.Id,
            new RevokeWorkspacePermissionRequest(account.Id, WorkspaceDeploymentPermissions.Read, account.Id));

        Assert.Single(await store.GetPermissionGrantsAsync(workspace.Id, account.Id),
            x => x.Id == grant.Id && x.RevokedByAccountId == account.Id);
        Assert.Equal(2, (await store.ListPermissionAuditRecordsAsync(workspace.Id, account.Id)).Count());
        Assert.Empty((await db.Database.GetPendingMigrationsAsync()));
    }

    [Fact]
    public async Task Migration_backfills_legacy_owners_without_restoring_revoked_permissions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        await using var db = new CatalogDbContext(options);
        await db.Database.MigrateAsync(PreviousMigration);

        var account = new Account { DisplayName = "Legacy Owner", Email = "legacy-owner@example.test" };
        var workspace = new Workspace { Name = "Legacy Permissions" };
        workspace.Memberships.Add(new WorkspaceMembership { Workspace = workspace, Account = account, Role = WorkspaceRole.Owner });
        db.Accounts.Add(account);
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var revokedGrantId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO WorkspacePermissionGrants
                (Id, WorkspaceId, AccountId, Permission, GrantedByAccountId, CreatedAt, UpdatedAt, RevokedAt)
            VALUES
                ({revokedGrantId}, {workspace.Id}, {account.Id}, {WorkspaceDeploymentPermissions.Read}, {account.Id}, {now}, {now}, {now})
            """);

        await db.Database.MigrateAsync();
        var store = new DeploymentWorkspaceStore(db);
        var grants = await store.GetPermissionGrantsAsync(workspace.Id, account.Id);
        var audit = await store.ListPermissionAuditRecordsAsync(workspace.Id, account.Id);

        Assert.Equal(15, grants.Count());
        Assert.Single(grants, x => x.Id == revokedGrantId && x.RevokedAt.HasValue);
        Assert.DoesNotContain(grants, x => x.Permission == WorkspaceDeploymentPermissions.Read && !x.RevokedAt.HasValue);
        Assert.Equal(16, audit.Count());
        Assert.Equal(2, audit.Count(x => x.GrantId == revokedGrantId));
    }
}
