using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Refuses to start when the worker flag is on without a real provider and a
/// durable store. In-memory is tests-only.
/// </summary>
public sealed class ProvisioningProviderGuard(
    IInfrastructureProvider provider,
    IProvisioningOperationStore store,
    IOptions<ProvisioningOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value;
        if (!configured.WorkerEnabled)
            return Task.CompletedTask;
        configured.EnsureHeartbeatFitsLease();
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

        if (store is not IDurableProvisioningStore)
        {
            throw new InvalidOperationException(
                "Provisioning worker is enabled but no durable store is configured.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
