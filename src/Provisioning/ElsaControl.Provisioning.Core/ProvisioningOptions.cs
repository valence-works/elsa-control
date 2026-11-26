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

    /// <summary>
    /// Optional vault for allowlisted secret locators such as an admin
    /// password override. Engine seeds are generated per stack, not loaded
    /// from this vault.
    /// </summary>
    public string? SeedVaultUri { get; set; }

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Lease heartbeat. Default 5s; the replica lease is 30s so several
    /// missed beats are still live.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(
        ProvisioningReplicaIdentity.DefaultHeartbeatSeconds);

    public int BatchSize { get; set; } = 10;
}
