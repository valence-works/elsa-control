using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.OrganizationDeployments;

public sealed class OrganizationDeploymentAuditService(
    AccountWorkspaceService accounts,
    IOrganizationDeploymentAuditStore store)
{
    public async Task<OrganizationDeploymentAuditApiResult> ListAsync(
        TrustedWorkspaceIdentity identity,
        Guid organizationId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var access = await accounts.GetOrganizationAccessAsync(
            identity,
            organizationId,
            OrganizationOperation.ViewOrganization,
            cancellationToken);
        if (!access.Succeeded)
            return OrganizationDeploymentAuditApiResult.Denied(access.Failure!.Value);

        IReadOnlyList<OrganizationDeploymentAuditRecord> records;
        try
        {
            records = await store.ListAsync(organizationId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return OrganizationDeploymentAuditApiResult.StoreUnavailable();
        }

        var items = records
            .OrderByDescending(record => record.OccurredAt)
            .ThenBy(record => record.Id, StringComparer.Ordinal)
            .Select(record => OrganizationDeploymentAuditRules.TryProject(record, out var item) ? item : null)
            .OfType<OrganizationDeploymentAuditItemResponse>()
            .ToList();
        var offset = (page - 1) * pageSize;
        var pageItems = offset >= items.Count
            ? []
            : items.Skip(offset).Take(pageSize).ToList();

        return OrganizationDeploymentAuditApiResult.Success(
            new OrganizationDeploymentAuditPageResponse(pageItems, page, pageSize, items.Count));
    }
}
