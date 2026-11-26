using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

public sealed class FakeCredentialResolver : ICredentialResolver
{
    public const string Kind = "fake";

    public bool CanResolve(CredentialReference reference) =>
        string.Equals(reference.Kind, Kind, StringComparison.Ordinal);

    public Task<ResolvedCredential> ResolveAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanResolve(reference))
            throw new InvalidOperationException("Credential kind is not fake.");
        return Task.FromResult(new ResolvedCredential(Kind, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PROVISIONING_CREDENTIAL_KIND"] = Kind
        }));
    }
}
