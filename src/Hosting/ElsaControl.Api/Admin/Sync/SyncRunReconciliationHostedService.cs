using ElsaControl.PackageCatalog.Core.Sync;

namespace ElsaControl.Api.Admin.Sync;

/// <summary>
/// Reconciles package sync runs left Running by a previous instance of this process, before that process's
/// scheduler or manual sync queue can start a run of their own.
/// </summary>
/// <remarks>
/// Registered before <see cref="ManualSyncHostedService"/> and <see cref="ScheduledSyncHostedService"/> so its
/// <see cref="StartAsync"/> runs first, mirroring the fail-fast configuration validators that already run this
/// early in <c>Program.cs</c>. That ordering is not, however, what makes this safe: the cutoff it reconciles
/// against is the time this process started, captured once here, so a run this process later starts always has a
/// StartedAt at or after that cutoff and can never be matched, whatever race exists between this service starting
/// and a request reaching the admin sync endpoints. Reconciling is safe at all only because
/// <see cref="SyncConcurrencyGuard"/> is a per-process, in-memory guard and only one API instance runs in
/// production today; a second, concurrently-running instance would see the other's active run as having started
/// before its own process and would wrongly reconcile a run that is still legitimately in progress.
/// </remarks>
public sealed class SyncRunReconciliationHostedService(IServiceProvider services, TimeProvider timeProvider, ILogger<SyncRunReconciliationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var processStartedAt = timeProvider.GetUtcNow();
        await using var scope = services.CreateAsyncScope();
        var reconciledCount = await scope.ServiceProvider.GetRequiredService<PackageSyncService>()
            .ReconcileInterruptedRunsAsync(processStartedAt, cancellationToken);

        if (reconciledCount > 0)
            logger.LogWarning("Reconciled {Count} package sync run(s) left running by a previous process.", reconciledCount);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
