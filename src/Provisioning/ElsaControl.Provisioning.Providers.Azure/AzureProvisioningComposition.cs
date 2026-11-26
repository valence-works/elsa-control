using ElsaControl.Provisioning.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Registers the Azure provider. The Control API does not call this unless a
/// later cutover turns the module on; tests compose it explicitly.
/// </summary>
public static class AzureProvisioningComposition
{
    public static IServiceCollection AddAzureInfrastructureProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(AzureProvisioningOptions.ConfigurationSection)
                          .Get<AzureProvisioningOptions>()
                      ?? new AzureProvisioningOptions();
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton<IStateBackend, AzureBlobStateBackend>();
        services.TryAddSingleton<ISecretsProvider, AzureKeyVaultSecretsProvider>();
        services.TryAddSingleton<ICredentialResolver, AzureWorkloadIdentityCredentialResolver>();
        services.TryAddSingleton<IPulumiAutomation, PulumiAutomationRunner>();
        services.TryAddSingleton<IInfrastructureProvider, AzureInfrastructureProvider>();
        return services;
    }
}
