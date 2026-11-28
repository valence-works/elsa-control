using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Key Vault-backed Pulumi secrets provider. Encrypts anything that must sit in
/// state. Vault names and key names only; no secret values.
/// </summary>
public sealed class AzureKeyVaultSecretsProvider : ISecretsProvider
{
    public const string AzureKeyVaultUrlPrefix = "azurekeyvault://";
    public const string VaultHostSuffix = ".vault.azure.net";
    public const string KeysPath = "/keys/";

    public AzureKeyVaultSecretsProvider(string vaultName, string keyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultName);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        VaultName = vaultName.Trim();
        KeyName = keyName.Trim();
        SecretsProviderUrl = $"{AzureKeyVaultUrlPrefix}{VaultName}{VaultHostSuffix}{KeysPath}{KeyName}";
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
        if (!TryParseLocator(reference.Locator, out var vaultName, out var keyName))
        {
            throw new ArgumentException(
                "Key Vault locator must be '{vault}/{key}' or 'azurekeyvault://{vault}.vault.azure.net/keys/{key}'.",
                nameof(reference));
        }

        return new AzureKeyVaultSecretsProvider(vaultName, keyName);
    }

    /// <summary>
    /// Accepts the short <c>{vault}/{key}</c> form and the full Pulumi
    /// <c>azurekeyvault://{vault}.vault.azure.net/keys/{key}</c> URL the
    /// Bicep module and worker overlay emit. Parsing never uses
    /// <see cref="Uri"/>: wrapping the URL as a host turns the inner
    /// <c>azurekeyvault:</c> scheme into an invalid port.
    /// </summary>
    public static bool TryParseLocator(string? locator, out string vaultName, out string keyName)
    {
        vaultName = "";
        keyName = "";
        if (string.IsNullOrWhiteSpace(locator))
            return false;

        var trimmed = locator.Trim();
        if (trimmed.StartsWith(AzureKeyVaultUrlPrefix, StringComparison.OrdinalIgnoreCase))
            return TryParseUrlLocator(trimmed, out vaultName, out keyName);

        return TryParseShortLocator(trimmed, out vaultName, out keyName);
    }

    private static bool TryParseUrlLocator(string locator, out string vaultName, out string keyName)
    {
        vaultName = "";
        keyName = "";
        var rest = locator[AzureKeyVaultUrlPrefix.Length..];
        var hostSuffix = VaultHostSuffix + KeysPath;
        var hostIndex = rest.IndexOf(hostSuffix, StringComparison.OrdinalIgnoreCase);
        if (hostIndex <= 0)
            return false;

        var vault = rest[..hostIndex];
        var keyPart = rest[(hostIndex + hostSuffix.Length)..];
        var cut = keyPart.IndexOfAny(['?', '/']);
        if (cut >= 0)
            keyPart = keyPart[..cut];
        if (!IsVaultName(vault) || !IsKeyName(keyPart))
            return false;

        vaultName = vault;
        keyName = keyPart;
        return true;
    }

    private static bool TryParseShortLocator(string locator, out string vaultName, out string keyName)
    {
        vaultName = "";
        keyName = "";
        var slash = locator.IndexOf('/');
        if (slash <= 0 || slash != locator.LastIndexOf('/'))
            return false;

        var vault = locator[..slash];
        var key = locator[(slash + 1)..];
        if (!IsVaultName(vault) || !IsKeyName(key))
            return false;

        vaultName = vault;
        keyName = key;
        return true;
    }

    private static bool IsVaultName(string value) =>
        value.Length is >= 3 and <= 24
        && char.IsAsciiLetterOrDigit(value[0])
        && char.IsAsciiLetterOrDigit(value[^1])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-');

    private static bool IsKeyName(string value) =>
        value.Length is >= 1 and <= 127
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-');
}
