using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        var vault = configuration["Provisioning:SeedVaultUri"] ?? options.SeedVaultUri;
        var credentialLocator = configuration["Provisioning:CredentialLocator"]
                                ?? options.CredentialLocator
                                ?? configuration["ARM_CLIENT_ID"];

        services.AddSingleton(new AzurePulumiAutomationRunner(options.AllowFileBackend));
        services.AddSingleton<ICredentialResolver, AzureWorkloadIdentityCredentials>();
        if (string.IsNullOrWhiteSpace(vault))
            services.AddSingleton<IProvisioningSecretResolver, EmptyProvisioningSecretResolver>();
        else
        {
            services.AddSingleton<IProvisioningSecretResolver>(_ =>
                new AzureKeyVaultProvisioningSecretResolver(vault, credentialLocator));
        }

        services.AddSingleton<IPulumiAutomationClient, LocalPulumiAutomationClient>();
        services.AddSingleton<IResourceGroupAbsenceProbe, AzureArmResourceGroupAbsenceProbe>();
        services.AddSingleton<AzureInfrastructureProvider>(provider =>
            new AzureInfrastructureProvider(
                provider.GetRequiredService<AzurePulumiAutomationRunner>(),
                provider.GetRequiredService<ICredentialResolver>(),
                provider.GetRequiredService<IPulumiAutomationClient>(),
                provider.GetRequiredService<IResourceGroupAbsenceProbe>(),
                provider.GetRequiredService<IProvisioningSecretResolver>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<IOptions<ProvisioningOptions>>().Value));
        services.AddSingleton<IInfrastructureProvider>(provider =>
            provider.GetRequiredService<AzureInfrastructureProvider>());
        services.AddHostedService<AzureProvisioningSecretGuard>();
        return services;
    }
}
