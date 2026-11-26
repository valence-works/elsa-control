using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ElsaControl.Provisioning.Core.Tests;

public sealed class ProvisioningModuleCompositionTests
{
    [Fact]
    public void Disabled_flag_registers_nothing()
    {
        var services = new ServiceCollection();
        var added = ProvisioningModuleComposition.Add(services, Config(("Provisioning:Enabled", "false")));
        Assert.False(added);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IProvisioningService));
        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType == typeof(ProvisioningOperationHostedService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IInfrastructureProvider));
    }

    [Fact]
    public void Missing_section_is_disabled()
    {
        var services = new ServiceCollection();
        var added = ProvisioningModuleComposition.Add(services, new ConfigurationBuilder().Build());
        Assert.False(added);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IProvisioningService));
    }

    [Fact]
    public void Enabled_fake_provider_registers_the_module_but_not_the_worker_in_testing()
    {
        var services = new ServiceCollection();
        var added = ProvisioningModuleComposition.Add(
            services,
            Config(("Provisioning:Enabled", "true"), ("Provisioning:Provider", "fake")),
            new TestingEnvironment());
        Assert.True(added);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IProvisioningService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IInfrastructureProvider));
        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType == typeof(ProvisioningOperationHostedService));
    }

    [Fact]
    public void Enabled_outside_testing_registers_the_hosted_worker()
    {
        var services = new ServiceCollection();
        ProvisioningModuleComposition.Add(
            services,
            Config(("Provisioning:Enabled", "true"), ("Provisioning:Provider", "fake")),
            new ProductionLikeEnvironment());
        Assert.Contains(services, descriptor => descriptor.ImplementationType == typeof(ProvisioningOperationHostedService));
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal))
            .Build();

    private sealed class TestingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class ProductionLikeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
