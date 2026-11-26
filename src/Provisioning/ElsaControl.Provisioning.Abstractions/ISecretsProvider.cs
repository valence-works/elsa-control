namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Pluggable Pulumi secrets provider. Azure Key Vault is first. Anything that
/// must sit in state is encrypted; nothing is stored in plain text.
/// </summary>
public interface ISecretsProvider
{
    string Kind { get; }

    /// <summary>
    /// Pulumi secrets-provider URI, for example a Key Vault key locator.
    /// Never a secret value.
    /// </summary>
    string Uri { get; }
}
