using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElsaControl.Provisioning.Core;

public static class ProvisioningServiceCollectionExtensions
{
    /// <summary>
    /// Registers the provisioning module. The hosted worker is added only when
    /// <paramref name="runWorker"/> is true and <c>Provisioning:WorkerEnabled</c>
    /// is true. The default is off, so the existing Bicep runner is unchanged.
    /// The fake provider is registered only when <paramref name="allowFakeProvider"/>
    /// is true (tests). Otherwise the unconfigured provider is selected until a
    /// real provider replaces it. In-memory storage is tests-only; production
    /// replaces it with the EF durable store on Control's catalog database.
    /// </summary>
    public static IServiceCollection AddProvisioning(
        this IServiceCollection services,
        IConfiguration configuration,
        bool runWorker = false,
        bool allowFakeProvider = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ProvisioningOptions>(configuration.GetSection(ProvisioningOptions.ConfigurationSection));
        services.PostConfigure<ProvisioningOptions>(options =>
        {
            options.AllowFakeProvider = allowFakeProvider;
            if (allowFakeProvider)
                options.AllowFileBackend = true;
        });
        services.AddSingleton<ProvisioningRunRegistry>();
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ProvisioningOptions>>().Value;
            return new ProvisioningReplicaIdentity(lease: options.LeaseDuration);
        });
        services.TryAddSingleton(TimeProvider.System);

        var configured = configuration.GetSection(ProvisioningOptions.ConfigurationSection).Get<ProvisioningOptions>()
                         ?? new ProvisioningOptions();
        services.AddSingleton<InMemoryProvisioningStore>(provider =>
            new InMemoryProvisioningStore(provider.GetRequiredService<ProvisioningReplicaIdentity>()));
        services.AddSingleton<IProvisioningOperationStore>(provider =>
            provider.GetRequiredService<InMemoryProvisioningStore>());
        services.AddSingleton<IProvisioningEventStore>(provider =>
            provider.GetRequiredService<InMemoryProvisioningStore>());
        services.AddSingleton<IProvisioningStackLockStore>(provider =>
            provider.GetRequiredService<InMemoryProvisioningStore>());
        services.AddSingleton<FakeInfrastructureProvider>();
        services.AddSingleton<UnconfiguredInfrastructureProvider>();
        if (allowFakeProvider)
        {
            services.AddSingleton<IInfrastructureProvider>(provider =>
                provider.GetRequiredService<FakeInfrastructureProvider>());
        }
        else
        {
            services.AddSingleton<IInfrastructureProvider>(provider =>
                provider.GetRequiredService<UnconfiguredInfrastructureProvider>());
        }

        services.AddLogging();
        services.TryAddSingleton<IProvisioningStepLedger, NoOpProvisioningStepLedger>();
        services.AddSingleton<IProvisioningLiveEventSink, ProvisioningLiveEventSink>();
        services.AddSingleton<IProvisioningService>(provider =>
            new ProvisioningService(
                provider.GetRequiredService<IProvisioningOperationStore>(),
                provider.GetRequiredService<IProvisioningEventStore>(),
                provider.GetRequiredService<ProvisioningRunRegistry>()));
        services.AddSingleton<ProvisioningOperationWorker>(provider =>
            new ProvisioningOperationWorker(
                provider.GetRequiredService<IProvisioningOperationStore>(),
                provider.GetRequiredService<IProvisioningEventStore>(),
                provider.GetRequiredService<IInfrastructureProvider>(),
                provider.GetRequiredService<ProvisioningRunRegistry>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetService<ILogger<ProvisioningOperationWorker>>(),
                provider.GetRequiredService<IOptions<ProvisioningOptions>>().Value.HeartbeatInterval,
                provider.GetRequiredService<IProvisioningLiveEventSink>()));
        services.AddSingleton<ProvisioningProviderGuard>();

        if (runWorker && configured.WorkerEnabled)
        {
            services.AddHostedService(provider => provider.GetRequiredService<ProvisioningProviderGuard>());
            services.AddHostedService<ProvisioningOperationHostedService>();
        }

        return services;
    }
}
