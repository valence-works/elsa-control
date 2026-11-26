namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// A locator the module resolves at run time. The value is never a secret.
/// Kind examples: <c>azure-workload-identity</c>, later <c>aws-role</c>,
/// <c>gcp-workload-identity</c>, <c>kubeconfig-vault</c>.
/// </summary>
public sealed record CredentialReference
{
    public CredentialReference(string kind, string locator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(locator);
        Kind = kind;
        Locator = locator;
    }

    public string Kind { get; }

    public string Locator { get; }
}

public sealed record ResolvedCredential
{
    public ResolvedCredential(string kind, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        Kind = kind;
        Environment = environment ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public string Kind { get; }

    /// <summary>
    /// Process environment for the provider run. Values here are locators and
    /// identity names only — never secret material.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; }
}
