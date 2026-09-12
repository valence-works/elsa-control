using System.Threading.Channels;
using ElsaControl.Api.Admin.Sync;
using ElsaControl.PackageCatalog.Core.Manifests;
using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Packaging;
using ElsaControl.PackageCatalog.Core.Sources;
using ElsaControl.PackageCatalog.Core.Sync;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Sources.NuGet;
using ElsaControl.PackageCatalog.Testing;
using Elsa.Specifications.PackageManifests.Validation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElsaControl.Api.Tests;

public sealed class ScheduledSyncHostedServiceTests : IAsyncLifetime
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly string _connectionString = $"Data Source=scheduled-sync-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly GatedDiscovery _discovery = new();
    private readonly ServiceProvider _services;
    private readonly ScheduledSyncHostedService _scheduler;

    public ScheduledSyncHostedServiceTests()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _services = new ServiceCollection()
            .AddDbContext<CatalogDbContext>(options => options.UseSqlite(_connectionString))
            .AddScoped<IPackageSourceStore, PackageSourceStore>()
            .AddScoped<ISyncCatalogStore, SyncCatalogStore>()
            .AddScoped<ISyncRunStore, SyncRunStore>()
            .AddSingleton<IPackageVersionDiscoveryClient>(_discovery)
            .AddScoped<IPackageArchiveDownloader, NuGetSyncPackageDownloader>()
            .AddScoped<IPackageArchiveManifestReader, PackageArchiveManifestReader>()
            .AddSingleton<ManifestValidator>()
            .AddScoped<ManifestIngestionService>()
            .AddSingleton<PackageVersionPolicy>()
            .AddSingleton<ISyncDiagnostics, NoopSyncDiagnostics>()
            .AddSingleton<SyncConcurrencyGuard>()
            .AddSingleton<SourceSyncActivityTracker>()
            .AddSingleton<SyncRunCancellationRegistry>()
            .AddSingleton<TimeProvider>(_clock)
            .AddScoped<PackageSyncService>()
            .BuildServiceProvider();
        _scheduler = CreateScheduler("01:00:00");
    }

    public async Task InitializeAsync()
    {
        await _keepAlive.OpenAsync();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.PackageSources.Add(PublicCatalogSeedData.CreatePackageSource());
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _scheduler.StopAsync(CancellationToken.None);
        _scheduler.Dispose();
        await _services.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }

    [Fact]
    public async Task Next_run_waits_a_full_interval_after_an_overlong_run()
    {
        await StartSchedulerAsync();
        _clock.Advance(Interval);
        var overlongRun = await _discovery.NextRunAsync();
        _clock.Advance(Interval * 3);
        overlongRun.SetResult();
        await _clock.NextTimerAsync();

        _clock.Advance(Interval - TimeSpan.FromTicks(1));
        Assert.Equal(1, _discovery.Calls);

        _clock.Advance(TimeSpan.FromTicks(1));
        await _discovery.NextRunAsync();
        var runs = await PersistedRunsAsync();
        Assert.Equal(2, runs.Count);
        Assert.Equal(Interval * 3 + Interval, runs[1].StartedAt - runs[0].StartedAt);
    }

    [Fact]
    public async Task Scheduled_runs_verify_stored_versions_once_per_configured_verification_interval()
    {
        await StartSchedulerAsync();

        for (var i = 0; i < 5; i++)
        {
            _clock.Advance(Interval);
            (await _discovery.NextRunAsync()).SetResult();
            await _clock.NextTimerAsync();
        }

        var runs = await PersistedRunsAsync();
        Assert.All(runs, run => Assert.Equal(SyncRunTrigger.Scheduled, run.Trigger));
        Assert.Equal(
            [SyncRunMode.Verification, SyncRunMode.NewVersionsOnly, SyncRunMode.NewVersionsOnly, SyncRunMode.Verification, SyncRunMode.NewVersionsOnly],
            runs.Select(run => run.Mode));
    }

    [Fact]
    public async Task Stopping_while_waiting_ends_the_schedule_without_a_run()
    {
        await StartSchedulerAsync();

        await _scheduler.StopAsync(CancellationToken.None);

        Assert.True(_scheduler.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(0, _discovery.Calls);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:05:00")]
    public async Task Rejects_an_interval_that_would_run_back_to_back(string interval)
    {
        using var scheduler = CreateScheduler(interval);

        await scheduler.StartAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.ExecuteTask!);
        Assert.Equal(0, _discovery.Calls);
    }

    private ScheduledSyncHostedService CreateScheduler(string interval) =>
        new(
            _services,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sync:Scheduled:Enabled"] = "true",
                ["Sync:Scheduled:Interval"] = interval,
                ["Sync:Scheduled:VerificationInterval"] = "03:00:00"
            }).Build(),
            _clock,
            NullLogger<ScheduledSyncHostedService>.Instance);

    private async Task StartSchedulerAsync()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        await _clock.NextTimerAsync();
    }

    private async Task<List<SyncRun>> PersistedRunsAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().SyncRuns
            .AsNoTracking()
            .OrderBy(x => x.StartedAt)
            .ToListAsync();
    }

    /// <summary>Holds every discovery call, and with it the whole sync run, until the test releases it.</summary>
    private sealed class GatedDiscovery : IPackageVersionDiscoveryClient
    {
        private readonly Channel<TaskCompletionSource> _runs = Channel.CreateUnbounded<TaskCompletionSource>();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async Task<IReadOnlyList<DiscoveredPackageVersion>> FindPackageVersionsAsync(PackageSource source, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _runs.Writer.TryWrite(release);
            await release.Task.WaitAsync(cancellationToken);
            return [];
        }

        public Task<TaskCompletionSource> NextRunAsync() => _runs.Reader.ReadAsync().AsTask().WaitAsync(WaitTimeout);
    }

    /// <summary>A clock that only moves when the test advances it, firing the one-shot timers that <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> creates.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _scheduled = [];
        private readonly Channel<ManualTimer> _created = Channel.CreateUnbounded<ManualTimer>();
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _created.Writer.TryWrite(timer);
            return timer;
        }

        public Task NextTimerAsync() => _created.Reader.ReadAsync().AsTask().WaitAsync(WaitTimeout);

        public void Advance(TimeSpan by)
        {
            DateTimeOffset target;
            lock (_gate)
                target = _now + by;

            while (true)
            {
                ManualTimer? due;
                lock (_gate)
                {
                    due = _scheduled.Where(x => x.DueAt <= target).MinBy(x => x.DueAt);
                    if (due is null)
                    {
                        _now = target;
                        return;
                    }

                    _now = due.DueAt;
                    _scheduled.Remove(due);
                }

                due.Fire();
            }
        }

        private void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan)
                throw new NotSupportedException("Only one-shot timers are supported.");

            lock (_gate)
            {
                _scheduled.Remove(timer);
                if (dueTime == Timeout.InfiniteTimeSpan)
                    return;

                timer.DueAt = _now + dueTime;
                _scheduled.Add(timer);
            }
        }

        private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset DueAt { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                clock.Schedule(this, dueTime, period);
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose() => Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
