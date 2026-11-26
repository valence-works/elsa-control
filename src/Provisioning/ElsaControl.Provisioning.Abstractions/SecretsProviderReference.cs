namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// A locator for a pluggable Pulumi secrets provider. Key Vault is first; the
/// reference never carries secret values.
/// </summary>
public sealed record SecretsProviderReference
{
    public const string AzureKeyVaultKind = "azure-key-vault";
    public const string InMemoryKind = "in-memory";

    public SecretsProviderReference(string kind, string locator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(locator);
        Kind = kind.Trim();
        Locator = locator.Trim();
    }

    public string Kind { get; }
    public string Locator { get; }
}
