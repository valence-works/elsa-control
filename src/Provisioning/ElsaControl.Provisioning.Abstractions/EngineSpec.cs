namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Cloud-neutral engine specification expressed in provider capabilities, not
/// Azure (or any other cloud) terms. The module does not know about customers.
/// </summary>
public sealed record EngineSpec
{
    public EngineSpec(
        string runtimeImage,
        DatabaseCapability? database = null,
        IReadOnlyList<SecretCapability>? secrets = null,
        EndpointCapability? endpoint = null,
        ScaleCapability? scale = null,
        IReadOnlyDictionary<string, string>? stackConfig = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeImage);
        RuntimeImage = runtimeImage;
        Database = database;
        Secrets = secrets ?? [];
        Endpoint = endpoint;
        Scale = scale;
        StackConfig = stackConfig ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public string RuntimeImage { get; }

    public DatabaseCapability? Database { get; }

    public IReadOnlyList<SecretCapability> Secrets { get; }

    public EndpointCapability? Endpoint { get; }

    public ScaleCapability? Scale { get; }

    /// <summary>
    /// Per-stack non-secret configuration. Secret values are forbidden here;
    /// secret slots use <see cref="SecretCapability.Locator"/> only.
    /// </summary>
    public IReadOnlyDictionary<string, string> StackConfig { get; }
}

public sealed record DatabaseCapability(string Engine, string SkuName, bool HighlyAvailable);

public sealed record SecretCapability
{
    public SecretCapability(string name, string locator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(locator);
        Name = name;
        Locator = locator;
    }

    public string Name { get; }

    /// <summary>A vault locator, never a secret value.</summary>
    public string Locator { get; }
}

public sealed record EndpointCapability(bool PublicIngress, string? HostnameHint);

public sealed record ScaleCapability(int MinReplicas, int MaxReplicas, double Cpu, int MemoryMiB);
