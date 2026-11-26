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
        Assert.IsType<FakeInfrastructureProvider>(provider.GetRequiredService<IInfrastructureProvider>());
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

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal))
            .Build();
}
