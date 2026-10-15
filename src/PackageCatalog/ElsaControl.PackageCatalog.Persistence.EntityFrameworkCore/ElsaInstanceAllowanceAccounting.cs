using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

/// <summary>
/// Assignment-based Dedicated and Shared counts (ADR-0017 Amendment 1).
/// Reserved and Active count; Retiring never does. During a move both the
/// source assignment and the Reserved target count until the flip commits.
/// Same-kind Shared moves may read L+1 for one in-flight move per org;
/// a second org-wide same-kind move is refused. Lab never counts. Instances with <c>DeletedAt</c> set stop counting at
/// the same terminal point as Dedicated. Shared counts the persisted
/// assignment <c>Kind</c>/<c>shardClass</c> against <c>MaxSharedTenants</c>,
/// not #818's isolation-profile proxy.
/// </summary>
internal static class ElsaInstanceAllowanceAccounting
{
    public static bool IsCountingState(string? state) =>
        string.Equals(state, ElsaPlacementAssignmentStates.Reserved, StringComparison.Ordinal) ||
        string.Equals(state, ElsaPlacementAssignmentStates.Active, StringComparison.Ordinal);

    public static bool ConsumesDedicatedAllowance(string? kind) =>
        string.Equals(kind, ElsaPlacementKinds.DedicatedValence, StringComparison.Ordinal) ||
        string.Equals(kind, ElsaPlacementKinds.ByoSubscription, StringComparison.Ordinal);

    public static bool ConsumesSharedAllowance(string? kind, string? shardClass) =>
        string.Equals(kind, ElsaPlacementKinds.SharedShard, StringComparison.Ordinal) &&
        string.Equals(shardClass, ElsaShardClasses.Standard, StringComparison.Ordinal);

    public static IQueryable<ElsaPlacementAssignmentEntity> WhereCounting(
        this IQueryable<ElsaPlacementAssignmentEntity> assignments) =>
        assignments.Where(x =>
            x.State == ElsaPlacementAssignmentStates.Reserved ||
            x.State == ElsaPlacementAssignmentStates.Active);

    public static Task<int> CountSharedStandardAsync(
        CatalogDbContext db,
        Guid organizationId,
        CancellationToken cancellationToken = default) =>
        CountByKindAsync(db, organizationId, dedicated: false, cancellationToken);

    public static Task<int> CountDedicatedAsync(
        CatalogDbContext db,
        Guid organizationId,
        CancellationToken cancellationToken = default) =>
        CountByKindAsync(db, organizationId, dedicated: true, cancellationToken);

    public static Task<int> CountShardOccupancyAsync(
        CatalogDbContext db,
        Guid shardId,
        CancellationToken cancellationToken = default) =>
        db.ElsaPlacementAssignments
            .AsNoTracking()
            .CountAsync(
                x => x.ShardId == shardId && x.State != ElsaPlacementAssignmentStates.Retired,
                cancellationToken);

    private static async Task<int> CountByKindAsync(
        CatalogDbContext db,
        Guid organizationId,
        bool dedicated,
        CancellationToken cancellationToken)
    {
        var assignmentCount = dedicated
            ? await (
                from assignment in db.ElsaPlacementAssignments.AsNoTracking().WhereCounting()
                join instance in db.ElsaInstances.AsNoTracking() on assignment.InstanceId equals instance.Id
                where assignment.OrganizationId == organizationId &&
                      instance.DeletedAt == null &&
                      (assignment.Kind == ElsaPlacementKinds.DedicatedValence ||
                       assignment.Kind == ElsaPlacementKinds.ByoSubscription)
                select assignment.Id).CountAsync(cancellationToken)
            : await (
                from assignment in db.ElsaPlacementAssignments.AsNoTracking().WhereCounting()
                join instance in db.ElsaInstances.AsNoTracking() on assignment.InstanceId equals instance.Id
                where assignment.OrganizationId == organizationId &&
                      instance.DeletedAt == null &&
                      assignment.Kind == ElsaPlacementKinds.SharedShard &&
                      assignment.ShardClass == ElsaShardClasses.Standard
                select assignment.Id).CountAsync(cancellationToken);

        if (!dedicated)
            return assignmentCount;

        // Legacy Dedicated rows written before assignment admission still
        // consume MaxInstances. Assigned instances are counted above.
        var unassignedDedicated = await db.ElsaInstances
            .AsNoTracking()
            .CountAsync(
                instance => instance.OrganizationId == organizationId &&
                            instance.DeletedAt == null &&
                            instance.TargetMode == "managed" &&
                            instance.IsolationProfile == "dedicated" &&
                            !db.ElsaPlacementAssignments.Any(assignment => assignment.InstanceId == instance.Id),
                cancellationToken);

        return assignmentCount + unassignedDedicated;
    }
}
