using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

internal static class OrganizationSubscriptionQueries
{
    /// <summary>
    /// Selects the single current commercial owner, falling back to the newest
    /// terminal history row only when no open subscription exists.
    /// </summary>
    public static IOrderedQueryable<OrganizationSubscription> CurrentForOrganization(
        this IQueryable<OrganizationSubscription> subscriptions,
        Guid organizationId) =>
        subscriptions
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.State == OrganizationSubscriptionState.Retained ||
                          x.State == OrganizationSubscriptionState.Deleted)
            .ThenByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id);

    /// <summary>
    /// Resolves replay state within the provider that produced the durable
    /// inbox entry, rather than returning a later replacement provider.
    /// </summary>
    public static IOrderedQueryable<OrganizationSubscription> LatestForOrganizationProvider(
        this IQueryable<OrganizationSubscription> subscriptions,
        Guid organizationId,
        string provider,
        string providerEventId) =>
        subscriptions
            .Where(x => x.OrganizationId == organizationId && x.Provider == provider)
            .OrderByDescending(x => x.LastProviderEventId == providerEventId)
            .ThenByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id);
}
