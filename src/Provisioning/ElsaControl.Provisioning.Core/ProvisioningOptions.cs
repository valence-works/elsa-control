namespace ElsaControl.Provisioning.Core;

public sealed class ProvisioningOptions
{
    public const string ConfigurationSection = "Provisioning";

    /// <summary>
    /// Off by default. The existing Bicep runner remains the production path.
    /// </summary>
    public bool WorkerEnabled { get; set; }

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int BatchSize { get; set; } = 10;
}
