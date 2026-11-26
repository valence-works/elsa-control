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
    /// Lease heartbeat. Default 5s; must stay at most one third of
    /// <see cref="LeaseDuration"/> so missed beats do not look expired.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(
        ProvisioningReplicaIdentity.DefaultHeartbeatSeconds);

    /// <summary>
    /// Replica lock lease. Default 30s. Heartbeats must be well below this.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(
        ProvisioningReplicaIdentity.DefaultLeaseSeconds);

    public int BatchSize { get; set; } = 10;

    public void EnsureHeartbeatFitsLease()
    {
        if (HeartbeatInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Provisioning heartbeat must be greater than zero.");
        }

        if (LeaseDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Provisioning lease duration must be greater than zero.");
        }

        if (HeartbeatInterval * 3 > LeaseDuration)
        {
            throw new InvalidOperationException(
                "Provisioning heartbeat must be at most one third of the lease duration.");
        }
    }
}
