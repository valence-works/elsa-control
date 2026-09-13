using System.Linq.Expressions;
using ElsaControl.Deployment.Core.Provisioning;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

internal static class EngineProvisioningTargetEligibility
{
    public static readonly Expression<Func<DeploymentEnvironmentEntity, bool>> IsAvailable =
        environment => environment.ElsaInstanceId == null &&
                       !environment.Engines.Any() &&
                       !environment.Revisions.Any() &&
                       !environment.ObservabilityBindings.Any() &&
                       !environment.DriftReports.Any() &&
                       environment.DesiredRevisionId == null &&
                       environment.DeployedRevisionId == null;
}

public sealed class EfCoreEngineProvisioningTargetStore(CatalogDbContext dbContext) : IEngineProvisioningTargetStore
{
    public async Task<IReadOnlyList<EngineProvisioningTarget>> GetAvailableTargetsAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty)
            return [];

        return await dbContext.DeploymentEnvironments
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .Where(EngineProvisioningTargetEligibility.IsAvailable)
            .OrderBy(x => x.ApplicationId)
            .ThenBy(x => x.Id)
            .Select(x => new EngineProvisioningTarget(x.ApplicationId, x.Id))
            .ToListAsync(cancellationToken);
    }
}
