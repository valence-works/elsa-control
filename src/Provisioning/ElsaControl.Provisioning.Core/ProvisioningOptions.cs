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
    /// Optional SQLite path for the module's own tables. Empty keeps the
    /// in-memory store (tests). Staging sets a durable path.
    /// </summary>
    public string? StorePath { get; set; }

    /// <summary>
    /// Pulumi organization prefix used on self-managed backends for
    /// <c>StackReference</c> names. Default is <c>organization</c>.
    /// </summary>
    public string PulumiOrganization { get; set; } = "organization";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int BatchSize { get; set; } = 10;
}
