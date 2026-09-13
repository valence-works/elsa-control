namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Persistence boundary of the periodic Ready-instance health monitor. Listing and single reads
/// return only the monitor's evaluated state set (see <see cref="ElsaInstanceHealthMonitor"/>).
/// A commit repeats that check, the expected version and the prior health inside one transaction
/// and throws <see cref="ElsaInstanceLifecycleConflictException"/> rather than overwrite a
/// concurrent lifecycle change.
/// </summary>
public interface IElsaInstanceHealthMonitorStore
{
    Task<IReadOnlyList<ElsaInstanceHealthMonitorTarget>> ListHealthMonitorTargetsAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ElsaInstanceHealthMonitorTarget?> GetHealthMonitorTargetAsync(
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Commits the health change and its audit event; returns the new instance version.</summary>
    Task<int> CommitHealthTransitionAsync(
        ElsaInstanceHealthTransition transition,
        CancellationToken cancellationToken = default);
}
