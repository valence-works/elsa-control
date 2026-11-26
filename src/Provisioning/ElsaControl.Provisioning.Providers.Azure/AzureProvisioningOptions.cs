namespace ElsaControl.Provisioning.Providers.Azure;

public sealed class AzureProvisioningOptions
{
    public const string ConfigurationSection = "Provisioning:Azure";

    public AzureBlobStateOptions State { get; set; } = new();

    public AzureKeyVaultSecretsOptions Secrets { get; set; } = new();

    public void Validate()
    {
        State.Validate();
        Secrets.Validate();
    }
}

public sealed class AzureBlobStateOptions
{
    public string AccountName { get; set; } = "";

    public string ContainerName { get; set; } = "";

    public void Validate()
    {
        if (!IsSafeName(AccountName) || !IsSafeName(ContainerName))
            throw new InvalidOperationException("Azure Blob state locators are missing or unsafe.");
    }

    internal static bool IsSafeName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
        && value.Length is >= 3 and <= 63;
}

public sealed class AzureKeyVaultSecretsOptions
{
    public string VaultName { get; set; } = "";

    public string KeyName { get; set; } = "";

    public void Validate()
    {
        if (!AzureBlobStateOptions.IsSafeName(VaultName) || !AzureBlobStateOptions.IsSafeName(KeyName))
            throw new InvalidOperationException("Azure Key Vault secrets-provider locators are missing or unsafe.");
    }
}
