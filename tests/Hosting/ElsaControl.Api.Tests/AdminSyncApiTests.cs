using System.Net;
using ElsaControl.Api.Admin.Sync;
using ElsaControl.Api.Authentication;
using ElsaControl.PackageCatalog.Core.Packaging;
using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Sync;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElsaControl.Api.Tests;

[Collection(AdminSyncApiTestCollection.Name)]
public sealed class AdminSyncApiTests : IClassFixture<DefaultControlApiTestApplicationFixture>
{
    private readonly ControlApiTestApplication _app;

    public AdminSyncApiTests(DefaultControlApiTestApplicationFixture fixture)
    {
        _app = fixture.Application;
    }

    [Fact]
    public async Task Manual_sync_creates_running_sync_run_and_completes_in_background()
    {
        var discovery = new GatedDiscoveryClient();
        await using var app = new ControlApiTestApplication().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPackageVersionDiscoveryClient>();
                services.AddSingleton<IPackageVersionDiscoveryClient>(discovery);
            });
        });

        await SeedAsync(app, db =>
        {
            db.PackageSources.Add(PublicCatalogSeedData.CreatePackageSource());
            return Task.CompletedTask;
        });

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");

        var response = await client.PostAsync("/api/admin/sync", null).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var run = await response.Content.ReadControlJsonAsync<AdminSyncRunResponse>();

        Assert.Equal(SyncRunStatus.Running, run!.Status);
        Assert.Equal(SyncRunMode.Verification, run.Mode);

        var runs = await client.GetControlJsonAsync<List<AdminSyncRunResponse>>("/api/admin/sync-runs");
        Assert.NotNull(runs);
        Assert.Single(runs!, x => x.Id == run.Id);

        await discovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        discovery.Release.SetResult();
        var completed = await WaitForRunStatusAsync(client, run.Id, SyncRunStatus.Completed);
        Assert.NotNull(completed.CompletedAt);
    }

    [Fact]
    public async Task Sync_run_list_includes_source_metadata_and_item_count()
    {
        var app = _app;
        var (runId, sourceId) = await SeedSyncRunWithSourceAsync(app);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");

        var runs = await client.GetControlJsonAsync<List<AdminSyncRunResponse>>("/api/admin/sync-runs");

        Assert.NotNull(runs);
        var run = Assert.Single(runs!, x => x.Id == runId);
        Assert.Equal(1, run.ItemCount);
        Assert.Equal(SyncRunMode.NewVersionsOnly, run.Mode);
        Assert.NotNull(run.Sources);
        Assert.Equal(new AdminSyncRunSourceResponse(sourceId, "Elsa Official"), Assert.Single(run.Sources!));
        Assert.NotNull(run.Items);
        Assert.Empty(run.Items!);
    }

    [Fact]
    public async Task Sync_run_details_include_source_metadata_and_item_count()
    {
        var app = _app;
        var (runId, sourceId) = await SeedSyncRunWithSourceAsync(app);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");

        var run = await client.GetControlJsonAsync<AdminSyncRunResponse>($"/api/admin/sync-runs/{runId}");

        Assert.Equal(1, run!.ItemCount);
        Assert.Equal(SyncRunMode.NewVersionsOnly, run.Mode);
        Assert.NotNull(run.Sources);
        Assert.Equal(new AdminSyncRunSourceResponse(sourceId, "Elsa Official"), Assert.Single(run.Sources!));
        Assert.NotNull(run.Items);
        Assert.Single(run.Items!, x => x.SourceId == sourceId && x.PackageId == "Elsa.Workflows");
    }

    [Fact]
    public async Task Manual_source_sync_response_includes_source_metadata()
    {
        await using var app = new ControlApiTestApplication().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPackageVersionDiscoveryClient>();
                services.AddScoped<IPackageVersionDiscoveryClient, ThrowingDiscoveryClient>();
            });
        });

        var sourceId = Guid.NewGuid();
        await SeedAsync(app, db =>
        {
            var source = PublicCatalogSeedData.CreatePackageSource();
            source.Id = sourceId;
            source.Name = "Elsa Official";
            db.PackageSources.Add(source);
            return Task.CompletedTask;
        });

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");

        var response = await client.PostAsync($"/api/admin/sync/sources/{sourceId}", null);
        var run = await response.Content.ReadControlJsonAsync<AdminSyncRunResponse>();

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal(SyncRunStatus.Running, run!.Status);
        Assert.Equal(0, run.ItemCount);
        Assert.NotNull(run.Sources);
        Assert.Equal(new AdminSyncRunSourceResponse(sourceId, "Elsa Official"), Assert.Single(run.Sources!));

        var completed = await WaitForRunStatusAsync(client, run.Id, SyncRunStatus.CompletedWithErrors);
        Assert.Equal(1, completed.ItemCount);
        Assert.NotNull(completed.Sources);
        Assert.Equal(new AdminSyncRunSourceResponse(sourceId, "Elsa Official"), Assert.Single(completed.Sources!));
    }

    private static async Task<AdminSyncRunResponse> WaitForRunStatusAsync(HttpClient client, Guid runId, SyncRunStatus status)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                var run = await client.GetControlJsonAsync<AdminSyncRunResponse>($"/api/admin/sync-runs/{runId}", timeout.Token);
                if (run?.Status == status)
                    return run;

                await Task.Delay(50, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Sync run {runId} did not reach {status}.");
            }
        }
    }

    [Fact]
    public async Task Delete_sync_run_removes_history_and_preserves_catalog_state()
    {
        var app = _app;
        var runId = await SeedPackageLinkedSyncRunAsync(app);
        var client = AuthenticatedClient(app);

        var response = await client.DeleteAsync($"/api/admin/sync-runs/{runId}");
        var result = await response.Content.ReadControlJsonAsync<AdminSyncRunCleanupResultResponse>();

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, result!.DeletedRunCount);
        Assert.Equal(1, result.DeletedItemCount);

        var missing = await client.GetAsync($"/api/admin/sync-runs/{runId}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(1, await db.PackageSources.CountAsync());
        Assert.Equal(1, await db.Packages.CountAsync());
        Assert.Equal(1, await db.PackageVersions.CountAsync());
        Assert.Equal(1, await db.ManifestValidationResults.CountAsync());
        Assert.Equal(1, await db.ApprovalRecords.CountAsync());
    }

    [Fact]
    public async Task Delete_sync_run_is_idempotent_for_missing_run()
    {
        var app = _app;
        await app.SeedAsync(_ => Task.CompletedTask);
        var client = AuthenticatedClient(app);

        var response = await client.DeleteAsync($"/api/admin/sync-runs/{Guid.NewGuid()}");
        var result = await response.Content.ReadControlJsonAsync<AdminSyncRunCleanupResultResponse>();

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, result!.NotFoundRunCount);
        Assert.Equal(0, result.DeletedRunCount);
    }

    [Fact]
    public async Task Delete_sync_run_refuses_running_run()
    {
        var app = _app;
        var runId = Guid.NewGuid();
        await app.SeedAsync(db =>
        {
            db.SyncRuns.Add(new SyncRun { Id = runId, Trigger = SyncRunTrigger.ManualAll, Status = SyncRunStatus.Running });
            return Task.CompletedTask;
        });
        var client = AuthenticatedClient(app);

        var response = await client.DeleteAsync($"/api/admin/sync-runs/{runId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Bulk_cleanup_previews_and_deletes_terminal_runs_before_cutoff()
    {
        var app = _app;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        var oldCompleted = CompletedRun(cutoff.AddDays(-1), SyncRunStatus.Completed, 2);
        var oldFailed = CompletedRun(cutoff.AddDays(-2), SyncRunStatus.Failed, 1);
        var recent = CompletedRun(cutoff.AddDays(1), SyncRunStatus.Completed, 1);
        var running = new SyncRun { Trigger = SyncRunTrigger.ManualAll, Status = SyncRunStatus.Running, StartedAt = cutoff.AddDays(-3) };
        await app.SeedAsync(db =>
        {
            db.SyncRuns.AddRange(oldCompleted, oldFailed, recent, running);
            return Task.CompletedTask;
        });
        var client = AuthenticatedClient(app);

        var preview = await client.GetControlJsonAsync<AdminSyncRunCleanupPreviewResponse>($"/api/admin/sync-runs/deletion-preview?completedBefore={Cutoff(cutoff)}");
        var response = await client.DeleteAsync($"/api/admin/sync-runs?completedBefore={Cutoff(cutoff)}");
        var result = await response.Content.ReadControlJsonAsync<AdminSyncRunCleanupResultResponse>();

        Assert.Equal(2, preview!.EligibleRunCount);
        Assert.Equal(3, preview.EligibleItemCount);
        Assert.Equal(1, preview.ExcludedRunCount);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal(2, result!.DeletedRunCount);
        Assert.Equal(3, result.DeletedItemCount);
        Assert.Equal(1, result.ExcludedRunCount);

        var runs = await client.GetControlJsonAsync<List<AdminSyncRunResponse>>("/api/admin/sync-runs");
        Assert.Equivalent(new[] { recent.Id, running.Id }, runs!.Select(x => x.Id));
    }

    [Fact]
    public async Task Bulk_cleanup_rejects_future_cutoff()
    {
        var app = _app;
        await app.SeedAsync(db =>
        {
            db.SyncRuns.Add(CompletedRun(DateTimeOffset.UtcNow.AddDays(-1)));
            return Task.CompletedTask;
        });
        var client = AuthenticatedClient(app);
        var futureCutoff = Cutoff(DateTimeOffset.UtcNow.AddMinutes(5));

        var preview = await client.GetAsync($"/api/admin/sync-runs/deletion-preview?completedBefore={futureCutoff}");
        var delete = await client.DeleteAsync($"/api/admin/sync-runs?completedBefore={futureCutoff}");

        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
        Assert.Single((await client.GetControlJsonAsync<List<AdminSyncRunResponse>>("/api/admin/sync-runs"))!);
    }

    [Fact]
    public async Task Delete_sync_run_requires_admin_authentication()
    {
        var app = _app;
        await app.SeedAsync(_ => Task.CompletedTask);
        var client = app.CreateClient();

        var response = await client.DeleteAsync($"/api/admin/sync-runs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<(Guid RunId, Guid SourceId)> SeedSyncRunWithSourceAsync(ControlApiTestApplication app)
    {
        var runId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        await app.SeedAsync(db =>
        {
            var source = PublicCatalogSeedData.CreatePackageSource();
            source.Id = sourceId;
            source.Name = "Elsa Official";

            var run = new SyncRun
            {
                Id = runId,
                Trigger = SyncRunTrigger.Scheduled,
                Mode = SyncRunMode.NewVersionsOnly,
                Status = SyncRunStatus.Completed,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CompletedAt = DateTimeOffset.UtcNow
            };

            run.Items.Add(new SyncRunItem
            {
                SyncRun = run,
                SyncRunId = run.Id,
                SourceId = source.Id,
                PackageId = "Elsa.Workflows",
                Version = "1.0.0",
                Status = SyncRunItemStatus.Indexed
            });

            db.PackageSources.Add(source);
            db.SyncRuns.Add(run);
            return Task.CompletedTask;
        });

        return (runId, sourceId);
    }

    private static async Task<Guid> SeedPackageLinkedSyncRunAsync(ControlApiTestApplication app)
    {
        var runId = Guid.NewGuid();
        await app.SeedAsync(db =>
        {
            var source = PublicCatalogSeedData.CreatePackageSource();
            var package = PublicCatalogSeedData.CreatePackage(source);
            var version = PublicCatalogSeedData.AddVersion(package);
            var run = CompletedRun(DateTimeOffset.UtcNow.AddDays(-1));
            run.Id = runId;
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
            db.AddRange(
                source,
                new ManifestValidationResultRecord
                {
                    PackageVersion = version,
                    PackageVersionId = version.Id,
                    Status = ValidationStatus.Valid
                },
                new ApprovalRecord
                {
                    TargetType = ApprovalTargetType.PackageVersion,
                    TargetId = version.Id,
                    Status = PackageApprovalStatus.Approved,
                    Actor = "tester"
                },
                run);
            return Task.CompletedTask;
        });

        return runId;
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

    private static HttpClient AuthenticatedClient(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        return client;
    }

    private static string Cutoff(DateTimeOffset cutoff) =>
        Uri.EscapeDataString(cutoff.ToUniversalTime().ToString("O"));

    private static async Task SeedAsync(WebApplicationFactory<Program> app, Func<CatalogDbContext, Task> seed)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetService<IPublicCatalogCacheInvalidator>()?.Invalidate();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        await seed(db);
        await db.SaveChangesAsync();
    }

    private sealed class ThrowingDiscoveryClient : IPackageVersionDiscoveryClient
    {
        public Task<IReadOnlyList<DiscoveredPackageVersion>> FindPackageVersionsAsync(PackageSource source, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Discovery failed.");
    }

    private sealed class GatedDiscoveryClient : IPackageVersionDiscoveryClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<DiscoveredPackageVersion>> FindPackageVersionsAsync(PackageSource source, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return [];
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AdminSyncApiTestCollection
{
    public const string Name = "Admin sync API";
}
