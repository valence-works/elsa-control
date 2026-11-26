using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Process lifetime for the module's own worker. Disabled unless
/// <see cref="ProvisioningOptions.WorkerEnabled"/> is true.
/// </summary>
public sealed class ProvisioningOperationHostedService(
    ProvisioningOperationWorker worker,
    IOptions<ProvisioningOptions> options,
    ILogger<ProvisioningOperationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configured = options.Value;
        if (!configured.WorkerEnabled)
        {
            logger.LogInformation("Provisioning worker is disabled.");
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
                await worker.ProcessOnceAsync(batchSize, stoppingToken).ConfigureAwait(false);
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
