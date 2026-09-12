using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Sync;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class SyncCatalogStore(CatalogDbContext dbContext) : ISyncCatalogStore
{
    // A sync run looks up every discovered version in one context, and each entity a lookup returns stays tracked for the
    // rest of the run. The lookups therefore load no navigations, and the display-name lookups project values only.
    public Task<Package?> GetPackageAsync(Guid sourceId, string packageId, CancellationToken cancellationToken = default) =>
        dbContext.Packages.SingleOrDefaultAsync(x => x.SourceId == sourceId && x.PackageId == packageId, cancellationToken);

    public Task<PackageVersion?> GetPackageVersionAsync(Guid packageId, string version, CancellationToken cancellationToken = default) =>
        dbContext.PackageVersions.SingleOrDefaultAsync(x => x.PackageId == packageId && x.Version == version, cancellationToken);

    public async Task<IReadOnlyList<string>> GetValidVersionsAsync(Guid packageId, CancellationToken cancellationToken = default) =>
        await dbContext.PackageVersions
            .Where(x => x.PackageId == packageId && x.ValidationStatus == ValidationStatus.Valid)
            .OrderBy(x => x.Id)
            .Select(x => x.Version)
            .ToListAsync(cancellationToken);

    public Task<string?> GetManifestJsonAsync(Guid packageId, string version, CancellationToken cancellationToken = default) =>
        dbContext.PackageVersions
            .Where(x => x.PackageId == packageId && x.Version == version)
            .Select(x => (string?)x.ManifestJson)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task AddPackageAsync(Package package, CancellationToken cancellationToken = default) =>
        await dbContext.Packages.AddAsync(package, cancellationToken);

    public async Task AddValidationResultAsync(ManifestValidationResultRecord result, CancellationToken cancellationToken = default) =>
        await dbContext.ManifestValidationResults.AddAsync(result, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
