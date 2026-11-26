using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using Microsoft.Extensions.Options;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Pins Pulumi only when the provisioning worker is on, the Azure provider is
/// the composed implementation, and dispatch options are complete (including
/// SeedVaultUri). Otherwise the first Create records Bicep.
/// </summary>
public sealed class AzurePulumiProvisioningBackendSelector(
    IOptions<ProvisioningOptions> provisioning,
    IOptions<AzurePulumiDispatchOptions> dispatch,
    IInfrastructureProvider? provider = null) : IElsaInstanceProvisioningBackendSelector
{
    public const string AzureProviderId = "azure";

    public ElsaInstanceProvisioningBackend SelectForCreate()
    {
        if (!provisioning.Value.WorkerEnabled
            || !dispatch.Value.IsComplete
            || !string.Equals(provider?.ProviderId, AzureProviderId, StringComparison.Ordinal))
        {
            return ElsaInstanceProvisioningBackend.Bicep;
        }

        return ElsaInstanceProvisioningBackend.Pulumi;
    }
}
