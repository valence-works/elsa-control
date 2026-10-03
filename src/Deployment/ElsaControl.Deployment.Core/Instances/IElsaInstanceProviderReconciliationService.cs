namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Provider-reconciliation orchestration boundary. Keeping the hosted worker
/// dependent on this contract lets it isolate submission/reconciliation failures
/// without coupling its scheduling loop to persistence details.
/// </summary>
public interface IElsaInstanceProviderReconciliationService
{
    Task<ElsaInstanceProviderReconciliationResult> ReconcileAsync(
        Guid workspaceId,
        Guid operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances the human-required clock on parked operations that have sat
    /// in a temporary or auto-resuming class past the 10-minute bound,
    /// including run-less parks the pending-operation query skips.
    /// </summary>
    Task<int> AdvanceDueHumanRequiredClocksAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}
