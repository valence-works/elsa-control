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
    public const int MaximumAutoResumes = 3;

    /// <summary>
    /// Claims one automatic resume slot when the stored count still equals
    /// <paramref name="expectedCount"/>. Returns the new count when the claim
    /// succeeded, or null when the cap is exhausted or the compare-and-set lost.
    /// </summary>
    Task<int?> TryChargeAutoResumeAsync(
        Guid workspaceId,
        Guid instanceId,
        Guid lifecycleOperationId,
        int expectedCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the persisted auto-resume count after a lost claim so the
    /// reconciler can label exhausted versus claim-conflict. Must not be used
    /// as the expected count for the claim itself.
    /// </summary>
    Task<int?> GetAutoResumeCountAsync(
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
