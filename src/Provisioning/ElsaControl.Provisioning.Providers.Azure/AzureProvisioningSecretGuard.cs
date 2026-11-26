using ElsaControl.Provisioning.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Fail closed at startup when the worker is on and seed values cannot be
/// resolved.
/// </summary>
public sealed class AzureProvisioningSecretGuard(
    IProvisioningSecretResolver secrets,
    IOptions<ProvisioningOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value;
        if (!configured.WorkerEnabled || configured.AllowFakeProvider)
            return;
        if (secrets is AzureKeyVaultProvisioningSecretResolver && !secrets.IsReady)
        {
            throw new InvalidOperationException("Provisioning seed vault is not configured.");
        }

        await secrets.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
