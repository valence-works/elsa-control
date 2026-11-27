using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Recover-time seam for a Delete parked in RecoveryRequired whose assignment inventory
/// may be stale. Implementations take one fresh, read-only observation of the engine
/// resource group and, only when ARM confirms absence, reconcile that inventory.
/// They must not mutate Azure resources.
/// </summary>
public interface IElsaInstanceDeleteStaleInventoryPort
{
    Task TryReconcileStaleDeleteInventoryFromEngineResourceGroupAbsenceAsync(
        ElsaInstance instance,
        ElsaInstanceOperation operation,
        CancellationToken cancellationToken = default);
}
