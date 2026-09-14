using System.Data;
using System.Globalization;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed partial class OrganizationBillingStore : IOrganizationAzureBoundEntitlementStore
{
    private const string AzureBoundEntitlementTargetType = "azure-bound-entitlement";

    public async Task<OrganizationAzureBoundEntitlementResult> GetAzureBoundEntitlementAsync(
        Guid organizationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        now = RequireUtc(now, nameof(now));
        if (!await dbContext.Organizations.AsNoTracking().AnyAsync(x => x.Id == organizationId, cancellationToken))
            return new(OrganizationAzureBoundEntitlementOutcome.OrganizationNotFound);

        var subscription = await dbContext.OrganizationSubscriptions.AsNoTracking()
            .CurrentForOrganization(organizationId)
            .FirstOrDefaultAsync(cancellationToken);
        var entitlement = await CurrentEntitlementAsync(organizationId, cancellationToken);
        return new(
            OrganizationAzureBoundEntitlementOutcome.Current,
            AzureBoundStatus(organizationId, subscription, entitlement, now));
    }

    public Task<OrganizationAzureBoundEntitlementResult> MintAzureBoundEntitlementAsync(
        OrganizationAzureBoundEntitlementMint mint,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mint);
        now = RequireUtc(now, nameof(now));
        OrganizationAzureBoundEntitlementPolicy.EnsureValid(mint.Terms, now);
        var auditId = Guid.Empty;
        return AzureBoundEntitlementCoreAsync(
            () => MintAzureBoundEntitlementTransactionAsync(mint, now, cancellationToken, id => auditId = id),
            async (result, verificationCancellationToken) =>
            {
                if (result.Outcome != OrganizationAzureBoundEntitlementOutcome.Minted)
                    return true;

                return auditId != Guid.Empty &&
                    await dbContext.OrganizationAuditRecords.AsNoTracking().AnyAsync(x =>
                        x.Id == auditId &&
                        x.OrganizationId == mint.OrganizationId &&
                        x.Action == OrganizationAuditAction.EntitlementChanged &&
                        x.TargetType == AzureBoundEntitlementTargetType,
                        verificationCancellationToken);
            },
            cancellationToken);
    }

    public Task<OrganizationAzureBoundEntitlementResult> RevokeAzureBoundEntitlementAsync(
        Guid organizationId,
        string? operatorSubject,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        now = RequireUtc(now, nameof(now));
        var auditId = Guid.Empty;
        return AzureBoundEntitlementCoreAsync(
            () => RevokeAzureBoundEntitlementTransactionAsync(
                organizationId,
                operatorSubject,
                now,
                cancellationToken,
                id => auditId = id),
            async (result, verificationCancellationToken) =>
            {
                if (result.Outcome != OrganizationAzureBoundEntitlementOutcome.Revoked)
                    return true;

                return auditId != Guid.Empty &&
                    await dbContext.OrganizationAuditRecords.AsNoTracking().AnyAsync(x =>
                        x.Id == auditId &&
                        x.OrganizationId == organizationId &&
                        x.Action == OrganizationAuditAction.EntitlementChanged &&
                        x.TargetType == AzureBoundEntitlementTargetType,
                        verificationCancellationToken);
            },
            cancellationToken);
    }

    private async Task<OrganizationAzureBoundEntitlementResult> AzureBoundEntitlementCoreAsync(
        Func<Task<OrganizationAzureBoundEntitlementResult>> transaction,
        Func<OrganizationAzureBoundEntitlementResult, CancellationToken, Task<bool>> verifySucceeded,
        CancellationToken cancellationToken,
        int attempt = 0)
    {
        try
        {
            return await dbContext.ExecuteInTransactionAsync(
                IsolationLevel.Serializable,
                transaction,
                verifySucceeded,
                cancellationToken);
        }
        catch (Exception exception) when (attempt < 2 && IsRetryableConflict(exception))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            return await AzureBoundEntitlementCoreAsync(
                transaction,
                verifySucceeded,
                cancellationToken,
                attempt + 1);
        }
    }

    private async Task<OrganizationAzureBoundEntitlementResult> MintAzureBoundEntitlementTransactionAsync(
        OrganizationAzureBoundEntitlementMint mint,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Action<Guid> auditIdSink)
    {
        var exists = await dbContext.Organizations.AsNoTracking()
            .AnyAsync(x => x.Id == mint.OrganizationId, cancellationToken);
        if (!exists)
            return new(OrganizationAzureBoundEntitlementOutcome.OrganizationNotFound);

        var subscription = await dbContext.OrganizationSubscriptions
            .CurrentForOrganization(mint.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken);
        var entitlement = await dbContext.OrganizationEntitlementSnapshots
            .SingleOrDefaultAsync(x => x.OrganizationId == mint.OrganizationId, cancellationToken);

        if (subscription is { State: OrganizationSubscriptionState.Active } &&
            string.Equals(subscription.Provider, BillingProviderNames.AzureBound, StringComparison.Ordinal))
            return RefuseAzureBound(
                OrganizationAzureBoundEntitlementOutcome.AlreadyMinted,
                mint.OrganizationId,
                subscription,
                entitlement,
                now);

        if (subscription is not null &&
            subscription.State is not (OrganizationSubscriptionState.Retained or OrganizationSubscriptionState.Deleted))
            return RefuseAzureBound(
                OrganizationAzureBoundEntitlementOutcome.CommercialSubscriptionExists,
                mint.OrganizationId,
                subscription,
                entitlement,
                now);

        var hasActiveBind = await dbContext.OrganizationAzureSubscriptionBinds.AsNoTracking()
            .AnyAsync(x =>
                x.OrganizationId == mint.OrganizationId &&
                x.State == OrganizationAzureSubscriptionBindState.Active &&
                x.SubscriptionId.Trim() != "",
                cancellationToken);
        if (!hasActiveBind)
            return RefuseAzureBound(
                OrganizationAzureBoundEntitlementOutcome.BindingRequired,
                mint.OrganizationId,
                subscription,
                entitlement,
                now);

        subscription = OrganizationSubscriptionLifecycle.CreateTrial(
            mint.OrganizationId,
            BillingProviderNames.AzureBound,
            now);
        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Active, now);
        dbContext.OrganizationSubscriptions.Add(subscription);

        entitlement = await ProjectEntitlementAsync(subscription, now, cancellationToken);
        entitlement.ManagedHostingEnabled = true;
        entitlement.MaxInstances = mint.Terms.MaxInstances;
        entitlement.ManagedHostingExpiresAt = mint.Terms.ExpiresAt?.ToUniversalTime();
        auditIdSink(AddAzureBoundEntitlementAudit(
            subscription,
            mint.OperatorSubject,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Azure-bound managed-hosting entitlement minted (max instances {mint.Terms.MaxInstances}, expires {ExpirySummary(entitlement.ManagedHostingExpiresAt)}). Reason: {mint.Terms.Reason.Trim()}"),
            now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(
            OrganizationAzureBoundEntitlementOutcome.Minted,
            AzureBoundStatus(mint.OrganizationId, subscription, entitlement, now));
    }

    private async Task<OrganizationAzureBoundEntitlementResult> RevokeAzureBoundEntitlementTransactionAsync(
        Guid organizationId,
        string? operatorSubject,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Action<Guid> auditIdSink)
    {
        var exists = await dbContext.Organizations.AsNoTracking().AnyAsync(x => x.Id == organizationId, cancellationToken);
        if (!exists)
            return new(OrganizationAzureBoundEntitlementOutcome.OrganizationNotFound);

        var subscription = await dbContext.OrganizationSubscriptions
            .CurrentForOrganization(organizationId)
            .FirstOrDefaultAsync(cancellationToken);
        var entitlement = await dbContext.OrganizationEntitlementSnapshots
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        if (subscription is null)
            return RefuseAzureBound(
                OrganizationAzureBoundEntitlementOutcome.NotMinted,
                organizationId,
                subscription,
                entitlement,
                now);
        if (!string.Equals(subscription.Provider, BillingProviderNames.AzureBound, StringComparison.Ordinal))
            return RefuseAzureBound(
                OrganizationAzureBoundEntitlementOutcome.CommercialSubscriptionExists,
                organizationId,
                subscription,
                entitlement,
                now);
        if (entitlement is not { ManagedHostingEnabled: true })
            return RefuseAzureBound(
                OrganizationAzureBoundEntitlementOutcome.Unchanged,
                organizationId,
                subscription,
                entitlement,
                now);

        subscription.EarlyDeletionRequestedAt ??= now;
        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Deleted, now, advanceLifecycleVersion: true);
        subscription.UpdatedAt = now;
        entitlement = await ProjectEntitlementAsync(subscription, now, cancellationToken);
        auditIdSink(AddAzureBoundEntitlementAudit(
            subscription,
            operatorSubject,
            "Azure-bound managed-hosting entitlement revoked.",
            now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(
            OrganizationAzureBoundEntitlementOutcome.Revoked,
            AzureBoundStatus(organizationId, subscription, entitlement, now));
    }

    private static OrganizationAzureBoundEntitlementResult RefuseAzureBound(
        OrganizationAzureBoundEntitlementOutcome outcome,
        Guid organizationId,
        OrganizationSubscription? subscription,
        OrganizationEntitlementSnapshot? entitlement,
        DateTimeOffset now) =>
        new(outcome, AzureBoundStatus(organizationId, subscription, entitlement, now));

    private static OrganizationAzureBoundEntitlementStatus AzureBoundStatus(
        Guid organizationId,
        OrganizationSubscription? subscription,
        OrganizationEntitlementSnapshot? entitlement,
        DateTimeOffset now)
    {
        if (subscription is not null &&
            !string.Equals(subscription.Provider, BillingProviderNames.AzureBound, StringComparison.Ordinal))
            return new(
                organizationId,
                OrganizationAzureBoundEntitlementState.CommercialSubscription,
                null,
                null,
                null);
        if (subscription is null)
            return new(organizationId, OrganizationAzureBoundEntitlementState.None, null, null, null);

        var state = subscription.State != OrganizationSubscriptionState.Active
            ? OrganizationAzureBoundEntitlementState.Closed
            : entitlement is not { ManagedHostingEnabled: true }
                ? OrganizationAzureBoundEntitlementState.Revoked
                : entitlement.ManagedHostingExpiresAt is { } expiresAt && expiresAt <= now
                    ? OrganizationAzureBoundEntitlementState.Expired
                    : OrganizationAzureBoundEntitlementState.Active;
        return new(organizationId, state, entitlement?.MaxInstances, entitlement?.ManagedHostingExpiresAt, entitlement?.UpdatedAt);
    }

    private Guid AddAzureBoundEntitlementAudit(
        OrganizationSubscription subscription,
        string? operatorSubject,
        string summary,
        DateTimeOffset createdAt)
    {
        var audit = new OrganizationAuditRecord
        {
            OrganizationId = subscription.OrganizationId,
            OperatorSubject = OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject(operatorSubject),
            Action = OrganizationAuditAction.EntitlementChanged,
            TargetType = AzureBoundEntitlementTargetType,
            TargetId = subscription.Id.ToString("D"),
            Summary = summary,
            CreatedAt = createdAt
        };
        dbContext.OrganizationAuditRecords.Add(audit);
        return audit.Id;
    }

    private static string ExpirySummary(DateTimeOffset? expiresAt) =>
        expiresAt is null ? "none" : expiresAt.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
