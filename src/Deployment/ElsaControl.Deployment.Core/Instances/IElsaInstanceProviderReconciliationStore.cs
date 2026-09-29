namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Compare-and-set persistence boundary for uncertain lifecycle reconciliation.
/// Implementations must atomically preserve the operation/run reservation until a
/// correlated provider observation establishes a terminal outcome.
/// </summary>
public interface IElsaInstanceProviderReconciliationStore
{
    Task<ElsaInstanceProviderReconciliationTarget?> GetTargetAsync(
        Guid workspaceId,
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<ElsaInstanceProviderReconciliationResult?> GetResultAsync(
        Guid workspaceId,
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<ElsaInstanceProviderReconciliationResult> CommitAsync(
        ElsaInstanceProviderReconciliationCommit commit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Binds operator-refreshed retry evidence onto a parked RecoveryRequired
    /// row without advancing the instance version. Recover then consumes the
    /// current-version receipt under the original If-Match.
    /// </summary>
    Task AttachRetryEvidenceAsync(
        Guid workspaceId,
        Guid instanceId,
        Guid operationId,
        string reference,
        string digest,
        string? reasonCode,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
