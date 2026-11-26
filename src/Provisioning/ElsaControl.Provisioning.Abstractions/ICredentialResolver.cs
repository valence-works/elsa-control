namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Resolves a credential reference at run time. Azure workload identity is
/// first. Later resolvers add AWS role assumption, GCP workload identity
/// federation, or a kubeconfig held in a vault.
/// </summary>
public interface ICredentialResolver
{
    bool CanResolve(CredentialReference reference);

    Task<ResolvedCredential> ResolveAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default);
}
