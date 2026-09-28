namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Reads safe provider state for one uncertain operation. Implementations must not
/// apply, retry or mutate provider resources from this read boundary.
/// </summary>
public interface IElsaInstanceProviderReconciliationPort
{
    Task<ElsaInstanceProviderObservation> ObserveAsync(
        ElsaInstanceProviderReconciliationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional auto-resume accounting for a provider that persists a per-operation
/// cap. Implementations must charge only when a resume is accepted and must
/// record each attempt outcome.
/// </summary>
public interface IElsaInstanceProviderAutoResumePort
{
    Task<bool> TryChargeAutoResumeAsync(
        Guid workspaceId,
        Guid instanceId,
        Guid lifecycleOperationId,
        CancellationToken cancellationToken = default);

    Task RecordAutoResumeOutcomeAsync(
        Guid workspaceId,
        Guid instanceId,
        Guid lifecycleOperationId,
        string outcomeCode,
        CancellationToken cancellationToken = default);
}
