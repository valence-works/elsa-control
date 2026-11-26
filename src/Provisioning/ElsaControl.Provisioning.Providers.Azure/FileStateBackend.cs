using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Pulumi local file backend. Used by CI tests with no Azure credentials.
/// </summary>
public sealed class FileStateBackend : IStateBackend
{
    public FileStateBackend(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        DirectoryPath = Path.GetFullPath(directory.Trim());
        BackendUrl = new Uri(DirectoryPath).AbsoluteUri;
    }

    public string Kind => StateBackendReference.FileKind;
    public string DirectoryPath { get; }
    public string BackendUrl { get; }
    public bool LockingEnabled => true;
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static FileStateBackend From(StateBackendReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.Kind, StateBackendReference.FileKind, StringComparison.Ordinal))
            throw new ArgumentException("File backend required.", nameof(reference));
        return new FileStateBackend(reference.Locator);
    }
}
