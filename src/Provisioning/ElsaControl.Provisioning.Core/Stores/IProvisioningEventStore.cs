using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core.Stores;

/// <summary>
/// Module-owned event table. Append-only. Not shared with the rest of Control.
/// </summary>
public interface IProvisioningEventStore
{
    Task AppendAsync(IReadOnlyList<ProvisioningEvent> events, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProvisioningEvent>> ListAsync(Guid operationId, CancellationToken cancellationToken = default);
}
