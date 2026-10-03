using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

/// <summary>
/// Tenant-scoped read projection for managed lifecycle operational health. Only
/// bounded lifecycle state, timestamps, and validated diagnostic codes cross this
/// adapter boundary; provider and customer-owned text remain in persistence.
/// </summary>
public sealed class EfCoreManagedElsaInstanceOperationalStore(CatalogDbContext dbContext) : IManagedElsaInstanceOperationalStore
{
    public async Task<ManagedLifecycleOperationalHealthSnapshot?> GetSnapshotAsync(
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty || instanceId == Guid.Empty)
            return null;

        var instance = await dbContext.ElsaInstances
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.WorkspaceId == workspaceId && x.Id == instanceId,
                cancellationToken);
        if (instance is null)
            return null;

        var operation = await FindPreferredOperationAsync(instance, workspaceId, instanceId, cancellationToken);
        var operationSnapshot = TryMapOperation(operation);
        if (operation is not null && operationSnapshot is null)
            return null;
        var selectedOperation = operationSnapshot is null ? null : operation;
        var run = selectedOperation?.DeploymentRunId is not { } deploymentRunId
            ? null
            : await dbContext.DeploymentRuns
                .AsNoTracking()
                .Where(x => x.Id == deploymentRunId &&
                            x.WorkspaceId == workspaceId &&
                            x.ElsaInstanceId == instanceId)
                .FirstOrDefaultAsync(cancellationToken);
        var runSnapshot = TryMapRun(run);
        if (run is not null && runSnapshot is null)
            return null;

        var hasReconciledObservedLifecycle = selectedOperation?.ReconciledObservedLifecycle is not null;
        var hasReconciledHealth = selectedOperation?.ReconciledHealth is not null;
        if (hasReconciledObservedLifecycle != hasReconciledHealth)
            return null;

        var observedLifecycle = selectedOperation?.ReconciledObservedLifecycle ?? instance.ObservedLifecycle;
        var health = selectedOperation?.ReconciledHealth ?? instance.Health;
        var providerDiagnosticCode = TrySafeDiagnosticCode(selectedOperation?.ReconciliationDiagnosticCode);
        ElsaInstanceProviderObservationKind? providerObservationKind = selectedOperation?.ReconciledObservedLifecycle is not null &&
                                      selectedOperation.ReconciledHealth is not null
            ? observedLifecycle == ElsaObservedLifecycle.Unknown || health == ElsaInstanceHealth.Unknown
                ? ElsaInstanceProviderObservationKind.Unknown
                : ElsaInstanceProviderObservationKind.Confirmed
            : null;

        try
        {
            return new ManagedLifecycleOperationalHealthSnapshot(
                workspaceId,
                instanceId,
                instance.DesiredLifecycle,
                observedLifecycle,
                health,
                providerObservationKind,
                operationSnapshot,
                runSnapshot,
                providerDiagnosticCode,
                selectedOperation?.ReconciledAt);
        }
        catch (ArgumentException)
        {
            // Existing rows are validated on write. If an older or externally
            // repaired row is malformed, fail closed rather than exposing an
            // incomplete or unsafe operational projection.
            return null;
        }
    }

    private async Task<ElsaInstanceOperationEntity?> FindPreferredOperationAsync(
        ElsaInstanceEntity instance,
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var operations = dbContext.ElsaInstanceOperations
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId &&
                        x.InstanceId == instanceId);

        if (Guid.TryParse(instance.LastOperationId, out var lastOperationId))
        {
            var lastOperation = await operations
                .Where(x => x.Id == lastOperationId)
                .FirstOrDefaultAsync(cancellationToken);
            if (lastOperation is not null)
                return lastOperation;
        }

        var active = await operations
            .Where(x => x.State == ElsaInstanceOperationState.Accepted ||
                        x.State == ElsaInstanceOperationState.WaitingForPriorOperation ||
                        x.State == ElsaInstanceOperationState.Queued ||
                        x.State == ElsaInstanceOperationState.Running ||
                        x.State == ElsaInstanceOperationState.RecoveryRequired)
            .OrderByDescending(x => x.State != ElsaInstanceOperationState.WaitingForPriorOperation)
            .ThenByDescending(x => x.AcceptedAt)
            .ThenByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (active is not null)
            return active;

        return await operations
            .Where(x => x.State == ElsaInstanceOperationState.Succeeded ||
                        x.State == ElsaInstanceOperationState.Failed ||
                        x.State == ElsaInstanceOperationState.Cancelled)
            .OrderByDescending(x => x.CompletedAt ?? x.AcceptedAt)
            .ThenByDescending(x => x.AcceptedAt)
            .ThenByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static ManagedLifecycleOperationSnapshot? TryMapOperation(ElsaInstanceOperationEntity? entity)
    {
        if (entity is null)
            return null;

        try
        {
            var diagnosticCode = entity.State == ElsaInstanceOperationState.Failed
                ? TrySafeDiagnosticCode(entity.FailureCode) ?? TrySafeDiagnosticCode(entity.ReconciliationDiagnosticCode)
                : TrySafeDiagnosticCode(entity.ReconciliationDiagnosticCode) ?? TrySafeDiagnosticCode(entity.FailureCode);
            var parkReason = ManagedElsaReasonCodeCatalog.SelectCurrentReason(
                entity.FailureCode,
                entity.ReconciliationDiagnosticCode);
            if (entity.State == ElsaInstanceOperationState.RecoveryRequired)
                diagnosticCode = TrySafeDiagnosticCode(parkReason) ?? diagnosticCode;
            return new ManagedLifecycleOperationSnapshot(
                entity.Id,
                entity.State,
                entity.AttemptNumber,
                entity.AcceptedAt,
                entity.StartedAt,
                diagnosticCode,
                entity.HeartbeatAt,
                requiresHumanAt: entity.RequiresHumanAt);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static ManagedLifecycleRunSnapshot? TryMapRun(DeploymentRunEntity? entity)
    {
        if (entity is null)
            return null;

        try
        {
            var diagnosticCode = entity.Status switch
            {
                WorkspaceDeploymentRunStatus.RecoveryRequired when !string.IsNullOrWhiteSpace(entity.RecoveryReason) =>
                    ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired,
                WorkspaceDeploymentRunStatus.Failed when !string.IsNullOrWhiteSpace(entity.FailureMessage) =>
                    ManagedLifecycleOperationalHealthDiagnosticCodes.RunFailed,
                _ => null
            };
            return new ManagedLifecycleRunSnapshot(
                entity.Id,
                entity.Status,
                entity.AttemptNumber,
                entity.QueuedAt,
                entity.StartedAt,
                diagnosticCode,
                entity.WorkerHeartbeatAt);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? TrySafeDiagnosticCode(string? value) =>
        ManagedLifecycleOperationalHealthDiagnosticCodes.IsSafe(value) ? value : null;
}
