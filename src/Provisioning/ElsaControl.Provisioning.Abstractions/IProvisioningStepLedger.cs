namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Maps sanitised provisioning events onto Control's existing step ledger.
/// A stale fencing token must not write. Implementations never copy secrets,
/// resource identifiers, or raw provider messages into the customer contract.
/// </summary>
public interface IProvisioningStepLedger
{
    Task<ProvisioningStepLedgerResult> TryWriteAsync(
        ProvisioningOperation operation,
        ProvisioningRequest? request,
        IReadOnlyList<ProvisioningEvent> events,
        CancellationToken cancellationToken = default);

    Task HeartbeatAsync(
        ProvisioningOperation operation,
        ProvisioningRequest? request,
        CancellationToken cancellationToken = default);
}

public enum ProvisioningStepLedgerResult
{
    Ignored = 0,
    Written = 1,
    Duplicate = 2,
    StaleFence = 3
}

/// <summary>
/// Default: the module event store is enough. Azure registers a writer that
/// checkpoints <c>AzureProviderOperationTransition</c> rows so Hub can poll.
/// </summary>
public sealed class NoOpProvisioningStepLedger : IProvisioningStepLedger
{
    public Task<ProvisioningStepLedgerResult> TryWriteAsync(
        ProvisioningOperation operation,
        ProvisioningRequest? request,
        IReadOnlyList<ProvisioningEvent> events,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(events);
        return Task.FromResult(ProvisioningStepLedgerResult.Ignored);
    }

    public Task HeartbeatAsync(
        ProvisioningOperation operation,
        ProvisioningRequest? request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(operation);
        return Task.CompletedTask;
    }
}
