namespace ElsaControl.Provisioning.Core;

public sealed class ProvisioningOptions
{
    public const string ConfigurationSection = "Provisioning";

    /// <summary>
    /// Off by default. The existing Bicep runner remains the production path.
    /// </summary>
    public bool WorkerEnabled { get; set; }

    /// <summary>
    /// When the worker is on, the selected provider must be the real Azure
    /// provider outside tests. The fake provider is test-only.
    /// </summary>
    public bool AllowFakeProvider { get; set; }

    /// <summary>
    /// File/passphrase Pulumi backends are test-only. Production uses Azure
    /// Blob plus Key Vault.
    /// </summary>
    public bool AllowFileBackend { get; set; }

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int BatchSize { get; set; } = 10;
}
