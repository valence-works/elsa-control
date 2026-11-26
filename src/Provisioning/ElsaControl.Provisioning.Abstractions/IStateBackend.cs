namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Pluggable stack-state backend. Azure Blob with locking is first.
/// </summary>
public interface IStateBackend
{
    string Kind { get; }
    string BackendUrl { get; }
    bool LockingEnabled { get; }
    IReadOnlyDictionary<string, string> EnvironmentVariables { get; }
}
