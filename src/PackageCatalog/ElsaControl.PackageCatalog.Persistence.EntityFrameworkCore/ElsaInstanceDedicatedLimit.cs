using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

/// <summary>
/// Dedicated instance-limit accounting (ADR-0017 F5). <c>MaxInstances</c> is
/// consumed only by live managed instances whose isolation profile is Dedicated,
/// the current catalog proxy for <c>dedicated-valence</c> and
/// <c>byo-subscription</c>. Shared isolation does not consume it. Lab placement
/// isn't modelled yet (ADR-0017).
/// </summary>
internal static class ElsaInstanceDedicatedLimit
{
    public const string ManagedTargetMode = "managed";
    public const string DedicatedIsolationProfile = "dedicated";

    public static bool ConsumesDedicatedInstanceLimit(string? targetMode, string? isolationProfile) =>
        string.Equals(targetMode, ManagedTargetMode, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(isolationProfile, DedicatedIsolationProfile, StringComparison.OrdinalIgnoreCase);

    public static IQueryable<ElsaInstanceEntity> WhereActiveDedicatedManaged(
        this IQueryable<ElsaInstanceEntity> instances,
        Guid organizationId) =>
        instances.Where(x =>
            x.OrganizationId == organizationId &&
            x.TargetMode == ManagedTargetMode &&
            x.IsolationProfile == DedicatedIsolationProfile &&
            x.DeletedAt == null);

    public static Task<int> CountActiveDedicatedManagedAsync(
        this IQueryable<ElsaInstanceEntity> instances,
        Guid organizationId,
        CancellationToken cancellationToken = default) =>
        instances.WhereActiveDedicatedManaged(organizationId).CountAsync(cancellationToken);
}
