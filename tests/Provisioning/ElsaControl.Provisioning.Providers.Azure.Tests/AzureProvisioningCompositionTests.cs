using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

public sealed class AzureProvisioningCompositionTests
{
    [Fact]
    public void Azure_provider_replaces_the_unconfigured_stand_in()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provisioning:WorkerEnabled"] = "true",
                ["Provisioning:AllowFakeProvider"] = "false",
                ["Provisioning:SeedVaultUri"] = "https://kv.example.vault.azure.net/"
            })
            .Build();
        services.AddProvisioning(configuration, runWorker: true, allowFakeProvider: false);
        services.AddAzureProvisioning(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<AzureInfrastructureProvider>(provider.GetRequiredService<IInfrastructureProvider>());
        Assert.IsType<AzureArmResourceGroupAbsenceProbe>(provider.GetRequiredService<IResourceGroupAbsenceProbe>());
        var secrets = provider.GetRequiredService<IProvisioningSecretResolver>();
        Assert.IsType<AzureKeyVaultProvisioningSecretResolver>(secrets);
        Assert.True(secrets.IsReady);
        Assert.NotEqual(
            FakeInfrastructureProvider.Id,
            provider.GetRequiredService<IInfrastructureProvider>().ProviderId);
    }
}
