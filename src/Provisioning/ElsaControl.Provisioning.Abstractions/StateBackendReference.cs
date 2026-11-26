namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// A locator for a pluggable stack-state backend. Azure Blob is first; the
/// reference never carries access keys.
/// </summary>
public sealed record StateBackendReference
{
    public const string AzureBlobKind = "azure-blob";
    public const string InMemoryKind = "in-memory";

    public StateBackendReference(string kind, string locator, bool lockingEnabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(locator);
        Kind = kind.Trim();
        Locator = locator.Trim();
        LockingEnabled = lockingEnabled;
    }

    public string Kind { get; }
    public string Locator { get; }
    public bool LockingEnabled { get; }
}
