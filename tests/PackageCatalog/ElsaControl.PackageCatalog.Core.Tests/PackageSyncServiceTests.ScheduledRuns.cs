using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Sync;
using ElsaControl.PackageCatalog.Testing;

namespace ElsaControl.PackageCatalog.Core.Tests;

public sealed partial class PackageSyncServiceTests
{
    public sealed class ScheduledRuns
    {
        private const string PackageId = "Elsa.Email";
        private const string StoredVersion = "1.0.0";
        private const string NewVersion = "2.0.0";
        private static readonly TimeSpan VerificationInterval = TimeSpan.FromHours(24);
        private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        private readonly PackageSource _source = PublicCatalogSeedData.CreatePackageSource();
        private readonly InMemorySyncRunStore _syncRuns = new();
        private readonly FakeDownloader _downloader = new("{}", new Dictionary<string, string>
        {
            [StoredVersion] = new ManifestFixtureBuilder().WithPackage(PackageId, StoredVersion).WithFeature("republished").BuildJson(),
            [NewVersion] = new ManifestFixtureBuilder().WithPackage(PackageId, NewVersion).WithFeature().BuildJson()
        });
        private readonly Package _package;
        private readonly PackageVersion _storedVersion;
        private readonly PackageSyncService _service;

        public ScheduledRuns()
        {
            _source.ApprovalPolicy = PackageSourceApprovalPolicy.AutoApprove;
            _package = PublicCatalogSeedData.CreatePackage(_source, PackageId);
            _storedVersion = PublicCatalogSeedData.AddVersion(_package, StoredVersion);
            _service = CreateService(
                new InMemorySourceStore([_source]),
                new InMemorySyncCatalogStore([_package]),
                _syncRuns,
                new FakeDiscovery([new(PackageId, StoredVersion), new(PackageId, NewVersion)]),
                _downloader,
                timeProvider: new FixedTimeProvider(Now));
        }

        [Fact]
        public async Task Regular_run_records_stored_version_as_unchanged_without_downloading_it()
        {
            SeedPriorRun(TimeSpan.FromHours(1));

            var run = await _service.SyncScheduledAsync(VerificationInterval);

            Assert.Equal(SyncRunTrigger.Scheduled, run.Trigger);
            Assert.Equal(SyncRunMode.NewVersionsOnly, run.Mode);
            Assert.DoesNotContain(StoredVersion, _downloader.DownloadedVersions);
            var item = Assert.Single(run.Items, x => x.Version == StoredVersion);
            Assert.Equal(SyncRunItemStatus.Unchanged, item.Status);
            Assert.Equal(_storedVersion.Id, item.PackageVersionId);
            Assert.Contains("not re-verified", item.Message);
            Assert.Contains("\"unchanged\":1", run.SummaryCountersJson);
            Assert.False(_storedVersion.SuspiciousChangeDetected);
        }

        [Fact]
        public async Task Regular_run_downloads_and_indexes_new_versions()
        {
            SeedPriorRun(TimeSpan.FromHours(1));

            var run = await _service.SyncScheduledAsync(VerificationInterval);

            Assert.Equal(SyncRunStatus.Completed, run.Status);
            Assert.Equal([NewVersion], _downloader.DownloadedVersions);
            Assert.Single(run.Items, x => x.Version == NewVersion && x.Status == SyncRunItemStatus.Indexed);
            Assert.Single(_package.Versions, x => x.Version == NewVersion && x.ValidationStatus == ValidationStatus.Valid);
            Assert.Equal(NewVersion, _package.LatestVersion);
        }

        [Fact]
        public async Task Verification_run_flags_a_republished_manifest_of_a_stored_version_as_suspicious()
        {
            SeedPriorRun(VerificationInterval + TimeSpan.FromHours(1));

            var run = await _service.SyncScheduledAsync(VerificationInterval);

            Assert.Equal(SyncRunMode.Verification, run.Mode);
            Assert.Equal([StoredVersion, NewVersion], _downloader.DownloadedVersions);
            Assert.Single(run.Items, x => x.Version == StoredVersion && x.Status == SyncRunItemStatus.Suspicious);
            Assert.True(_storedVersion.SuspiciousChangeDetected);
            Assert.Equal(ValidationStatus.Suspicious, _storedVersion.ValidationStatus);
        }

