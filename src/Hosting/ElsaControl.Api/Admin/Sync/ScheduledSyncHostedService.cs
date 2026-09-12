using ElsaControl.PackageCatalog.Core.Sync;

namespace ElsaControl.Api.Admin.Sync;

public sealed class ScheduledSyncHostedService(IServiceProvider services, IConfiguration configuration, TimeProvider timeProvider, ILogger<ScheduledSyncHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = configuration.GetValue("Sync:Scheduled:Enabled", false);
        if (!enabled)
            return;

        var interval = configuration.GetValue("Sync:Scheduled:Interval", TimeSpan.FromHours(1));
        if (interval <= TimeSpan.Zero)
            throw new InvalidOperationException("Sync:Scheduled:Interval must be a positive duration.");

        var verificationInterval = configuration.GetValue("Sync:Scheduled:VerificationInterval", TimeSpan.FromHours(24));

        while (true)
        {
            try
            {
                // The wait starts when the previous run has finished, so a run that outlasts the interval is followed by a
                // full interval of rest instead of an immediate next run, and scheduled runs never overlap.
                await Task.Delay(interval, timeProvider, stoppingToken);
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PackageSyncService>().SyncScheduledAsync(verificationInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled package catalog sync failed.");
            }
        }
    }
}
