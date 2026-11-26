using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Azure Blob backend configuration with locking. Account keys are never set;
/// the Automation API run uses workload identity.
/// </summary>
public sealed class AzureBlobStateBackend : IStateBackend
{
    public AzureBlobStateBackend(AzureProvisioningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.State.Validate();
        AccountName = options.State.AccountName;
        ContainerName = options.State.ContainerName;
        BackendUrl = $"azblob://{ContainerName}";
        Environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AZURE_STORAGE_ACCOUNT"] = AccountName
        };
    }

    public string Kind => "azure-blob";

    public string BackendUrl { get; }

    public bool SupportsLocking => true;

    public IReadOnlyDictionary<string, string> Environment { get; }

    public string AccountName { get; }

    public string ContainerName { get; }
}