        [Fact]
        public async Task First_scheduled_run_is_a_verification_run()
        {
            var run = await _service.SyncScheduledAsync(VerificationInterval);

            Assert.Equal(SyncRunMode.Verification, run.Mode);
            Assert.Contains(StoredVersion, _downloader.DownloadedVersions);
        }

        [Theory]
        [InlineData(23, false)]
        [InlineData(24, true)]
        [InlineData(25, true)]
        [InlineData(-1, true)]
        public async Task Scheduled_run_becomes_a_verification_run_once_the_last_verification_is_an_interval_old(int hoursAgo, bool verifies)
        {
            SeedPriorRun(TimeSpan.FromHours(hoursAgo));

            var run = await _service.SyncScheduledAsync(VerificationInterval);

            AssertVerification(run, verifies);
        }

        [Theory]
        [InlineData(SyncRunTrigger.ManualAll, SyncRunMode.Verification, SyncRunStatus.Completed, true)]
        [InlineData(SyncRunTrigger.Scheduled, SyncRunMode.Verification, SyncRunStatus.CompletedWithErrors, true)]
        [InlineData(SyncRunTrigger.Scheduled, SyncRunMode.Verification, SyncRunStatus.Failed, false)]
        [InlineData(SyncRunTrigger.Scheduled, SyncRunMode.Verification, SyncRunStatus.Canceled, false)]
        [InlineData(SyncRunTrigger.Scheduled, SyncRunMode.Verification, SyncRunStatus.Running, false)]
        [InlineData(SyncRunTrigger.ManualSource, SyncRunMode.Verification, SyncRunStatus.Completed, false)]
        [InlineData(SyncRunTrigger.Scheduled, SyncRunMode.NewVersionsOnly, SyncRunStatus.Completed, false)]
        public async Task Only_a_finished_all_source_verification_run_postpones_the_next_verification(
            SyncRunTrigger trigger,
            SyncRunMode mode,
            SyncRunStatus status,
            bool postpones)
        {
            SeedPriorRun(TimeSpan.FromHours(1), trigger, mode, status);

            var run = await _service.SyncScheduledAsync(VerificationInterval);

            AssertVerification(run, verifies: !postpones);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Manual_sync_verifies_stored_versions_even_when_a_verification_is_current(bool singleSource)
        {
            SeedPriorRun(TimeSpan.FromHours(1));

            var start = singleSource ? await _service.StartManualSourceAsync(_source.Id) : await _service.StartManualAllAsync();
            await _service.ExecuteManualWorkItemAsync(start.WorkItem!);

            AssertVerification(start.Run, verifies: true);
            Assert.Single(start.Run.Items, x => x.Version == StoredVersion && x.Status == SyncRunItemStatus.Suspicious);
            Assert.True(_storedVersion.SuspiciousChangeDetected);
        }

        [Fact]
        public async Task Scheduled_run_is_rejected_while_a_manual_sync_is_active()
        {
            using var manual = (await _service.StartManualAllAsync()).WorkItem!;

            var run = await _service.SyncScheduledAsync(VerificationInterval);

            Assert.Equal(SyncRunStatus.Failed, run.Status);
            Assert.Contains("already active", run.Error);
            Assert.DoesNotContain(_syncRuns.Runs, x => x.Trigger == SyncRunTrigger.Scheduled);
            Assert.Empty(_downloader.DownloadedVersions);
        }

        private void SeedPriorRun(
            TimeSpan age,
            SyncRunTrigger trigger = SyncRunTrigger.Scheduled,
            SyncRunMode mode = SyncRunMode.Verification,
            SyncRunStatus status = SyncRunStatus.Completed) =>
            _syncRuns.Runs.Add(new SyncRun { Trigger = trigger, Mode = mode, Status = status, StartedAt = Now - age });

        private void AssertVerification(SyncRun run, bool verifies)
        {
            Assert.Equal(verifies ? SyncRunMode.Verification : SyncRunMode.NewVersionsOnly, run.Mode);
            Assert.Equal(verifies, _downloader.DownloadedVersions.Contains(StoredVersion));
        }

        private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
