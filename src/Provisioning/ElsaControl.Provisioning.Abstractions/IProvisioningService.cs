namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Narrow public surface of the provisioning module. Control's lifecycle layer
/// talks only to this. Designed so the module can later run as a separate
/// service or a bring-your-own-cloud agent over a message or API boundary.
/// </summary>
public interface IProvisioningService
{
    Task<ProvisioningEnqueueResult> PreviewAsync(ProvisioningRequest request, CancellationToken cancellationToken = default);
    Task<ProvisioningEnqueueResult> ProvisionAsync(ProvisioningRequest request, CancellationToken cancellationToken = default);
    Task<ProvisioningEnqueueResult> UpdateAsync(ProvisioningRequest request, CancellationToken cancellationToken = default);
    Task<ProvisioningEnqueueResult> RefreshAsync(ProvisioningRequest request, CancellationToken cancellationToken = default);
    Task<ProvisioningEnqueueResult> CancelAsync(ProvisioningRequest request, CancellationToken cancellationToken = default);
    Task<ProvisioningEnqueueResult> DestroyAsync(ProvisioningRequest request, CancellationToken cancellationToken = default);

    Task<ProvisioningOperation?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProvisioningEvent>> ListEventsAsync(Guid operationId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ProvisioningEvent> WatchAsync(Guid operationId, CancellationToken cancellationToken = default);
}
