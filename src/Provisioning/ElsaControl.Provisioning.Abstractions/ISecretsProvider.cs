namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Pluggable Pulumi secrets provider. Key Vault is first. Encrypts anything
/// that must sit in state; secret values never appear in plaintext state.
/// </summary>
public interface ISecretsProvider
{
    string Kind { get; }
    string SecretsProviderUrl { get; }
}
