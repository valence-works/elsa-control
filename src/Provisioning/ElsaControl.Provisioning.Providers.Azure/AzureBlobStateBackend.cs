using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Azure Blob stack-state backend with locking. Account keys never appear here;
/// the process authenticates with workload identity.
/// </summary>
public sealed class AzureBlobStateBackend : IStateBackend
{
    public AzureBlobStateBackend(string storageAccountName, string containerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageAccountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        StorageAccountName = storageAccountName.Trim();
        ContainerName = containerName.Trim();
        BackendUrl = $"azblob://{ContainerName}";
        EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AZURE_STORAGE_ACCOUNT"] = StorageAccountName
        };
    }

    public string Kind => StateBackendReference.AzureBlobKind;
    public string StorageAccountName { get; }
    public string ContainerName { get; }
    public string BackendUrl { get; }
    public bool LockingEnabled => true;
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; }

    public static AzureBlobStateBackend From(StateBackendReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.Kind, StateBackendReference.AzureBlobKind, StringComparison.Ordinal))
            throw new ArgumentException("Azure Blob backend required.", nameof(reference));
        var parts = reference.Locator.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new ArgumentException("Azure Blob locator is '{account}/{container}'.", nameof(reference));
        return new AzureBlobStateBackend(parts[0], parts[1]);
    }
}
