namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Pluggable Pulumi state backend. Azure Blob is first, with locking.
/// </summary>
public interface IStateBackend
{
    string Kind { get; }

    string BackendUrl { get; }

    bool SupportsLocking { get; }

    /// <summary>
    /// Process environment for the backend. Locators only — never account keys
    /// or connection strings.
    /// </summary>
    IReadOnlyDictionary<string, string> Environment { get; }
}
