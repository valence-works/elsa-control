using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

/// <summary>
/// Evaluates the current provider-neutral organization projection. This is kept
/// in the catalog persistence layer so API and workers consume the same row and
/// the lifecycle store can evaluate it inside its serializable admission tx.
/// Entitlement expiry is compared with the injected clock on every decision, so
/// an expired grant fails closed even when no background job has run.
/// </summary>
public sealed class EfCoreElsaInstanceCommercialGate(CatalogDbContext db, TimeProvider? timeProvider = null) : IElsaInstanceCommercialGate
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<ElsaInstanceCommercialGateDecision> EvaluateAsync(
        Guid organizationId,
        ElsaInstanceOperationAction action,
        int? activeInstanceCount = null,
        CancellationToken cancellationToken = default) =>
        EvaluateAsync(organizationId, action, activeInstanceCount, placement: null, cancellationToken);

    public async Task<ElsaInstanceCommercialGateDecision> EvaluateAsync(
        Guid organizationId,
        ElsaInstanceOperationAction action,
        int? activeInstanceCount,
        ElsaInstanceCommercialPlacementRequest? placement,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
            return Deny(ElsaInstanceCommercialOperation.EntitlementRequired, "The organization entitlement is unavailable.");

        // Safe exits are deliberately independent of commercial state.
        if (action is ElsaInstanceOperationAction.Stop or ElsaInstanceOperationAction.Delete)
            return ElsaInstanceCommercialGateDecision.Allow();

        var entitlement = await db.OrganizationEntitlementSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        if (entitlement is null || !entitlement.ManagedHostingEnabled)
            return Deny(ElsaInstanceCommercialOperation.EntitlementRequired, "Managed hosting is not enabled for this organization.");

        if (entitlement.ManagedHostingExpiresAt is { } expiresAt && expiresAt <= _timeProvider.GetUtcNow())
            return Deny(ElsaInstanceCommercialOperation.EntitlementExpired, "The organization managed-hosting entitlement has expired.");

        if (entitlement.SubscriptionState is null)
            return Deny(ElsaInstanceCommercialOperation.SubscriptionStateRequired, "The organization subscription lifecycle is unavailable.");

        if (entitlement.SubscriptionState is OrganizationSubscriptionState.Constrained or
            OrganizationSubscriptionState.Suspended or
            OrganizationSubscriptionState.Retained or
            OrganizationSubscriptionState.Deleted)
            return Deny(
                ElsaInstanceCommercialOperation.LifecycleConstrained,
                ManagedElsaProvisioningProgressCopy.ForHold(action));

        var provider = entitlement.SubscriptionId is { } subscriptionId
            ? await db.OrganizationSubscriptions.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.Id == subscriptionId)
                .Select(x => x.Provider)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        if (string.Equals(provider, BillingProviderNames.AzureBound, StringComparison.Ordinal))
        {
            var hasActiveBind = await db.OrganizationAzureSubscriptionBinds.AsNoTracking()
                .AnyAsync(x =>
                    x.OrganizationId == organizationId &&
                    x.State == OrganizationAzureSubscriptionBindState.Active &&
                    x.SubscriptionId.Trim() != "",
                    cancellationToken);
            if (!hasActiveBind)
                return Deny(
                    ElsaInstanceCommercialOperation.BindingRequired,
                    "An active Azure subscription bind is required for this organization.");
        }

        // Updates never change the count and do not check either allowance
        // (ADR-0017 I1 / Amendment 1 clause 5).
        if (action is ElsaInstanceOperationAction.UpdateIntent or
            ElsaInstanceOperationAction.ApproveMinorUpgrade or
            ElsaInstanceOperationAction.MajorMigration)
            return ElsaInstanceCommercialGateDecision.Allow();

        // MaxInstances is the Dedicated placement allowance (ADR-0017 F5).
        // Shared admission uses MaxSharedTenants and the counting assignment.
        if (ShouldCheckDedicatedAllowance(action, placement) &&
            DedicatedCount(activeInstanceCount, placement) is { } dedicatedCount &&
            dedicatedCount >= entitlement.MaxInstances)
            return new ElsaInstanceCommercialGateDecision(
                false,
                ElsaInstanceCommercialOperation.InstanceLimitReached,
                "The organization has reached its managed-instance limit.")
            {
                CurrentInstanceCount = dedicatedCount,
                MaxInstances = entitlement.MaxInstances
            };

        if (ShouldCheckSharedAllowance(action, placement))
        {
            var sharedLimit = entitlement.MaxSharedTenants ?? 0;
            var sharedCount = placement?.ActiveSharedCount ?? 0;
            if (placement?.IsSameKindSharedMove == true)
            {
                // Hard org ceiling: admit only while count <= L. One in-flight
                // same-kind move may read L+1; a second org-wide move is 409.
                if (sharedCount > sharedLimit)
                    return Deny(
                        ElsaInstanceCommercialOperation.SharedAllowanceMoveInProgress,
                        "A Shared placement move is already using the transient allowance.");
            }
            else if (sharedCount >= sharedLimit)
            {
                return Deny(
                    ElsaInstanceCommercialOperation.SharedAllowanceExceeded,
                    "The organization has reached its Shared-tenant allowance.");
            }
        }

        return ElsaInstanceCommercialGateDecision.Allow();
    }

    private static bool ShouldCheckDedicatedAllowance(
        ElsaInstanceOperationAction action,
        ElsaInstanceCommercialPlacementRequest? placement)
    {
        if (placement is null)
            return action == ElsaInstanceOperationAction.Create;
        return placement.Selection.ConsumesDedicatedAllowance &&
               (action == ElsaInstanceOperationAction.Create || placement.CreatesTargetTenant);
    }

    private static bool ShouldCheckSharedAllowance(
        ElsaInstanceOperationAction action,
        ElsaInstanceCommercialPlacementRequest? placement)
    {
        if (placement is null || !placement.Selection.ConsumesSharedAllowance)
            return false;
        if (placement.IsSameKindSharedMove)
            return true;
        return action == ElsaInstanceOperationAction.Create || placement.CreatesTargetTenant;
    }

    private static int? DedicatedCount(int? activeInstanceCount, ElsaInstanceCommercialPlacementRequest? placement) =>
        placement?.ActiveDedicatedCount ?? activeInstanceCount;

    private static ElsaInstanceCommercialGateDecision Deny(string code, string summary) =>
        new(false, code, summary);
}
