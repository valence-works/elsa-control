using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Publishes sanitised engine events as they arrive so Hub can poll live
/// status before the whole Pulumi <c>up</c> returns. Bind a claimed run
/// first; a lost fence drops later writes.
/// </summary>
public interface IProvisioningLiveEventSink
{
    IDisposable Bind(ProvisioningOperation operation, ProvisioningRequest request);

    Task PublishAsync(
        Guid operationId,
        IReadOnlyList<ProvisioningEvent> events,
        CancellationToken cancellationToken = default);

    Task HeartbeatAsync(Guid operationId, CancellationToken cancellationToken = default);
}
