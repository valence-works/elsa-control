using System.Text.Json;
using ElsaControl.PackageCatalog.Core.Manifests;
using ElsaControl.PackageCatalog.Core.Packaging;
using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Sources;
using Elsa.Specifications.PackageManifests;
using Elsa.Specifications.PackageManifests.Validation;
using NuGet.Versioning;

namespace ElsaControl.PackageCatalog.Core.Sync;

public sealed class PackageSyncService(
    IPackageSourceStore sources,
    ISyncCatalogStore catalog,
    ISyncRunStore syncRuns,
    IPackageVersionDiscoveryClient discovery,
    IPackageArchiveDownloader downloader,
    IPackageArchiveManifestReader manifestReader,
    ManifestValidator validator,
    ManifestIngestionService ingestion,
    PackageVersionPolicy versionPolicy,
    ISyncDiagnostics diagnostics,
    SyncConcurrencyGuard concurrencyGuard,
    SourceSyncActivityTracker syncActivity,
    SyncRunCancellationRegistry cancellationRegistry,
    IPublicCatalogCacheInvalidator? publicCatalogCache = null,
    TimeProvider? timeProvider = null)
{
    private const string SyncScope = "sync";
    private const string NotReverifiedMessage = "Already indexed; the stored manifest was not re-verified in this run.";
    private const string NoManifestMessage = "Package does not contain elsa-package.json.";
    private const string NoManifestNotRereadMessage = "Previously found without elsa-package.json; not re-read in this run.";

    // A verification counts once it has run to the end over all enabled sources. Runs that completed with item errors count
    // too: their failures stay visible on the run and its sources, and one persistently failing version must not turn every
    // scheduled run back into a full verification.
    private static readonly SyncRunTrigger[] AllSourceTriggers = [SyncRunTrigger.Scheduled, SyncRunTrigger.ManualAll];
    private static readonly SyncRunStatus[] CompletedStatuses = [SyncRunStatus.Completed, SyncRunStatus.CompletedWithErrors];

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<SyncRun> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        SyncRun? run = null;
        var executed = await concurrencyGuard.TryRunAsync(SyncScope, async () =>
        {
            run = new SyncRun { Trigger = SyncRunTrigger.ManualAll };
            await ExecuteRunAsync(run, () => sources.ListAsync(cancellationToken), cancellationToken, addRun: true);
        });

        return executed
            ? run!
            : RejectedRun(SyncRunTrigger.ManualAll, "A sync run is already active for all sources.");
    }

    /// <summary>
    /// Runs a scheduled sync of all enabled sources. It is a verification run, which downloads and reads every discovered
    /// version, when no completed all-source verification run started within <paramref name="verificationInterval"/>.
    /// Otherwise it is a regular run: stored versions, and versions that the last verification run read without finding
    /// elsa-package.json, are recorded without being downloaded; every other version is downloaded, read, validated and indexed.
    /// </summary>
    public async Task<SyncRun> SyncScheduledAsync(TimeSpan verificationInterval, CancellationToken cancellationToken = default)
    {
        SyncRun? run = null;
        var executed = await concurrencyGuard.TryRunAsync(SyncScope, async () =>
        {
            var startedAt = _timeProvider.GetUtcNow();
            var lastVerification = await syncRuns.GetLatestRunAsync(SyncRunMode.Verification, AllSourceTriggers, CompletedStatuses, cancellationToken);
            // A verification dated in the future (clock skew) is not trusted to postpone the next one.
            var currentVerification = startedAt - lastVerification?.StartedAt is { } age && age >= TimeSpan.Zero && age < verificationInterval
                ? lastVerification
                : null;
            // Read from the same run that postpones the verification, so a version stays unread only while the read that found it
            // without a manifest is current. Versions that run failed to read, or never saw, are still read.
            var foundWithoutManifest = currentVerification is null
                ? null
                : await syncRuns.GetVersionsFoundWithoutManifestAsync(currentVerification.Id, cancellationToken);
            run = new SyncRun
            {
                Trigger = SyncRunTrigger.Scheduled,
                Mode = currentVerification is null ? SyncRunMode.Verification : SyncRunMode.NewVersionsOnly,
                StartedAt = startedAt
            };
            await ExecuteRunAsync(run, () => sources.ListAsync(cancellationToken), cancellationToken, addRun: true, foundWithoutManifest);
        });

        return executed
            ? run!
            : RejectedRun(SyncRunTrigger.Scheduled, "A sync run is already active for all sources.");
    }

    public async Task<SyncRun> SyncSourceAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        SyncRun? run = null;
        var executed = await concurrencyGuard.TryRunAsync(SyncScope, async () =>
        {
            run = new SyncRun { Trigger = SyncRunTrigger.ManualSource };
            await ExecuteRunAsync(run, async () =>
            {
                var source = await sources.GetAsync(sourceId, cancellationToken);
                return source is null ? [] : [source];
            }, cancellationToken, addRun: true);
        });

        return executed
            ? run!
            : RejectedRun(SyncRunTrigger.ManualSource, $"A sync run is already active for source '{sourceId}'.");
    }

    public async Task<PackageSyncStartResult> StartManualAllAsync(CancellationToken cancellationToken = default)
    {
        if (!concurrencyGuard.TryAcquire(SyncScope, out var lease))
            return PackageSyncStartResult.Rejected(RejectedRun(SyncRunTrigger.ManualAll, "A sync run is already active for all sources."));

        var run = new SyncRun { Trigger = SyncRunTrigger.ManualAll };
        try
        {
            await AddRunAsync(run, cancellationToken);
            return PackageSyncStartResult.AcceptedRun(run, new PackageSyncWorkItem(run.Id, run.Trigger, null, lease));
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async Task<PackageSyncStartResult> StartManualSourceAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        if (!concurrencyGuard.TryAcquire(SyncScope, out var lease))
            return PackageSyncStartResult.Rejected(RejectedRun(SyncRunTrigger.ManualSource, $"A sync run is already active for source '{sourceId}'."));

        var run = new SyncRun { Trigger = SyncRunTrigger.ManualSource };
        try
        {
            var source = await sources.GetAsync(sourceId, cancellationToken);
            await AddRunAsync(run, cancellationToken);
            var sourceReference = source is null ? null : new SyncRunSourceReference(source.Id, source.Name);
            return PackageSyncStartResult.AcceptedRun(run, new PackageSyncWorkItem(run.Id, run.Trigger, sourceId, lease), sourceReference);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async Task ExecuteManualWorkItemAsync(PackageSyncWorkItem workItem, CancellationToken cancellationToken = default)
    {
        using (workItem)
        {
            var run = await syncRuns.GetAsync(workItem.RunId, cancellationToken);
            if (run is null)
                return;

            if (workItem.Trigger == SyncRunTrigger.ManualSource && workItem.SourceId.HasValue)
            {
                await ExecuteRunAsync(run, async () =>
                {
                    var source = await sources.GetAsync(workItem.SourceId.Value, cancellationToken);
                    return source is null ? [] : [source];
                }, cancellationToken, addRun: false);
                return;
            }

            await ExecuteRunAsync(run, () => sources.ListAsync(cancellationToken), cancellationToken, addRun: false);
        }
    }

    public async Task MarkManualWorkItemFailedAsync(PackageSyncWorkItem workItem, string error, CancellationToken cancellationToken = default)
    {
        var run = await syncRuns.GetAsync(workItem.RunId, cancellationToken);
        if (run is null || run.Status != SyncRunStatus.Running)
            return;

        run.Status = SyncRunStatus.Failed;
        run.Error = LimitFailureDetail(error);
        run.CompletedAt = DateTimeOffset.UtcNow;
        await syncRuns.SaveChangesAsync(CancellationToken.None);
    }

    private async Task ExecuteRunAsync(
        SyncRun run,
        Func<Task<IReadOnlyList<PackageSource>>> getSources,
        CancellationToken cancellationToken,
        bool addRun,
        IReadOnlySet<SyncRunPackageVersionKey>? foundWithoutManifest = null)
    {
        using var runCancellation = cancellationRegistry.Track(run.Id, cancellationToken);
        diagnostics.SyncRunStarted(run.Id);
        if (addRun)
            await AddRunAsync(run, runCancellation.Token);

        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var source in (await getSources()).Where(x => x.Enabled))
            {
                runCancellation.Token.ThrowIfCancellationRequested();
                await SyncSourceAsync(run, source, counters, foundWithoutManifest, runCancellation.Token);
                await sources.SaveChangesAsync(runCancellation.Token);
            }

            run.Status = run.Items.Any(x => x.Status == SyncRunItemStatus.Failed)
                ? SyncRunStatus.CompletedWithErrors
                : SyncRunStatus.Completed;
        }
        catch (OperationCanceledException) when (runCancellation.IsOperatorCancellationRequested)
        {
            run.Status = SyncRunStatus.Canceled;
            run.Error = "Sync canceled by operator.";
        }
        catch (Exception ex)
        {
            run.Status = SyncRunStatus.Failed;
            run.Error = ex.Message;
        }
        finally
        {
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.SummaryCountersJson = JsonSerializer.Serialize(counters);
            await syncRuns.SaveChangesAsync(CancellationToken.None);
            publicCatalogCache?.Invalidate();
            diagnostics.SyncRunCompleted(run.Id, run.Status);
        }
    }

    private async Task AddRunAsync(SyncRun run, CancellationToken cancellationToken)
    {
        await syncRuns.AddAsync(run, cancellationToken);
        await syncRuns.SaveChangesAsync(cancellationToken);
    }

    private async Task SyncSourceAsync(SyncRun run, PackageSource source, Dictionary<string, int> counters, IReadOnlySet<SyncRunPackageVersionKey>? foundWithoutManifest, CancellationToken cancellationToken)
    {
        using var activity = syncActivity.BeginSourceSync(source.Id);
        IReadOnlyList<DiscoveredPackageVersion> discovered;
        try
        {
            discovered = await discovery.FindPackageVersionsAsync(source, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await AddItemAsync(run, source.Id, null, null, SyncRunItemStatus.Failed, cancellationToken, error: ex.Message);
            Increment(counters, "failed");
            source.LastSyncedAt = DateTimeOffset.UtcNow;
            source.Status = PackageSourceStatus.Error;
            source.LastSyncError = LimitFailureDetail(ex.Message);
            return;
        }

        var failureCount = 0;
        SyncRunItem? firstFailure = null;
        foreach (var item in discovered)
        {
            var runItem = await SyncPackageVersionAsync(run, source, item, counters, foundWithoutManifest, cancellationToken);
            if (runItem.Status != SyncRunItemStatus.Failed)
                continue;

            failureCount++;
            firstFailure ??= runItem;
        }

        source.LastSyncedAt = DateTimeOffset.UtcNow;
        var hasFailures = failureCount > 0;
        source.Status = hasFailures ? PackageSourceStatus.Warning : PackageSourceStatus.Healthy;
        source.LastSyncError = hasFailures ? BuildSourceFailureDetail(firstFailure, failureCount) : null;
        if (!hasFailures)
            source.LastSuccessfulSyncAt = source.LastSyncedAt;
    }

    private async Task<SyncRunItem> SyncPackageVersionAsync(
        SyncRun run,
        PackageSource source,
        DiscoveredPackageVersion discovered,
        Dictionary<string, int> counters,
        IReadOnlySet<SyncRunPackageVersionKey>? foundWithoutManifest,
        CancellationToken cancellationToken)
    {
        var runItem = await AddItemAsync(run, source.Id, discovered.PackageId, discovered.Version, SyncRunItemStatus.Discovered, cancellationToken);
        try
        {
            var package = await catalog.GetPackageAsync(source.Id, discovered.PackageId, cancellationToken)
                ?? new Package
                {
                    SourceId = source.Id,
                    PackageId = discovered.PackageId,
                    DisplayName = PackageDisplayNamePolicy.DefaultForPackageId(discovered.PackageId),
                    Approved = source.ApprovalPolicy == PackageSourceApprovalPolicy.AutoApprove,
                    Listed = true
                };

            var existingVersion = await catalog.GetPackageVersionAsync(package.Id, discovered.Version, cancellationToken);
            if (run.Mode == SyncRunMode.NewVersionsOnly)
            {
                if (existingVersion is not null)
                {
                    UpdateLatestVersion(package, discovered.Version);
                    MarkUnchanged(runItem, existingVersion, counters);
                    runItem.Message = NotReverifiedMessage;
                    return runItem;
                }

                // Checked after the stored version, so a version indexed since the last verification is never reported as lacking a manifest.
                if (foundWithoutManifest?.Contains(new SyncRunPackageVersionKey(source.Id, discovered.PackageId, discovered.Version)) == true)
                {
                    MarkWithoutManifest(runItem, NoManifestNotRereadMessage, counters);
                    return runItem;
                }
            }

            await using var packageStream = await downloader.DownloadPackageAsync(source, discovered.PackageId, discovered.Version, cancellationToken);
            var read = await manifestReader.ReadAsync(packageStream, cancellationToken);
            if (!read.Exists || read.ManifestJson is null || read.ManifestHash is null)
            {
                MarkWithoutManifest(runItem, NoManifestMessage, counters);
                return runItem;
            }

            UpdateLatestVersion(package, discovered.Version);
            if (existingVersion is not null)
            {
                var change = versionPolicy.CompareManifest(existingVersion, read.ManifestHash);
                if (change.IsSuspicious)
                {
                    existingVersion.SuspiciousChangeDetected = true;
                    existingVersion.SuspiciousManifestHash = change.ObservedHash;
                    existingVersion.ValidationStatus = ValidationStatus.Suspicious;
                    runItem.Status = SyncRunItemStatus.Suspicious;
                    runItem.PackageVersion = existingVersion;
                    runItem.PackageVersionId = existingVersion.Id;
                    diagnostics.SuspiciousManifestChange(run.Id, discovered.PackageId, discovered.Version, read.ManifestHash);
                    Increment(counters, "suspicious");
                }
                else
                {
                    MarkUnchanged(runItem, existingVersion, counters);
                }

                return runItem;
            }

            var validation = validator.Validate(read.ManifestJson, discovered.PackageId, discovered.Version);
            var schemaVersion = ExtractSchemaVersion(read.ManifestJson);
            var packageVersion = new PackageVersion
            {
                Package = package,
                PackageId = package.Id,
                Version = discovered.Version,
                ManifestJson = read.ManifestJson,
                ManifestHash = read.ManifestHash,
                PublishedAt = discovered.PublishedAt,
                ValidationStatus = ToValidationStatus(validation),
                ValidationErrors = JsonSerializer.Serialize(validation.Errors, ManifestJsonSerializerOptions.Default),
                ApprovalStatus = source.ApprovalPolicy == PackageSourceApprovalPolicy.AutoApprove ? PackageApprovalStatus.Approved : PackageApprovalStatus.Pending,
                IsListed = true,
                SchemaVersion = schemaVersion
            };

            if (packageVersion.ValidationStatus == ValidationStatus.Valid)
            {
                ingestion.Ingest(packageVersion, read.ManifestJson);
            }

            package.Versions.Add(packageVersion);
            UpdatePackageDisplayName(package);

            if (await catalog.GetPackageAsync(source.Id, discovered.PackageId, cancellationToken) is null)
                await catalog.AddPackageAsync(package, cancellationToken);

            await catalog.AddValidationResultAsync(new ManifestValidationResultRecord
            {
                PackageVersion = packageVersion,
                PackageVersionId = packageVersion.Id,
                SchemaVersion = schemaVersion,
                Status = packageVersion.ValidationStatus,
                ErrorsJson = JsonSerializer.Serialize(validation.Errors, ManifestJsonSerializerOptions.Default),
                WarningsJson = JsonSerializer.Serialize(validation.Warnings.Select(x => x.Message).Concat(read.Warnings), ManifestJsonSerializerOptions.Default),
                ValidatorVersion = "v1"
            }, cancellationToken);

            runItem.Status = packageVersion.ValidationStatus == ValidationStatus.Valid ? SyncRunItemStatus.Indexed : SyncRunItemStatus.Invalid;
            runItem.PackageVersion = packageVersion;
            runItem.PackageVersionId = packageVersion.Id;
            Increment(counters, runItem.Status == SyncRunItemStatus.Indexed ? "indexed" : "invalid");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            runItem.Status = SyncRunItemStatus.Failed;
            runItem.Error = ex.Message;
            diagnostics.SyncItemFailed(run.Id, discovered.PackageId, discovered.Version, ex.Message);
            Increment(counters, "failed");
        }
        finally
        {
            runItem.CompletedAt = DateTimeOffset.UtcNow;
            await catalog.SaveChangesAsync(CancellationToken.None);
        }

        return runItem;
    }

    private async Task<SyncRunItem> AddItemAsync(
        SyncRun run,
        Guid? sourceId,
        string? packageId,
        string? version,
        SyncRunItemStatus status,
        CancellationToken cancellationToken,
        string? error = null)
    {
        var item = new SyncRunItem
        {
            SyncRun = run,
            SyncRunId = run.Id,
            SourceId = sourceId,
            PackageId = packageId,
            Version = version,
            Status = status,
            Error = error
        };
        run.Items.Add(item);
        await syncRuns.AddItemAsync(item, cancellationToken);
        return item;
    }

    private static void MarkUnchanged(SyncRunItem runItem, PackageVersion existingVersion, Dictionary<string, int> counters)
    {
        runItem.Status = SyncRunItemStatus.Unchanged;
        runItem.PackageVersion = existingVersion;
        runItem.PackageVersionId = existingVersion.Id;
        Increment(counters, "unchanged");
    }

    // Recorded as Invalid without a stored version: the shape ISyncRunStore.GetVersionsFoundWithoutManifestAsync reads back.
    private static void MarkWithoutManifest(SyncRunItem runItem, string message, Dictionary<string, int> counters)
    {
        runItem.Status = SyncRunItemStatus.Invalid;
        runItem.Message = message;
        Increment(counters, "invalid");
    }

    private static ValidationStatus ToValidationStatus(ManifestValidationResult validation) =>
        validation.Status == ManifestValidationStatus.UnsupportedSchema
            ? ValidationStatus.UnsupportedSchema
            : validation.IsValid
                ? ValidationStatus.Valid
                : ValidationStatus.Invalid;

    private static void UpdatePackageDisplayName(Package package)
    {
        var latestValidVersion = package.Versions
            .Where(x => x.ValidationStatus == ValidationStatus.Valid)
            .OrderByDescending(x => x.Version, Comparer<string>.Create(CompareVersions))
            .FirstOrDefault();

        if (latestValidVersion is null)
            return;

        var manifest = JsonSerializer.Deserialize<ElsaPackageManifest>(latestValidVersion.ManifestJson, ManifestJsonSerializerOptions.Default);
        package.DisplayName = ResolveManifestDisplayName(package, manifest);
    }

    private static string ResolveManifestDisplayName(Package package, ElsaPackageManifest? manifest) =>
        string.IsNullOrWhiteSpace(manifest?.DisplayName) ||
        string.Equals(manifest.DisplayName, package.PackageId, StringComparison.OrdinalIgnoreCase)
            ? PackageDisplayNamePolicy.DefaultForPackageId(package.PackageId)
            : manifest.DisplayName;

    private static string? ExtractSchemaVersion(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        return document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion)
            ? schemaVersion.GetString()
            : null;
    }

    private static void UpdateLatestVersion(Package package, string version)
    {
        if (package.LatestVersion is null || CompareVersions(version, package.LatestVersion) > 0)
            package.LatestVersion = version;
    }

    private static int CompareVersions(string left, string right)
    {
        if (NuGetVersion.TryParse(left, out var leftVersion) && NuGetVersion.TryParse(right, out var rightVersion))
            return leftVersion.CompareTo(rightVersion);

        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static SyncRun RejectedRun(SyncRunTrigger trigger, string error) =>
        new()
        {
            Trigger = trigger,
            Status = SyncRunStatus.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            Error = error
        };

    private static void Increment(Dictionary<string, int> counters, string name) =>
        counters[name] = counters.GetValueOrDefault(name) + 1;

    private static string BuildSourceFailureDetail(SyncRunItem? firstFailure, int failureCount)
    {
        if (firstFailure is null)
            return "Sync failed.";

        var subject = FormatPackageSubject(firstFailure);
        var detail = string.IsNullOrWhiteSpace(subject)
            ? firstFailure.Error ?? "Sync failed."
            : $"{subject}: {firstFailure.Error ?? "Sync failed."}";

        if (failureCount > 1)
            detail = $"{detail} ({failureCount - 1} more failed)";

        return LimitFailureDetail(detail);
    }

    private static string FormatPackageSubject(SyncRunItem item)
    {
        if (string.IsNullOrWhiteSpace(item.PackageId))
            return "";

        return string.IsNullOrWhiteSpace(item.Version)
            ? item.PackageId
            : $"{item.PackageId} {item.Version}";
    }

    private static string LimitFailureDetail(string detail) =>
        detail.Length <= 2048 ? detail : string.Concat(detail.AsSpan(0, 2045), "...");
}

public interface ISyncCatalogStore
{
    Task<Package?> GetPackageAsync(Guid sourceId, string packageId, CancellationToken cancellationToken = default);
    Task<PackageVersion?> GetPackageVersionAsync(Guid packageId, string version, CancellationToken cancellationToken = default);
    Task AddPackageAsync(Package package, CancellationToken cancellationToken = default);
    Task AddValidationResultAsync(ManifestValidationResultRecord result, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface ISyncRunStore
{
    Task<IReadOnlyList<SyncRun>> ListAsync(CancellationToken cancellationToken = default);
    Task<SyncRun?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<SyncRun?> GetLatestRunAsync(SyncRunMode mode, IReadOnlyCollection<SyncRunTrigger> triggers, IReadOnlyCollection<SyncRunStatus> statuses, CancellationToken cancellationToken = default);

    /// <summary>The versions the run read without finding elsa-package.json: its Invalid items that stored no package version.</summary>
    Task<IReadOnlySet<SyncRunPackageVersionKey>> GetVersionsFoundWithoutManifestAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, SyncRunListMetadata>> GetListMetadataAsync(IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken = default);
    Task<SyncRunDeletionCandidate?> GetDeletionCandidateAsync(Guid id, CancellationToken cancellationToken = default);
    Task<SyncRunCleanupPreview> PreviewDeleteBeforeAsync(DateTimeOffset completedBefore, IReadOnlyCollection<SyncRunStatus> terminalStatuses, CancellationToken cancellationToken = default);
    Task<SyncRunCleanupResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<SyncRunCleanupResult> DeleteBeforeAsync(DateTimeOffset completedBefore, IReadOnlyCollection<SyncRunStatus> terminalStatuses, CancellationToken cancellationToken = default);
    Task AddAsync(SyncRun run, CancellationToken cancellationToken = default);
    Task AddItemAsync(SyncRunItem item, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed record SyncRunPackageVersionKey(
    Guid SourceId,
    string PackageId,
    string Version);

public sealed record SyncRunDeletionCandidate(
    Guid Id,
    SyncRunStatus Status,
    int ItemCount);

public sealed record SyncRunCleanupPreview(
    DateTimeOffset CompletedBefore,
    int EligibleRunCount,
    int EligibleItemCount,
    int ExcludedRunCount,
    DateTimeOffset? OldestEligibleCompletedAt,
    DateTimeOffset? NewestEligibleCompletedAt);

public sealed record SyncRunCleanupResult(
    int DeletedRunCount,
    int DeletedItemCount,
    int ExcludedRunCount,
    int NotFoundRunCount,
    DateTimeOffset? CompletedBefore,
    IReadOnlyList<Guid> DeletedRunIds);

public sealed record SyncRunListMetadata(
    int ItemCount,
    IReadOnlyList<SyncRunSourceReference> Sources);

public sealed record SyncRunSourceReference(
    Guid Id,
    string? Name);
