namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Locators and host names needed to enqueue an engine stack. Bound from the
/// <c>Provisioning</c> section. Completeness includes <see cref="SeedVaultUri"/>.
/// </summary>
public sealed class AzurePulumiDispatchOptions
{
    public const string ConfigurationSection = "Provisioning";

    public string? EnvironmentName { get; set; }
    public string? StateBackendLocator { get; set; }
    public string? SecretsProviderLocator { get; set; }
    public string? CredentialLocator { get; set; }
    public string? SqlBootstrapObjectId { get; set; }
    public string? SqlBootstrapLogin { get; set; }
    public string? SeedVaultUri { get; set; }
    public string? RegistryLoginServer { get; set; }
    public string? RegistryId { get; set; }

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(EnvironmentName)
        && !string.IsNullOrWhiteSpace(StateBackendLocator)
        && !string.IsNullOrWhiteSpace(SecretsProviderLocator)
        && !string.IsNullOrWhiteSpace(CredentialLocator)
        && Guid.TryParse(SqlBootstrapObjectId, out var bootstrap)
        && bootstrap != Guid.Empty
        && !string.IsNullOrWhiteSpace(SqlBootstrapLogin)
        && IsAbsoluteHttpsUri(SeedVaultUri)
        && IsRegistryLoginServer(RegistryLoginServer)
        && IsRegistryId(RegistryId);

    internal static bool IsAbsoluteHttpsUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.IsAbsoluteUri
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(uri.Host);

    internal static bool IsRegistryLoginServer(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.EndsWith(".azurecr.io", StringComparison.OrdinalIgnoreCase)
        && value.IndexOfAny(['/', ':', '?', '#']) < 0;

    internal static bool IsRegistryId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains("/providers/Microsoft.ContainerRegistry/registries/", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("..", StringComparison.Ordinal);
}
