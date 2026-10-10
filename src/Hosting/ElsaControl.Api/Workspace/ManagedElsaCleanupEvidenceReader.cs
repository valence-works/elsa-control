using System.Security.Cryptography;
using System.Text;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Api.Workspace;

/// <summary>
/// Shared read-side validation for historical managed-instance Delete cleanup evidence.
/// This reader never mutates lifecycle, provider, or billing state.
/// </summary>
internal static class ManagedElsaCleanupEvidenceReader
{
    internal sealed record HistoricalEvidence(
        ElsaInstance Instance,
        ElsaInstanceOperationSummary LifecycleOperation,
        AzureProviderResourceAssignment Assignment,
        AzureProviderOperation ProviderOperation,
        string Digest);

    internal enum FreshObservationFailure
    {
        None,
        NotFound,
        Unavailable,
        EvidenceChanged
    }

    internal sealed record FreshObservationResult(
        FreshObservationFailure Failure,
        HistoricalEvidence? Evidence = null,
        AzureProviderFreshResourceGroupObservation? Observation = null);

    internal static ManagedElsaCleanupReceiptResponse ToReceiptResponse(HistoricalEvidence evidence) =>
        new(
            InstanceTombstonePresent: true,
            LifecycleDeleteSucceeded: true,
            ProviderDeleteSucceeded: true,
            ProviderAbsenceVerifiedAtCompletion: true,
            ProviderCompletedAt: evidence.ProviderOperation.CompletedAt!.Value,
            LifecycleCompletedAt: evidence.LifecycleOperation.CompletedAt!.Value,
            TombstoneDeletedAt: evidence.Instance.DeletedAt!.Value,
            ReceiptDigest: evidence.Digest);

    internal static ManagedElsaFreshCleanupObservationResponse ToFreshObservationResponse(
        HistoricalEvidence evidence,
        AzureProviderFreshResourceGroupObservation observation) =>
        new(
            observation.State,
            observation.ObservedAt,
            observation.ReasonCode,
            observation.EvidenceDigest,
            evidence.Digest);

    internal static async Task<HistoricalEvidence?> ReadHistoricalAsync(
        Guid workspaceId,
        Guid organizationId,
        Guid instanceId,
        Guid operationId,
        IElsaInstanceLifecycleStore lifecycle,
        IManagedElsaInstanceApiStore queries,
        IAzureProviderOperationStore providerOperations,
        IAzureProviderResourceAssignmentStore assignments,
        CancellationToken cancellationToken)
    {
        if (workspaceId == Guid.Empty || instanceId == Guid.Empty ||
            operationId == Guid.Empty || organizationId == Guid.Empty)
            return null;

        var instance = await lifecycle.GetInstanceAsync(workspaceId, instanceId, cancellationToken);
        var operation = await queries.GetOperationForOrganizationAsync(
            workspaceId, organizationId, instanceId, operationId, cancellationToken);
        if (instance is null || operation is null ||
            instance.OrganizationId != organizationId ||
            instance.WorkspaceId != workspaceId || instance.Id != instanceId ||
            !IsHistoricalDeleteTombstone(instance, operation, operationId))
            return null;

        // Deletion clears the placement reference: require one exact retained owner.
        var ownerAssignments = await assignments.ListForInstanceAsync(
            workspaceId, organizationId, instanceId, cancellationToken);
        if (ownerAssignments.Count != 1)
            return null;
        var assignment = ownerAssignments[0];
        if (assignment.LastOperationId is not { } providerOperationId)
            return null;
        var providerOperation = await providerOperations.GetAsync(
            workspaceId, providerOperationId, cancellationToken);
        if (!IsHistoricalCleanupReceiptEligible(
                instance, operation, operationId, assignment, providerOperation))
            return null;

        return new(instance, operation, assignment, providerOperation!,
            ComputeHistoricalCleanupReceiptDigest(
                workspaceId, organizationId, instanceId, operationId,
                operation.CompletedAt!.Value, assignment, providerOperation!));
    }

