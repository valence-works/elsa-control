using ElsaControl.Provisioning.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Refuses to start when the worker flag is on and the fake provider is
/// selected outside tests.
/// </summary>
public sealed class ProvisioningProviderGuard(
    IInfrastructureProvider provider,
    IOptions<ProvisioningOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value;
        if (!configured.WorkerEnabled)
            return Task.CompletedTask;
        if (configured.AllowFakeProvider)
            return Task.CompletedTask;
        if (string.Equals(provider.ProviderId, FakeInfrastructureProvider.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Provisioning worker cannot run with the fake provider outside tests.");
        }

        if (string.Equals(provider.ProviderId, UnconfiguredInfrastructureProvider.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Provisioning worker is enabled but no real infrastructure provider is registered.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
