using ElsaControl.Api.Admin.Sync;
using ElsaControl.PackageCatalog.Core.Manifests;
using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Packaging;
using ElsaControl.PackageCatalog.Core.Sources;
using ElsaControl.PackageCatalog.Core.Sync;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Sources.NuGet;
using Elsa.Specifications.PackageManifests.Validation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElsaControl.Api.Tests;

public sealed class SyncRunReconciliationHostedServiceTests : IAsyncLifetime
{
    private readonly string _connectionString = $"Data Source=sync-reconciliation-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;
    private readonly ServiceProvider _services;

    public SyncRunReconciliationHostedServiceTests()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _services = new ServiceCollection()
            .AddDbContext<CatalogDbContext>(options => options.UseSqlite(_connectionString))
            .AddScoped<IPackageSourceStore, PackageSourceStore>()
            .AddScoped<ISyncCatalogStore, SyncCatalogStore>()
            .AddScoped<ISyncRunStore, SyncRunStore>()
            .AddSingleton<IPackageVersionDiscoveryClient>(new ThrowingDiscovery())
            .AddScoped<IPackageArchiveDownloader, NuGetSyncPackageDownloader>()
            .AddScoped<IPackageArchiveManifestReader, PackageArchiveManifestReader>()
            .AddSingleton<ManifestValidator>()
            .AddScoped<ManifestIngestionService>()
            .AddSingleton<PackageVersionPolicy>()
            .AddSingleton<ISyncDiagnostics, NoopSyncDiagnostics>()
            .AddSingleton<SyncConcurrencyGuard>()
            .AddSingleton<SourceSyncActivityTracker>()
            .AddSingleton<SyncRunCancellationRegistry>()
            .AddSingleton(TimeProvider.System)
            .AddScoped<PackageSyncService>()
            .BuildServiceProvider();
    }

    public async Task InitializeAsync()
    {
        await _keepAlive.OpenAsync();
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }

    [Fact]
    public async Task Startup_reconciles_a_run_left_running_by_a_previous_process_and_leaves_completed_runs_alone()
    {
        var stale = await SeedRunAsync(SyncRunStatus.Running, DateTimeOffset.UtcNow.AddHours(-1));
        var completed = await SeedRunAsync(SyncRunStatus.Completed, DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1).AddMinutes(30));

        var before = DateTimeOffset.UtcNow;
        await CreateHostedService().StartAsync(CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        var reconciled = await GetRunAsync(stale.Id);
        Assert.Equal(SyncRunStatus.Failed, reconciled.Status);
        Assert.InRange(reconciled.CompletedAt!.Value, before, after);
        Assert.Equal("Interrupted by an API restart.", reconciled.Error);

        var untouchedCompleted = await GetRunAsync(completed.Id);
        Assert.Equal(SyncRunStatus.Completed, untouchedCompleted.Status);
        Assert.Equal(completed.CompletedAt, untouchedCompleted.CompletedAt);
    }

    [Fact]
    public async Task A_manual_run_started_after_reconciliation_is_left_running()
    {
        await CreateHostedService().StartAsync(CancellationToken.None);

        await using var scope = _services.CreateAsyncScope();
        var started = await scope.ServiceProvider.GetRequiredService<PackageSyncService>().StartManualAllAsync();
        started.WorkItem?.Dispose();

        Assert.Equal(SyncRunStatus.Running, started.Run.Status);
        Assert.Equal(SyncRunStatus.Running, (await GetRunAsync(started.Run.Id)).Status);
    }

    [Fact]
    public async Task Reconciliation_is_idempotent()
    {
        var stale = await SeedRunAsync(SyncRunStatus.Running, DateTimeOffset.UtcNow.AddHours(-1));

        await CreateHostedService().StartAsync(CancellationToken.None);
        var firstCompletedAt = (await GetRunAsync(stale.Id)).CompletedAt;

        await CreateHostedService().StartAsync(CancellationToken.None);
        var secondRun = await GetRunAsync(stale.Id);

        Assert.Equal(SyncRunStatus.Failed, secondRun.Status);
        Assert.Equal(firstCompletedAt, secondRun.CompletedAt);
    }

    [Fact]
    public async Task A_reconciliation_failure_does_not_keep_the_api_from_starting()
    {
        await using var unavailable = new ServiceCollection().BuildServiceProvider();
        var service = new SyncRunReconciliationHostedService(unavailable, TimeProvider.System, NullLogger<SyncRunReconciliationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
    }

    private SyncRunReconciliationHostedService CreateHostedService() =>
        new(_services, TimeProvider.System, NullLogger<SyncRunReconciliationHostedService>.Instance);

    private async Task<SyncRun> SeedRunAsync(SyncRunStatus status, DateTimeOffset startedAt, DateTimeOffset? completedAt = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var run = new SyncRun { Trigger = SyncRunTrigger.Scheduled, Status = status, StartedAt = startedAt, CompletedAt = completedAt };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private async Task<SyncRun> GetRunAsync(Guid id)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().SyncRuns.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private sealed class ThrowingDiscovery : IPackageVersionDiscoveryClient
    {
        public Task<IReadOnlyList<DiscoveredPackageVersion>> FindPackageVersionsAsync(PackageSource source, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Reconciliation must not discover package versions.");
    }
}
