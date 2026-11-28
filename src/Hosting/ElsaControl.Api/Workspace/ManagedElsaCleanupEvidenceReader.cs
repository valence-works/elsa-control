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
    internal const string ProviderConfirmedAbsentProofKind = "provider-confirmed-absent";
    internal const string LocalNoOwnedResourcesProofKind = "local-no-owned-resources";

    internal const string NotFoundNoTombstone = "no-tombstone";
    internal const string NotFoundDeletedAtMismatch = "deletedAt-mismatch";
    internal const string NotFoundDiagnosticMismatch = "diagnostic-mismatch";
    internal const string NotFoundHasAnyListMismatch = "hasany-list-mismatch";
    internal const string NotFoundAmbiguousAssignments = "retained-assignment:ambiguous";

    internal static string NotFoundRetainedAssignment(AzureProviderAssignmentState state) =>
        "retained-assignment:" + state;

    internal sealed record HistoricalReadResult(
        HistoricalEvidence? Evidence,
        string? NotFoundReason)
    {
        public static HistoricalReadResult Found(HistoricalEvidence evidence) => new(evidence, null);
        public static HistoricalReadResult NotFound(string reason) => new(null, reason);
    }

    internal sealed record HistoricalEvidence(
        ElsaInstance Instance,
        ElsaInstanceOperationSummary LifecycleOperation,
        AzureProviderResourceAssignment? Assignment,
        AzureProviderOperation? ProviderOperation,
        string Digest,
        string ProofKind);

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
        AzureProviderFreshResourceGroupObservation? Observation = null,
        string? NotFoundReason = null);

    internal static ManagedElsaCleanupReceiptResponse ToReceiptResponse(HistoricalEvidence evidence) =>
        evidence.ProviderOperation is null
            ? new(
                InstanceTombstonePresent: true,
                LifecycleDeleteSucceeded: true,
                ProviderDeleteSucceeded: false,
                ProviderAbsenceVerifiedAtCompletion: true,
                ProviderCompletedAt: null,
                LifecycleCompletedAt: evidence.LifecycleOperation.CompletedAt!.Value,
                TombstoneDeletedAt: evidence.Instance.DeletedAt!.Value,
                ReceiptDigest: evidence.Digest,
                ProofKind: evidence.ProofKind)
            : new(
                InstanceTombstonePresent: true,
                LifecycleDeleteSucceeded: true,
                ProviderDeleteSucceeded: true,
                ProviderAbsenceVerifiedAtCompletion: true,
                ProviderCompletedAt: evidence.ProviderOperation.CompletedAt!.Value,
                LifecycleCompletedAt: evidence.LifecycleOperation.CompletedAt!.Value,
                TombstoneDeletedAt: evidence.Instance.DeletedAt!.Value,
                ReceiptDigest: evidence.Digest,
                ProofKind: ProviderConfirmedAbsentProofKind);

    internal static ManagedElsaFreshCleanupObservationResponse ToFreshObservationResponse(
        HistoricalEvidence evidence,
        AzureProviderFreshResourceGroupObservation observation) =>
        new(
            observation.State,
            observation.ObservedAt,
            observation.ReasonCode,
            observation.EvidenceDigest,
            evidence.Digest);

    internal static async Task<HistoricalReadResult> ReadHistoricalAsync(
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
            return HistoricalReadResult.NotFound(NotFoundNoTombstone);

        var instance = await lifecycle.GetInstanceAsync(workspaceId, instanceId, cancellationToken);
        var operation = await queries.GetOperationForOrganizationAsync(
            workspaceId, organizationId, instanceId, operationId, cancellationToken);
        if (instance is null || operation is null ||
            instance.OrganizationId != organizationId ||
            instance.WorkspaceId != workspaceId || instance.Id != instanceId)
            return HistoricalReadResult.NotFound(NotFoundNoTombstone);

        if (TombstoneNotFoundReason(instance, operation, operationId) is { } tombstoneReason)
            return HistoricalReadResult.NotFound(tombstoneReason);

        var hasAssignmentRow =
            await assignments.HasAnyForInstanceAsync(workspaceId, instanceId, cancellationToken);
        var hasAzureOperations =
            await providerOperations.HasAnyForInstanceAsync(workspaceId, instanceId, cancellationToken);
        var ownerAssignments = await assignments.ListForInstanceAsync(
            workspaceId, organizationId, instanceId, cancellationToken);
        // Lightweight stores claim provenance without listing rows. Fail closed.
        if (hasAssignmentRow && ownerAssignments.Count == 0)
            return HistoricalReadResult.NotFound(NotFoundHasAnyListMismatch);

        var retained = ownerAssignments
            .Where(assignment => !IsReleasedForReceipt(assignment, operation))
            .ToList();
        if (retained.Count == 0)
            return LocalOrConfirmedAbsentReceipt(
                instance, operation, workspaceId, organizationId, instanceId, operationId,
                hasAzureOperations);

        // Deletion clears the placement reference: require one exact retained owner.
        if (retained.Count != 1)
            return HistoricalReadResult.NotFound(NotFoundAmbiguousAssignments);

        var assignment = retained[0];
        AzureProviderOperation? providerOperation = null;
        if (assignment.LastOperationId is { } providerOperationId)
            providerOperation = await providerOperations.GetAsync(
                workspaceId, providerOperationId, cancellationToken);
        if (IsHistoricalCleanupReceiptEligible(
                instance, operation, operationId, assignment, providerOperation))
        {
            return HistoricalReadResult.Found(new(
                instance, operation, assignment, providerOperation!,
                ComputeHistoricalCleanupReceiptDigest(
                    workspaceId, organizationId, instanceId, operationId,
                    operation.CompletedAt!.Value, assignment, providerOperation!),
                ProviderConfirmedAbsentProofKind));
        }

        // Leftover Deleted placement on a confirmed-absent tombstone is the
        // P-FAILDEL case: no provider Delete ran, so it is not retained.
        if (assignment.State == AzureProviderAssignmentState.Deleted &&
            IsProviderConfirmedAbsentTombstone(operation))
            return LocalOrConfirmedAbsentReceipt(
                instance, operation, workspaceId, organizationId, instanceId, operationId,
                hasAzureOperations);

        return HistoricalReadResult.NotFound(NotFoundRetainedAssignment(assignment.State));
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

        var historical = await ReadHistoricalAsync(
            workspaceId, organizationId, instanceId, operationId,
            lifecycle, queries, providerOperations, assignments, cancellationToken);
        if (historical.Evidence is null)
            return new(FreshObservationFailure.NotFound, NotFoundReason: historical.NotFoundReason);

        var evidence = historical.Evidence;
        AzureProviderFreshResourceGroupObservation observation;
        try
        {
            observation = evidence.ProviderOperation is null
                ? await observer.ObserveDeterministicResourceGroupAsync(
                    workspaceId, organizationId, instanceId, operationId, cancellationToken)
                : await observer.ObserveAsync(
                    evidence.ProviderOperation, operationId, evidence.Assignment!, cancellationToken);
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
        if (current.Evidence is null ||
            !string.Equals(current.Evidence.Digest, evidence.Digest, StringComparison.Ordinal))
            return new(FreshObservationFailure.EvidenceChanged);

        return new(FreshObservationFailure.None, evidence, observation);
    }

    private static string? TombstoneNotFoundReason(
        ElsaInstance instance,
        ElsaInstanceOperationSummary operation,
        Guid operationId)
    {
        if (operation.Id != operationId ||
            operation.InstanceId != instance.Id ||
            operation.Action != ElsaInstanceOperationAction.Delete ||
            operation.State != ElsaInstanceOperationState.Succeeded ||
            operation.CompletedAt is null ||
            instance.Intent.DesiredLifecycle != ElsaDesiredLifecycle.Deleting ||
            instance.ObservedLifecycle != ElsaObservedLifecycle.Deleted ||
            !Guid.TryParseExact(instance.LastOperationId?.Value, "D", out var lastOperationId) ||
            lastOperationId != operationId)
            return NotFoundNoTombstone;

        if (instance.DeletedAt is null ||
            instance.DeletedAt.Value != operation.CompletedAt.Value)
            return NotFoundDeletedAtMismatch;

        return null;
    }

    private static HistoricalReadResult LocalOrConfirmedAbsentReceipt(
        ElsaInstance instance,
        ElsaInstanceOperationSummary operation,
        Guid workspaceId,
        Guid organizationId,
        Guid instanceId,
        Guid operationId,
        bool hasAzureOperations)
    {
        if (!IsLocalNoOwnedResourcesTombstone(operation, hasAzureOperations) &&
            !IsProviderConfirmedAbsentTombstone(operation))
            return HistoricalReadResult.NotFound(NotFoundDiagnosticMismatch);

        return HistoricalReadResult.Found(new(
            instance, operation, null, null,
            ComputeLocalCleanupReceiptDigest(
                workspaceId, organizationId, instanceId, operationId,
                operation.CompletedAt!.Value, operation.DeletionDiagnosticCode!),
            LocalNoOwnedResourcesProofKind));
    }

    private static bool IsLocalNoOwnedResourcesTombstone(
        ElsaInstanceOperationSummary operation,
        bool hasAzureOperations) =>
        !hasAzureOperations &&
        string.Equals(
            operation.DeletionDiagnosticCode,
            ManagedElsaReasonCodeCatalog.DeletionLocalAbsent,
            StringComparison.Ordinal);

    private static bool IsProviderConfirmedAbsentTombstone(
        ElsaInstanceOperationSummary operation) =>
        string.Equals(
            operation.DeletionDiagnosticCode,
            ManagedElsaReasonCodeCatalog.DeletionProviderConfirmedAbsent,
            StringComparison.Ordinal);

    // Released rows, and leftover live rows on a confirmed-absent tombstone,
    // are not retained. Deleted leftovers are decided after the provider-backed
    // path so a verified cleanup assignment still issues a provider receipt.
    private static bool IsReleasedForReceipt(
        AzureProviderResourceAssignment assignment,
        ElsaInstanceOperationSummary operation) =>
        assignment.State == AzureProviderAssignmentState.Released ||
        (AzureProviderAssignmentStates.IsLive(assignment.State) &&
         IsProviderConfirmedAbsentTombstone(operation));

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

    private static string ComputeLocalCleanupReceiptDigest(
        Guid workspaceId,
        Guid organizationId,
        Guid instanceId,
        Guid lifecycleOperationId,
        DateTimeOffset lifecycleCompletedAt,
        string diagnosticCode)
    {
        var canonical = string.Join('\n',
        [
            "elsa-managed-delete-cleanup-receipt-local-v1",
            workspaceId.ToString("D"),
            organizationId.ToString("D"),
            instanceId.ToString("D"),
            lifecycleOperationId.ToString("D"),
            lifecycleCompletedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            diagnosticCode
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
    DateTimeOffset? ProviderCompletedAt,
    DateTimeOffset LifecycleCompletedAt,
    DateTimeOffset TombstoneDeletedAt,
    string ReceiptDigest,
    string ProofKind);

/// <summary>Fresh ARM observation bound to the exact retained cleanup receipt.</summary>
public sealed record ManagedElsaFreshCleanupObservationResponse(
    AzureProviderFreshResourceGroupState State,
    DateTimeOffset? ObservedAt,
    string ReasonCode,
    string? EvidenceDigest,
    string HistoricalReceiptDigest);
