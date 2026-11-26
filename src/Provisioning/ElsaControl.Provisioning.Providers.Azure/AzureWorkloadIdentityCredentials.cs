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

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AZURE_CLIENT_ID"] = reference.Locator ?? string.Empty
        };
        return Task.FromResult<IReadOnlyDictionary<string, string>>(environment);
    }
}
