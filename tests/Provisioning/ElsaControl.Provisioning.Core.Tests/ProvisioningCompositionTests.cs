using ElsaControl.Provisioning.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ElsaControl.Provisioning.Core.Tests;

public sealed class ProvisioningCompositionTests
{
    [Fact]
    public void Worker_is_not_registered_when_the_flag_is_off()
    {
        var services = new ServiceCollection();
        services.AddProvisioning(Configuration(("Provisioning:WorkerEnabled", "false")), runWorker: true);
        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType == typeof(ProvisioningOperationHostedService));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<UnconfiguredInfrastructureProvider>(provider.GetRequiredService<IInfrastructureProvider>());
        Assert.NotNull(provider.GetRequiredService<IProvisioningService>());
    }

    [Fact]
    public void Worker_is_registered_only_when_the_flag_is_on_and_runWorker_is_true()
    {
        var off = new ServiceCollection();
        off.AddProvisioning(Configuration(("Provisioning:WorkerEnabled", "true")), runWorker: false);
        Assert.DoesNotContain(off, descriptor => descriptor.ImplementationType == typeof(ProvisioningOperationHostedService));

        var on = new ServiceCollection();
        on.AddProvisioning(Configuration(("Provisioning:WorkerEnabled", "true")), runWorker: true);
        Assert.Contains(on, descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(ProvisioningOperationHostedService));
    }

    [Fact]
    public void Fake_provider_is_registered_only_when_tests_opt_in()
    {
        var production = new ServiceCollection();
        production.AddProvisioning(
            Configuration(("Provisioning:WorkerEnabled", "true")),
            runWorker: true,
            allowFakeProvider: false);
        using var productionProvider = production.BuildServiceProvider();
        Assert.IsType<UnconfiguredInfrastructureProvider>(productionProvider.GetRequiredService<IInfrastructureProvider>());

        var tests = new ServiceCollection();
        tests.AddProvisioning(
            Configuration(("Provisioning:WorkerEnabled", "true")),
            runWorker: true,
            allowFakeProvider: true);
        using var testProvider = tests.BuildServiceProvider();
        Assert.IsType<FakeInfrastructureProvider>(testProvider.GetRequiredService<IInfrastructureProvider>());
    }

    [Fact]
    public async Task Guard_refuses_to_start_when_the_flag_is_on_and_the_fake_is_selected()
    {
        var services = new ServiceCollection();
        services.AddProvisioning(
            Configuration(("Provisioning:WorkerEnabled", "true")),
            runWorker: true,
            allowFakeProvider: false);
        services.AddSingleton<IInfrastructureProvider, FakeInfrastructureProvider>();
        using var provider = services.BuildServiceProvider();
        var guard = provider.GetRequiredService<ProvisioningProviderGuard>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.StartAsync(CancellationToken.None));
        Assert.Equal("Provisioning worker cannot run with the fake provider outside tests.", error.Message);
    }

    [Fact]
    public async Task Guard_refuses_to_start_when_the_flag_is_on_and_no_real_provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProvisioning(
            Configuration(("Provisioning:WorkerEnabled", "true")),
            runWorker: true,
            allowFakeProvider: false);
        using var provider = services.BuildServiceProvider();
        var guard = provider.GetRequiredService<ProvisioningProviderGuard>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.StartAsync(CancellationToken.None));
        Assert.Equal("Provisioning worker is enabled but no real infrastructure provider is registered.", error.Message);
    }

    [Fact]
    public async Task Guard_refuses_to_start_when_the_flag_is_on_without_a_durable_store()
    {
        var services = new ServiceCollection();
        services.AddProvisioning(
            Configuration(("Provisioning:WorkerEnabled", "true")),
            runWorker: true,
            allowFakeProvider: false);
        services.AddSingleton<IInfrastructureProvider>(new UnconfiguredInfrastructureProvider());
        using var provider = services.BuildServiceProvider();
        // Replace the unconfigured provider with a stand-in that is not fake, still no durable store.
        var guard = new ProvisioningProviderGuard(
            new AzureStandIn(),
            provider.GetRequiredService<Stores.IProvisioningOperationStore>(),
            Microsoft.Extensions.Options.Options.Create(new ProvisioningOptions
            {
                WorkerEnabled = true,
                AllowFakeProvider = false
            }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.StartAsync(CancellationToken.None));
        Assert.Equal("Provisioning worker is enabled but no durable store is configured.", error.Message);
    }

    [Fact]
    public void Hosted_catalog_store_requires_sql_server()
    {
        Assert.False(ElsaControl.Provisioning.Persistence.EntityFrameworkCore.ProvisioningPersistenceServiceCollectionExtensions.TryGetCatalogStore(
            Configuration(("Database:Provider", "Sqlite"), ("ConnectionStrings:Catalog", "Data Source=:memory:")),
            out _,
            out _,
            requireSqlServer: true));
        Assert.False(ElsaControl.Provisioning.Persistence.EntityFrameworkCore.ProvisioningPersistenceServiceCollectionExtensions.TryGetCatalogStore(
            Configuration(("ConnectionStrings:Catalog", "Server=catalog")),
            out _,
            out _,
            requireSqlServer: true));
        Assert.True(ElsaControl.Provisioning.Persistence.EntityFrameworkCore.ProvisioningPersistenceServiceCollectionExtensions.TryGetCatalogStore(
            Configuration(("Database:Provider", "SqlServer"), ("ConnectionStrings:Catalog", "Server=catalog")),
            out var provider,
            out _,
            requireSqlServer: true));
        Assert.Equal("SqlServer", provider);
    }

    private sealed class AzureStandIn : IInfrastructureProvider
    {
        public string ProviderId => "azure";
        public IReadOnlySet<string> Capabilities { get; } = new HashSet<string>(StringComparer.Ordinal);

        public Task<ProviderRunResult> PreviewAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ProviderRunResult> ProvisionAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ProviderRunResult> UpdateAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ProviderRunResult> RefreshAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ProviderRunResult> CancelAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ProviderRunResult> DestroyAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal))
            .Build();
}
