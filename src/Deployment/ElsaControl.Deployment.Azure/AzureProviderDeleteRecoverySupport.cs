using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Shared, value-free predicate for the narrow window in which a verified Azure delete
/// cleanup still needs lifecycle finalization. It is deliberately stricter than ordinary
/// cleanup replay: the operation and assignment must describe the same durable owner and
/// both persisted inventories must already be reduced to the retained resource-group name.
/// </summary>
public static class AzureProviderDeleteRecoverySupport
{
    /// <summary>
    /// A completed provider Delete can finish a retained lifecycle Delete without another
    /// provider attempt. The caller must additionally verify the lifecycle idempotency key
    /// and that the historical placement still matches its configured Azure destination.
    /// </summary>
    public static bool IsTerminalVerifiedCleanupEligible(
        AzureProviderOperation? operation,
        AzureProviderResourceAssignment? assignment) =>
        IsBoundGroupOnly(operation, assignment) &&
        operation!.Status == AzureProviderOperationStatus.Succeeded &&
        operation.Phase == AzureProviderOperationPhase.CleanupVerified &&
        operation.AttemptedStep is null &&
        operation.Endpoint is null &&
        assignment!.State == AzureProviderAssignmentState.Deleted &&
        string.Equals(operation.ProviderScopeFingerprint, assignment.ProviderScopeFingerprint, StringComparison.Ordinal);

    public static bool IsVerifiedCleanupEligible(
        AzureProviderOperation? operation,
        AzureProviderResourceAssignment? assignment)
    {
        if (!IsBoundGroupOnly(operation, assignment) ||
            operation!.Endpoint is not null ||
            operation.Phase != AzureProviderOperationPhase.CleanupVerified ||
            operation.AttemptedStep is not null ||
            operation.Status is not (AzureProviderOperationStatus.Running or AzureProviderOperationStatus.RecoveryRequired) ||
            assignment!.State is not (AzureProviderAssignmentState.Deleted or AzureProviderAssignmentState.Unknown))
            return false;

        return true;
    }

    /// <summary>
    /// Identifies the narrow terminal-cleanup boundary that may be claimed for one fresh
    /// provider delete attempt. Both durable inventories must already be reduced to the
    /// retained resource-group name; the runner must still prove remote absence.
    /// </summary>
    public static bool IsTerminalCleanupRetryEligible(
        AzureProviderOperation? operation,
        AzureProviderResourceAssignment? assignment)
    {
        if (!IsBoundGroupOnly(operation, assignment) ||
            operation!.Phase != AzureProviderOperationPhase.CleanupSubmitted ||
            operation.AttemptedStep != AzureProviderRunnerStep.Cleanup ||
            operation.Status is not (AzureProviderOperationStatus.Failed or AzureProviderOperationStatus.Cancelled) ||
            assignment!.State == AzureProviderAssignmentState.Deleted)
            return false;

        return true;
    }

    public static bool IsBoundGroupOnly(
        AzureProviderOperation? operation,
        AzureProviderResourceAssignment? assignment)
    {
        if (operation is null || assignment is null || operation.PersistedMetadataInvalid ||
            operation.Id == Guid.Empty || assignment.Id == Guid.Empty ||
            operation.WorkspaceId == Guid.Empty || assignment.WorkspaceId == Guid.Empty ||
            operation.OrganizationId is not { } organizationId || organizationId == Guid.Empty ||
            operation.InstanceId is not { } instanceId || instanceId == Guid.Empty ||
            assignment.OrganizationId == Guid.Empty || assignment.InstanceId == Guid.Empty ||
            operation.WorkspaceId != assignment.WorkspaceId ||
            organizationId != assignment.OrganizationId ||
            instanceId != assignment.InstanceId ||
            assignment.LastOperationId != operation.Id ||
            operation.ProviderAssignmentId != assignment.Id ||
            !IsFingerprint(operation.ProviderScopeFingerprint) ||
            !IsFingerprint(assignment.ProviderScopeFingerprint) ||
            operation.Action != AzureProviderOperationAction.Delete ||
            operation.LifecycleAction != ElsaInstanceOperationAction.Delete ||
            !string.Equals(operation.TargetKey, assignment.WorkloadName, StringComparison.Ordinal) ||
            !IsGroupOnly(assignment.Resources, assignment.ResourceGroupName) ||
            !IsGroupOnly(operation.Resources, assignment.ResourceGroupName))
            return false;

        return true;
    }

    /// <summary>
    /// Durable assignment inventory that already proves the workload is gone. This is
    /// assignment-owned evidence, not a substitute for correlating a provider Delete to
    /// the current lifecycle operation when remote resources may still exist.
    /// </summary>
    public static bool IsConfirmedAbsentAssignment(AzureProviderResourceAssignment? assignment) =>
        assignment is not null &&
        assignment.State == AzureProviderAssignmentState.Deleted &&
        IsGroupOnly(assignment.Resources, assignment.ResourceGroupName);

    private static bool IsGroupOnly(
        AzureProviderResourceReferences resources,
        string resourceGroupName) =>
        !string.IsNullOrWhiteSpace(resourceGroupName) &&
        resources == new AzureProviderResourceReferences(resourceGroupName);

    private static bool IsFingerprint(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
