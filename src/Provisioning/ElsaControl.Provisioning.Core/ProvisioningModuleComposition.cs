using ElsaControl.Provisioning.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Composes the provisioning module. The default production flag is off, so this
/// registers nothing unless <c>Provisioning:Enabled=true</c>.
/// </summary>
public static class ProvisioningModuleComposition
{
    public static bool Add(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(ProvisioningModuleOptions.ConfigurationSection)
                          .Get<ProvisioningModuleOptions>()
                      ?? new ProvisioningModuleOptions();
        if (!options.Enabled)
            return false;
        services.TryAddSingleton(Options.Create(options));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<InMemoryProvisioningStore>();
        services.TryAddSingleton<IProvisioningOperationStore>(provider =>
            provider.GetRequiredService<InMemoryProvisioningStore>());
        services.TryAddSingleton<IProvisioningEventStore>(provider =>
            provider.GetRequiredService<InMemoryProvisioningStore>());
        services.TryAddSingleton<IProvisioningStackLockStore>(provider =>
            provider.GetRequiredService<InMemoryProvisioningStore>());
        services.TryAddSingleton<IProvisioningService, ProvisioningService>();
        services.TryAddSingleton<ProvisioningOperationWorker>();

        if (string.Equals(options.Provider, "fake", StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<FakeInfrastructureProvider>();
            services.TryAddSingleton<IInfrastructureProvider>(provider =>
                provider.GetRequiredService<FakeInfrastructureProvider>());
            services.TryAddSingleton<ICredentialResolver, FakeCredentialResolver>();
        }

        var runWorker = environment is null || !environment.IsEnvironment("Testing");
        if (runWorker)
            services.AddHostedService<ProvisioningOperationHostedService>();
        return true;
    }
}
