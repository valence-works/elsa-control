using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ElsaInstanceSharedAllowanceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T10:00:00Z");

    [Fact]
    public async Task Shared_create_at_the_allowance_is_refused_without_writing_assignment_or_touching_a_shard()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, maxInstances: 2, maxSharedTenants: 1);
        var shard = SeedShard(db, workspace, capacity: 4);
        await db.SaveChangesAsync();
        var service = CreateLifecycleService(db);

        await service.CreateAsync(CreateRequest(workspace, "Shared one", "shared-one", "shared"));
        var denied = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            service.CreateAsync(CreateRequest(workspace, "Shared two", "shared-two", "shared")));

        Assert.Equal(ElsaInstanceLifecycleConflictReason.CommercialDenied, denied.Reason);
        Assert.Equal(ElsaInstanceCommercialOperation.SharedAllowanceExceeded, denied.CommercialCode);
        Assert.Equal(1, await db.ElsaInstances.CountAsync(x => x.DeletedAt == null));
        Assert.Equal(1, await db.ElsaPlacementAssignments.CountAsync());
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountShardOccupancyAsync(db, shard.Id));
        Assert.Equal(1, await db.ElsaInstanceLifecycleOutbox.CountAsync());

        var dedicated = await service.CreateAsync(CreateRequest(workspace, "Dedicated engine", "dedicated-engine", "dedicated"));
        Assert.Equal("dedicated", dedicated.Instance.PlacementIntent.IsolationProfile);
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountDedicatedAsync(db, workspace.OrganizationId));
    }

    [Fact]
    public async Task Dedicated_limit_does_not_block_a_Shared_create_with_allowance_room()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, maxInstances: 1, maxSharedTenants: 1);
        var service = CreateLifecycleService(db);

        await service.CreateAsync(CreateRequest(workspace, "Dedicated engine", "dedicated-engine", "dedicated"));
        var shared = await service.CreateAsync(CreateRequest(workspace, "Shared tenant", "shared-tenant", "shared"));

        Assert.Equal("shared", shared.Instance.PlacementIntent.IsolationProfile);
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountDedicatedAsync(db, workspace.OrganizationId));
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
    }

    [Fact]
    public async Task Absent_Shared_allowance_is_zero_and_refuses_a_Shared_create()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, maxInstances: 3, maxSharedTenants: null);
        var gate = new EfCoreElsaInstanceCommercialGate(db);

        var decision = await gate.EvaluateAsync(
            workspace.OrganizationId,
            ElsaInstanceOperationAction.Create,
            activeInstanceCount: 0,
            new ElsaInstanceCommercialPlacementRequest(
                ElsaPlacementKinds.SharedShard,
                ElsaShardClasses.Standard,
                ActiveSharedCount: 0),
            CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.SharedAllowanceExceeded, decision.Code);

        var denied = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            CreateLifecycleService(db).CreateAsync(CreateRequest(workspace, "Shared tenant", "shared-tenant", "shared")));
        Assert.Equal(ElsaInstanceCommercialOperation.SharedAllowanceExceeded, denied.CommercialCode);
        Assert.Equal(0, await db.ElsaInstances.CountAsync());
        Assert.Equal(0, await db.ElsaPlacementAssignments.CountAsync());
        Assert.Equal(0, await db.ElsaInstanceLifecycleOutbox.CountAsync());
    }

    [Fact]
    public async Task Lab_placements_do_not_change_the_Shared_count_or_see_the_Shared_limit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, maxInstances: 0, maxSharedTenants: 0);
        var service = CreateLifecycleService(db);

        var lab = await service.CreateAsync(CreateRequest(workspace, "Lab tenant", "lab-tenant", "lab"));

        Assert.Equal("lab", lab.Instance.PlacementIntent.IsolationProfile);
        var assignment = await db.ElsaPlacementAssignments.SingleAsync();
        Assert.Equal(ElsaPlacementKinds.SharedShard, assignment.Kind);
        Assert.Equal(ElsaShardClasses.Lab, assignment.ShardClass);
        Assert.Equal(0, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
        Assert.Equal(0, await ElsaInstanceAllowanceAccounting.CountDedicatedAsync(db, workspace.OrganizationId));

        var denied = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            service.CreateAsync(CreateRequest(workspace, "Shared tenant", "shared-tenant", "shared")));
        Assert.Equal(ElsaInstanceCommercialOperation.SharedAllowanceExceeded, denied.CommercialCode);
        Assert.Equal(1, await db.ElsaInstances.CountAsync(x => x.DeletedAt == null));
    }

    [Fact]
    public async Task Parallel_Shared_creates_admit_exactly_one_when_one_slot_remains()
    {
        const string connectionString = "Data Source=file:shared-allowance-concurrency;Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        Guid organizationId;
        Guid workspaceId;
        await using (var setup = CreateContext(keeper))
        {
            await setup.Database.EnsureCreatedAsync();
            var workspace = await CreateWorkspaceAsync(setup, maxInstances: 4, maxSharedTenants: 2);
            organizationId = workspace.OrganizationId;
            workspaceId = workspace.Id;
            await CreateLifecycleService(setup).CreateAsync(
                CreateRequest(workspace, "Shared seated", "shared-seated", "shared"));
        }

        var outcomes = await Task.WhenAll(
            CreateConcurrentlyAsync(connectionString, organizationId, workspaceId, "first"),
            CreateConcurrentlyAsync(connectionString, organizationId, workspaceId, "second"));

        Assert.Equal(1, outcomes.Count(x => x.Error is null));
        var loser = Assert.Single(outcomes, x => x.Error is not null);
        var conflict = Assert.IsType<ElsaInstanceLifecycleConflictException>(loser.Error);
        Assert.Equal(ElsaInstanceLifecycleConflictReason.CommercialDenied, conflict.Reason);
        Assert.Equal(ElsaInstanceCommercialOperation.SharedAllowanceExceeded, conflict.CommercialCode);
        await using var verify = CreateContext(keeper);
        Assert.Equal(2, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(verify, organizationId));
        Assert.Equal(2, await verify.ElsaInstances.CountAsync(x => x.OrganizationId == organizationId && x.DeletedAt == null));
    }

    [Fact]
    public async Task Shared_to_Shared_package_set_move_at_L_reads_L_plus_one_until_commit_or_abort()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, maxInstances: 1, maxSharedTenants: 1);
        var shard = SeedShard(db, workspace, capacity: 4);
        await db.SaveChangesAsync();
        var service = CreateLifecycleService(db);
        var created = await service.CreateAsync(CreateRequest(workspace, "Shared tenant", "shared-tenant", "shared"));

        var moved = await service.MovePlacementAsync(new ElsaInstancePlacementMoveRequest(
            workspace.OrganizationId,
            workspace.Id,
            created.Instance.Id,
            ElsaPlacementKinds.SharedShard,
            ElsaShardClasses.Standard,
            PackageSetDigest: "next",
            TargetShardId: shard.Id));

        Assert.Equal(ElsaPlacementAssignmentStates.Reserved, moved.TargetState);
        Assert.Equal(2, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));

        var aborted = await service.AbortPlacementMoveAsync(new ElsaInstancePlacementMoveCompletionRequest(
            workspace.OrganizationId,
            workspace.Id,
            created.Instance.Id,
            moved.TargetAssignmentId));
        Assert.Equal(ElsaPlacementAssignmentStates.Retired, aborted.TargetState);
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));

        var reservedAgain = await service.MovePlacementAsync(new ElsaInstancePlacementMoveRequest(
            workspace.OrganizationId,
            workspace.Id,
            created.Instance.Id,
            ElsaPlacementKinds.SharedShard,
            ElsaShardClasses.Standard,
            PackageSetDigest: "retry",
            TargetShardId: shard.Id));
        Assert.Equal(2, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
        var committed = await service.CommitPlacementMoveAsync(new ElsaInstancePlacementMoveCompletionRequest(
            workspace.OrganizationId,
            workspace.Id,
            created.Instance.Id,
            reservedAgain.TargetAssignmentId));
        Assert.Equal(ElsaPlacementAssignmentStates.Active, committed.TargetState);
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
    }

    [Fact]
    public async Task Shared_to_Shared_move_counts_both_sides_until_commit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, maxInstances: 1, maxSharedTenants: 2);
        var shard = SeedShard(db, workspace, capacity: 4);
        await db.SaveChangesAsync();
        var service = CreateLifecycleService(db);
        var created = await service.CreateAsync(CreateRequest(workspace, "Shared tenant", "shared-tenant", "shared"));

        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));

        var moved = await service.MovePlacementAsync(new ElsaInstancePlacementMoveRequest(
            workspace.OrganizationId,
            workspace.Id,
            created.Instance.Id,
            ElsaPlacementKinds.SharedShard,
            ElsaShardClasses.Standard,
            PackageSetDigest: "base",
            TargetShardId: shard.Id));

        Assert.Equal(ElsaPlacementAssignmentStates.Reserved, moved.TargetState);
        Assert.Equal(ElsaPlacementAssignmentStates.Reserved, moved.SourceState);
        Assert.Equal(2, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
        Assert.Equal(0, await ElsaInstanceAllowanceAccounting.CountDedicatedAsync(db, workspace.OrganizationId));
        var inFlight = await db.ElsaPlacementAssignments.AsNoTracking().ToListAsync();
        Assert.Equal(2, inFlight.Count);
        Assert.Contains(inFlight, x => x.State == ElsaPlacementAssignmentStates.Reserved && x.PackageSetDigest == "base");
        Assert.Contains(inFlight, x => x.Id == moved.SourceAssignmentId && x.State == ElsaPlacementAssignmentStates.Reserved);

        var committed = await service.CommitPlacementMoveAsync(new ElsaInstancePlacementMoveCompletionRequest(
            workspace.OrganizationId,
            workspace.Id,
            created.Instance.Id,
            moved.TargetAssignmentId));

        Assert.Equal(ElsaPlacementAssignmentStates.Active, committed.TargetState);
        Assert.Equal(ElsaPlacementAssignmentStates.Retired, committed.SourceState);
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
    }

    [Fact]
    public async Task Shared_to_Dedicated_move_requires_Dedicated_room_and_then_counts_only_as_Dedicated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, maxInstances: 1, maxSharedTenants: 1);
        var service = CreateLifecycleService(db);
        var dedicated = await service.CreateAsync(CreateRequest(workspace, "Dedicated engine", "dedicated-engine", "dedicated"));
        var shared = await service.CreateAsync(CreateRequest(workspace, "Shared tenant", "shared-tenant", "shared"));

        var refused = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            service.MovePlacementAsync(new ElsaInstancePlacementMoveRequest(
                workspace.OrganizationId,
                workspace.Id,
                shared.Instance.Id,
                ElsaPlacementKinds.DedicatedValence)));
        Assert.Equal(ElsaInstanceCommercialOperation.InstanceLimitReached, refused.CommercialCode);
        Assert.Equal("shared", (await db.ElsaInstances.SingleAsync(x => x.Id == shared.Instance.Id)).IsolationProfile);
        Assert.Equal(ElsaPlacementAssignmentStates.Reserved,
            (await db.ElsaPlacementAssignments.SingleAsync(x => x.InstanceId == shared.Instance.Id)).State);

        await TombstoneAsync(db, dedicated.Instance.Id);

        var admitted = await service.MovePlacementAsync(new ElsaInstancePlacementMoveRequest(
            workspace.OrganizationId,
            workspace.Id,
            shared.Instance.Id,
            ElsaPlacementKinds.DedicatedValence));

        Assert.Equal(ElsaPlacementAssignmentStates.Reserved, admitted.TargetState);
        Assert.Equal("shared", (await db.ElsaInstances.SingleAsync(x => x.Id == shared.Instance.Id)).IsolationProfile);
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountDedicatedAsync(db, workspace.OrganizationId));
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));

        var committed = await service.CommitPlacementMoveAsync(new ElsaInstancePlacementMoveCompletionRequest(
            workspace.OrganizationId,
            workspace.Id,
            shared.Instance.Id,
            admitted.TargetAssignmentId));

        Assert.Equal(ElsaPlacementAssignmentStates.Active, committed.TargetState);
        Assert.Equal(ElsaPlacementAssignmentStates.Retired, committed.SourceState);
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountDedicatedAsync(db, workspace.OrganizationId));
        Assert.Equal(0, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
    }

    [Fact]
    public async Task Dedicated_to_Shared_move_is_refused_without_Shared_allowance_room()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, maxInstances: 2, maxSharedTenants: 1);
        var service = CreateLifecycleService(db);
        await service.CreateAsync(CreateRequest(workspace, "Shared seated", "shared-seated", "shared"));
        var dedicated = await service.CreateAsync(CreateRequest(workspace, "Dedicated engine", "dedicated-engine", "dedicated"));

        var denied = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            service.MovePlacementAsync(new ElsaInstancePlacementMoveRequest(
                workspace.OrganizationId,
                workspace.Id,
                dedicated.Instance.Id,
                ElsaPlacementKinds.SharedShard,
                ElsaShardClasses.Standard)));

        Assert.Equal(ElsaInstanceCommercialOperation.SharedAllowanceExceeded, denied.CommercialCode);
        Assert.Equal("dedicated", (await db.ElsaInstances.SingleAsync(x => x.Id == dedicated.Instance.Id)).IsolationProfile);
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountDedicatedAsync(db, workspace.OrganizationId));
    }

    [Fact]
    public async Task Aborted_move_never_exceeds_the_Shared_limit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, maxInstances: 2, maxSharedTenants: 2);
        var shard = SeedShard(db, workspace, capacity: 4);
        await db.SaveChangesAsync();
        var service = CreateLifecycleService(db);
        var moving = await service.CreateAsync(CreateRequest(workspace, "Shared moving", "shared-moving", "shared"));

        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));

        var admitted = await service.MovePlacementAsync(new ElsaInstancePlacementMoveRequest(
            workspace.OrganizationId,
            workspace.Id,
            moving.Instance.Id,
            ElsaPlacementKinds.SharedShard,
            ElsaShardClasses.Standard,
            PackageSetDigest: "next",
            TargetShardId: shard.Id));

        Assert.Equal(2, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));

        var aborted = await service.AbortPlacementMoveAsync(new ElsaInstancePlacementMoveCompletionRequest(
            workspace.OrganizationId,
            workspace.Id,
            moving.Instance.Id,
            admitted.TargetAssignmentId));

        Assert.Equal(ElsaPlacementAssignmentStates.Retired, aborted.TargetState);
        Assert.Equal(ElsaPlacementAssignmentStates.Reserved, aborted.SourceState);
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
        Assert.Equal(0, await ElsaInstanceAllowanceAccounting.CountDedicatedAsync(db, workspace.OrganizationId));
        Assert.Equal(1, await db.ElsaPlacementAssignments.CountAsync(x =>
            x.State == ElsaPlacementAssignmentStates.Reserved || x.State == ElsaPlacementAssignmentStates.Active));
    }

    [Fact]
    public async Task Abandoned_Shared_to_Dedicated_move_never_exceeds_either_limit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, maxInstances: 1, maxSharedTenants: 1);
        var service = CreateLifecycleService(db);
        var shared = await service.CreateAsync(CreateRequest(workspace, "Shared tenant", "shared-tenant", "shared"));

        var admitted = await service.MovePlacementAsync(new ElsaInstancePlacementMoveRequest(
            workspace.OrganizationId,
            workspace.Id,
            shared.Instance.Id,
            ElsaPlacementKinds.DedicatedValence));

        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountDedicatedAsync(db, workspace.OrganizationId));

        await service.AbortPlacementMoveAsync(new ElsaInstancePlacementMoveCompletionRequest(
            workspace.OrganizationId,
            workspace.Id,
            shared.Instance.Id,
            admitted.TargetAssignmentId));

        Assert.Equal("shared", (await db.ElsaInstances.SingleAsync(x => x.Id == shared.Instance.Id)).IsolationProfile);
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
        Assert.Equal(0, await ElsaInstanceAllowanceAccounting.CountDedicatedAsync(db, workspace.OrganizationId));
    }

    [Fact]
    public async Task Parallel_Shared_create_and_Dedicated_to_Shared_move_admit_exactly_one()
    {
        const string connectionString = "Data Source=file:shared-allowance-create-move;Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        Guid organizationId;
        Guid workspaceId;
        Guid dedicatedId;
        await using (var setup = CreateContext(keeper))
        {
            await setup.Database.EnsureCreatedAsync();
            var workspace = await CreateWorkspaceAsync(setup, maxInstances: 2, maxSharedTenants: 1);
            organizationId = workspace.OrganizationId;
            workspaceId = workspace.Id;
            var dedicated = await CreateLifecycleService(setup).CreateAsync(
                CreateRequest(workspace, "Dedicated engine", "dedicated-engine", "dedicated"));
            dedicatedId = dedicated.Instance.Id;
        }

        var outcomes = await Task.WhenAll(
            AdmitSharedCreateAsync(connectionString, organizationId, workspaceId, "race"),
            AdmitDedicatedToSharedMoveAsync(connectionString, organizationId, workspaceId, dedicatedId));

        Assert.Equal(1, outcomes.Count(x => x.Error is null));
        var loser = Assert.Single(outcomes, x => x.Error is not null);
        var conflict = Assert.IsType<ElsaInstanceLifecycleConflictException>(loser.Error);
        await using var verify = CreateContext(keeper);
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(verify, organizationId));
    }

    [Fact]
    public async Task Shared_rebalance_at_L_reads_L_plus_one_and_returns_to_L()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, maxInstances: 1, maxSharedTenants: 1);
        var sourceShard = SeedShard(db, workspace, capacity: 4);
        var targetShard = SeedShard(db, workspace, capacity: 4, nameSuffix: "b");
        await db.SaveChangesAsync();
        var service = CreateLifecycleService(db);
        var created = await service.CreateAsync(CreateRequest(workspace, "Shared tenant", "shared-tenant", "shared"));
        var sourceAssignment = await db.ElsaPlacementAssignments.SingleAsync(x => x.InstanceId == created.Instance.Id);
        sourceAssignment.ShardId = sourceShard.Id;
        await db.SaveChangesAsync();

        var moved = await service.MovePlacementAsync(new ElsaInstancePlacementMoveRequest(
            workspace.OrganizationId,
            workspace.Id,
            created.Instance.Id,
            ElsaPlacementKinds.SharedShard,
            ElsaShardClasses.Standard,
            TargetShardId: targetShard.Id));
        Assert.Equal(2, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
        Assert.Equal(targetShard.Id, (await db.ElsaPlacementAssignments.SingleAsync(x => x.Id == moved.TargetAssignmentId)).ShardId);

        await service.AbortPlacementMoveAsync(new ElsaInstancePlacementMoveCompletionRequest(
            workspace.OrganizationId, workspace.Id, created.Instance.Id, moved.TargetAssignmentId));
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));

        var reservedAgain = await service.MovePlacementAsync(new ElsaInstancePlacementMoveRequest(
            workspace.OrganizationId,
            workspace.Id,
            created.Instance.Id,
            ElsaPlacementKinds.SharedShard,
            ElsaShardClasses.Standard,
            TargetShardId: targetShard.Id));
        await service.CommitPlacementMoveAsync(new ElsaInstancePlacementMoveCompletionRequest(
            workspace.OrganizationId, workspace.Id, created.Instance.Id, reservedAgain.TargetAssignmentId));
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
        Assert.Equal(targetShard.Id,
            (await db.ElsaPlacementAssignments.SingleAsync(x =>
                x.InstanceId == created.Instance.Id && x.State == ElsaPlacementAssignmentStates.Active)).ShardId);
    }

    [Fact]
    public async Task Second_move_of_the_same_instance_is_refused_as_operation_active()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, maxInstances: 1, maxSharedTenants: 1);
        var shard = SeedShard(db, workspace, capacity: 4);
        await db.SaveChangesAsync();
        var service = CreateLifecycleService(db);
        var created = await service.CreateAsync(CreateRequest(workspace, "Shared tenant", "shared-tenant", "shared"));
        await service.MovePlacementAsync(new ElsaInstancePlacementMoveRequest(
            workspace.OrganizationId,
            workspace.Id,
            created.Instance.Id,
            ElsaPlacementKinds.SharedShard,
            ElsaShardClasses.Standard,
            PackageSetDigest: "base",
            TargetShardId: shard.Id));

        var denied = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            service.MovePlacementAsync(new ElsaInstancePlacementMoveRequest(
                workspace.OrganizationId,
                workspace.Id,
                created.Instance.Id,
                ElsaPlacementKinds.SharedShard,
                ElsaShardClasses.Standard,
                PackageSetDigest: "other",
                TargetShardId: shard.Id)));

        Assert.Equal(ElsaInstanceLifecycleConflictReason.OperationActive, denied.Reason);
        Assert.Equal(2, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
        Assert.Equal(1, await db.ElsaPlacementAssignments.CountAsync(x =>
            x.InstanceId == created.Instance.Id &&
            x.State == ElsaPlacementAssignmentStates.Reserved &&
            x.SupersedesAssignmentId != null));
    }

    [Fact]
    public async Task Update_that_changes_placement_is_refused_and_the_count_is_unchanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, maxInstances: 1, maxSharedTenants: 1);
        var service = CreateLifecycleService(db);
        var created = await service.CreateAsync(CreateRequest(workspace, "Shared tenant", "shared-tenant", "shared"));
        await CompleteOperationAsync(db, created.Operation.Id);

        var denied = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            service.UpdateIntentAsync(new ElsaInstanceIntentUpdateRequest(
                workspace.Id,
                created.Instance.Id,
                CreateIntent("dedicated"),
                created.Instance.Version,
                "update-shared-to-dedicated")));

        Assert.Equal(ElsaInstanceLifecycleConflictReason.PlacementChangeRequiresMove, denied.Reason);
        Assert.Contains("explicit move", denied.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("shared", (await db.ElsaInstances.SingleAsync(x => x.Id == created.Instance.Id)).IsolationProfile);
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
        Assert.Equal(0, await ElsaInstanceAllowanceAccounting.CountDedicatedAsync(db, workspace.OrganizationId));
    }

    [Fact]
    public async Task Allowance_room_with_every_eligible_shard_full_is_shard_capacity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, maxInstances: 2, maxSharedTenants: 2);
        SeedShard(db, workspace, capacity: 0);
        SeedShard(db, workspace, capacity: 0, nameSuffix: "b");
        await db.SaveChangesAsync();
        var service = CreateLifecycleService(db);

        var denied = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            service.CreateAsync(CreateRequest(workspace, "Shared tenant", "shared-tenant", "shared")));

        Assert.Equal(ElsaInstanceCommercialOperation.ShardCapacity, denied.CommercialCode);
        Assert.Equal(0, await db.ElsaInstances.CountAsync());
        Assert.Equal(0, await db.ElsaPlacementAssignments.CountAsync());
        Assert.Equal(0, await db.ElsaInstanceLifecycleOutbox.CountAsync());
        Assert.Equal(0, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
    }

    [Fact]
    public async Task Delete_frees_the_Shared_allowance_at_the_same_terminal_point_as_Dedicated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, maxInstances: 1, maxSharedTenants: 1);
        var service = CreateLifecycleService(db);
        var created = await service.CreateAsync(CreateRequest(workspace, "Shared tenant", "shared-tenant", "shared"));

        var blocked = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            service.CreateAsync(CreateRequest(workspace, "Shared two", "shared-two", "shared")));
        Assert.Equal(ElsaInstanceCommercialOperation.SharedAllowanceExceeded, blocked.CommercialCode);

        await TombstoneAsync(db, created.Instance.Id);

        Assert.Equal(0, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
        var replacement = await service.CreateAsync(CreateRequest(workspace, "Shared replacement", "shared-replacement", "shared"));
        Assert.Equal("shared", replacement.Instance.PlacementIntent.IsolationProfile);
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(db, workspace.OrganizationId));
    }

    [Theory]
    [InlineData("shared", null, ElsaPlacementKinds.SharedShard, ElsaShardClasses.Standard, true, false)]
    [InlineData("lab", null, ElsaPlacementKinds.SharedShard, ElsaShardClasses.Lab, false, false)]
    [InlineData("dedicated", null, ElsaPlacementKinds.DedicatedValence, null, false, true)]
    [InlineData("shared", ElsaPlacementKindResolver.TrainingLabOrigin, ElsaPlacementKinds.SharedShard, ElsaShardClasses.Lab, false, false)]
    public void Kind_selection_maps_isolation_to_the_counting_assignment(
        string isolation,
        string? origin,
        string kind,
        string? shardClass,
        bool shared,
        bool dedicated)
    {
        var selection = ElsaPlacementKindResolver.FromIsolation(isolation, origin);
        Assert.Equal(kind, selection.Kind);
        Assert.Equal(shardClass, selection.ShardClass);
        Assert.Equal(shared, selection.ConsumesSharedAllowance);
        Assert.Equal(dedicated, selection.ConsumesDedicatedAllowance);
    }

    private static async Task<(bool Succeeded, Exception? Error)> AdmitSharedCreateAsync(
        string connectionString,
        Guid organizationId,
        Guid workspaceId,
        string suffix)
    {
        var outcome = await CreateConcurrentlyAsync(connectionString, organizationId, workspaceId, suffix);
        return (outcome.Error is null, outcome.Error);
    }

    private static async Task<(bool Succeeded, Exception? Error)> AdmitDedicatedToSharedMoveAsync(
        string connectionString,
        Guid organizationId,
        Guid workspaceId,
        Guid instanceId)
    {
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var db = CreateContext(connection);
            await CreateLifecycleService(db).MovePlacementAsync(new ElsaInstancePlacementMoveRequest(
                organizationId,
                workspaceId,
                instanceId,
                ElsaPlacementKinds.SharedShard,
                ElsaShardClasses.Standard));
            return (true, null);
        }
        catch (Exception error)
        {
            return (false, error);
        }
    }

    private static async Task<(ElsaInstanceLifecycleAcceptance? Acceptance, Exception? Error)> CreateConcurrentlyAsync(
        string connectionString,
        Guid organizationId,
        Guid workspaceId,
        string suffix)
    {
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var db = CreateContext(connection);
            var acceptance = await CreateLifecycleService(db).CreateAsync(new ElsaInstanceCreateRequest(
                organizationId,
                workspaceId,
                $"Shared {suffix}",
                $"shared-{suffix}",
                CreateIntent("shared"),
                $"create-shared-{suffix}"));
            return (acceptance, null);
        }
        catch (Exception error)
        {
            return (null, error);
        }
    }

    private static async Task<Workspace> CreateWorkspaceAsync(
        CatalogDbContext db,
        int maxInstances,
        int? maxSharedTenants)
    {
        var workspace = new Workspace { Name = "Shared allowance workspace " + Guid.NewGuid().ToString("N") };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = workspace.OrganizationId,
            ManagedHostingEnabled = true,
            SubscriptionState = OrganizationSubscriptionState.Active,
            MaxInstances = maxInstances,
            MaxSharedTenants = maxSharedTenants,
            SyncedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();
        return workspace;
    }

    private static ElsaSharedShardEntity SeedShard(
        CatalogDbContext db,
        Workspace workspace,
        int capacity,
        string nameSuffix = "a")
    {
        var shard = new ElsaSharedShardEntity
        {
            Id = Guid.NewGuid(),
            RegionCode = "westeurope",
            ShardClass = ElsaShardClasses.Standard,
            Capacity = capacity
        };
        db.ElsaSharedShards.Add(shard);
        _ = workspace;
        _ = nameSuffix;
        return shard;
    }

    private static ElsaInstanceCreateRequest CreateRequest(
        Workspace workspace,
        string name,
        string slug,
        string isolation) =>
        new(
            workspace.OrganizationId,
            workspace.Id,
            name,
            slug,
            CreateIntent(isolation),
            $"create-{slug}");

    private static ElsaInstanceLifecycleService CreateLifecycleService(CatalogDbContext db) =>
        new(
            new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now)),
            new FixedTimeProvider(Now));

    private static async Task TombstoneAsync(CatalogDbContext db, Guid instanceId)
    {
        db.ChangeTracker.Clear();
        var instance = await db.ElsaInstances.SingleAsync(x => x.Id == instanceId);
        instance.DesiredLifecycle = ElsaDesiredLifecycle.Deleting;
        instance.ObservedLifecycle = ElsaObservedLifecycle.Deleted;
        instance.DeletedAt = Now;
        await db.SaveChangesAsync();
    }

    private static async Task CompleteOperationAsync(CatalogDbContext db, Guid operationId)
    {
        db.ChangeTracker.Clear();
        var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == operationId);
        operation.State = ElsaInstanceOperationState.Succeeded;
        await db.SaveChangesAsync();
    }

    private static CatalogDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(connection).Options);

    private static ElsaInstanceIntent CreateIntent(string isolationProfile) => new(
        new ElsaReleaseIntent("valence-runtime", "3.8", "3.8.0", "stable"),
        new ElsaApplicationIntent("combined", "starter", new Dictionary<string, ElsaFeatureOverride>()),
        new ElsaPlacementIntent("managed", "westeurope", isolationProfile, "standard-small", "public", "managed"));

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
}
