using System.Data;
using System.Globalization;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

/// <summary>
/// Operator-granted internal entitlement. Provider ownership is decided inside
/// the same serializable transaction that writes, with the billing store's retry
/// discipline, so concurrent grants and a concurrent provider checkout converge
/// on one subscription and one snapshot, and a provider-owned subscription is
/// never modified. The grant only owns the managed-hosting capability, its
/// instance cap and its expiry; every other capability field is preserved.
/// Each attempt runs inside the configured execution strategy because the SQL
/// Server provider's retrying strategy rejects user-initiated transactions.
/// Unlike the other billing audit records, a grant's audit summary deliberately
/// carries the operator's reason (#312 requires the grant to be audited with a
/// reason); the policy bounds it to 200 characters and rejects control,
/// formatting and separator characters, and it is never echoed in responses.
/// </summary>
public sealed partial class OrganizationBillingStore : IOrganizationInternalEntitlementStore
{
    private const string InternalEntitlementTargetType = "internal-entitlement";

    public async Task<OrganizationInternalEntitlementResult> GetInternalEntitlementAsync(
        Guid organizationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        now = RequireUtc(now, nameof(now));
        if (!await dbContext.Organizations.AsNoTracking().AnyAsync(x => x.Id == organizationId, cancellationToken))
            return new(OrganizationInternalEntitlementOutcome.OrganizationNotFound);

        var subscription = await dbContext.OrganizationSubscriptions.AsNoTracking()
            .CurrentForOrganization(organizationId)
            .FirstOrDefaultAsync(cancellationToken);
        var entitlement = await CurrentEntitlementAsync(organizationId, cancellationToken);
        return new(OrganizationInternalEntitlementOutcome.Current, InternalStatus(organizationId, subscription, entitlement, now));
    }

    public Task<OrganizationInternalEntitlementResult> GrantInternalEntitlementAsync(
        OrganizationInternalEntitlementGrant grant,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        now = RequireUtc(now, nameof(now));
        OrganizationInternalEntitlementPolicy.EnsureValid(grant.Terms, now);
        var auditId = Guid.Empty;
        return InternalEntitlementCoreAsync(
            () => GrantInternalEntitlementTransactionAsync(grant, now, cancellationToken, id => auditId = id),
            async (result, verificationCancellationToken) =>
            {
                if (result.Outcome is not (OrganizationInternalEntitlementOutcome.Granted or OrganizationInternalEntitlementOutcome.Regranted))
                    return true;

                return auditId != Guid.Empty &&
                    await dbContext.OrganizationAuditRecords.AsNoTracking().AnyAsync(x =>
                        x.Id == auditId &&
                        x.OrganizationId == grant.OrganizationId &&
                        x.Action == OrganizationAuditAction.EntitlementChanged &&
                        x.TargetType == InternalEntitlementTargetType,
                        verificationCancellationToken);
            },
            cancellationToken);
    }

    public Task<OrganizationInternalEntitlementResult> RevokeInternalEntitlementAsync(
        Guid organizationId,
        string? operatorSubject,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        now = RequireUtc(now, nameof(now));
        var auditId = Guid.Empty;
        return InternalEntitlementCoreAsync(
            () => RevokeInternalEntitlementTransactionAsync(organizationId, operatorSubject, now, cancellationToken, id => auditId = id),
            async (result, verificationCancellationToken) =>
            {
                if (result.Outcome != OrganizationInternalEntitlementOutcome.Revoked)
                    return true;

                return auditId != Guid.Empty &&
                    await dbContext.OrganizationAuditRecords.AsNoTracking().AnyAsync(x =>
                        x.Id == auditId &&
                        x.OrganizationId == organizationId &&
                        x.Action == OrganizationAuditAction.EntitlementChanged &&
                        x.TargetType == InternalEntitlementTargetType,
                        verificationCancellationToken);
            },
            cancellationToken);
    }

