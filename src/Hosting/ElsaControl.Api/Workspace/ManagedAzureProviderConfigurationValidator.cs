using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Workspace;

/// <summary>
/// Rejects half-enabled production compositions. Managed lifecycle admission,
/// provider hand-off/reconciliation, and Azure execution are one operational
/// pipeline and must start or stop together. The Ready-instance health monitor
/// probes through that pipeline, so it may only run while the pipeline runs.
/// </summary>
public sealed class ManagedAzureProviderConfigurationValidator(
    IOptions<ElsaInstanceLifecycleWorkerOptions> lifecycle,
    IOptions<AzureProviderOperationOptions> provider,
    AzureElsaInstanceProviderOptions? instanceProvider = null,
    IAzureProviderAuthorityPreflight? authorityPreflight = null,
    IOptions<ElsaInstanceHealthMonitorOptions>? healthMonitor = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var lifecycleEnabled = lifecycle.Value.Enabled;
        var providerWorkerEnabled = provider.Value.WorkerEnabled;
        var instanceProviderEnabled = instanceProvider?.Enabled == true;

        if (healthMonitor?.Value is { Enabled: true } monitor)
        {
            if (!lifecycleEnabled || !providerWorkerEnabled || !instanceProviderEnabled)
                throw new InvalidOperationException(
                    "The managed instance health monitor requires the lifecycle worker, instance provider, and Azure provider worker.");
            monitor.Validate();
        }

        if (lifecycleEnabled || providerWorkerEnabled || instanceProviderEnabled)
        {
            if (!lifecycleEnabled || !providerWorkerEnabled || !instanceProviderEnabled)
                throw new InvalidOperationException(
                    "Managed Azure lifecycle requires the lifecycle worker, instance provider, and Azure provider worker to be enabled together.");
            instanceProvider!.Validate();
            ValidateProviderWorker(provider.Value);

            if (authorityPreflight is null)
                throw new InvalidOperationException("Managed Azure lifecycle requires an Azure authority preflight.");
            var result = await authorityPreflight.ValidateAsync(cancellationToken);
            if (!result.Succeeded)
                throw new InvalidOperationException($"Managed Azure authority preflight failed ({result.Code}).");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void ValidateProviderWorker(AzureProviderOperationOptions options)
    {
        if (options.BatchSize is < 1 or > 100)
            throw new InvalidOperationException("The Azure provider worker batch size is outside the governed range.");
        if (options.PollInterval < TimeSpan.FromSeconds(1) || options.PollInterval > TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("The Azure provider worker poll interval is outside the governed range.");
    }
}
