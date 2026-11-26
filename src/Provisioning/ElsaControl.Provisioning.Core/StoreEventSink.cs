using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

internal sealed class StoreEventSink(IProvisioningEventStore events) : IProviderEventSink
{
    public Task AppendAsync(ProvisioningEvent @event, CancellationToken cancellationToken = default) =>
        events.AppendAsync(ProvisioningEventSanitizer.Sanitize(@event), cancellationToken);
}