    private async Task<OrganizationInternalEntitlementResult> InternalEntitlementCoreAsync(
        Func<Task<OrganizationInternalEntitlementResult>> transaction,
        Func<OrganizationInternalEntitlementResult, CancellationToken, Task<bool>> verifySucceeded,
        CancellationToken cancellationToken,
        int attempt = 0)
    {
        try
        {
            // Every attempt decides from committed rows only: the helper clears the change
            // tracker and opens a new serializable transaction for each strategy attempt.
            return await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, transaction, verifySucceeded, cancellationToken);
        }
        catch (Exception exception) when (attempt < 2 && IsRetryableConflict(exception))
        {
            // A concurrent grant or provider checkout committed first. Re-run the
            // whole decision so it observes that commit (re-grant or refusal).
            await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            return await InternalEntitlementCoreAsync(transaction, verifySucceeded, cancellationToken, attempt + 1);
        }
    }

    private async Task<OrganizationInternalEntitlementResult> GrantInternalEntitlementTransactionAsync(
        OrganizationInternalEntitlementGrant grant,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Action<Guid> auditIdSink)
    {
        var (exists, subscription, entitlement) = await LoadInternalEntitlementAsync(grant.OrganizationId, cancellationToken);
        OrganizationInternalEntitlementOutcome? refusal =
            !exists ? OrganizationInternalEntitlementOutcome.OrganizationNotFound :
            IsProviderOwned(subscription, entitlement) ? OrganizationInternalEntitlementOutcome.CommercialSubscriptionExists :
            subscription is { State: not OrganizationSubscriptionState.Active } ? OrganizationInternalEntitlementOutcome.SubscriptionClosed :
            null;
        if (refusal is { } refused)
            return Refuse(refused, grant.OrganizationId, subscription, entitlement, now);

        var outcome = subscription is null
            ? OrganizationInternalEntitlementOutcome.Granted
            : OrganizationInternalEntitlementOutcome.Regranted;
        if (subscription is null)
        {
            // The existing provider-neutral factory; an internal grant starts Active
            // and the lifecycle scheduler only advances Trial rows by their end date.
            subscription = OrganizationSubscriptionLifecycle.CreateTrial(grant.OrganizationId, BillingProviderNames.Internal, now);
            OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Active, now);
            dbContext.OrganizationSubscriptions.Add(subscription);
        }

        entitlement = await ProjectEntitlementAsync(subscription, now, cancellationToken);
        entitlement.ManagedHostingEnabled = true;
        entitlement.MaxInstances = grant.Terms.MaxInstances;
        entitlement.ManagedHostingExpiresAt = grant.Terms.ExpiresAt.ToUniversalTime();
        var verb = outcome == OrganizationInternalEntitlementOutcome.Granted ? "granted" : "re-granted";
        auditIdSink(AddInternalEntitlementAudit(
            subscription,
            grant.OperatorSubject,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Internal managed-hosting entitlement {verb} (max instances {grant.Terms.MaxInstances}, expires {entitlement.ManagedHostingExpiresAt.Value.UtcDateTime:O}). Reason: {grant.Terms.Reason.Trim()}"),
            now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(outcome, InternalStatus(grant.OrganizationId, subscription, entitlement, now));
    }

    private async Task<OrganizationInternalEntitlementResult> RevokeInternalEntitlementTransactionAsync(
        Guid organizationId,
        string? operatorSubject,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Action<Guid> auditIdSink)
    {
        var (exists, subscription, entitlement) = await LoadInternalEntitlementAsync(organizationId, cancellationToken);
        OrganizationInternalEntitlementOutcome? refusal =
            !exists ? OrganizationInternalEntitlementOutcome.OrganizationNotFound :
            IsProviderOwned(subscription, entitlement) ? OrganizationInternalEntitlementOutcome.CommercialSubscriptionExists :
            subscription is null ? OrganizationInternalEntitlementOutcome.NotGranted :
            entitlement is not { ManagedHostingEnabled: true } ? OrganizationInternalEntitlementOutcome.Unchanged :
            null;
        if (refusal is { } refused)
            return Refuse(refused, organizationId, subscription, entitlement, now);

        // Expire as well as disable, so a later capability change elsewhere cannot
        // silently revive the revoked grant through the old expiry.
        entitlement!.ManagedHostingEnabled = false;
        entitlement.ManagedHostingExpiresAt = entitlement.ManagedHostingExpiresAt is { } expiresAt && expiresAt < now ? expiresAt : now;
        entitlement.SyncedAt = now;
        entitlement.UpdatedAt = now;
        auditIdSink(AddInternalEntitlementAudit(subscription!, operatorSubject, "Internal managed-hosting entitlement revoked.", now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(OrganizationInternalEntitlementOutcome.Revoked, InternalStatus(organizationId, subscription, entitlement, now));
    }

    private async Task<(bool Exists, OrganizationSubscription? Subscription, OrganizationEntitlementSnapshot? Entitlement)> LoadInternalEntitlementAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var exists = await dbContext.Organizations.AsNoTracking().AnyAsync(x => x.Id == organizationId, cancellationToken);
        var subscription = await dbContext.OrganizationSubscriptions
            .CurrentForOrganization(organizationId)
            .FirstOrDefaultAsync(cancellationToken);
        var entitlement = await dbContext.OrganizationEntitlementSnapshots
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        return (exists, subscription, entitlement);
    }

    private static OrganizationInternalEntitlementResult Refuse(
        OrganizationInternalEntitlementOutcome outcome,
        Guid organizationId,
        OrganizationSubscription? subscription,
        OrganizationEntitlementSnapshot? entitlement,
        DateTimeOffset now) =>
        outcome == OrganizationInternalEntitlementOutcome.OrganizationNotFound
            ? new(outcome)
            : new(outcome, InternalStatus(organizationId, subscription, entitlement, now));

    // Any subscription of another provider, or a lifecycle projected without a
    // subscription row, is commercial state this path does not own.
    private static bool IsProviderOwned(OrganizationSubscription? subscription, OrganizationEntitlementSnapshot? entitlement) =>
        subscription is null
            ? entitlement is { SubscriptionState: not null } or { SubscriptionId: not null }
            : !string.Equals(subscription.Provider, BillingProviderNames.Internal, StringComparison.Ordinal);

    private static OrganizationInternalEntitlementStatus InternalStatus(
        Guid organizationId,
        OrganizationSubscription? subscription,
        OrganizationEntitlementSnapshot? entitlement,
        DateTimeOffset now)
    {
        if (IsProviderOwned(subscription, entitlement))
            return new(organizationId, OrganizationInternalEntitlementState.CommercialSubscription, null, null, null);
        if (subscription is null)
            return new(organizationId, OrganizationInternalEntitlementState.None, null, null, null);

        var state = subscription.State != OrganizationSubscriptionState.Active
            ? OrganizationInternalEntitlementState.Closed
            : entitlement is not { ManagedHostingEnabled: true }
                ? OrganizationInternalEntitlementState.Revoked
                : entitlement.ManagedHostingExpiresAt is { } expiresAt && expiresAt > now
                    ? OrganizationInternalEntitlementState.Active
                    : OrganizationInternalEntitlementState.Expired;
        return new(organizationId, state, entitlement?.MaxInstances, entitlement?.ManagedHostingExpiresAt, entitlement?.UpdatedAt);
    }

    private Guid AddInternalEntitlementAudit(
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
            TargetType = InternalEntitlementTargetType,
            TargetId = subscription.Id.ToString("D"),
            Summary = summary,
            CreatedAt = createdAt
        };
        dbContext.OrganizationAuditRecords.Add(audit);
        return audit.Id;
    }
}
