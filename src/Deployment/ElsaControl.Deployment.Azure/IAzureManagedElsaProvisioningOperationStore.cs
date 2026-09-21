namespace ElsaControl.Deployment.Azure;

/// <summary>
/// A server-correlated Create operation and its durable transition history.
/// The operation identifiers remain internal to Control and are never part of
/// the customer-facing provisioning progress contract.
/// </summary>
public sealed record AzureManagedElsaProvisioningOperationSnapshot(
    AzureProviderOperation? Operation,
    IReadOnlyList<AzureProviderOperationTransition> Transitions,
    bool IsAmbiguous = false);

/// <summary>
/// Reads the Azure Create operation that belongs to a managed Elsa instance.
/// Implementations must derive correlation from persisted lifecycle state;
/// callers must never supply a provider operation identifier.
/// </summary>
public interface IAzureManagedElsaProvisioningOperationStore
{
    Task<AzureManagedElsaProvisioningOperationSnapshot?> GetCreateProvisioningAsync(
        Guid workspaceId,
        Guid instanceId,
        Guid? lifecycleOperationId,
        int expectedInstanceVersion,
        CancellationToken cancellationToken = default);
}
