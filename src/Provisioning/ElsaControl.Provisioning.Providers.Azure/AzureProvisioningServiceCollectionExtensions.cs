using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Provisioning.Providers.Azure;

public static class AzureProvisioningServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the unconfigured/fake provider with the Azure Pulumi provider.
    /// Call only when the provisioning worker flag is on, outside tests.
    /// </summary>
    public static IServiceCollection AddAzureProvisioning(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(ProvisioningOptions.ConfigurationSection).Get<ProvisioningOptions>()
                      ?? new ProvisioningOptions();
        services.AddSingleton(new AzurePulumiAutomationRunner(options.PulumiOrganization));
        services.AddSingleton<ICredentialResolver, AzureWorkloadIdentityCredentials>();
        services.AddSingleton<IPulumiAutomationClient, LocalPulumiAutomationClient>();
        services.AddSingleton<IResourceGroupAbsenceProbe, RecordingResourceGroupAbsenceProbe>();
        services.AddSingleton<AzureInfrastructureProvider>();
        services.AddSingleton<IInfrastructureProvider>(provider =>
            provider.GetRequiredService<AzureInfrastructureProvider>());
        return services;
    }
}
