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
                ["Provisioning:SeedVaultUri"] = "https://kv.example.vault.azure.net/",
                ["Provisioning:CredentialLocator"] = "d57a6279-2806-4289-9442-d862b53451a1"
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

    [Fact]
    public void Azure_provider_does_not_require_a_shared_seed_vault()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provisioning:WorkerEnabled"] = "true",
                ["Provisioning:AllowFakeProvider"] = "false"
            })
            .Build();
        services.AddProvisioning(configuration, runWorker: true, allowFakeProvider: false);
        services.AddAzureProvisioning(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<EmptyProvisioningSecretResolver>(provider.GetRequiredService<IProvisioningSecretResolver>());
        var worker = provider.GetRequiredService<ProvisioningOperationWorker>();
        Assert.NotNull(worker);
        var options = configuration.GetSection(ProvisioningOptions.ConfigurationSection).Get<ProvisioningOptions>()!;
        Assert.Equal(
            TimeSpan.FromSeconds(ProvisioningReplicaIdentity.DefaultHeartbeatSeconds),
            options.HeartbeatInterval);
        Assert.Equal(
            TimeSpan.FromSeconds(ProvisioningReplicaIdentity.DefaultLeaseSeconds),
            options.LeaseDuration);
        Assert.Equal(30, ProvisioningReplicaIdentity.DefaultLeaseSeconds);
        Assert.True(options.LeaseDuration >= options.HeartbeatInterval * 3);
        options.EnsureHeartbeatFitsLease();
        Assert.Equal(
            options.LeaseDuration,
            provider.GetRequiredService<ProvisioningReplicaIdentity>().Lease);
    }
}
