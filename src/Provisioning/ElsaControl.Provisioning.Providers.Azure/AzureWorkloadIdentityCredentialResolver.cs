using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Providers.Azure;

public sealed class AzureWorkloadIdentityCredentialResolver : ICredentialResolver
{
    public const string Kind = "azure-workload-identity";

    public bool CanResolve(CredentialReference reference) =>
        string.Equals(reference.Kind, Kind, StringComparison.Ordinal);

    public Task<ResolvedCredential> ResolveAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanResolve(reference))
            throw new InvalidOperationException("Credential kind is not Azure workload identity.");
        if (!AzureBlobStateOptions.IsSafeName(reference.Locator) && !LooksLikeClientId(reference.Locator))
            throw new InvalidOperationException("Workload identity locator is unsafe.");
        return Task.FromResult(new ResolvedCredential(Kind, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AZURE_CLIENT_ID"] = reference.Locator
        }));
    }

    private static bool LooksLikeClientId(string value) =>
        Guid.TryParse(value, out _);
}
