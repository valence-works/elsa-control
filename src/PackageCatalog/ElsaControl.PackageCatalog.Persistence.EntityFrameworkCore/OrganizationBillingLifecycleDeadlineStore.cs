using System.Data;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed partial class OrganizationBillingStore : IOrganizationBillingLifecycleDeadlineStore
{
    public async Task<OrganizationBillingLifecycleDeadlineMove> MoveDeadlineAsync(
        Guid organizationId,
        OrganizationBillingLifecycleDeadline deadline,
        DateTimeOffset now,
        string? operatorSubject,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("Organization ID is required.", nameof(organizationId));
        if (!Enum.IsDefined(deadline))
            throw new ArgumentOutOfRangeException(nameof(deadline));
        now = now.ToUniversalTime();
        if (now == default)
            throw new ArgumentException("A UTC timestamp is required.", nameof(now));

        return await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            if (!await dbContext.Organizations.AsNoTracking().AnyAsync(x => x.Id == organizationId, cancellationToken))
                return new OrganizationBillingLifecycleDeadlineMove(
                    OrganizationBillingLifecycleDeadlineMoveOutcome.OrganizationNotFound,
                    organizationId);

            var subscription = await dbContext.OrganizationSubscriptions
                .CurrentForOrganization(organizationId)
                .FirstOrDefaultAsync(cancellationToken);
            if (subscription is null)
                return new OrganizationBillingLifecycleDeadlineMove(
                    OrganizationBillingLifecycleDeadlineMoveOutcome.SubscriptionNotFound,
                    organizationId);

            if (!TryResolveDeadline(subscription, deadline, now, out var previous, out var next, out var dueAt))
                return new OrganizationBillingLifecycleDeadlineMove(
                    OrganizationBillingLifecycleDeadlineMoveOutcome.DeadlineNotApplicable,
                    organizationId,
                    subscription.Id,
                    deadline,
                    State: subscription.State);

            var moved = next < previous;
            if (moved)
            {
                AssignDeadline(subscription, deadline, next);
                dbContext.PermitLifecycleDeadlineOverride(subscription.Id, PropertyName(deadline));
                subscription.UpdatedAt = now;
            }

            dbContext.OrganizationAuditRecords.Add(new OrganizationAuditRecord
            {
                OrganizationId = organizationId,
                OperatorSubject = OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject(operatorSubject),
                Action = OrganizationAuditAction.BillingLifecycleDeadlineMoved,
                TargetType = "subscription",
                TargetId = subscription.Id.ToString("D"),
                Summary = moved
                    ? $"Staging lifecycle lever moved {deadline} from {Format(previous)} to {Format(next)}."
                    : $"Staging lifecycle lever found {deadline} already due at {Format(dueAt)}.",
                CreatedAt = now
            });

            await dbContext.SaveChangesAsync(cancellationToken);
            return new OrganizationBillingLifecycleDeadlineMove(
                moved
                    ? OrganizationBillingLifecycleDeadlineMoveOutcome.Moved
                    : OrganizationBillingLifecycleDeadlineMoveOutcome.AlreadyDue,
                organizationId,
                subscription.Id,
                deadline,
                previous,
                moved ? next : previous,
                subscription.State);
        }, cancellationToken);
    }

    private static bool TryResolveDeadline(
        OrganizationSubscription subscription,
        OrganizationBillingLifecycleDeadline deadline,
        DateTimeOffset now,
        out DateTimeOffset previous,
        out DateTimeOffset next,
        out DateTimeOffset dueAt)
    {
        previous = default;
        next = default;
        dueAt = default;

        switch (deadline)
        {
            case OrganizationBillingLifecycleDeadline.GraceEndsAt:
                if (subscription.State != OrganizationSubscriptionState.PastDue ||
                    subscription.GraceEndsAt is not { } graceEndsAt)
                    return false;
                previous = graceEndsAt.ToUniversalTime();
                dueAt = previous;
                next = dueAt <= now ? previous : now;
                return true;
            case OrganizationBillingLifecycleDeadline.ConstrainedAt:
                if (subscription.State != OrganizationSubscriptionState.Constrained ||
                    subscription.ConstrainedAt is not { } constrainedAt)
                    return false;
                previous = constrainedAt.ToUniversalTime();
                dueAt = previous.Add(OrganizationSubscriptionLifecycle.ConstraintPeriod);
                next = dueAt <= now ? previous : now.Subtract(OrganizationSubscriptionLifecycle.ConstraintPeriod);
                return next <= previous;
            default:
                return false;
        }
    }

    private static void AssignDeadline(
        OrganizationSubscription subscription,
        OrganizationBillingLifecycleDeadline deadline,
        DateTimeOffset value)
    {
        switch (deadline)
        {
            case OrganizationBillingLifecycleDeadline.GraceEndsAt:
                subscription.GraceEndsAt = value;
                break;
            case OrganizationBillingLifecycleDeadline.ConstrainedAt:
                subscription.ConstrainedAt = value;
                break;
        }
    }

    private static string PropertyName(OrganizationBillingLifecycleDeadline deadline) => deadline switch
    {
        OrganizationBillingLifecycleDeadline.GraceEndsAt => nameof(OrganizationSubscription.GraceEndsAt),
        OrganizationBillingLifecycleDeadline.ConstrainedAt => nameof(OrganizationSubscription.ConstrainedAt),
        _ => throw new ArgumentOutOfRangeException(nameof(deadline))
    };

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("o");
}
