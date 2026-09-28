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
    IReadOnlyList<OrganizationBillingLifecycleAdvance>? Advances = null);

/// <summary>
/// Staging-only operator action: move a grace or constraint deadline into the
/// past on a harness-created or allowlisted organization, then run the normal
/// lifecycle advancer. It never writes commercial state or engine rows itself.
/// </summary>
public sealed class StagingBillingLifecycleLever(
    IOrganizationBillingLifecycleDeadlineStore deadlines,
    OrganizationBillingService billing,
    CatalogDbContext dbContext,
    IOptions<StagingBillingLifecycleLeverOptions> options,
    IHostEnvironment environment,
    TimeProvider timeProvider)
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

        if (environment.IsProduction() || !options.Value.Enabled)
            return new StagingBillingLifecycleLeverResult(StagingBillingLifecycleLeverOutcome.Disabled);

        var organization = await dbContext.Organizations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == organizationId, cancellationToken);
        if (organization is null)
            return new StagingBillingLifecycleLeverResult(StagingBillingLifecycleLeverOutcome.OrganizationNotFound);

        if (!options.Value.AllowsOrganization(organizationId, organization.CustomerReference))
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

        var advances = await billing.AdvanceLifecycleAsync(cancellationToken);
        return new StagingBillingLifecycleLeverResult(StagingBillingLifecycleLeverOutcome.Advanced, move, advances);
    }
}
