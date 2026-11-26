using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Providers.Azure;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

public sealed class AzureBackendConfigurationTests
{
    [Fact]
    public void Blob_backend_uses_locking_and_never_sets_a_storage_key()
    {
        var backend = new AzureBlobStateBackend(FixtureOptions());
        Assert.Equal("azure-blob", backend.Kind);
        Assert.True(backend.SupportsLocking);
        Assert.Equal("azblob://pulumi-state-fixture", backend.BackendUrl);
        Assert.Equal("stprovisioningfixture", backend.Environment["AZURE_STORAGE_ACCOUNT"]);
        Assert.DoesNotContain("AZURE_STORAGE_KEY", backend.Environment.Keys);
        Assert.DoesNotContain(backend.Environment.Values, value => value.Contains("key=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Key_vault_secrets_provider_is_a_locator_uri()
    {
        var secrets = new AzureKeyVaultSecretsProvider(FixtureOptions());
        Assert.Equal("azure-keyvault", secrets.Kind);
        Assert.Equal("azurekeyvault://kv-provisioning-fixture.vault.azure.net/keys/pulumi-state", secrets.Uri);
        Assert.DoesNotContain("secret", secrets.Uri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", secrets.Uri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Workload_identity_resolver_exposes_the_client_locator_only()
    {
        var resolver = new AzureWorkloadIdentityCredentialResolver();
        var resolved = await resolver.ResolveAsync(
            new CredentialReference(AzureWorkloadIdentityCredentialResolver.Kind, "mi-provisioning-fixture"));
        Assert.Equal("mi-provisioning-fixture", resolved.Environment["AZURE_CLIENT_ID"]);
        Assert.DoesNotContain("SECRET", resolved.Environment.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unsafe_blob_locators_fail_closed()
    {
        var options = FixtureOptions();
        options.State.AccountName = "not a locator";
        Assert.Throws<InvalidOperationException>(() => new AzureBlobStateBackend(options));
    }

    internal static AzureProvisioningOptions FixtureOptions() => new()
    {
        State = new AzureBlobStateOptions
        {
            AccountName = "stprovisioningfixture",
            ContainerName = "pulumi-state-fixture"
        },
        Secrets = new AzureKeyVaultSecretsOptions
        {
            VaultName = "kv-provisioning-fixture",
            KeyName = "pulumi-state"
        }
    };
}
