using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElsaControl.PackageCatalog.Core.Tests;

public sealed class SyncRunCleanupServiceTests
{
    [Fact]
    public async Task Deletes_single_terminal_run_and_reports_item_count()
    {
        var run = CompletedRun(items: 2);
        var store = new InMemorySyncRunStore([run]);
        var service = CreateService(store);

        var result = await service.DeleteAsync(run.Id, "tester");

        Assert.False(result.IsConflict);
        Assert.Equal(1, result.Cleanup!.DeletedRunCount);
        Assert.Equal(2, result.Cleanup.DeletedItemCount);
        Assert.Equal(run.Id, Assert.Single(result.Cleanup.DeletedRunIds));
        Assert.Empty(store.Runs);
    }

    [Fact]
    public async Task Missing_single_run_delete_is_idempotent_no_match()
    {
        var service = CreateService(new InMemorySyncRunStore());

        var result = await service.DeleteAsync(Guid.NewGuid());

        Assert.False(result.IsConflict);
        Assert.Equal(1, result.Cleanup!.NotFoundRunCount);
        Assert.Equal(0, result.Cleanup.DeletedRunCount);
    }

    [Fact]
    public async Task Refuses_single_running_run_delete()
    {
        var run = new SyncRun { Status = SyncRunStatus.Running, Trigger = SyncRunTrigger.ManualAll };
        var store = new InMemorySyncRunStore([run]);
        var service = CreateService(store);

        var result = await service.DeleteAsync(run.Id);

        Assert.True(result.IsConflict);
        Assert.Equal(SyncRunStatus.Running, result.NonTerminalStatus);
        Assert.Single(store.Runs);
    }

    [Fact]
    public async Task Previews_and_deletes_bulk_terminal_runs_before_cutoff()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        var oldCompleted = CompletedRun(completedAt: cutoff.AddDays(-1), items: 2);
        var oldFailed = CompletedRun(SyncRunStatus.Failed, cutoff.AddDays(-2), items: 1);
        var recent = CompletedRun(completedAt: cutoff.AddDays(1));
        var running = new SyncRun { Status = SyncRunStatus.Running, Trigger = SyncRunTrigger.ManualAll, StartedAt = cutoff.AddDays(-3) };
        var store = new InMemorySyncRunStore([oldCompleted, oldFailed, recent, running]);
        var service = CreateService(store);

        var preview = await service.PreviewDeleteBeforeAsync(cutoff);
        var result = await service.DeleteBeforeAsync(cutoff, "tester");