    internal static async Task<FreshObservationResult> ReadFreshObservationAsync(
        Guid workspaceId,
        Guid organizationId,
        Guid instanceId,
        Guid operationId,
        IElsaInstanceLifecycleStore lifecycle,
        IManagedElsaInstanceApiStore queries,
        IAzureProviderOperationStore providerOperations,
        IAzureProviderResourceAssignmentStore assignments,
        IAzureProviderFreshResourceGroupObserver? observer,
        CancellationToken cancellationToken)
    {
        if (observer is null)
            return new(FreshObservationFailure.Unavailable);

        var evidence = await ReadHistoricalAsync(
            workspaceId, organizationId, instanceId, operationId,
            lifecycle, queries, providerOperations, assignments, cancellationToken);
        if (evidence is null)
            return new(FreshObservationFailure.NotFound);

        AzureProviderFreshResourceGroupObservation observation;
        try
        {
            observation = await observer.ObserveAsync(
                evidence.ProviderOperation, operationId, evidence.Assignment, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new(FreshObservationFailure.Unavailable);
        }

        // The network read may outlive a concurrent retained-evidence change.
        // Never pair an observation with a different historical ownership tuple.
        var current = await ReadHistoricalAsync(
            workspaceId, organizationId, instanceId, operationId,
            lifecycle, queries, providerOperations, assignments, cancellationToken);
        if (current is null || !string.Equals(current.Digest, evidence.Digest, StringComparison.Ordinal))
            return new(FreshObservationFailure.EvidenceChanged);

        return new(FreshObservationFailure.None, evidence, observation);
    }

    private static bool IsHistoricalDeleteTombstone(
        ElsaInstance instance,
        ElsaInstanceOperationSummary operation,
        Guid operationId)
    {
        return operation.Id == operationId &&
               operation.InstanceId == instance.Id &&
               operation.Action == ElsaInstanceOperationAction.Delete &&
               operation.State == ElsaInstanceOperationState.Succeeded &&
               operation.CompletedAt is not null &&
               instance.Intent.DesiredLifecycle == ElsaDesiredLifecycle.Deleting &&
               instance.ObservedLifecycle == ElsaObservedLifecycle.Deleted &&
               instance.DeletedAt is not null &&
               instance.DeletedAt.Value == operation.CompletedAt.Value &&
               Guid.TryParseExact(instance.LastOperationId?.Value, "D", out var lastOperationId) &&
               lastOperationId == operationId;
    }

    private static bool IsHistoricalCleanupReceiptEligible(
        ElsaInstance instance,
        ElsaInstanceOperationSummary lifecycleOperation,
        Guid lifecycleOperationId,
        AzureProviderResourceAssignment assignment,
        AzureProviderOperation? providerOperation)
    {
        var expectedWorkloadName = AzureElsaInstanceProvider.WorkloadName(instance.Id);
        return assignment.WorkspaceId == instance.WorkspaceId &&
               assignment.OrganizationId == instance.OrganizationId &&
               assignment.InstanceId == instance.Id &&
               string.Equals(assignment.WorkloadName, expectedWorkloadName, StringComparison.OrdinalIgnoreCase) &&
               assignment.LastOperationId is { } providerOperationId &&
               providerOperation is not null &&
               providerOperation.Id == providerOperationId &&
               providerOperation.CompletedAt is not null &&
               lifecycleOperation.CompletedAt is { } lifecycleCompletedAt &&
               providerOperation.CompletedAt.Value <= lifecycleCompletedAt &&
               assignment.DeletedAt is { } assignmentDeletedAt &&
               assignmentDeletedAt <= providerOperation.CompletedAt.Value &&
               AzureProviderOperationValidation.IsLifecycleDeleteIdempotencyKey(
                   providerOperation.IdempotencyKey, lifecycleOperationId) &&
               AzureProviderDeleteRecoverySupport.IsTerminalVerifiedCleanupEligible(
                   providerOperation, assignment);
    }

    private static string ComputeHistoricalCleanupReceiptDigest(
        Guid workspaceId,
        Guid organizationId,
        Guid instanceId,
        Guid lifecycleOperationId,
        DateTimeOffset lifecycleCompletedAt,
        AzureProviderResourceAssignment assignment,
        AzureProviderOperation providerOperation)
    {
        var canonical = string.Join('\n',
        [
            "elsa-managed-delete-cleanup-receipt-v1",
            workspaceId.ToString("D"),
            organizationId.ToString("D"),
            instanceId.ToString("D"),
            lifecycleOperationId.ToString("D"),
            lifecycleCompletedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            assignment.Id.ToString("D"),
            assignment.SubscriptionId,
            assignment.ResourceGroupName,
            assignment.ProviderScopeFingerprint,
            providerOperation.Id.ToString("D"),
            providerOperation.CheckpointSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            providerOperation.CompletedAt!.Value.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            providerOperation.Phase.ToString(),
            providerOperation.Status.ToString()
        ]);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>
/// Safe historical receipt for a completed managed-instance Delete. The provider
/// absence flag describes the runner's verified result at completion; it is not a
/// current Azure Resource Manager observation.
/// </summary>
public sealed record ManagedElsaCleanupReceiptResponse(
    bool InstanceTombstonePresent,
    bool LifecycleDeleteSucceeded,
    bool ProviderDeleteSucceeded,
    bool ProviderAbsenceVerifiedAtCompletion,
    DateTimeOffset ProviderCompletedAt,
    DateTimeOffset LifecycleCompletedAt,
    DateTimeOffset TombstoneDeletedAt,
    string ReceiptDigest);

/// <summary>Fresh ARM observation bound to the exact retained cleanup receipt.</summary>
public sealed record ManagedElsaFreshCleanupObservationResponse(
    AzureProviderFreshResourceGroupState State,
    DateTimeOffset? ObservedAt,
    string ReasonCode,
    string? EvidenceDigest,
    string HistoricalReceiptDigest);
