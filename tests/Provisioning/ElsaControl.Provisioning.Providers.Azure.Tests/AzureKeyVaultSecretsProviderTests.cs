using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

public sealed class AzureKeyVaultSecretsProviderTests
{
    public const string StagingUrl =
        "azurekeyvault://ecseedkvtud53zotij43k.vault.azure.net/keys/pulumi-secrets";

    [Theory]
    [InlineData("ecseedkvtud53zotij43k/pulumi-secrets")]
    [InlineData(StagingUrl)]
    [InlineData("azurekeyvault://ecseedkvtud53zotij43k.vault.azure.net/keys/pulumi-secrets/version")]
    public void Parses_short_form_and_full_pulumi_url(string locator)
    {
        var provider = AzureKeyVaultSecretsProvider.From(
            new SecretsProviderReference(SecretsProviderReference.AzureKeyVaultKind, locator));
        Assert.Equal("ecseedkvtud53zotij43k", provider.VaultName);
        Assert.Equal("pulumi-secrets", provider.KeyName);
        Assert.Equal(StagingUrl, provider.SecretsProviderUrl);
    }

    [Fact]
    public void Round_trips_the_renderer_and_bicep_locator()
    {
        var provider = AzureKeyVaultSecretsProvider.From(
            new SecretsProviderReference(SecretsProviderReference.AzureKeyVaultKind, StagingUrl));
        Assert.Equal(StagingUrl, provider.SecretsProviderUrl);
        var again = AzureKeyVaultSecretsProvider.From(
            new SecretsProviderReference(SecretsProviderReference.AzureKeyVaultKind, provider.SecretsProviderUrl));
        Assert.Equal(provider.VaultName, again.VaultName);
        Assert.Equal(provider.KeyName, again.KeyName);
        Assert.Equal(StagingUrl, again.SecretsProviderUrl);
    }

    [Theory]
    [InlineData("vault-only")]
    [InlineData("https://ecseedkvtud53zotij43k.vault.azure.net/keys/pulumi-secrets")]
    [InlineData("azurekeyvault://ecseedkvtud53zotij43k/keys/pulumi-secrets")]
    [InlineData("kv/name/extra")]
    [InlineData("azurekeyvault://not a vault.vault.azure.net/keys/pulumi-secrets")]
    public void Rejects_anything_that_is_not_vault_slash_key_or_the_pulumi_url(string locator)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            AzureKeyVaultSecretsProvider.From(
                new SecretsProviderReference(SecretsProviderReference.AzureKeyVaultKind, locator)));
        Assert.Contains("azurekeyvault://", error.Message, StringComparison.Ordinal);
        Assert.Contains("{vault}/{key}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_from_the_full_url_keeps_the_canonical_secrets_provider()
    {
        var runner = new AzurePulumiAutomationRunner();
        var target = new ProvisioningTarget(
            AzurePulumiAutomationRunner.ProviderId,
            ProvisioningStackKind.Platform,
            "staging",
            new CredentialReference(CredentialReference.AzureWorkloadIdentityKind, "workload-identity-client"),
            new StateBackendReference(StateBackendReference.AzureBlobKind, "stpulumitud53zotij43k/pulumi-state"),
            new SecretsProviderReference(SecretsProviderReference.AzureKeyVaultKind, StagingUrl));
        var workspace = runner.CreateWorkspace(
            target,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["environmentName"] = "staging" },
            new Dictionary<string, string>(StringComparer.Ordinal));
        Assert.Equal(StagingUrl, workspace.SecretsProviderUrl);
        Assert.Equal("stpulumitud53zotij43k", workspace.EnvironmentVariables["AZURE_STORAGE_ACCOUNT"]);
    }
}
