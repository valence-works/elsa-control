namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// A locator for credentials resolved at run time. Never carries secret values.
/// </summary>
public sealed record CredentialReference
{
    public const string AzureWorkloadIdentityKind = "azure-workload-identity";
    public const string InMemoryKind = "in-memory";

    public CredentialReference(string kind, string? locator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        Kind = kind.Trim();
        Locator = string.IsNullOrWhiteSpace(locator) ? null : locator.Trim();
    }

    public string Kind { get; }
    public string? Locator { get; }
}
