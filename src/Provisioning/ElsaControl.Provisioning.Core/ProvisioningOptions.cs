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

    /// <summary>
    /// Current provisioner workload-identity client id. Overlay wins over a
    /// stored request snapshot after a settings fix.
    /// </summary>
    public string? CredentialLocator { get; set; }

    /// <summary>
    /// Current Blob state locator <c>{account}/{container}</c>.
    /// </summary>
    public string? StateBackendLocator { get; set; }

    /// <summary>
    /// Current Key Vault secrets-provider locator. Short
    /// <c>{vault}/{key}</c> or the full <c>azurekeyvault://</c> URL.
    /// </summary>
    public string? SecretsProviderLocator { get; set; }

    /// <summary>
    /// Commercial registry login server used when the platform stack is
    /// absent. Config wins over a <c>StackReference</c>.
    /// </summary>
    public string? RegistryLoginServer { get; set; }

    /// <summary>
    /// Commercial registry resource id used when the platform stack is
    /// absent. Overlayed at run time; never stored in the request snapshot.
    /// </summary>
    public string? RegistryId { get; set; }

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