        Assert.True(preview.IsValid);
        Assert.Equal(2, preview.Preview!.EligibleRunCount);
        Assert.Equal(3, preview.Preview.EligibleItemCount);
        Assert.Equal(1, preview.Preview.ExcludedRunCount);
        Assert.True(result.IsValid);
        Assert.Equal(2, result.Cleanup!.DeletedRunCount);
        Assert.Equal(3, result.Cleanup.DeletedItemCount);
        Assert.Equal(1, result.Cleanup.ExcludedRunCount);
        Assert.Equal(new[] { recent.Id, running.Id }.Order(), store.Runs.Select(x => x.Id).Order());
    }

    [Fact]
    public async Task Rejects_future_bulk_cutoff()
    {
        var store = new InMemorySyncRunStore([CompletedRun()]);
        var service = CreateService(store);

        var preview = await service.PreviewDeleteBeforeAsync(DateTimeOffset.UtcNow.AddMinutes(1));
        var result = await service.DeleteBeforeAsync(DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.False(preview.IsValid);
        Assert.False(result.IsValid);
        Assert.Single(store.Runs);
    }

    private static SyncRunCleanupService CreateService(InMemorySyncRunStore store) =>
        new(store, NullLogger<SyncRunCleanupService>.Instance);

    private static SyncRun CompletedRun(SyncRunStatus status = SyncRunStatus.Completed, DateTimeOffset? completedAt = null, int items = 0)
    {
        var run = new SyncRun
        {
            Status = status,
            Trigger = SyncRunTrigger.ManualAll,
            StartedAt = (completedAt ?? DateTimeOffset.UtcNow).AddMinutes(-2),
            CompletedAt = completedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        for (var i = 0; i < items; i++)
            run.Items.Add(new SyncRunItem { SyncRun = run, SyncRunId = run.Id, Status = SyncRunItemStatus.Indexed });

        return run;
    }

    private sealed class InMemorySyncRunStore(IReadOnlyList<SyncRun>? initialRuns = null) : ISyncRunStore
    {
        public List<SyncRun> Runs { get; } = initialRuns?.ToList() ?? [];

        public Task<IReadOnlyList<SyncRun>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SyncRun>>(Runs);

        public Task<SyncRun?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Runs.SingleOrDefault(x => x.Id == id));

        public Task<SyncRun?> GetLatestRunAsync(SyncRunMode mode, IReadOnlyCollection<SyncRunTrigger> triggers, IReadOnlyCollection<SyncRunStatus> statuses, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<SyncRunPackageVersionKey>> GetVersionsFoundWithoutManifestAsync(Guid runId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, SyncRunListMetadata>> GetListMetadataAsync(IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, SyncRunListMetadata>>(new Dictionary<Guid, SyncRunListMetadata>());

        public Task<SyncRunDeletionCandidate?> GetDeletionCandidateAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var run = Runs.SingleOrDefault(x => x.Id == id);
            return Task.FromResult(run is null ? null : new SyncRunDeletionCandidate(run.Id, run.Status, run.Items.Count));
        }

        public Task<SyncRunCleanupPreview> PreviewDeleteBeforeAsync(DateTimeOffset completedBefore, IReadOnlyCollection<SyncRunStatus> terminalStatuses, CancellationToken cancellationToken = default)
        {
            var eligible = EligibleRuns(completedBefore, terminalStatuses).ToList();
            var completedAtValues = eligible.Select(x => x.CompletedAt).OfType<DateTimeOffset>().ToList();
            return Task.FromResult(new SyncRunCleanupPreview(
                completedBefore,
                eligible.Count,
                eligible.Sum(x => x.Items.Count),
                ProtectedRuns(completedBefore, terminalStatuses).Count(),
                completedAtValues.Count == 0 ? null : completedAtValues.Min(),
                completedAtValues.Count == 0 ? null : completedAtValues.Max()));
        }

        public Task<SyncRunCleanupResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var run = Runs.SingleOrDefault(x => x.Id == id);
            if (run is null)
                return Task.FromResult(new SyncRunCleanupResult(0, 0, 0, 1, null, []));

            Runs.Remove(run);
            return Task.FromResult(new SyncRunCleanupResult(1, run.Items.Count, 0, 0, null, [id]));
        }

        public Task<SyncRunCleanupResult> DeleteBeforeAsync(DateTimeOffset completedBefore, IReadOnlyCollection<SyncRunStatus> terminalStatuses, CancellationToken cancellationToken = default)
        {
            var eligible = EligibleRuns(completedBefore, terminalStatuses).ToList();
            foreach (var run in eligible)
                Runs.Remove(run);

            return Task.FromResult(new SyncRunCleanupResult(
                eligible.Count,
                eligible.Sum(x => x.Items.Count),
                ProtectedRuns(completedBefore, terminalStatuses).Count(),
                0,
                completedBefore,
                eligible.Select(x => x.Id).ToList()));
        }

        public Task AddAsync(SyncRun run, CancellationToken cancellationToken = default)
        {
            Runs.Add(run);
            return Task.CompletedTask;
        }

        public Task AddItemAsync(SyncRunItem item, CancellationToken cancellationToken = default)
        {
            var run = Runs.Single(x => x.Id == item.SyncRunId);
            run.Items.Add(item);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        private IEnumerable<SyncRun> EligibleRuns(DateTimeOffset completedBefore, IReadOnlyCollection<SyncRunStatus> terminalStatuses) =>
            Runs.Where(x => x.CompletedAt.HasValue && x.CompletedAt < completedBefore && terminalStatuses.Contains(x.Status));

        private IEnumerable<SyncRun> ProtectedRuns(DateTimeOffset completedBefore, IReadOnlyCollection<SyncRunStatus> terminalStatuses) =>
            Runs.Where(x =>
                !terminalStatuses.Contains(x.Status)
                && ((x.CompletedAt.HasValue && x.CompletedAt < completedBefore)
                    || (!x.CompletedAt.HasValue && x.StartedAt < completedBefore)));
    }
}
