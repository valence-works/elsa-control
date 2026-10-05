using System.Collections.Frozen;
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
    public const string RecoveryRequiredUnavailableReason =
        "The last change to this engine didn't finish on its own. Email hello@valence.works with this engine's name and we'll help during business hours.";
    public const string RecoveryRequiredUnavailableReasonCode = "instance.recovery-required";
    public const string ProvisioningUnavailableReasonCode = "instance.provisioning";
    public const string FailedUnavailableReasonCode = "instance.failed";
    public const string UnknownUnavailableReasonCode = "instance.unknown";
    public const string GenericUnavailableReasonCode = "instance.unavailable";
    public const string NotAuthorizedUnavailableReasonCode = "not-authorized";
    public const string HandoffUnavailableReasonCode = "handoff-unavailable";
    public const string IdentityUnavailableReasonCode = "identity-unavailable";
    public const string NeedsAttentionLabel = "Needs attention";

    private static readonly FrozenSet<string> CustomerSafeOperationReasonCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        ManagedElsaReasonCodeCatalog.DeletionBlockedByOperationInFlight,
        ManagedElsaReasonCodeCatalog.DeletionProviderProgressStale,
        ManagedElsaReasonCodeCatalog.DeletionProviderCleanupPending,
        RecoveryRequiredUnavailableReasonCode,
        ManagedElsaReasonCodeCatalog.AzureDeploymentFailed,
        ManagedElsaReasonCodeCatalog.AzureDeploymentCanceled,
        ManagedElsaReasonCodeCatalog.AzureRecoveryRetrying,
        ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted,
        ManagedElsaReasonCodeCatalog.AzureRecoveryNeedsOperator,
        ProvisioningUnavailableReasonCode,
        FailedUnavailableReasonCode,
        UnknownUnavailableReasonCode,
        GenericUnavailableReasonCode,
        NotAuthorizedUnavailableReasonCode,
        HandoffUnavailableReasonCode,
        IdentityUnavailableReasonCode,
        ElsaInstanceCommercialOperation.EntitlementRequired,
        ElsaInstanceCommercialOperation.EntitlementExpired,
        ElsaInstanceCommercialOperation.SubscriptionStateRequired,
        ElsaInstanceCommercialOperation.LifecycleConstrained,
        ElsaInstanceCommercialOperation.InstanceLimitReached,
        ElsaInstanceCommercialOperation.BindingRequired,
        ElsaInstanceCommercialOperation.EntitlementSafeExitSuperseded
    }.ToFrozenSet(StringComparer.Ordinal);

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

        if (IsParkedProvisioningRecovery(activeOperation) || IsConfirmedArmFailurePark(activeOperation))
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
        string? identityUnavailableReason = "The current identity binding is unavailable.",
        ElsaInstanceOperationSummary? activeOperation = null)
    {
        ElsaInstanceValue.RequireEnum(observedLifecycle, nameof(observedLifecycle));
        if (!canOpen)
            return unauthorizedReason;
        if (activeOperation is { } parkedDelete && IsParkedCustomerDelete(parkedDelete))
            return ParkedDeleteUnavailableReason(parkedDelete.Id);
        if (observedLifecycle == ElsaObservedLifecycle.RecoveryRequired)
        {
            return activeOperation is { Id: var operationId } && operationId != Guid.Empty
                ? FormatRecoveryRequiredUnavailableReason(operationId)
                : RecoveryRequiredUnavailableReason;
        }
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

    /// <summary>
    /// Origin for the #605 AC7 elapsed value. Counts from when the
    /// operation started — the earlier of <paramref name="startedAt"/> and
    /// <paramref name="acceptedAt"/>. <c>ReasonEnteredAt</c> is internal
    /// (the 10-minute human-required clock) and must never be used here.
    /// Current-attempt start is also ignored so auto-resume cannot reset
    /// the customer timer.
    /// </summary>
    public static DateTimeOffset? CustomerElapsedOrigin(
        DateTimeOffset? startedAt,
        DateTimeOffset? acceptedAt = null)
    {
        if (startedAt is { } started && acceptedAt is { } accepted)
            return started <= accepted ? started : accepted;
        return startedAt ?? acceptedAt;
    }

    public static string? UnavailableReasonCode(
        bool canOpen,
        bool healthy,
        bool handoffConfigured,
        bool hasIdentity,
        ElsaObservedLifecycle observedLifecycle,
        ElsaInstanceOperationSummary? activeOperation = null)
    {
        ElsaInstanceValue.RequireEnum(observedLifecycle, nameof(observedLifecycle));
        if (!canOpen)
            return NotAuthorizedUnavailableReasonCode;
        if (IsParkedCustomerDelete(activeOperation))
            return CustomerSafeOperationReason(activeOperation) ?? RecoveryRequiredUnavailableReasonCode;
        if (observedLifecycle == ElsaObservedLifecycle.RecoveryRequired)
            return RecoveryRequiredUnavailableReasonCode;
        if (IsKnownInProgress(observedLifecycle))
            return ProvisioningUnavailableReasonCode;
        if (observedLifecycle == ElsaObservedLifecycle.Failed)
            return FailedUnavailableReasonCode;
        if (observedLifecycle == ElsaObservedLifecycle.Unknown)
            return UnknownUnavailableReasonCode;
        if (!healthy)
            return GenericUnavailableReasonCode;
        if (!handoffConfigured)
            return HandoffUnavailableReasonCode;
        if (!hasIdentity)
            return IdentityUnavailableReasonCode;
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

    /// <summary>
    /// Allowlisted customer operation, cleanup, progress, and recovery codes.
    /// Unknown provider diagnostics, Azure inventory, and resource identifiers
    /// are discarded. Generic operation and Delete DTOs share this boundary.
    /// </summary>
    public static string? CustomerSafeOperationReason(string? reasonCode) =>
        !string.IsNullOrWhiteSpace(reasonCode) && CustomerSafeOperationReasonCodes.Contains(reasonCode)
            ? reasonCode
            : null;

    public static string? CustomerSafeOperationReason(ElsaInstanceOperationSummary? operation) =>
        CustomerSafeOperationReason(ManagedElsaReasonCodeCatalog.SelectCurrentReason(
            operation?.FailureCode,
            operation?.ReasonCode,
            operation?.RecoveryReason));

    public static string FormatRecoveryRequiredUnavailableReason(Guid operationId) =>
        $"The last change to this engine didn't finish on its own. Email hello@valence.works with reference {operationId:D} and we'll help during business hours.";

    public static string ParkedDeleteUnavailableReason(Guid operationId) =>
        $"Deletion of this engine didn't finish on its own. Email hello@valence.works with reference {operationId:D} and we'll help during business hours.";

    public static bool IsParkedCustomerDelete(ElsaInstanceOperationSummary? operation) =>
        operation is
        {
            Action: ElsaInstanceOperationAction.Delete,
            State: ElsaInstanceOperationState.RecoveryRequired,
            RequiresHumanAt: not null
        };

    private static bool HasStoredTerminalPriority(ElsaObservedLifecycle stored) =>
        stored is ElsaObservedLifecycle.Failed
            or ElsaObservedLifecycle.Deleting
            or ElsaObservedLifecycle.Deleted;

    private static bool IsParkedProvisioningRecovery(ActiveLifecycleOperation? activeOperation) =>
        activeOperation is { State: ElsaInstanceOperationState.RecoveryRequired, RequiresHumanAt: not null } parked &&
        IsProvisioningAction(parked.Action);

    private static bool IsConfirmedArmFailurePark(ActiveLifecycleOperation? activeOperation) =>
        activeOperation is { State: ElsaInstanceOperationState.RecoveryRequired } parked &&
        IsProvisioningAction(parked.Action) &&
        (IsArmFailureCustomerCode(parked.ParkReason) || IsArmFailureCustomerCode(parked.FailureCode));

    private static bool IsArmFailureCustomerCode(string? code) =>
        code is ManagedElsaReasonCodeCatalog.AzureDeploymentFailed
            or ManagedElsaReasonCodeCatalog.AzureDeploymentCanceled
            or ManagedElsaReasonCodeCatalog.AzureRecoveryRetrying
            or ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted
            or ManagedElsaReasonCodeCatalog.AzureRecoveryNeedsOperator;

    private static ActiveLifecycleOperation? ToActive(ElsaInstanceOperationSummary? operation) =>
        operation is null
            ? null
            : new(
                operation.Action,
                operation.State,
                ManagedElsaReasonCodeCatalog.SelectCurrentReason(
                    operation.FailureCode,
                    operation.ReasonCode,
                    operation.RecoveryReason),
                operation.FailureCode,
                operation.UpdatedAt ?? operation.AttemptStartedAt ?? operation.StartedAt ?? operation.AcceptedAt,
                operation.ReasonEnteredAt,
                operation.RequiresHumanAt);

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
    DateTimeOffset? ParkedAt = null,
    DateTimeOffset? ReasonEnteredAt = null,
    DateTimeOffset? RequiresHumanAt = null);
