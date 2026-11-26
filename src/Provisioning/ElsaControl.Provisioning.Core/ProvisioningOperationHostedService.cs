using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Periodically wakes the module worker. Registered only when the feature flag
/// is on and the host is not the Testing environment.
/// </summary>
public sealed class ProvisioningOperationHostedService(
    ProvisioningOperationWorker worker,
    IOptions<ProvisioningModuleOptions> options,
    ILogger<ProvisioningOperationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configured = options.Value;
        if (!configured.Enabled)
        {
            logger.LogInformation("Provisioning module worker is disabled.");
            return;
        }

        var interval = configured.PollInterval > TimeSpan.Zero
            ? configured.PollInterval
            : TimeSpan.FromSeconds(5);
        var batchSize = configured.BatchSize is >= 1 and <= 100 ? configured.BatchSize : 10;
        using var timer = new PeriodicTimer(interval);

        while (true)
        {
            try
            {
                var processed = await worker.ProcessOnceAsync(batchSize, stoppingToken).ConfigureAwait(false);
                if (processed > 0)
                    logger.LogInformation("Processed {ProvisioningOperationCount} provisioning operations.", processed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Provisioning operation processing failed.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
