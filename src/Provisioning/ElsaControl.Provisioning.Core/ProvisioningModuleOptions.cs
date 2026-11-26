namespace ElsaControl.Provisioning.Core;

public sealed class ProvisioningModuleOptions
{
    public const string ConfigurationSection = "Provisioning";

    /// <summary>
    /// Master switch. Off by default so production behaviour is unchanged.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Optional provider id to auto-register. <c>fake</c> is for tests only.
    /// Azure is registered by the Azure package, never by Core.
    /// </summary>
    public string? Provider { get; set; }

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int BatchSize { get; set; } = 10;
}
