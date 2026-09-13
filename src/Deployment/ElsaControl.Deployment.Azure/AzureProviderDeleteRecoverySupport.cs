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
    public static bool IsVerifiedCleanupEligible(
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
            operation.Phase != AzureProviderOperationPhase.CleanupVerified ||
            operation.AttemptedStep is not null ||
            operation.Status is not (AzureProviderOperationStatus.Running or AzureProviderOperationStatus.RecoveryRequired) ||
            !IsGroupOnly(assignment.Resources, assignment.ResourceGroupName) ||
            !IsGroupOnly(operation.Resources, assignment.ResourceGroupName) ||
            operation.Endpoint is not null ||
            assignment.State is not (AzureProviderAssignmentState.Deleted or AzureProviderAssignmentState.Unknown))
            return false;

        return true;
    }

    private static bool IsGroupOnly(
        AzureProviderResourceReferences resources,
        string resourceGroupName) =>
        !string.IsNullOrWhiteSpace(resourceGroupName) &&
        resources == new AzureProviderResourceReferences(resourceGroupName);

    private static bool IsFingerprint(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
