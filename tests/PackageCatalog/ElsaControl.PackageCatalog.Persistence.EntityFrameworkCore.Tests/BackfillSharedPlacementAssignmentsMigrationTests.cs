using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class BackfillSharedPlacementAssignmentsMigrationTests
{
    private const string PreviousSqliteMigration = "20261015191000_AddSharedAllowance";
    private const string SqliteMigration = "20261015200000_BackfillSharedPlacementAssignments";
    private const string PreviousSqlServerMigration = "20261015191001_AddSharedAllowance";
    private const string SqlServerMigration = "20261015200001_BackfillSharedPlacementAssignments";

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
    public void SqlServer_script_backfills_active_assignments_for_live_managed_shared_instances()
    {
        using var db = CreateSqlServerContext();
        var script = db.GetService<IMigrator>().GenerateScript(
            fromMigration: PreviousSqlServerMigration,
            toMigration: SqlServerMigration,
            options: MigrationsSqlGenerationOptions.Idempotent);

        Assert.Contains("INSERT INTO ElsaPlacementAssignments", script, StringComparison.Ordinal);
        Assert.Contains("TargetMode = N'managed'", script, StringComparison.Ordinal);
        Assert.Contains("IsolationProfile = N'shared'", script, StringComparison.Ordinal);
        Assert.Contains("DeletedAt IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("N'shared-shard'", script, StringComparison.Ordinal);
        Assert.Contains("N'standard'", script, StringComparison.Ordinal);
        Assert.Contains("N'Active'", script, StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sqlite_backfill_writes_active_assignments_for_unassigned_shared_instances()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateSqliteContext(connection);
        await db.Database.MigrateAsync(PreviousSqliteMigration);

        var organization = new Organization { Name = "Backfill shared organization" };
        db.Organizations.Add(organization);
        var workspace = new Workspace
        {
            Name = "Backfill shared workspace",
            OrganizationId = organization.Id,
            Organization = organization
        };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var liveId = Guid.NewGuid();
        var deletedId = Guid.NewGuid();
        var assignedId = Guid.NewGuid();
        var assignedAssignmentId = Guid.NewGuid();
        var createdAt = DateTimeOffset.Parse("2026-10-01T08:00:00Z").UtcTicks;
        await InsertInstanceAsync(db, liveId, workspace, "shared", deleted: false, createdAt);
        await InsertInstanceAsync(db, deletedId, workspace, "shared", deleted: true, createdAt);
        await InsertInstanceAsync(db, assignedId, workspace, "shared", deleted: false, createdAt);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ElsaPlacementAssignments" (
                "Id", "InstanceId", "OrganizationId", "WorkspaceId",
                "Kind", "ShardClass", "State", "CreatedAt")
            VALUES (
                {assignedAssignmentId}, {assignedId}, {workspace.OrganizationId}, {workspace.Id},
                {"shared-shard"}, {"standard"}, {"Reserved"}, {createdAt});
            """);

        await db.Database.MigrateAsync(SqliteMigration);

        var liveAssignment = await db.ElsaPlacementAssignments.SingleAsync(x => x.InstanceId == liveId);
        Assert.Equal(ElsaPlacementAssignmentStates.Active, liveAssignment.State);
        Assert.Equal(ElsaPlacementKinds.SharedShard, liveAssignment.Kind);
        Assert.Equal(ElsaShardClasses.Standard, liveAssignment.ShardClass);
        Assert.Null(liveAssignment.SupersedesAssignmentId);
        Assert.Equal(createdAt, liveAssignment.CreatedAt.UtcTicks);
        Assert.False(await db.ElsaPlacementAssignments.AnyAsync(x => x.InstanceId == deletedId));
        Assert.Equal(assignedAssignmentId, (await db.ElsaPlacementAssignments.SingleAsync(x => x.InstanceId == assignedId)).Id);
    }

    private static async Task InsertInstanceAsync(
        CatalogDbContext db,
        Guid id,
        Workspace workspace,
        string isolation,
        bool deleted,
        long createdAt)
    {
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ElsaInstances" (
                    "Id", "OrganizationId", "WorkspaceId", "Name", "Slug",
                    "DistributionId", "ReleaseLine", "Channel",
                    "PatchUpdates", "MinorUpdates", "MajorMigrations",
                    "TopologyId", "FeatureOverridesJson",
                    "TargetMode", "RequiresProvisioningContext", "RegionCode",
                    "IsolationProfile", "CapacityProfile", "NetworkOutcome", "DomainOutcome",
                    "DesiredLifecycle", "ObservedLifecycle", "Health",
                    "CurrentDeploymentManagedHandoff", "CurrentDeploymentStudioGrants",
                    "Version", "CreatedAt", "UpdatedAt", "DeletedAt")
                VALUES (
                    {id}, {workspace.OrganizationId}, {workspace.Id}, {isolation + "-" + id.ToString("N")[..8]}, {isolation + "-" + id.ToString("N")[..8]},
                    {"valence-runtime"}, {"3.8"}, {"stable"},
                    {"automatic-within-minor"}, {"explicit-approval"}, {"explicit-migration"},
                    {"combined"}, {"{}"},
                    {"managed"}, 0, {"westeurope"},
                    {isolation}, {"standard-small"}, {"public"}, {"managed"},
                    {"Running"}, {"Pending"}, {"Unknown"},
                    0, 0,
                    1, {createdAt}, {createdAt}, { (deleted ? createdAt : (long?)null) });
                """);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        }
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
}
