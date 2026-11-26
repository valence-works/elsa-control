using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Provisioning.Core;

public static class ProvisioningServiceCollectionExtensions
{
    /// <summary>
    /// Registers the provisioning module. The hosted worker is added only when
    /// <paramref name="runWorker"/> is true and <c>Provisioning:WorkerEnabled</c>
    /// is true. The default is off, so the existing Bicep runner is unchanged.
    /// </summary>
    public static IServiceCollection AddProvisioning(
        this IServiceCollection services,
        IConfiguration configuration,
        bool runWorker = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ProvisioningOptions>(configuration.GetSection(ProvisioningOptions.ConfigurationSection));
        services.AddSingleton<InMemoryProvisioningStore>();
        services.AddSingleton<IProvisioningOperationStore>(provider => provider.GetRequiredService<InMemoryProvisioningStore>());
        services.AddSingleton<IProvisioningEventStore>(provider => provider.GetRequiredService<InMemoryProvisioningStore>());
        services.AddSingleton<IProvisioningStackLockStore>(provider => provider.GetRequiredService<InMemoryProvisioningStore>());
        services.AddSingleton<FakeInfrastructureProvider>();
        services.AddSingleton<IInfrastructureProvider>(provider => provider.GetRequiredService<FakeInfrastructureProvider>());
        services.AddSingleton<IProvisioningService, ProvisioningService>();
        services.AddSingleton<ProvisioningOperationWorker>();

        var options = configuration.GetSection(ProvisioningOptions.ConfigurationSection).Get<ProvisioningOptions>()
                      ?? new ProvisioningOptions();
        if (runWorker && options.WorkerEnabled)
            services.AddHostedService<ProvisioningOperationHostedService>();

        return services;
    }
}
