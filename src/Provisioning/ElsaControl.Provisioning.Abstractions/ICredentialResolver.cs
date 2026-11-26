namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Pluggable credential resolution. Resolves a reference into process
/// environment values at run time. Never returns secret values to callers.
/// </summary>
public interface ICredentialResolver
{
    string Kind { get; }

    Task<IReadOnlyDictionary<string, string>> ResolveEnvironmentAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default);
}
