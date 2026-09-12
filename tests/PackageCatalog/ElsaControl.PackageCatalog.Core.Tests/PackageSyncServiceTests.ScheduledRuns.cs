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
        private const string ArchiveWithoutManifest = ""; // FakeManifestReader finds no elsa-package.json in an empty archive.
        private const string NotRereadMessage = "Previously found without elsa-package.json; not re-read in this run.";
        private static readonly TimeSpan VerificationInterval = TimeSpan.FromHours(24);
        private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        private readonly PackageSource _source = PublicCatalogSeedData.CreatePackageSource();
        private readonly InMemorySyncRunStore _syncRuns = new();
        private readonly Dictionary<string, string> _archives = new()
        {
            [StoredVersion] = new ManifestFixtureBuilder().WithPackage(PackageId, StoredVersion).WithFeature("republished").BuildJson(),
            [NewVersion] = new ManifestFixtureBuilder().WithPackage(PackageId, NewVersion).WithFeature().BuildJson()
        };
        private readonly FakeDownloader _downloader;
        private readonly Package _package;
        private readonly PackageVersion _storedVersion;
        private readonly PackageSyncService _service;

        public ScheduledRuns()
        {
            _source.ApprovalPolicy = PackageSourceApprovalPolicy.AutoApprove;
            _downloader = new FakeDownloader("{}", _archives);
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
        public async Task Regular_run_records_a_version_the_last_verification_found_without_manifest_as_invalid_without_downloading_it()
        {
            _archives[NewVersion] = ArchiveWithoutManifest;
            var verification = await _service.SyncScheduledAsync(VerificationInterval);
            _downloader.DownloadedVersions.Clear();

            var run = await _service.SyncScheduledAsync(VerificationInterval);

            Assert.Equal(SyncRunMode.Verification, verification.Mode);
            Assert.Single(verification.Items, x => x.Version == NewVersion && x.Status == SyncRunItemStatus.Invalid);
            Assert.Equal(SyncRunMode.NewVersionsOnly, run.Mode);
            Assert.Empty(_downloader.DownloadedVersions);
            var item = Assert.Single(run.Items, x => x.Version == NewVersion);
            Assert.Equal(SyncRunItemStatus.Invalid, item.Status);
            Assert.Equal(NotRereadMessage, item.Message);
            Assert.Null(item.PackageVersionId);
            Assert.Contains("\"invalid\":1", run.SummaryCountersJson);
            Assert.Equal(SyncRunStatus.Completed, run.Status);
        }

        [Theory]
        [InlineData(SyncRunItemStatus.Failed, false)]
        [InlineData(SyncRunItemStatus.Invalid, true)]
        public async Task Regular_run_reads_a_version_the_last_verification_did_not_find_without_manifest_at_its_source(SyncRunItemStatus recorded, bool atAnotherSource)
        {
            SeedItem(SeedPriorRun(TimeSpan.FromHours(1)), NewVersion, recorded, atAnotherSource ? Guid.NewGuid() : _source.Id);

            var run = await _service.SyncScheduledAsync(VerificationInterval);

            Assert.Equal(SyncRunMode.NewVersionsOnly, run.Mode);
            Assert.Equal([NewVersion], _downloader.DownloadedVersions);
            Assert.Single(run.Items, x => x.Version == NewVersion && x.Status == SyncRunItemStatus.Indexed);
        }

        [Theory]
        [InlineData(SyncRunTrigger.Scheduled, SyncRunMode.NewVersionsOnly, SyncRunStatus.Completed, 1)]
        [InlineData(SyncRunTrigger.ManualSource, SyncRunMode.Verification, SyncRunStatus.Completed, 1)]
        [InlineData(SyncRunTrigger.ManualAll, SyncRunMode.Verification, SyncRunStatus.Failed, 1)]
        [InlineData(SyncRunTrigger.ManualAll, SyncRunMode.Verification, SyncRunStatus.Running, 1)]
        [InlineData(SyncRunTrigger.Scheduled, SyncRunMode.Verification, SyncRunStatus.Completed, 3)]
        public async Task Regular_run_ignores_versions_found_without_manifest_by_any_run_but_the_last_verification(
            SyncRunTrigger trigger,
            SyncRunMode mode,
            SyncRunStatus status,
            int hoursAgo)
        {
            SeedPriorRun(TimeSpan.FromHours(2));
            SeedItem(SeedPriorRun(TimeSpan.FromHours(hoursAgo), trigger, mode, status), NewVersion, SyncRunItemStatus.Invalid);

            var run = await _service.SyncScheduledAsync(VerificationInterval);

            Assert.Equal(SyncRunMode.NewVersionsOnly, run.Mode);
            Assert.Equal([NewVersion], _downloader.DownloadedVersions);
        }

        [Fact]
        public async Task Regular_run_records_a_version_indexed_since_it_was_found_without_manifest_as_unchanged()
        {
            SeedItem(SeedPriorRun(TimeSpan.FromHours(1)), StoredVersion, SyncRunItemStatus.Invalid);

            var run = await _service.SyncScheduledAsync(VerificationInterval);

            Assert.DoesNotContain(StoredVersion, _downloader.DownloadedVersions);
            Assert.Single(run.Items, x => x.Version == StoredVersion && x.Status == SyncRunItemStatus.Unchanged);
        }

        [Fact]
        public async Task Verification_run_reads_a_version_found_without_manifest_again_and_indexes_the_manifest_it_has_now()
        {
            SeedItem(SeedPriorRun(VerificationInterval + TimeSpan.FromHours(1)), NewVersion, SyncRunItemStatus.Invalid);

            var run = await _service.SyncScheduledAsync(VerificationInterval);

            Assert.Equal(SyncRunMode.Verification, run.Mode);
            Assert.Contains(NewVersion, _downloader.DownloadedVersions);
            Assert.Single(run.Items, x => x.Version == NewVersion && x.Status == SyncRunItemStatus.Indexed);
            Assert.Single(_package.Versions, x => x.Version == NewVersion && x.ValidationStatus == ValidationStatus.Valid);
        }

        [Fact]
        public async Task Run_left_running_by_a_host_restart_neither_blocks_nor_replaces_the_last_verification()
        {
            SeedItem(SeedPriorRun(TimeSpan.FromHours(2)), NewVersion, SyncRunItemStatus.Invalid);
            SeedPriorRun(TimeSpan.FromHours(1), SyncRunTrigger.ManualAll, status: SyncRunStatus.Running);

            var run = await _service.SyncScheduledAsync(VerificationInterval);

            Assert.Equal(SyncRunStatus.Completed, run.Status);
            Assert.Equal(SyncRunMode.NewVersionsOnly, run.Mode);
            Assert.Empty(_downloader.DownloadedVersions);
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
        public async Task Manual_sync_reads_every_version_even_when_a_verification_is_current(bool singleSource)
        {
            SeedItem(SeedPriorRun(TimeSpan.FromHours(1)), NewVersion, SyncRunItemStatus.Invalid);

            var start = singleSource ? await _service.StartManualSourceAsync(_source.Id) : await _service.StartManualAllAsync();
            await _service.ExecuteManualWorkItemAsync(start.WorkItem!);

            AssertVerification(start.Run, verifies: true);
            Assert.Single(start.Run.Items, x => x.Version == StoredVersion && x.Status == SyncRunItemStatus.Suspicious);
            Assert.True(_storedVersion.SuspiciousChangeDetected);
            Assert.Single(start.Run.Items, x => x.Version == NewVersion && x.Status == SyncRunItemStatus.Indexed);
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

        private SyncRun SeedPriorRun(
            TimeSpan age,
            SyncRunTrigger trigger = SyncRunTrigger.Scheduled,
            SyncRunMode mode = SyncRunMode.Verification,
            SyncRunStatus status = SyncRunStatus.Completed)
        {
            var run = new SyncRun { Trigger = trigger, Mode = mode, Status = status, StartedAt = Now - age };
            _syncRuns.Runs.Add(run);
            return run;
        }

        /// <summary>Records an outcome the way a run records it; an Invalid outcome is one without elsa-package.json, so it stores no version.</summary>
        private void SeedItem(SyncRun run, string version, SyncRunItemStatus status, Guid? sourceId = null)
        {
            var item = new SyncRunItem { SyncRun = run, SyncRunId = run.Id, SourceId = sourceId ?? _source.Id, PackageId = PackageId, Version = version, Status = status };
            run.Items.Add(item);
            _syncRuns.Items.Add(item);
        }

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
