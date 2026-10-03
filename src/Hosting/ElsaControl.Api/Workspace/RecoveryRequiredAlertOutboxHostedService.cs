using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Telemetry;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Workspace;

/// <summary>
/// Delivers RecoveryRequired outbox rows that survived catalog commit.
/// A crash after commit still delivers on the next tick or nudge.
/// </summary>
public sealed class RecoveryRequiredAlertOutboxHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<ElsaInstanceLifecycleWorkerOptions> options,
    ILogger<RecoveryRequiredAlertOutboxHostedService> logger,
    IRecoveryRequiredAlertDispatchSignal? dispatchSignal = null) : BackgroundService
{
    private readonly IRecoveryRequiredAlertDispatchSignal _dispatchSignal =
        dispatchSignal ?? RecoveryRequiredAlertDispatchSignal.Instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = ElsaInstanceLifecycleHostedService.NormalizePollInterval(options.Value.PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "RecoveryRequired alert outbox dispatch failed.");
            }

            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var tick = Task.Delay(interval, wait.Token);
                var nudged = _dispatchSignal.WaitAsync(wait.Token);
                await Task.WhenAny(tick, nudged);
                wait.Cancel();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    internal async Task DispatchPendingAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IRecoveryRequiredAlertOutboxDispatcher>();
        var delivered = await dispatcher.DispatchPendingAsync(32, stoppingToken);
        if (delivered > 0)
            logger.LogInformation("Delivered {AlertCount} RecoveryRequired operator alerts.", delivered);
    }
}
