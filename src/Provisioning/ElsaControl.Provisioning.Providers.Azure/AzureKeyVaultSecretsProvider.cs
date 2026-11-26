using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Key Vault-backed Pulumi secrets provider. Encrypts anything that must sit in
/// state. Vault names and key names only; no secret values.
/// </summary>
public sealed class AzureKeyVaultSecretsProvider : ISecretsProvider
{
    public AzureKeyVaultSecretsProvider(string vaultName, string keyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultName);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        VaultName = vaultName.Trim();
        KeyName = keyName.Trim();
        SecretsProviderUrl = $"azurekeyvault://{VaultName}.vault.azure.net/keys/{KeyName}";
    }

    public string Kind => SecretsProviderReference.AzureKeyVaultKind;
    public string VaultName { get; }
    public string KeyName { get; }
    public string SecretsProviderUrl { get; }

    public static AzureKeyVaultSecretsProvider From(SecretsProviderReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.Kind, SecretsProviderReference.AzureKeyVaultKind, StringComparison.Ordinal))
            throw new ArgumentException("Key Vault secrets provider required.", nameof(reference));
        var parts = reference.Locator.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new ArgumentException("Key Vault locator is '{vault}/{key}'.", nameof(reference));
        return new AzureKeyVaultSecretsProvider(parts[0], parts[1]);
    }
}
