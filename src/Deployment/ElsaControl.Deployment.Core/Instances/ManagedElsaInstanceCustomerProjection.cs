using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Customer-facing observed-lifecycle projection. Known in-progress operation
/// phases stay visible; Unknown is reserved for genuinely indeterminate state.
/// Lifecycle and endpoint health remain separate observations.
/// <see cref="ElsaObservedLifecycle.RecoveryRequired"/> is produced only here
/// and is never stored.
/// </summary>
public static class ManagedElsaInstanceCustomerProjection
{
    public const string GenericUnavailableReason = "This instance is not currently available.";
    public const string ProvisioningUnavailableReason =
        "This instance is still being provisioned. Refresh to update its observed state.";
    public const string FailedUnavailableReason =
        "This instance failed. Refresh for the latest Control outcome, or recover it when it is safe.";
    public const string UnknownUnavailableReason =
        "Control cannot determine this instance's state. Refresh to retry observation, or recover the instance if it remains unknown.";
    public const string RecoveryRequiredUnavailableReasonCode = "instance.recovery-required";
    public const string NeedsAttentionLabel = "Needs attention";

    public static ElsaObservedLifecycle ProjectObservedLifecycle(
        ElsaInstance instance,
        ElsaInstanceOperationSummary? activeOperation,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return ProjectObservedLifecycle(
            instance.ObservedLifecycle,
            ToActive(activeOperation),
            instance.DesiredLifecycle,
            now);
    }

    public static ElsaObservedLifecycle ProjectObservedLifecycle(
        ElsaObservedLifecycle stored,
        ActiveLifecycleOperation? activeOperation,
        ElsaDesiredLifecycle desiredLifecycle = ElsaDesiredLifecycle.Running,
        DateTimeOffset? now = null)
    {
        ElsaInstanceValue.RequireEnum(stored, nameof(stored));
        ElsaInstanceValue.RequireEnum(desiredLifecycle, nameof(desiredLifecycle));
        if (HasStoredTerminalPriority(stored) || desiredLifecycle == ElsaDesiredLifecycle.Deleting)
            return stored;

        if (IsParkedProvisioningRecovery(activeOperation, now ?? DateTimeOffset.UtcNow))
            return ElsaObservedLifecycle.RecoveryRequired;

        if (stored != ElsaObservedLifecycle.Unknown)
            return stored;

        if (activeOperation is not { } operation ||
            !ElsaInstanceOperationGuard.IsBlocking(operation.State) ||
            !IsProvisioningAction(operation.Action))
            return ElsaObservedLifecycle.Unknown;

        return operation.State switch
        {
            ElsaInstanceOperationState.Accepted or
            ElsaInstanceOperationState.WaitingForPriorOperation or
            ElsaInstanceOperationState.Queued or
            ElsaInstanceOperationState.EntitlementHeld => ElsaObservedLifecycle.Pending,
            ElsaInstanceOperationState.Running or
            ElsaInstanceOperationState.RecoveryRequired => ElsaObservedLifecycle.Provisioning,
            _ => ElsaObservedLifecycle.Unknown
        };
    }

    public static bool IsCustomerHealthy(
        ElsaDesiredLifecycle desired,
        ElsaObservedLifecycle projectedObserved,
        ElsaInstanceHealth health) =>
        desired == ElsaDesiredLifecycle.Running &&
        projectedObserved == ElsaObservedLifecycle.Ready &&
        health == ElsaInstanceHealth.Healthy;

    public static ElsaInstance Apply(
        ElsaInstance instance,
        ElsaInstanceOperationSummary? activeOperation,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var observed = ProjectObservedLifecycle(instance, activeOperation, now);
        if (observed == instance.ObservedLifecycle)
            return instance;

        return ElsaInstance.Hydrate(
            instance.Id,
            instance.OrganizationId,
            instance.WorkspaceId,
            instance.Name,
            instance.Slug,
            instance.Intent,
            observed,
            instance.Health,
            instance.Version,
            instance.IdentityBinding,
            instance.DesiredStateRevisionId,
            instance.ResolvedPlanReference,
            instance.CurrentResolvedRelease,
            instance.CurrentDeploymentReference,
            instance.PlacementAssignmentReference,
            instance.ElsaTenantReference,
            instance.LastOperationId,
            instance.DeletedAt,
            instance.CreatedAt,
            instance.UpdatedAt);
    }

