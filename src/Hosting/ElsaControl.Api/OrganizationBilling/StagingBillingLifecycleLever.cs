using ElsaControl.Billing.Stripe;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.OrganizationBilling;

public enum StagingBillingLifecycleLeverOutcome
{
    Advanced,
    Disabled,
    OrganizationNotAllowed,
    OrganizationNotFound,
    SubscriptionNotFound,
    DeadlineNotApplicable
}

public sealed record StagingBillingLifecycleLeverResult(
    StagingBillingLifecycleLeverOutcome Outcome,
    OrganizationBillingLifecycleDeadlineMove? Move = null,
    OrganizationBillingLifecycleAdvance? Advance = null);

/// <summary>
/// Staging-only operator action: move a grace or constraint deadline into the
/// past on an allowlisted organization, then run the normal lifecycle advancer
/// for that organization alone. It never writes commercial state or engine rows
/// itself.
/// </summary>
public sealed class StagingBillingLifecycleLever(
    IOrganizationBillingLifecycleDeadlineStore deadlines,
    OrganizationBillingService billing,
    CatalogDbContext dbContext,
    IOptions<StagingBillingLifecycleLeverOptions> options,
    IOptions<StripeBillingOptions> stripeOptions,
    TimeProvider timeProvider,
    ILogger<StagingBillingLifecycleLever> logger)
{
    public async Task<StagingBillingLifecycleLeverResult> AdvanceAsync(
        Guid organizationId,
        OrganizationBillingLifecycleDeadline deadline,
        string? operatorSubject,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("Organization ID is required.", nameof(organizationId));
        if (!Enum.IsDefined(deadline))
            throw new ArgumentOutOfRangeException(nameof(deadline));

        if (!options.Value.Enabled || !IsStripeTestMode(stripeOptions.Value))
        {
            logger.LogWarning(
                "The staging billing lifecycle lever is disabled because a required staging signal is missing.");
            return new StagingBillingLifecycleLeverResult(StagingBillingLifecycleLeverOutcome.Disabled);
        }

        var organizationExists = await dbContext.Organizations.AsNoTracking()
            .AnyAsync(x => x.Id == organizationId, cancellationToken);
        if (!organizationExists)
            return new StagingBillingLifecycleLeverResult(StagingBillingLifecycleLeverOutcome.OrganizationNotFound);

        if (!options.Value.AllowsOrganization(organizationId))
            return new StagingBillingLifecycleLeverResult(StagingBillingLifecycleLeverOutcome.OrganizationNotAllowed);

        var move = await deadlines.MoveDeadlineAsync(
            organizationId,
            deadline,
            timeProvider.GetUtcNow(),
            operatorSubject,
            cancellationToken);

        if (move.Outcome is OrganizationBillingLifecycleDeadlineMoveOutcome.OrganizationNotFound)
            return new StagingBillingLifecycleLeverResult(StagingBillingLifecycleLeverOutcome.OrganizationNotFound, move);
        if (move.Outcome is OrganizationBillingLifecycleDeadlineMoveOutcome.SubscriptionNotFound)
            return new StagingBillingLifecycleLeverResult(StagingBillingLifecycleLeverOutcome.SubscriptionNotFound, move);
        if (move.Outcome is OrganizationBillingLifecycleDeadlineMoveOutcome.DeadlineNotApplicable)
            return new StagingBillingLifecycleLeverResult(StagingBillingLifecycleLeverOutcome.DeadlineNotApplicable, move);

        var advance = await billing.AdvanceOneAsync(
            organizationId,
            move.SubscriptionId!.Value,
            cancellationToken);
        return new StagingBillingLifecycleLeverResult(StagingBillingLifecycleLeverOutcome.Advanced, move, advance);
    }

    private static bool IsStripeTestMode(StripeBillingOptions stripe)
    {
        if (!stripe.Enabled || string.IsNullOrWhiteSpace(stripe.SecretKey))
            return false;

        return stripe.SecretKey.StartsWith(
            StagingBillingLifecycleLeverDefaults.StripeTestSecretKeyPrefix,
            StringComparison.Ordinal);
    }
}
