using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

/// <summary>
/// Reads only the safe instance projection needed by the Control console. A
/// malformed or stale identity binding is intentionally returned without an
/// audience/callback so callers cannot invent one from an endpoint URL, and so is
/// the binding of a deployment whose runtime handoff the provider did not configure.
/// </summary>
public sealed class EfCoreManagedElsaInstanceCatalog(CatalogDbContext dbContext) : IManagedElsaInstanceCatalog
{
    public async Task<IReadOnlyList<ManagedElsaInstanceSummary>> ListAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty)
            return [];

        var instances = await dbContext.ElsaInstances
            .AsNoTracking()
            .Include(x => x.IdentityBinding)
            .Where(x => x.WorkspaceId == workspaceId && x.DeletedAt == null)
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        return instances.Select(Map).ToList();
    }

    private static ManagedElsaInstanceSummary Map(ElsaInstanceEntity entity)
    {
        string? audience = null;
        Uri? callbackUri = null;
        int? bindingVersion = null;
        if (entity.CurrentDeploymentManagedHandoff &&
            ManagedElsaIdentityBindingMapper.TryMapCurrent(entity, out var binding, out callbackUri))
        {
            audience = binding!.Audience;
            bindingVersion = binding.BindingVersion;
        }

        return new ManagedElsaInstanceSummary(
            entity.OrganizationId,
            entity.WorkspaceId,
            entity.Id,
            entity.Name,
            entity.Slug,
            entity.DesiredLifecycle,
            entity.ObservedLifecycle,
            entity.Health,
            audience,
            callbackUri,
            bindingVersion);
    }
}
