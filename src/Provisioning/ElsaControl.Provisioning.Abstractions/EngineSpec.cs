namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Cloud-neutral desired engine shape. Expressed in capability terms, never in
/// provider resource identifiers.
/// </summary>
public sealed record EngineSpec
{
    public EngineSpec(
        string runtimeImage,
        IReadOnlySet<string> requiredCapabilities,
        string? databaseKind = null,
        string? secretsKind = null,
        string? endpointKind = null,
        string? scaleKind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeImage);
        ArgumentNullException.ThrowIfNull(requiredCapabilities);
        if (requiredCapabilities.Count == 0)
            throw new ArgumentException("At least one capability is required.", nameof(requiredCapabilities));

        RuntimeImage = runtimeImage.Trim();
        RequiredCapabilities = requiredCapabilities;
        DatabaseKind = NullIfWhiteSpace(databaseKind);
        SecretsKind = NullIfWhiteSpace(secretsKind);
        EndpointKind = NullIfWhiteSpace(endpointKind);
        ScaleKind = NullIfWhiteSpace(scaleKind);
    }

    public string RuntimeImage { get; }
    public IReadOnlySet<string> RequiredCapabilities { get; }
    public string? DatabaseKind { get; }
    public string? SecretsKind { get; }
    public string? EndpointKind { get; }
    public string? ScaleKind { get; }

    public static EngineSpec PlatformEnvironment(string environmentName) =>
        new(
            runtimeImage: "platform",
            requiredCapabilities: new HashSet<string>(StringComparer.Ordinal)
            {
                ProvisioningCapability.SharedEnvironment,
                ProvisioningCapability.SharedRegistry
            },
            endpointKind: environmentName);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
