using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
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
        var storedId = await db.Database
            .SqlQueryRaw<string>("""SELECT "Id" AS "Value" FROM "ElsaPlacementAssignments" WHERE "InstanceId" = {0}""", liveId)
            .SingleAsync();
        Assert.Equal(storedId.ToUpperInvariant(), storedId);
        Assert.False(await db.ElsaPlacementAssignments.AnyAsync(x => x.InstanceId == deletedId));
        Assert.Equal(assignedAssignmentId, (await db.ElsaPlacementAssignments.SingleAsync(x => x.InstanceId == assignedId)).Id);
    }

    [Fact]
    public async Task Sqlite_backfill_assignment_can_be_moved_then_aborted_and_committed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateSqliteContext(connection);
        await db.Database.MigrateAsync(PreviousSqliteMigration);

        var organization = new Organization { Name = "Backfill move organization" };
        db.Organizations.Add(organization);
        var workspace = new Workspace
        {
            Name = "Backfill move workspace",
            OrganizationId = organization.Id,
            Organization = organization
        };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = organization.Id,
            ManagedHostingEnabled = true,
            SubscriptionState = OrganizationSubscriptionState.Active,
            MaxInstances = 1,
            MaxSharedTenants = 1,
            SyncedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        var shard = new ElsaSharedShardEntity
        {
            Id = Guid.NewGuid(),
            RegionCode = "westeurope",
            ShardClass = ElsaShardClasses.Standard,
            Capacity = 4
        };
        db.ElsaSharedShards.Add(shard);
        await db.SaveChangesAsync();
        var instanceId = Guid.NewGuid();
        await InsertInstanceAsync(db, instanceId, workspace, "shared", deleted: false, Now.UtcTicks);

        await db.Database.MigrateAsync(SqliteMigration);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, organization.Id));

        var service = CreateLifecycleService(db);
        var moved = await service.MovePlacementAsync(new ElsaInstancePlacementMoveRequest(
            organization.Id,
            workspace.Id,
            instanceId,
            ElsaPlacementKinds.SharedShard,
            ElsaShardClasses.Standard,
            PackageSetDigest: "after-backfill",
            TargetShardId: shard.Id));

        Assert.Equal(ElsaPlacementAssignmentStates.Reserved, moved.TargetState);
        Assert.True(await ElsaInstanceAllowanceAccounting.HasSameKindSharedMoveInFlightAsync(db, organization.Id));
        Assert.Equal(2, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, organization.Id));

        var aborted = await service.AbortPlacementMoveAsync(new ElsaInstancePlacementMoveCompletionRequest(
            organization.Id,
            workspace.Id,
            instanceId,
            moved.TargetAssignmentId));
        Assert.Equal(ElsaPlacementAssignmentStates.Retired, aborted.TargetState);
        Assert.False(await ElsaInstanceAllowanceAccounting.HasSameKindSharedMoveInFlightAsync(db, organization.Id));
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, organization.Id));

        var reservedAgain = await service.MovePlacementAsync(new ElsaInstancePlacementMoveRequest(
            organization.Id,
            workspace.Id,
            instanceId,
            ElsaPlacementKinds.SharedShard,
            ElsaShardClasses.Standard,
            PackageSetDigest: "after-backfill-retry",
            TargetShardId: shard.Id));
        Assert.True(await ElsaInstanceAllowanceAccounting.HasSameKindSharedMoveInFlightAsync(db, organization.Id));

        var committed = await service.CommitPlacementMoveAsync(new ElsaInstancePlacementMoveCompletionRequest(
            organization.Id,
            workspace.Id,
            instanceId,
            reservedAgain.TargetAssignmentId));
        Assert.Equal(ElsaPlacementAssignmentStates.Active, committed.TargetState);
        Assert.Equal(ElsaPlacementAssignmentStates.Retired, committed.SourceState);
        Assert.False(await ElsaInstanceAllowanceAccounting.HasSameKindSharedMoveInFlightAsync(db, organization.Id));
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, organization.Id));
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

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T10:00:00Z");

    private static ElsaInstanceLifecycleService CreateLifecycleService(CatalogDbContext db) =>
        new(
            new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now)),
            new FixedTimeProvider(Now));

    private static CatalogDbContext CreateSqliteContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(
                CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);

    private sealed class EmptyResolutionInputSource : IElsaInstanceLifecycleResolutionInputSource
    {
        public static EmptyResolutionInputSource Instance { get; } = new();

        public Task<ElsaInstanceLifecycleResolutionInput?> GetAsync(
            ElsaInstance instance,
            ElsaInstanceOperation operation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ElsaInstanceLifecycleResolutionInput?>(null);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static CatalogDbContext CreateSqlServerContext() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                @"Server=(localdb)\MSSQLLocalDB;Initial Catalog=ElsaControlMigrationScriptTests;Integrated Security=True;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(
                    CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);
}