    public static string? UnavailableReason(
        bool canOpen,
        bool healthy,
        bool handoffConfigured,
        bool hasIdentity,
        ElsaObservedLifecycle observedLifecycle,
        string? unauthorizedReason = "Not authorized to open this instance.",
        string? handoffUnavailableReason = null,
        string? identityUnavailableReason = "The current identity binding is unavailable.")
    {
        ElsaInstanceValue.RequireEnum(observedLifecycle, nameof(observedLifecycle));
        if (!canOpen)
            return unauthorizedReason;
        if (observedLifecycle == ElsaObservedLifecycle.RecoveryRequired)
            return GenericUnavailableReason;
        if (observedLifecycle is ElsaObservedLifecycle.Pending or
            ElsaObservedLifecycle.Provisioning or
            ElsaObservedLifecycle.Updating or
            ElsaObservedLifecycle.Stopping)
            return ProvisioningUnavailableReason;
        if (observedLifecycle == ElsaObservedLifecycle.Failed)
            return FailedUnavailableReason;
        if (observedLifecycle == ElsaObservedLifecycle.Unknown)
            return UnknownUnavailableReason;
        if (!healthy)
            return GenericUnavailableReason;
        if (!handoffConfigured)
            return handoffUnavailableReason;
        if (!hasIdentity)
            return identityUnavailableReason;
        return null;
    }

    public static bool IsKnownInProgress(ElsaObservedLifecycle lifecycle) =>
        lifecycle is ElsaObservedLifecycle.Pending
            or ElsaObservedLifecycle.Provisioning
            or ElsaObservedLifecycle.Updating
            or ElsaObservedLifecycle.Stopping
            or ElsaObservedLifecycle.Deleting;

    public static string? CustomerLabel(ElsaObservedLifecycle lifecycle)
    {
        ElsaInstanceValue.RequireEnum(lifecycle, nameof(lifecycle));
        return lifecycle == ElsaObservedLifecycle.RecoveryRequired ? NeedsAttentionLabel : null;
    }

    public static string? UnavailableReasonCode(
        bool canOpen,
        bool healthy,
        bool handoffConfigured,
        bool hasIdentity,
        ElsaObservedLifecycle observedLifecycle)
    {
        ElsaInstanceValue.RequireEnum(observedLifecycle, nameof(observedLifecycle));
        if (!canOpen)
            return "not-authorized";
        if (observedLifecycle == ElsaObservedLifecycle.RecoveryRequired)
            return RecoveryRequiredUnavailableReasonCode;
        if (IsKnownInProgress(observedLifecycle))
            return "instance.provisioning";
        if (observedLifecycle == ElsaObservedLifecycle.Failed)
            return "instance.failed";
        if (observedLifecycle == ElsaObservedLifecycle.Unknown)
            return "instance.unknown";
        if (!healthy)
            return "instance.unavailable";
        if (!handoffConfigured)
            return "handoff-unavailable";
        if (!hasIdentity)
            return "identity-unavailable";
        return null;
    }

    public static ElsaObservedLifecycle ProjectVerifiedInProgress(ElsaObservedLifecycle current) =>
        current switch
        {
            ElsaObservedLifecycle.Ready => ElsaObservedLifecycle.Ready,
            ElsaObservedLifecycle.Updating => ElsaObservedLifecycle.Updating,
            ElsaObservedLifecycle.Provisioning => ElsaObservedLifecycle.Provisioning,
            _ => ElsaObservedLifecycle.Provisioning
        };

    private static bool HasStoredTerminalPriority(ElsaObservedLifecycle stored) =>
        stored is ElsaObservedLifecycle.Failed
            or ElsaObservedLifecycle.Deleting
            or ElsaObservedLifecycle.Deleted;

    private static bool IsParkedProvisioningRecovery(
        ActiveLifecycleOperation? activeOperation,
        DateTimeOffset now) =>
        activeOperation is { State: ElsaInstanceOperationState.RecoveryRequired } parked &&
        IsProvisioningAction(parked.Action) &&
        ManagedElsaHumanRequiredPark.RequiresHuman(
            parked.ParkReason,
            parked.FailureCode,
            parked.ParkedAt,
            now);

    private static ActiveLifecycleOperation? ToActive(ElsaInstanceOperationSummary? operation) =>
        operation is null
            ? null
            : new(
                operation.Action,
                operation.State,
                FirstReason(operation.RecoveryReason, operation.ReasonCode),
                operation.FailureCode,
                operation.UpdatedAt ?? operation.AttemptStartedAt ?? operation.StartedAt ?? operation.AcceptedAt);

    private static string? FirstReason(string? recoveryReason, string? reasonCode) =>
        !string.IsNullOrWhiteSpace(recoveryReason) ? recoveryReason
        : string.IsNullOrWhiteSpace(reasonCode) ? null
        : reasonCode;

    private static bool IsProvisioningAction(ElsaInstanceOperationAction action) =>
        action is ElsaInstanceOperationAction.Create
            or ElsaInstanceOperationAction.Start
            or ElsaInstanceOperationAction.Restart
            or ElsaInstanceOperationAction.Reconcile
            or ElsaInstanceOperationAction.Recover
            or ElsaInstanceOperationAction.Retry
            or ElsaInstanceOperationAction.UpdateIntent
            or ElsaInstanceOperationAction.ApproveMinorUpgrade
            or ElsaInstanceOperationAction.MajorMigration;
}

public readonly record struct ActiveLifecycleOperation(
    ElsaInstanceOperationAction Action,
    ElsaInstanceOperationState State,
    string? ParkReason = null,
    string? FailureCode = null,
    DateTimeOffset? ParkedAt = null);
