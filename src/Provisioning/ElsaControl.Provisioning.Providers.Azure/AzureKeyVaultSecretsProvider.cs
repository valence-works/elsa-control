using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Pulumi secrets provider backed by a Key Vault key locator. The URI contains
/// names only — never a secret value.
/// </summary>
public sealed class AzureKeyVaultSecretsProvider : ISecretsProvider
{
    public AzureKeyVaultSecretsProvider(AzureProvisioningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Secrets.Validate();
        VaultName = options.Secrets.VaultName;
        KeyName = options.Secrets.KeyName;
        Uri = $"azurekeyvault://{VaultName}.vault.azure.net/keys/{KeyName}";
    }

    public string Kind => "azure-keyvault";

    public string Uri { get; }

    public string VaultName { get; }

    public string KeyName { get; }
}
