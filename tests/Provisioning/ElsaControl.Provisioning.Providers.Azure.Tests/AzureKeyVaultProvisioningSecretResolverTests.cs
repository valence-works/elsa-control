using Azure.Identity;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

public sealed class AzureKeyVaultProvisioningSecretResolverTests
{
    [Fact]
    public void Uses_the_provisioner_managed_identity_not_default_azure_credential()
    {
        var credential = AzureKeyVaultProvisioningSecretResolver.CreateCredential(
            "https://ecseedkvtud53zotij43k.vault.azure.net/",
            "d57a6279-2806-4289-9442-d862b53451a1");
        Assert.IsType<ManagedIdentityCredential>(credential);
        Assert.NotEqual(nameof(DefaultAzureCredential), credential.GetType().Name);
    }

    [Fact]
    public void Missing_credential_locator_fails_closed()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            AzureKeyVaultProvisioningSecretResolver.CreateCredential(
                "https://ecseedkvtud53zotij43k.vault.azure.net/",
                null));
        Assert.Equal("Provisioning credential locator is required.", error.Message);
    }

    [Fact]
    public void Composition_binds_the_provisioner_client_id()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provisioning:WorkerEnabled"] = "true",
                ["Provisioning:AllowFakeProvider"] = "false",
                ["Provisioning:SeedVaultUri"] = "https://ecseedkvtud53zotij43k.vault.azure.net/",
                ["Provisioning:CredentialLocator"] = "d57a6279-2806-4289-9442-d862b53451a1"
            })
            .Build();
        services.AddProvisioning(configuration, runWorker: true, allowFakeProvider: false);
        services.AddAzureProvisioning(configuration);
        using var provider = services.BuildServiceProvider();
        var secrets = Assert.IsType<AzureKeyVaultProvisioningSecretResolver>(
            provider.GetRequiredService<IProvisioningSecretResolver>());
        Assert.True(secrets.IsReady);
        Assert.IsType<ManagedIdentityCredential>(secrets.Credential);
        Assert.NotEqual(nameof(DefaultAzureCredential), secrets.Credential!.GetType().Name);
    }
}
