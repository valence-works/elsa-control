using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Publishes sanitised engine events as they arrive so Hub can poll live
/// status before the whole Pulumi <c>up</c> returns. Bind a claimed run
/// first; a lost fence drops later live writes. Terminal writes use
/// <see cref="PublishTerminalAsync"/> so they still land after the module
/// lock is released.
/// </summary>
public interface IProvisioningLiveEventSink
{
    IDisposable Bind(ProvisioningOperation operation, ProvisioningRequest request);

    Task PublishAsync(
        Guid operationId,
        IReadOnlyList<ProvisioningEvent> events,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a terminal event and writes the ledger using the completing
    /// token, even when the module lock row is already gone.
    /// </summary>
    Task PublishTerminalAsync(
        ProvisioningOperation operation,
        ProvisioningRequest? request,
        IReadOnlyList<ProvisioningEvent> events,
        CancellationToken cancellationToken = default);

    Task HeartbeatAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Overlays the provider failure code onto the bound run so the next
    /// ledger write can classify it. No-op when the run is not bound.
    /// </summary>
    void SetFailureCode(Guid operationId, string? failureCode)
    {
    }

    /// <summary>
    /// Overlays sanitised stack outputs onto the bound run so the next ledger
    /// write can record the engine origin. No-op when the run is not bound.
    /// </summary>
    void SetOutputs(Guid operationId, IReadOnlyDictionary<string, string>? outputs)
    {
    }
}
