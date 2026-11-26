namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Narrow public surface of the provisioning module. Implementations enqueue
/// work and return immediately; they never run a provider on the caller thread.
/// </summary>
public interface IProvisioningService
{
    Task<ProvisioningCommandResult> PreviewAsync(
        ProvisioningCommand command,
        CancellationToken cancellationToken = default);

    Task<ProvisioningCommandResult> ProvisionAsync(
        ProvisioningCommand command,
        CancellationToken cancellationToken = default);

    Task<ProvisioningCommandResult> UpdateAsync(
        ProvisioningCommand command,
        CancellationToken cancellationToken = default);

    Task<ProvisioningCommandResult> RefreshAsync(
        ProvisioningCommand command,
        CancellationToken cancellationToken = default);

    Task<ProvisioningCommandResult> CancelAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<ProvisioningCommandResult> DestroyAsync(
        ProvisioningCommand command,
        CancellationToken cancellationToken = default);

    Task<ProvisioningOperation?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProvisioningEvent>> ReadEventsAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);
}
