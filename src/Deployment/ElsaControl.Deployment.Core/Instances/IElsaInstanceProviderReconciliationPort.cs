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
/// cap. Implementations must claim a slot atomically before Recover runs and
/// must record each attempt outcome. A claimed slot stays charged even when
/// Recover then loses its compare-and-set.
/// </summary>
public interface IElsaInstanceProviderAutoResumePort
{
    /// <summary>
    /// Claims one automatic resume slot. Returns the new count when the claim
    /// succeeded, or null when the cap is exhausted or the compare-and-set lost.
    /// </summary>
    Task<int?> TryChargeAutoResumeAsync(
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
