using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Resolves Azure workload identity into process environment values. Never
/// returns client secrets or storage keys.
/// </summary>
public sealed class AzureWorkloadIdentityCredentials : ICredentialResolver
{
    public string Kind => CredentialReference.AzureWorkloadIdentityKind;

    public Task<IReadOnlyDictionary<string, string>> ResolveEnvironmentAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.Kind, Kind, StringComparison.Ordinal))
            throw new ArgumentException("Azure workload identity is required.", nameof(reference));

        var clientId = reference.Locator ?? string.Empty;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AZURE_CLIENT_ID"] = clientId,
            ["ARM_CLIENT_ID"] = clientId,
            ["ARM_USE_MSI"] = "true"
        };
        CopyEnvironment(environment, "ARM_SUBSCRIPTION_ID");
        CopyEnvironment(environment, "AZURE_SUBSCRIPTION_ID");
        CopyEnvironment(environment, "ARM_TENANT_ID");
        environment.Remove("ARM_USE_CLI");
        return Task.FromResult<IReadOnlyDictionary<string, string>>(environment);
    }

    private static void CopyEnvironment(IDictionary<string, string> environment, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
            environment[name] = value;
    }
}
