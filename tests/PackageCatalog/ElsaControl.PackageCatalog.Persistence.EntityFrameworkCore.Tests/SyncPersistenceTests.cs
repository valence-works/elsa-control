using System.Data.Common;
using ElsaControl.PackageCatalog.Abstractions.Catalog;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Core.Manifests;
using ElsaControl.PackageCatalog.Core.Packaging;
using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Sources;
using ElsaControl.PackageCatalog.Core.Sync;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Testing;
using Elsa.Specifications.PackageManifests.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class SyncPersistenceTests
{
    [Fact]
    public async Task Persists_sync_run_items_for_diagnostics()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite("Data Source=:memory:", sqlite =>
            {
                sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly);
            })
            .Options;

        await using var db = new CatalogDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();

        var run = new SyncRun { Trigger = SyncRunTrigger.ManualAll };
        run.Items.Add(new SyncRunItem { SyncRun = run, SyncRunId = run.Id, PackageId = "Elsa.Email", Version = "1.0.0", Status = SyncRunItemStatus.Failed, Error = "No manifest" });
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync();

        var stored = await db.SyncRuns.Include(x => x.Items).SingleAsync();

        Assert.Single(stored.Items, x => x.Error == "No manifest");
    }

    [Fact]
    public async Task Initial_migration_creates_catalog_tables()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite("Data Source=:memory:", sqlite =>
            {
                sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly);
            })
            .Options;

        await using var db = new CatalogDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();

        db.PackageSources.Add(PublicCatalogSeedData.CreatePackageSource());
        await db.SaveChangesAsync();

        Assert.Equal(1, (await db.PackageSources.CountAsync()));
    }

    [Fact]
    public async Task Lists_most_recent_sync_runs_before_limiting()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite("Data Source=:memory:")
            .Options;

        await using var db = new CatalogDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();

        var oldest = DateTimeOffset.UtcNow.AddDays(-101);
        for (var i = 0; i < 101; i++)
            db.SyncRuns.Add(new SyncRun { Trigger = SyncRunTrigger.Scheduled, StartedAt = oldest.AddMinutes(i) });

        var newest = new SyncRun { Trigger = SyncRunTrigger.ManualAll, StartedAt = DateTimeOffset.UtcNow.AddDays(1) };
        db.SyncRuns.Add(newest);
        await db.SaveChangesAsync();

        var runs = await new SyncRunStore(db).ListAsync();

        Assert.Equal(100, runs.Count());
        Assert.Equal(newest.Id, runs[0].Id);
        Assert.DoesNotContain(runs, x => x.StartedAt == oldest);
    }

    [Fact]
    public async Task Sync_service_persists_new_run_items_without_concurrency_failure()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite("Data Source=:memory:")
            .Options;

        await using var db = new CatalogDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();

        var source = PublicCatalogSeedData.CreatePackageSource();
        source.ApprovalPolicy = PackageSourceApprovalPolicy.AutoApprove;
        db.PackageSources.Add(source);
        await db.SaveChangesAsync();

        var manifestJson = new ManifestFixtureBuilder()
            .WithPackage("Elsa.Email", "1.0.0")
            .WithFeature()
            .BuildJson();
        var service = new PackageSyncService(
            new PackageSourceStore(db),
            new SyncCatalogStore(db),
            new SyncRunStore(db),
            new FakeDiscovery([new DiscoveredPackageVersion("Elsa.Email", "1.0.0")]),
            new FakeDownloader(manifestJson),
            new FakeManifestReader(),
            new ManifestValidator(),
            new ManifestIngestionService(),
            new PackageVersionPolicy(),
            new NoopSyncDiagnostics(),
            new SyncConcurrencyGuard(),
            new SourceSyncActivityTracker(),
            new SyncRunCancellationRegistry());

        var run = await service.SyncAllAsync();

        Assert.Equal(SyncRunStatus.Completed, run.Status);
        Assert.Equal(1, (await db.SyncRunItems.CountAsync()));
    }

    [Fact]
    public async Task Public_catalog_projects_runtime_kinds_from_stored_manifest_json()
    {
        await using var db = await CreateOpenDbContextAsync();
        var source = PublicCatalogSeedData.CreatePackageSource();
        var package = PublicCatalogSeedData.CreatePackage(source, "Elsa.Mixed");
        var version = PublicCatalogSeedData.AddVersion(package);
        version.ManifestHash = new string('A', 64);
        version.ManifestJson = new ManifestFixtureBuilder()
            .WithPackage("Elsa.Mixed", "1.0.0")
            .WithRuntimeKinds("elsa.server", "acme.custom-host")
            .WithFeature("server", "Elsa.Mixed.ServerFeature", null)
            .WithFeature("studio", "Elsa.Mixed.StudioFeature", ["elsa.studio"])
            .BuildJson();
        PublicCatalogSeedData.AddFeature(version, "server", "Server");
        PublicCatalogSeedData.AddFeature(version, "studio", "Studio");
        db.PackageSources.Add(source);
        await db.SaveChangesAsync();

        var packages = await new PublicCatalogQueries(db).ListPackagesAsync([]);

        var projectedVersion = Assert.Single(Assert.Single(packages).Versions);
        Assert.Equal(new[] { "elsa.server", "acme.custom-host" }.Order(), projectedVersion.RuntimeKinds.Order());
        Assert.Equal($"sha256:{new string('a', 64)}", projectedVersion.ManifestDigest);

        Assert.Equal(new[] { "elsa.server", "acme.custom-host" }.Order(), projectedVersion.Features.Single(x => x.FeatureId == "server").RuntimeKinds.Order());

        Assert.Equal("elsa.studio", Assert.Single(projectedVersion.Features.Single(x => x.FeatureId == "studio").RuntimeKinds));
    }

    [Fact]
    public async Task Public_catalog_derives_extension_class_and_evidence_from_source_authority()
    {
        await using var db = await CreateOpenDbContextAsync();
        var organization = new Organization { Name = "Customer organization" };
        var workspace = new Workspace { Name = "Customer workspace", Organization = organization };
        var governedSource = PublicCatalogSeedData.CreatePackageSource();
        var governedPackage = PublicCatalogSeedData.CreatePackage(governedSource, "Elsa.Governed");
        var governedVersion = PublicCatalogSeedData.AddVersion(governedPackage);
        governedVersion.ManifestHash = new string('a', 64);
        var workspaceSource = PublicCatalogSeedData.CreatePackageSource();
        workspaceSource.Visibility = PackageSourceVisibility.Workspace;
        workspaceSource.OwnerWorkspaceId = workspace.Id;
        workspaceSource.OwnerWorkspace = workspace;
        var workspacePackage = PublicCatalogSeedData.CreatePackage(workspaceSource, "Customer.Arbitrary");
        var workspaceVersion = PublicCatalogSeedData.AddVersion(workspacePackage);
        workspaceVersion.ManifestHash = new string('b', 64);
        db.AddRange(organization, workspace, governedSource, workspaceSource);
        await db.SaveChangesAsync();

        var queries = new PublicCatalogQueries(db);
        var governed = await queries.GetVersionForWorkspaceAsync(
            workspace.Id,
            governedSource.Id,
            governedPackage.PackageId,
            governedVersion.Version);
        var arbitrary = await queries.GetVersionForWorkspaceAsync(
            workspace.Id,
            workspaceSource.Id,
            workspacePackage.PackageId,
            workspaceVersion.Version);
        var governedAgain = await queries.GetVersionForWorkspaceAsync(
            workspace.Id,
            governedSource.Id,
            governedPackage.PackageId,
            governedVersion.Version);

        Assert.Equal(PackageExtensionClass.ValenceApproved, Assert.IsType<PublicPackageVersionProjection>(governed).ExtensionClass);
        Assert.Equal(PackageExtensionClass.ArbitraryCustomer, Assert.IsType<PublicPackageVersionProjection>(arbitrary).ExtensionClass);
        Assert.StartsWith("sha256:", governed.PolicyEvidenceDigest, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", arbitrary.PolicyEvidenceDigest, StringComparison.Ordinal);
        Assert.Equal(governed.PolicyEvidenceDigest, Assert.IsType<PublicPackageVersionProjection>(governedAgain).PolicyEvidenceDigest);
        Assert.NotEqual(governed.PolicyEvidenceDigest, arbitrary.PolicyEvidenceDigest);
    }

    [Fact]
    public async Task Public_catalog_withholds_unapproved_invalid_or_suspicious_versions_from_admission()
    {
        await using var db = await CreateOpenDbContextAsync();
        var source = PublicCatalogSeedData.CreatePackageSource();
        var package = PublicCatalogSeedData.CreatePackage(source, "Elsa.Governed");
        PublicCatalogSeedData.AddVersion(package, "1.0.0", manifestHash: new string('a', 64));
        PublicCatalogSeedData.AddVersion(
            package, "1.0.1", approvalStatus: PackageApprovalStatus.Pending, manifestHash: new string('b', 64));
        PublicCatalogSeedData.AddVersion(
            package, "1.0.2", validationStatus: ValidationStatus.Invalid, manifestHash: new string('c', 64));
        PublicCatalogSeedData.AddVersion(
            package, "1.0.3", suspicious: true, manifestHash: new string('d', 64));
        db.Add(source);
        await db.SaveChangesAsync();

        var versions = await new PublicCatalogQueries(db).ListVersionsAsync(source.Id, package.PackageId);

        var admitted = Assert.Single(versions);
        Assert.Equal("1.0.0", admitted.Version);
        Assert.Equal(PackageExtensionClass.ValenceApproved, admitted.ExtensionClass);
    }

    [Fact]
    public async Task Public_catalog_listing_does_not_depend_on_owner_workspace_join()
    {
        var commandRecorder = new CommandRecorder();
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite("Data Source=:memory:")
            .AddInterceptors(commandRecorder)
            .Options;

        await using var db = new CatalogDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();

        var source = PublicCatalogSeedData.CreatePackageSource();
        var package = PublicCatalogSeedData.CreatePackage(source, "Elsa.Public");
        PublicCatalogSeedData.AddVersion(package);
        db.PackageSources.Add(source);
        await db.SaveChangesAsync();
        commandRecorder.Commands.Clear();

        var packages = await new PublicCatalogQueries(db).ListPackagesAsync([]);

        Assert.Single(packages);
        var catalogQuery = Assert.Single(
            commandRecorder.Commands,
            command => command.Contains("FROM \"Packages\"", StringComparison.Ordinal));
        Assert.DoesNotContain("Workspaces", catalogQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Workspace_catalog_hides_sources_owned_by_soft_deleted_workspaces()
    {
        await using var db = await CreateOpenDbContextAsync();
        var organization = new Organization { Name = "Deleted organization" };
        var workspace = new Workspace
        {
            Name = "Deleted workspace",
            Organization = organization,
            SoftDeletedAt = DateTimeOffset.UtcNow
        };
        var source = PublicCatalogSeedData.CreatePackageSource();
        source.Visibility = PackageSourceVisibility.Workspace;
        source.OwnerWorkspaceId = workspace.Id;
        source.OwnerWorkspace = workspace;
        var package = PublicCatalogSeedData.CreatePackage(source, "Elsa.Private");
        var version = PublicCatalogSeedData.AddVersion(package);
        db.AddRange(organization, workspace, source);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var projection = await new PublicCatalogQueries(db).GetVersionForWorkspaceAsync(
            workspace.Id,
            source.Id,
            package.PackageId,
            version.Version);

        Assert.Null(projection);
    }

    [Fact]
    public async Task Bulk_sync_persists_source_last_synced_timestamp()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite("Data Source=:memory:")
            .Options;

        await using var db = new CatalogDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();

        var source = PublicCatalogSeedData.CreatePackageSource();
        db.PackageSources.Add(source);
        await db.SaveChangesAsync();

        var service = new PackageSyncService(
            new PackageSourceStore(db),
            new SyncCatalogStore(db),
            new SyncRunStore(db),
            new FakeDiscovery([]),
            new FakeDownloader("{}"),
            new FakeManifestReader(),
            new ManifestValidator(),
            new ManifestIngestionService(),
            new PackageVersionPolicy(),
            new NoopSyncDiagnostics(),
            new SyncConcurrencyGuard(),
            new SourceSyncActivityTracker(),
            new SyncRunCancellationRegistry());

        await service.SyncAllAsync();

        Assert.NotNull((await db.PackageSources.SingleAsync()).LastSyncedAt);
    }

    [Fact]
    public async Task Delete_sync_run_removes_items_and_preserves_catalog_state()
    {
        await using var db = await CreateOpenDbContextAsync();
        var source = PublicCatalogSeedData.CreatePackageSource();
        var package = PublicCatalogSeedData.CreatePackage(source);
        var version = PublicCatalogSeedData.AddVersion(package);
        var validation = new ManifestValidationResultRecord
        {
            PackageVersion = version,
            PackageVersionId = version.Id,
            Status = ValidationStatus.Valid
        };
        var approval = new ApprovalRecord
        {
            TargetType = ApprovalTargetType.PackageVersion,
            TargetId = version.Id,
            Status = PackageApprovalStatus.Approved,
            Actor = "tester"
        };
        var run = CompletedRun(DateTimeOffset.UtcNow.AddDays(-1));
        run.Items.Add(new SyncRunItem
        {
            SyncRun = run,
            SyncRunId = run.Id,
            PackageVersion = version,
            PackageVersionId = version.Id,
            PackageId = package.PackageId,
            Version = version.Version,
            Status = SyncRunItemStatus.Indexed
        });

        db.AddRange(source, validation, approval, run);
        await db.SaveChangesAsync();

        var result = await new SyncRunStore(db).DeleteAsync(run.Id);

        Assert.Equal(1, result.DeletedRunCount);
        Assert.Equal(1, result.DeletedItemCount);
        Assert.Equal(0, (await db.SyncRuns.CountAsync()));
        Assert.Equal(0, (await db.SyncRunItems.CountAsync()));
        Assert.Equal(1, (await db.PackageSources.CountAsync()));
        Assert.Equal(1, (await db.Packages.CountAsync()));
        Assert.Equal(1, (await db.PackageVersions.CountAsync()));
        Assert.Equal(1, (await db.ManifestValidationResults.CountAsync()));
        Assert.Equal(1, (await db.ApprovalRecords.CountAsync()));
    }

    [Fact]
    public async Task Bulk_delete_removes_only_terminal_runs_before_cutoff()
    {
        await using var db = await CreateOpenDbContextAsync();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        var oldCompleted = CompletedRun(cutoff.AddDays(-1), SyncRunStatus.Completed, 2);
        var oldFailed = CompletedRun(cutoff.AddDays(-2), SyncRunStatus.Failed, 1);
        var recent = CompletedRun(cutoff.AddDays(1), SyncRunStatus.Completed, 1);
        var running = new SyncRun { Trigger = SyncRunTrigger.ManualAll, Status = SyncRunStatus.Running, StartedAt = cutoff.AddDays(-3) };
        var recentRunning = new SyncRun { Trigger = SyncRunTrigger.ManualAll, Status = SyncRunStatus.Running, StartedAt = cutoff.AddDays(1) };
        db.SyncRuns.AddRange(oldCompleted, oldFailed, recent, running, recentRunning);
        await db.SaveChangesAsync();

        var preview = await new SyncRunStore(db).PreviewDeleteBeforeAsync(cutoff, [SyncRunStatus.Completed, SyncRunStatus.CompletedWithErrors, SyncRunStatus.Failed]);
        var result = await new SyncRunStore(db).DeleteBeforeAsync(cutoff, [SyncRunStatus.Completed, SyncRunStatus.CompletedWithErrors, SyncRunStatus.Failed]);

        Assert.Equal(2, preview.EligibleRunCount);
        Assert.Equal(3, preview.EligibleItemCount);
        Assert.Equal(1, preview.ExcludedRunCount);
        Assert.Equal(2, result.DeletedRunCount);
        Assert.Equal(3, result.DeletedItemCount);
        Assert.Equal(1, result.ExcludedRunCount);
        Assert.Equal(new[] { recent.Id, running.Id, recentRunning.Id }.Order(), (await db.SyncRuns.Select(x => x.Id).ToListAsync()).Order());

    }

    [Fact]
    public async Task Bulk_delete_handles_large_historical_history()
    {
        await using var db = await CreateOpenDbContextAsync();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        var oldest = cutoff.AddDays(-10);
        for (var i = 0; i < 1000; i++)
            db.SyncRuns.Add(CompletedRun(oldest.AddMinutes(i)));

        db.SyncRuns.Add(CompletedRun(cutoff.AddMinutes(1)));
        await db.SaveChangesAsync();

        var result = await new SyncRunStore(db).DeleteBeforeAsync(cutoff, [SyncRunStatus.Completed, SyncRunStatus.CompletedWithErrors, SyncRunStatus.Failed]);

        Assert.Equal(1000, result.DeletedRunCount);
        Assert.Equal(1, (await db.SyncRuns.CountAsync()));
    }

    [Fact]
    public async Task Latest_run_only_considers_runs_of_the_requested_mode_triggers_and_statuses()
    {
        await using var db = await CreateOpenDbContextAsync();
        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        SyncRun Run(SyncRunTrigger trigger, SyncRunMode mode, SyncRunStatus status, int hour) =>
            new() { Trigger = trigger, Mode = mode, Status = status, StartedAt = start.AddHours(hour) };
        var latestVerification = Run(SyncRunTrigger.Scheduled, SyncRunMode.Verification, SyncRunStatus.CompletedWithErrors, 1);
        db.SyncRuns.AddRange(
            Run(SyncRunTrigger.ManualAll, SyncRunMode.Verification, SyncRunStatus.Completed, 0),
            latestVerification,
            Run(SyncRunTrigger.Scheduled, SyncRunMode.NewVersionsOnly, SyncRunStatus.Completed, 2),
            Run(SyncRunTrigger.ManualSource, SyncRunMode.Verification, SyncRunStatus.Completed, 3),
            Run(SyncRunTrigger.ManualAll, SyncRunMode.Verification, SyncRunStatus.Failed, 4),
            Run(SyncRunTrigger.ManualAll, SyncRunMode.Verification, SyncRunStatus.Running, 5));
        await db.SaveChangesAsync();
        var store = new SyncRunStore(db);

        var latest = await store.GetLatestRunAsync(SyncRunMode.Verification, [SyncRunTrigger.Scheduled, SyncRunTrigger.ManualAll], [SyncRunStatus.Completed, SyncRunStatus.CompletedWithErrors]);
        var none = await store.GetLatestRunAsync(SyncRunMode.NewVersionsOnly, [SyncRunTrigger.ManualAll], [SyncRunStatus.Completed]);

        Assert.Equal(latestVerification.Id, latest?.Id);
        Assert.Equal(latestVerification.StartedAt, latest?.StartedAt);
        Assert.Null(none);
    }

    [Fact]
    public async Task Versions_found_without_manifest_are_the_invalid_items_of_the_run_that_stored_no_version()
    {
        await using var db = await CreateOpenDbContextAsync();
        var source = PublicCatalogSeedData.CreatePackageSource();
        var storedInvalidVersion = PublicCatalogSeedData.AddVersion(PublicCatalogSeedData.CreatePackage(source, "Elsa.Stored"), validationStatus: ValidationStatus.Invalid);
        db.PackageSources.Add(source);
        var otherSourceId = Guid.NewGuid();
        var verification = new SyncRun { Trigger = SyncRunTrigger.ManualAll, Status = SyncRunStatus.Completed };
        var regular = new SyncRun { Trigger = SyncRunTrigger.Scheduled, Mode = SyncRunMode.NewVersionsOnly, Status = SyncRunStatus.Completed };
        void Item(SyncRun run, Guid sourceId, string version, SyncRunItemStatus status, PackageVersion? stored = null) =>
            run.Items.Add(new SyncRunItem
            {
                SyncRun = run,
                SyncRunId = run.Id,
                SourceId = sourceId,
                PackageId = stored?.Package?.PackageId ?? "Elsa.Legacy",
                Version = version,
                Status = status,
                PackageVersion = stored,
                PackageVersionId = stored?.Id
            });
        Item(verification, source.Id, "1.0.0", SyncRunItemStatus.Invalid);
        Item(verification, otherSourceId, "1.0.0", SyncRunItemStatus.Invalid);
        Item(verification, source.Id, storedInvalidVersion.Version, SyncRunItemStatus.Invalid, storedInvalidVersion);
        Item(verification, source.Id, "2.0.0", SyncRunItemStatus.Failed);
        Item(regular, source.Id, "3.0.0", SyncRunItemStatus.Invalid);
        db.SyncRuns.AddRange(verification, regular);
        await db.SaveChangesAsync();

        var versions = await new SyncRunStore(db).GetVersionsFoundWithoutManifestAsync(verification.Id);

        Assert.Equal(
            new HashSet<SyncRunPackageVersionKey> { new(source.Id, "Elsa.Legacy", "1.0.0"), new(otherSourceId, "Elsa.Legacy", "1.0.0") },
            versions);
    }

    private static async Task<CatalogDbContext> CreateOpenDbContextAsync()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite("Data Source=:memory:")
            .Options;

        var db = new CatalogDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static SyncRun CompletedRun(DateTimeOffset completedAt, SyncRunStatus status = SyncRunStatus.Completed, int items = 0)
    {
        var run = new SyncRun
        {
            Trigger = SyncRunTrigger.ManualAll,
            Status = status,
            StartedAt = completedAt.AddMinutes(-2),
            CompletedAt = completedAt
        };

        for (var i = 0; i < items; i++)
            run.Items.Add(new SyncRunItem { SyncRun = run, SyncRunId = run.Id, Status = SyncRunItemStatus.Indexed });

        return run;
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeDiscovery(IReadOnlyList<DiscoveredPackageVersion> versions) : IPackageVersionDiscoveryClient
    {
        public Task<IReadOnlyList<DiscoveredPackageVersion>> FindPackageVersionsAsync(PackageSource source, CancellationToken cancellationToken = default) => Task.FromResult(versions);
    }

    private sealed class FakeDownloader(string manifestJson) : IPackageArchiveDownloader
    {
        public Task<Stream> DownloadPackageAsync(PackageSource source, string packageId, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifestJson)));
    }

    private sealed class FakeManifestReader : IPackageArchiveManifestReader
    {
        public async Task<PackageManifestReadResult> ReadAsync(Stream packageStream, CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(packageStream);
            var json = await reader.ReadToEndAsync(cancellationToken);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            return PackageManifestReadResult.Found("elsa-package.json", json, hash, []);
        }
    }
}
