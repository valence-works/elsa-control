using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using Microsoft.Extensions.Logging;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Resolves managed Create lineage from Control-owned lifecycle and provider
/// persistence before projecting the allowlisted customer progress contract.
/// </summary>
public sealed class AzureManagedElsaProvisioningProgressReader(
    IManagedElsaInstanceApiStore instances,
    IAzureManagedElsaProvisioningOperationStore providerOperations,
    ILogger<AzureManagedElsaProvisioningProgressReader> logger)
    : IManagedElsaProvisioningProgressReader
{
    public async Task<ManagedElsaProvisioningProgress?> ReadAsync(
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var topology = await instances.GetLifecycleTopologyAsync(
            workspaceId,
            instanceId,
            cancellationToken);
        if (topology is null)
            return null;
        if (topology.ObservedLifecycle == ElsaObservedLifecycle.Deleted)
            return null;

        var activeCreates = topology.Operations
            .Where(operation => operation.Action == ElsaInstanceOperationAction.Create)
            .Take(2)
            .ToArray();
        if (activeCreates.Length > 1)
            return Unavailable(topology, activeCreates[0]);

        var lifecycle = activeCreates.SingleOrDefault();
        if (lifecycle is null && topology.LastOperationId is { } lastOperationId)
        {
            var operation = await instances.GetOperationAsync(
                workspaceId,
                instanceId,
                lastOperationId,
                cancellationToken);
            if (operation?.Action == ElsaInstanceOperationAction.Create)
                lifecycle = ToTopologyOperation(operation);
        }

        var providerSnapshot = await providerOperations.GetCreateProvisioningAsync(
            workspaceId,
            instanceId,
            lifecycle?.Id,
            topology.InstanceVersion,
            cancellationToken);
        if (providerSnapshot?.IsAmbiguous == true)
            return Unavailable(topology, lifecycle);

        var anomalies = AzureManagedElsaProvisioningProgressProjector.DetectMappingAnomalies(
            providerSnapshot?.Operation,
            providerSnapshot?.Transitions);
        if (anomalies.HasFlag(AzureManagedElsaProvisioningMappingAnomaly.UnknownPhase))
        {
            logger.LogWarning(
                new EventId(53001, "UnknownManagedProvisioningPhase"),
                "Managed provisioning progress encountered an unknown provider phase for workspace {WorkspaceId} and instance {InstanceId}.",
                workspaceId,
                instanceId);
        }
        if (anomalies.HasFlag(AzureManagedElsaProvisioningMappingAnomaly.NonMonotonicStage))
        {
            logger.LogWarning(
                new EventId(53002, "NonMonotonicManagedProvisioningStage"),
                "Managed provisioning progress encountered a non-monotonic provider stage for workspace {WorkspaceId} and instance {InstanceId}.",
                workspaceId,
                instanceId);
        }

        return AzureManagedElsaProvisioningProgressProjector.Project(new(
            topology,
            lifecycle,
            providerSnapshot?.Operation,
            providerSnapshot?.Transitions));
    }

    private static ManagedElsaProvisioningProgress Unavailable(
        ElsaInstanceLifecycleTopologySnapshot? topology = null,
        ElsaInstanceLifecycleTopologyOperation? lifecycle = null) =>
        AzureManagedElsaProvisioningProgressProjector.Project(new(
            topology,
            lifecycle,
            ProviderOperation: null,
            Transitions: null,
            HistoryUnavailable: true));

    private static ElsaInstanceLifecycleTopologyOperation ToTopologyOperation(
        ElsaInstanceOperationSummary operation) =>
        new(
            operation.Id,
            operation.Action,
            operation.State,
            operation.ExpectedVersion,
            operation.AttemptNumber,
            operation.AcceptedAt,
            operation.StartedAt,
            operation.CompletedAt,
            operation.DeploymentRunId,
            operation.FailureCode,
            DeletionDiagnosticCode: null,
            ReconciliationDiagnosticCode: null,
            Outbox: null);
}
