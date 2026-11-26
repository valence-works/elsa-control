namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Fail-closed admission for the elsa-platform program. One stack per
/// environment; it never creates a second registry.
/// </summary>
public static class AzurePlatformProgramPolicy
{
    public static void Validate(IReadOnlyDictionary<string, string> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.TryGetValue("environmentName", out var environment) || string.IsNullOrWhiteSpace(environment))
            throw new InvalidOperationException("Platform stack environment name is required.");
        if (!config.TryGetValue("location", out var location) || string.IsNullOrWhiteSpace(location))
            throw new InvalidOperationException("Platform stack location is required.");
        if (!AzureEngineProgramPolicy.AdmittedLocations.Contains(location))
            throw new InvalidOperationException("The requested location is not admitted.");
        if (!config.TryGetValue("registryName", out var registry) || string.IsNullOrWhiteSpace(registry))
            throw new InvalidOperationException("Platform stack registry name is required.");
        if (registry.Contains('/', StringComparison.Ordinal) || registry.Contains('\\', StringComparison.Ordinal))
            throw new InvalidOperationException("Registry name must not be a resource path.");
        if (!config.TryGetValue("registryResourceGroupName", out var registryGroup)
            || string.IsNullOrWhiteSpace(registryGroup))
        {
            throw new InvalidOperationException("Platform stack registry resource group is required.");
        }

        if (registryGroup.Contains('/', StringComparison.Ordinal) || registryGroup.Contains('\\', StringComparison.Ordinal))
            throw new InvalidOperationException("Registry resource group must not be a resource path.");
    }
}
