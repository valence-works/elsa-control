using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Sync;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class SyncRunStore(CatalogDbContext dbContext) : ISyncRunStore
{
    public async Task<IReadOnlyList<SyncRun>> ListAsync(CancellationToken cancellationToken = default) =>
        await dbContext.SyncRuns
            .AsNoTracking()
            .OrderByDescending(x => x.StartedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

    public Task<SyncRun?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.SyncRuns
            .Include(x => x.Items)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<SyncRun?> GetLatestRunAsync(SyncRunMode mode, IReadOnlyCollection<SyncRunTrigger> triggers, IReadOnlyCollection<SyncRunStatus> statuses, CancellationToken cancellationToken = default)
    {
        var triggerValues = triggers.ToArray();
        var statusValues = statuses.ToArray();
        return dbContext.SyncRuns
            .AsNoTracking()
            .Where(x => x.Mode == mode && triggerValues.Contains(x.Trigger) && statusValues.Contains(x.Status))
            .OrderByDescending(x => x.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<SyncRunPackageVersionKey>> GetVersionsFoundWithoutManifestAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        // One seek on the SyncRunId index for the whole run; an Invalid item stores a package version unless the archive had no manifest.
        var versions = await dbContext.SyncRunItems
            .AsNoTracking()
            .Where(x => x.SyncRunId == runId &&
                        x.Status == SyncRunItemStatus.Invalid &&
                        x.PackageVersionId == null &&
                        x.SourceId != null &&
                        x.PackageId != null &&
                        x.Version != null)
            .Select(x => new SyncRunPackageVersionKey(x.SourceId!.Value, x.PackageId!, x.Version!))
            .ToListAsync(cancellationToken);

        return versions.ToHashSet();
    }

    public async Task<IReadOnlyDictionary<Guid, SyncRunListMetadata>> GetListMetadataAsync(IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken = default)
    {
        if (runIds.Count == 0)
            return new Dictionary<Guid, SyncRunListMetadata>();

        var itemSources = await dbContext.SyncRunItems
            .AsNoTracking()
            .Where(x => runIds.Contains(x.SyncRunId))
            .Select(x => new { x.SyncRunId, x.SourceId })
            .ToListAsync(cancellationToken);

        var sourceIds = itemSources
            .Where(x => x.SourceId.HasValue)
            .Select(x => x.SourceId!.Value)
            .Distinct()
            .ToList();

        var sourceNames = await dbContext.PackageSources
            .AsNoTracking()
            .Where(x => sourceIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        return itemSources
            .GroupBy(x => x.SyncRunId)
            .ToDictionary(
                x => x.Key,
                x => new SyncRunListMetadata(
                    x.Count(),
                    x.Where(item => item.SourceId.HasValue)
                        .Select(item => item.SourceId!.Value)
                        .Distinct()
                        .Select(sourceId => new SyncRunSourceReference(sourceId, sourceNames.GetValueOrDefault(sourceId)))
                        .OrderBy(source => source.Name ?? source.Id.ToString())
                        .ToList()));
    }

    public async Task<SyncRunDeletionCandidate?> GetDeletionCandidateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var run = await dbContext.SyncRuns
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.Status,
                ItemCount = x.Items.Count
            })
            .SingleOrDefaultAsync(cancellationToken);

        return run is null
            ? null
            : new SyncRunDeletionCandidate(run.Id, run.Status, run.ItemCount);
    }

    public async Task<SyncRunCleanupPreview> PreviewDeleteBeforeAsync(DateTimeOffset completedBefore, IReadOnlyCollection<SyncRunStatus> terminalStatuses, CancellationToken cancellationToken = default)
    {
        var terminalStatusValues = terminalStatuses.ToArray();
        var eligibleRuns = await EligibleRuns(completedBefore, terminalStatusValues)
            .Select(x => new { x.CompletedAt })
            .ToListAsync(cancellationToken);
        var eligibleItemCount = eligibleRuns.Count == 0
            ? 0
            : await dbContext.SyncRunItems
                .AsNoTracking()
                .CountAsync(x => EligibleRunIds(completedBefore, terminalStatusValues).Contains(x.SyncRunId), cancellationToken);

        var excludedRunCount = await CountProtectedRunsAsync(completedBefore, terminalStatusValues, cancellationToken);

        var completedAtValues = eligibleRuns
            .Select(x => x.CompletedAt)
            .OfType<DateTimeOffset>()
            .ToList();

        return new SyncRunCleanupPreview(
            completedBefore,
            eligibleRuns.Count,
            eligibleItemCount,
            excludedRunCount,
            completedAtValues.Count == 0 ? null : completedAtValues.Min(),
            completedAtValues.Count == 0 ? null : completedAtValues.Max());
    }

    public async Task<SyncRunCleanupResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var itemCount = await dbContext.SyncRunItems
            .AsNoTracking()
            .CountAsync(x => x.SyncRunId == id, cancellationToken);

        var run = await dbContext.SyncRuns
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (run is null)
            return new SyncRunCleanupResult(0, 0, 0, 1, null, []);

        dbContext.SyncRuns.Remove(run);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new SyncRunCleanupResult(1, itemCount, 0, 0, null, [id]);
    }

    public async Task<SyncRunCleanupResult> DeleteBeforeAsync(DateTimeOffset completedBefore, IReadOnlyCollection<SyncRunStatus> terminalStatuses, CancellationToken cancellationToken = default)
    {
        var terminalStatusValues = terminalStatuses.ToArray();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var deletedRunIds = await EligibleRuns(completedBefore, terminalStatusValues)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var excludedRunCount = await CountProtectedRunsAsync(completedBefore, terminalStatusValues, cancellationToken);
        var deletedItemCount = deletedRunIds.Count == 0
            ? 0
            : await dbContext.SyncRunItems
                .AsNoTracking()
                .CountAsync(x => EligibleRunIds(completedBefore, terminalStatusValues).Contains(x.SyncRunId), cancellationToken);

        if (deletedRunIds.Count > 0)
            await EligibleRuns(completedBefore, terminalStatusValues).ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new SyncRunCleanupResult(deletedRunIds.Count, deletedItemCount, excludedRunCount, 0, completedBefore, deletedRunIds);
    }

    public async Task AddAsync(SyncRun run, CancellationToken cancellationToken = default) =>
        await dbContext.SyncRuns.AddAsync(run, cancellationToken);

    public async Task AddItemAsync(SyncRunItem item, CancellationToken cancellationToken = default) =>
        await dbContext.SyncRunItems.AddAsync(item, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);

    private IQueryable<SyncRun> EligibleRuns(DateTimeOffset completedBefore, SyncRunStatus[] terminalStatusValues) =>
        dbContext.SyncRuns
            .AsNoTracking()
            .Where(x => x.CompletedAt.HasValue && x.CompletedAt < completedBefore && terminalStatusValues.Contains(x.Status));

    private IQueryable<Guid> EligibleRunIds(DateTimeOffset completedBefore, SyncRunStatus[] terminalStatusValues) =>
        EligibleRuns(completedBefore, terminalStatusValues)
            .Select(x => x.Id);

    private Task<int> CountProtectedRunsAsync(DateTimeOffset completedBefore, SyncRunStatus[] terminalStatusValues, CancellationToken cancellationToken) =>
        dbContext.SyncRuns
            .AsNoTracking()
            .CountAsync(
                x => !terminalStatusValues.Contains(x.Status)
                    && ((x.CompletedAt.HasValue && x.CompletedAt < completedBefore)
                        || (!x.CompletedAt.HasValue && x.StartedAt < completedBefore)),
                cancellationToken);
}
