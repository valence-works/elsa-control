namespace ElsaControl.Provisioning.Core.Stores;

/// <summary>
/// Module-owned per-stack lock table. Not shared with the rest of Control.
/// </summary>
public interface IProvisioningStackLockStore
{
    Task<bool> TryAcquireAsync(string stackName, Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task ReleaseAsync(string stackName, Guid operationId, CancellationToken cancellationToken = default);

    Task<Guid?> GetHolderAsync(string stackName, CancellationToken cancellationToken = default);
}
