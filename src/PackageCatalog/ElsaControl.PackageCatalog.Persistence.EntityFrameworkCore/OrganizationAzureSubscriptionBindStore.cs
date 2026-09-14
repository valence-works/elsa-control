using System.Data;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class OrganizationAzureSubscriptionBindStore(CatalogDbContext dbContext) : IOrganizationAzureSubscriptionBindStore
{
    public Task<OrganizationAzureSubscriptionBind?> GetAsync(Guid organizationId, Guid bindId, CancellationToken cancellationToken = default) =>
        dbContext.OrganizationAzureSubscriptionBinds
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == bindId, cancellationToken);

    public Task<OrganizationAzureSubscriptionBind?> GetLatestAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        dbContext.OrganizationAzureSubscriptionBinds
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.State == OrganizationAzureSubscriptionBindState.Unbound)
            .ThenByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<OrganizationAzureSubscriptionBindResult> CreatePendingAsync(
        OrganizationAzureSubscriptionBind bind,
        CancellationToken cancellationToken = default) =>
        await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            if (!await dbContext.Organizations.AsNoTracking().AnyAsync(x => x.Id == bind.OrganizationId, cancellationToken))
                return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.OrganizationNotFound);

            var current = await dbContext.OrganizationAzureSubscriptionBinds
                .Where(x => x.OrganizationId == bind.OrganizationId)
                .OrderBy(x => x.State == OrganizationAzureSubscriptionBindState.Unbound)
                .ThenByDescending(x => x.UpdatedAt)
                .ThenByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            var failure = current?.State switch
            {
                OrganizationAzureSubscriptionBindState.Active => OrganizationAzureSubscriptionBindFailure.AlreadyBound,
                OrganizationAzureSubscriptionBindState.PendingConsent or OrganizationAzureSubscriptionBindState.Verifying => OrganizationAzureSubscriptionBindFailure.BindInFlight,
                OrganizationAzureSubscriptionBindState.Degraded => OrganizationAzureSubscriptionBindFailure.DegradedBindRequiresUnbind,
                _ => (OrganizationAzureSubscriptionBindFailure?)null
            };
            if (current?.State == OrganizationAzureSubscriptionBindState.Unbound &&
                string.Equals(current.SubscriptionId, bind.SubscriptionId, StringComparison.Ordinal))
                failure = OrganizationAzureSubscriptionBindFailure.SubscriptionMustChange;
            if (failure is not null)
                return OrganizationAzureSubscriptionBindResult.Denied(failure.Value);

            dbContext.OrganizationAzureSubscriptionBinds.Add(bind);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return OrganizationAzureSubscriptionBindResult.Success(bind);
            }
            catch (DbUpdateException)
            {
                // The filtered unique indexes protect the policy when two requests race.
                dbContext.Entry(bind).State = EntityState.Detached;
                return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.BindInFlight);
            }
        }, cancellationToken);

    public async Task<OrganizationAzureSubscriptionBindResult> TransitionAsync(
        OrganizationAzureSubscriptionBindTransition transition,
        CancellationToken cancellationToken = default) =>
        await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            var bind = await dbContext.OrganizationAzureSubscriptionBinds
                .SingleOrDefaultAsync(x => x.OrganizationId == transition.OrganizationId && x.Id == transition.BindId, cancellationToken);
            if (bind is null)
                return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.BindNotFound);
            if (bind.State != transition.ExpectedState ||
                (transition.ExpectedUpdatedAt.HasValue && bind.UpdatedAt != transition.ExpectedUpdatedAt.Value) ||
                !OrganizationAzureSubscriptionBindLifecycle.CanTransition(bind.State, transition.NewState))
                return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.InvalidState);

            bind.State = transition.NewState;
            bind.UpdatedAt = transition.ChangedAt.ToUniversalTime();
            if (transition.VerifiedAt.HasValue)
                bind.VerifiedAt = transition.VerifiedAt.Value.ToUniversalTime();
            if (transition.LastPreflightCode is not null)
                bind.LastPreflightCode = transition.LastPreflightCode;
            if (transition.RegistrationDefinitionFingerprint is not null)
                bind.RegistrationDefinitionFingerprint = transition.RegistrationDefinitionFingerprint;
            if (transition.NewState == OrganizationAzureSubscriptionBindState.Unbound)
                bind.UnbindReason = transition.UnbindReason;
            await dbContext.SaveChangesAsync(cancellationToken);
            return OrganizationAzureSubscriptionBindResult.Success(bind);
        }, cancellationToken);
}
