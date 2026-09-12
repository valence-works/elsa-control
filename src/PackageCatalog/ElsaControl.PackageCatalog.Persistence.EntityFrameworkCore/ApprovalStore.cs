using System.Data;
using ElsaControl.PackageCatalog.Core.Approvals;
using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Sync;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class ApprovalStore(CatalogDbContext dbContext) : IApprovalStore
{
    public async Task<IReadOnlyList<Package>> ListPackagesAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Packages
            .AsNoTracking()
            .Include(x => x.Source)
            .Include(x => x.Versions)
            .ThenInclude(x => x.Features)
            .OrderBy(x => x.PackageId)
            .ToListAsync(cancellationToken);

    public async Task<Package?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        IQueryable<Package> query = dbContext.Packages
            .Include(x => x.Source)
            .Include(x => x.Versions)
            .ThenInclude(x => x.Features)
            .ThenInclude(x => x.Settings);

        return await FindPackageByIdAsync(query, packageId, cancellationToken);
    }

    public async Task<PackageVersion?> GetPackageVersionAsync(string packageId, string version, CancellationToken cancellationToken = default)
    {
        IQueryable<PackageVersion> query = dbContext.PackageVersions
            .Include(x => x.Package);

        var exactMatch = await query.SingleOrDefaultAsync(x => x.Package != null && x.Package.PackageId == packageId && x.Version == version, cancellationToken);
        if (exactMatch is not null)
            return exactMatch;

        var normalizedPackageId = packageId.ToLowerInvariant();
        return await query.SingleOrDefaultAsync(
            x => x.Package != null && x.Package.PackageId.ToLower() == normalizedPackageId && x.Version == version,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ManifestValidationResultRecord>> GetValidationResultsAsync(PackageVersion packageVersion, CancellationToken cancellationToken = default)
    {
        return (await dbContext.ManifestValidationResults
            .AsNoTracking()
            .Where(x => x.PackageVersionId == packageVersion.Id)
            .ToListAsync(cancellationToken))
            .OrderByDescending(x => x.ValidatedAt)
            .ToList();
    }

    public Task<VersionApprovalUpdateResult> TryUpdateVersionApprovalAsync(PackageVersion packageVersion, PackageApprovalStatus status, ApprovalRecord approvalRecord, CancellationToken cancellationToken = default) =>
        dbContext.ExecuteInTransactionAsync(IsolationLevel.Unspecified, async () =>
        {
            var updated = await dbContext.PackageVersions
                .Where(x =>
                    x.Id == packageVersion.Id &&
                    x.ApprovalStatus == packageVersion.ApprovalStatus &&
                    x.ValidationStatus == packageVersion.ValidationStatus &&
                    x.IsListed == packageVersion.IsListed &&
                    x.SuspiciousChangeDetected == packageVersion.SuspiciousChangeDetected &&
                    x.ManifestHash == packageVersion.ManifestHash &&
                    x.SuspiciousManifestHash == packageVersion.SuspiciousManifestHash)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ApprovalStatus, status), cancellationToken);

            if (updated == 0)
                return VersionApprovalUpdateResult.Conflict;

            await dbContext.ApprovalRecords.AddAsync(approvalRecord, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return VersionApprovalUpdateResult.Updated;
        }, cancellationToken);

    public async Task AddApprovalRecordAsync(ApprovalRecord record, CancellationToken cancellationToken = default) =>
        await dbContext.ApprovalRecords.AddAsync(record, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);

    private static async Task<Package?> FindPackageByIdAsync(IQueryable<Package> query, string packageId, CancellationToken cancellationToken)
    {
        var exactMatch = await query.SingleOrDefaultAsync(x => x.PackageId == packageId, cancellationToken);
        if (exactMatch is not null)
            return exactMatch;

        var normalizedPackageId = packageId.ToLowerInvariant();
        return await query.SingleOrDefaultAsync(x => x.PackageId.ToLower() == normalizedPackageId, cancellationToken);
    }
}
