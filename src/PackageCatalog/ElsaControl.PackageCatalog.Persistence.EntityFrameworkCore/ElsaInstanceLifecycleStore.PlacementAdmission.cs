using System.Data;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed partial class EfCoreElsaInstanceLifecycleStore
{
    public Task<ElsaInstancePlacementMoveResult> MovePlacementAsync(
        ElsaInstancePlacementMoveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InstanceId == Guid.Empty)
            throw new ArgumentException("Instance ID cannot be empty.", nameof(request));

        dbContext.ChangeTracker.Clear();
        return dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            var instance = await LoadPlacementInstanceAsync(
                request.OrganizationId, request.WorkspaceId, request.InstanceId, cancellationToken);
            if (await HasInFlightTargetAsync(instance.Id, cancellationToken))
                throw Conflict(
                    "A placement move is already in progress.",
                    ElsaInstanceLifecycleConflictReason.OperationActive);
            var source = await FindSourceAssignmentAsync(instance.Id, cancellationToken: cancellationToken);

            var target = new ElsaPlacementKindSelection(request.TargetKind, request.TargetShardClass);
            var dedicatedCount = await ElsaInstanceAllowanceAccounting.CountDedicatedAsync(
                dbContext, instance.OrganizationId, cancellationToken);
            var sharedCount = await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(
                dbContext, instance.OrganizationId, cancellationToken);
            var placement = new ElsaInstanceCommercialPlacementRequest(
                target.Kind,
                target.ShardClass,
                IsMove: true,
                SourceKind: source?.Kind,
                SourceShardClass: source?.ShardClass,
                ActiveSharedCount: sharedCount,
                ActiveDedicatedCount: dedicatedCount);

            var decision = await _commercialGate.EvaluateAsync(
                instance.OrganizationId,
                ElsaInstanceOperationAction.Reconcile,
                dedicatedCount,
                placement,
                cancellationToken);
            if (!decision.Allowed)
                throw new ElsaInstanceLifecycleConflictException(
                    decision.Summary,
                    ElsaInstanceLifecycleConflictReason.CommercialDenied,
                    decision.Code,
                    decision.CurrentInstanceCount,
                    decision.MaxInstances);

            Guid? shardId = request.TargetShardId;
            if (target.ConsumesSharedAllowance && shardId is null)
            {
                var reservation = await TryReserveSharedShardAsync(
                    instance.RegionCode,
                    target.ShardClass!,
                    cancellationToken);
                if (reservation.ShardsConfigured && reservation.ShardId is null)
                    throw new ElsaInstanceLifecycleConflictException(
                        "No eligible Shared shard has remaining capacity.",
                        ElsaInstanceLifecycleConflictReason.CommercialDenied,
                        ElsaInstanceCommercialOperation.ShardCapacity);
                shardId = reservation.ShardId;
            }

            var now = _timeProvider.GetUtcNow();
            var reserved = await AddCountingAssignmentAsync(
                instance,
                target,
                shardId,
                request.PackageSetDigest,
                source?.Id,
                now,
                cancellationToken);
            instance.UpdatedAt = now.ToUniversalTime();

            await dbContext.SaveChangesAsync(cancellationToken);
            return ToMoveResult(instance.Id, reserved, source);
        }, cancellationToken);
    }

    public Task<ElsaInstancePlacementMoveResult> CommitPlacementMoveAsync(
        ElsaInstancePlacementMoveCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        dbContext.ChangeTracker.Clear();
        return dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            var instance = await LoadPlacementInstanceAsync(
                request.OrganizationId, request.WorkspaceId, request.InstanceId, cancellationToken);
            var target = await FindTargetReservationAsync(instance.Id, request.TargetAssignmentId, cancellationToken);
            var source = await FindSourceAssignmentAsync(instance.Id, excludingAssignmentId: target.Id, cancellationToken: cancellationToken);
            if (source is not null)
                source.State = ElsaPlacementAssignmentStates.Retired;
            target.State = ElsaPlacementAssignmentStates.Active;
            instance.IsolationProfile = ElsaPlacementKindResolver.IsolationFor(
                new ElsaPlacementKindSelection(target.Kind, target.ShardClass));
            instance.UpdatedAt = _timeProvider.GetUtcNow().ToUniversalTime();
            await dbContext.SaveChangesAsync(cancellationToken);
            return ToMoveResult(instance.Id, target, source);
        }, cancellationToken);
    }

    public Task<ElsaInstancePlacementMoveResult> AbortPlacementMoveAsync(
        ElsaInstancePlacementMoveCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        dbContext.ChangeTracker.Clear();
        return dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            var instance = await LoadPlacementInstanceAsync(
                request.OrganizationId, request.WorkspaceId, request.InstanceId, cancellationToken);
            var target = await FindTargetReservationAsync(instance.Id, request.TargetAssignmentId, cancellationToken);
            var source = await FindSourceAssignmentAsync(instance.Id, excludingAssignmentId: target.Id, cancellationToken: cancellationToken);
            target.State = ElsaPlacementAssignmentStates.Retired;
            instance.UpdatedAt = _timeProvider.GetUtcNow().ToUniversalTime();
            await dbContext.SaveChangesAsync(cancellationToken);
            return ToMoveResult(instance.Id, target, source);
        }, cancellationToken);
    }

    private async Task<(bool ShardsConfigured, Guid? ShardId)> TryReserveSharedShardAsync(
        string regionCode,
        string shardClass,
        CancellationToken cancellationToken)
    {
        var shards = await dbContext.ElsaSharedShards
            .AsNoTracking()
            .Where(x => x.RegionCode == regionCode && x.ShardClass == shardClass)
            .ToListAsync(cancellationToken);
        if (shards.Count == 0)
            return (false, null);

        foreach (var shard in shards)
        {
            var occupancy = await ElsaInstanceAllowanceAccounting.CountShardOccupancyAsync(
                dbContext, shard.Id, cancellationToken);
            if (occupancy < shard.Capacity)
                return (true, shard.Id);
        }

        return (true, null);
    }

    private async Task<ElsaPlacementAssignmentEntity> AddCountingAssignmentAsync(
        ElsaInstanceEntity instance,
        ElsaPlacementKindSelection selection,
        Guid? shardId,
        string? packageSetDigest,
        Guid? supersedesAssignmentId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var assignment = new ElsaPlacementAssignmentEntity
        {
            Id = Guid.NewGuid(),
            InstanceId = instance.Id,
            OrganizationId = instance.OrganizationId,
            WorkspaceId = instance.WorkspaceId,
            Kind = selection.Kind,
            ShardClass = selection.ShardClass,
            State = ElsaPlacementAssignmentStates.Reserved,
            ShardId = shardId is null || shardId == Guid.Empty ? null : shardId,
            PackageSetDigest = packageSetDigest,
            SupersedesAssignmentId = supersedesAssignmentId,
            CreatedAt = createdAt.ToUniversalTime()
        };
        await dbContext.ElsaPlacementAssignments.AddAsync(assignment, cancellationToken);
        return assignment;
    }

    private async Task RetireCountingAssignmentsAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var assignments = await dbContext.ElsaPlacementAssignments
            .Where(x => x.InstanceId == instanceId &&
                        x.State != ElsaPlacementAssignmentStates.Retired)
            .ToListAsync(cancellationToken);
        foreach (var assignment in assignments)
            assignment.State = ElsaPlacementAssignmentStates.Retired;
    }

    private async Task<ElsaInstanceEntity> LoadPlacementInstanceAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var instance = await dbContext.ElsaInstances
            .SingleOrDefaultAsync(
                x => x.Id == instanceId &&
                     x.WorkspaceId == workspaceId &&
                     x.OrganizationId == organizationId,
                cancellationToken)
            ?? throw Conflict("Elsa instance does not exist in the workspace.", ElsaInstanceLifecycleConflictReason.InvalidState);
        if (instance.DeletedAt is not null)
            throw Conflict("The requested operation is not valid for the current instance state.", ElsaInstanceLifecycleConflictReason.InvalidState);
        return instance;
    }

    private async Task<ElsaPlacementAssignmentEntity?> FindSourceAssignmentAsync(
        Guid instanceId,
        Guid? excludingAssignmentId = null,
        CancellationToken cancellationToken = default)
    {
        var assignments = await dbContext.ElsaPlacementAssignments
            .Where(x => x.InstanceId == instanceId &&
                        (x.State == ElsaPlacementAssignmentStates.Active ||
                         x.State == ElsaPlacementAssignmentStates.Reserved) &&
                        (excludingAssignmentId == null || x.Id != excludingAssignmentId))
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return assignments.Count switch
        {
            0 => null,
            1 => assignments[0],
            _ => throw Conflict(
                "A placement move is already in progress.",
                ElsaInstanceLifecycleConflictReason.OperationActive)
        };
    }

    private Task<bool> HasInFlightTargetAsync(Guid instanceId, CancellationToken cancellationToken) =>
        dbContext.ElsaPlacementAssignments.AnyAsync(
            x => x.InstanceId == instanceId &&
                 x.State == ElsaPlacementAssignmentStates.Reserved &&
                 x.SupersedesAssignmentId != null,
            cancellationToken);

    private async Task<ElsaPlacementAssignmentEntity> FindTargetReservationAsync(
        Guid instanceId,
        Guid targetAssignmentId,
        CancellationToken cancellationToken)
    {
        var target = await dbContext.ElsaPlacementAssignments
            .SingleOrDefaultAsync(
                x => x.Id == targetAssignmentId &&
                     x.InstanceId == instanceId &&
                     x.State == ElsaPlacementAssignmentStates.Reserved,
                cancellationToken)
            ?? throw Conflict(
                "The placement move reservation is not active.",
                ElsaInstanceLifecycleConflictReason.InvalidState);
        return target;
    }

    private static ElsaInstancePlacementMoveResult ToMoveResult(
        Guid instanceId,
        ElsaPlacementAssignmentEntity target,
        ElsaPlacementAssignmentEntity? source) =>
        new(
            instanceId,
            target.Id,
            target.Kind,
            target.ShardClass,
            target.State,
            source?.Id,
            source?.State);
}
