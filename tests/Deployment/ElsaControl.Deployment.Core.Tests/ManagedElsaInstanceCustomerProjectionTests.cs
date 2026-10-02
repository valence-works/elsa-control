using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests;

public sealed class ManagedElsaInstanceCustomerProjectionTests
{
    private static readonly Guid OrganizationId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 4, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ElsaInstanceOperationState.Accepted, ElsaObservedLifecycle.Pending)]
    [InlineData(ElsaInstanceOperationState.Queued, ElsaObservedLifecycle.Pending)]
    [InlineData(ElsaInstanceOperationState.EntitlementHeld, ElsaObservedLifecycle.Pending)]
    [InlineData(ElsaInstanceOperationState.Running, ElsaObservedLifecycle.Provisioning)]
    [InlineData(ElsaInstanceOperationState.RecoveryRequired, ElsaObservedLifecycle.RecoveryRequired)]
    public void Active_create_projects_a_known_phase_instead_of_unknown(
        ElsaInstanceOperationState operationState,
        ElsaObservedLifecycle expected)
    {
        var instance = Instance(ElsaObservedLifecycle.Unknown);
        var operation = Operation(instance.Id, ElsaInstanceOperationAction.Create, operationState);

        var projected = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation);
        var applied = ManagedElsaInstanceCustomerProjection.Apply(instance, operation);

        Assert.Equal(expected, projected);
        Assert.Equal(expected, applied.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Unknown, applied.Health);
        Assert.Equal(instance.Version, applied.Version);
    }

    [Fact]
    public void Refresh_of_a_parked_create_projects_recovery_required_and_does_not_invent_ready()
    {
        var instance = Instance(ElsaObservedLifecycle.Provisioning);
        var operation = Operation(instance.Id, ElsaInstanceOperationAction.Create, ElsaInstanceOperationState.RecoveryRequired);

        var first = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation);
        var refreshed = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation);

        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, first);
        Assert.Equal(first, refreshed);
        Assert.NotEqual(ElsaObservedLifecycle.Provisioning, refreshed);
        Assert.NotEqual(ElsaObservedLifecycle.Ready, refreshed);
        Assert.NotEqual(ElsaObservedLifecycle.Failed, refreshed);
        Assert.False(ManagedElsaInstanceCustomerProjection.IsKnownInProgress(refreshed));
    }

    [Fact]
    public void Ready_outcome_comes_from_authoritative_instance_data()
    {
        var instance = Instance(ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy);
        var operation = Operation(instance.Id, ElsaInstanceOperationAction.Create, ElsaInstanceOperationState.Succeeded);

        Assert.Equal(ElsaObservedLifecycle.Ready,
            ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation));
        Assert.Null(ManagedElsaInstanceCustomerProjection.UnavailableReason(
            canOpen: true, healthy: true, handoffConfigured: true, hasIdentity: true,
            ElsaObservedLifecycle.Ready));
        Assert.Null(ManagedElsaInstanceCustomerProjection.CustomerLabel(ElsaObservedLifecycle.Ready));
        Assert.Null(ManagedElsaInstanceCustomerProjection.UnavailableReasonCode(
            canOpen: true, healthy: true, handoffConfigured: true, hasIdentity: true,
            ElsaObservedLifecycle.Ready));
    }

    [Fact]
    public void Terminal_failure_comes_from_authoritative_instance_data()
    {
        var instance = Instance(ElsaObservedLifecycle.Failed, ElsaInstanceHealth.Unreachable);
        var operation = Operation(instance.Id, ElsaInstanceOperationAction.Create, ElsaInstanceOperationState.Failed);

        Assert.Equal(ElsaObservedLifecycle.Failed,
            ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation));
        Assert.Equal(ManagedElsaInstanceCustomerProjection.FailedUnavailableReason,
            ManagedElsaInstanceCustomerProjection.UnavailableReason(
                canOpen: true, healthy: false, handoffConfigured: false, hasIdentity: false,
                ElsaObservedLifecycle.Failed));
    }

    [Fact]
    public void Stale_health_keeps_ready_lifecycle_separate_from_endpoint_health()
    {
        var instance = Instance(ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Unknown);

        Assert.Equal(ElsaObservedLifecycle.Ready,
            ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, activeOperation: null));
        Assert.Equal(ElsaInstanceHealth.Unknown, instance.Health);
        Assert.Equal(ManagedElsaInstanceCustomerProjection.GenericUnavailableReason,
            ManagedElsaInstanceCustomerProjection.UnavailableReason(
                canOpen: true, healthy: false, handoffConfigured: true, hasIdentity: true,
                ElsaObservedLifecycle.Ready));
    }

    [Fact]
    public void Genuine_unknown_keeps_fail_safe_and_explains_refresh_recovery()
    {
        var instance = Instance(ElsaObservedLifecycle.Unknown);

        Assert.Equal(ElsaObservedLifecycle.Unknown,
            ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, activeOperation: null));
        Assert.Equal(ManagedElsaInstanceCustomerProjection.UnknownUnavailableReason,
            ManagedElsaInstanceCustomerProjection.UnavailableReason(
                canOpen: true, healthy: false, handoffConfigured: false, hasIdentity: false,
                ElsaObservedLifecycle.Unknown));
        Assert.Contains("Refresh", ManagedElsaInstanceCustomerProjection.UnknownUnavailableReason, StringComparison.Ordinal);
        Assert.Contains("recover", ManagedElsaInstanceCustomerProjection.UnknownUnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Failed_operation_does_not_invent_failed_lifecycle_from_unknown()
    {
        var instance = Instance(ElsaObservedLifecycle.Unknown);
        var operation = Operation(instance.Id, ElsaInstanceOperationAction.Create, ElsaInstanceOperationState.Failed);

        Assert.Equal(ElsaObservedLifecycle.Unknown,
            ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation));
    }

    [Fact]
    public void Active_delete_does_not_invent_a_deleting_lifecycle_from_unknown()
    {
        var instance = Instance(ElsaObservedLifecycle.Unknown);
        var operation = Operation(instance.Id, ElsaInstanceOperationAction.Delete, ElsaInstanceOperationState.Running);

        Assert.Equal(ElsaObservedLifecycle.Unknown,
            ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation));
    }

    [Fact]
    public void In_progress_unavailable_reason_asks_for_refresh_without_claiming_ready()
    {
        Assert.Equal(ManagedElsaInstanceCustomerProjection.ProvisioningUnavailableReason,
            ManagedElsaInstanceCustomerProjection.UnavailableReason(
                canOpen: true, healthy: false, handoffConfigured: false, hasIdentity: false,
                ElsaObservedLifecycle.Provisioning));
        Assert.DoesNotContain("Ready", ManagedElsaInstanceCustomerProjection.ProvisioningUnavailableReason, StringComparison.Ordinal);
    }

    public static TheoryData<ElsaObservedLifecycle> AllStoredLifecycles
    {
        get
        {
            var data = new TheoryData<ElsaObservedLifecycle>();
            foreach (var stored in Enum.GetValues<ElsaObservedLifecycle>())
                data.Add(stored);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllStoredLifecycles))]
    public void Parked_restart_wins_over_stored_lifecycle_except_terminal_priority(
        ElsaObservedLifecycle stored)
    {
        AssertParkedRecoveryOverride(stored, ElsaInstanceOperationAction.Restart);
    }

    [Theory]
    [MemberData(nameof(AllStoredLifecycles))]
    public void Parked_update_intent_wins_over_stored_lifecycle_except_terminal_priority(
        ElsaObservedLifecycle stored)
    {
        AssertParkedRecoveryOverride(stored, ElsaInstanceOperationAction.UpdateIntent);
    }

    [Theory]
    [InlineData(ElsaObservedLifecycle.Failed)]
    [InlineData(ElsaObservedLifecycle.Deleting)]
    [InlineData(ElsaObservedLifecycle.Deleted)]
    public void Stored_terminal_lifecycle_keeps_priority_over_recovery_required(
        ElsaObservedLifecycle stored)
    {
        var instance = Instance(stored);
        var operation = Operation(instance.Id, ElsaInstanceOperationAction.Restart, ElsaInstanceOperationState.RecoveryRequired);

        Assert.Equal(stored, ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation));
        Assert.NotEqual(ElsaObservedLifecycle.RecoveryRequired,
            ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation));
    }

    [Theory]
    [MemberData(nameof(AllStoredLifecycles))]
    public void Desired_deleting_keeps_priority_over_recovery_required(ElsaObservedLifecycle stored)
    {
        if (stored is ElsaObservedLifecycle.Deleted or ElsaObservedLifecycle.RecoveryRequired)
            return;

        var instance = Instance(stored, desired: ElsaDesiredLifecycle.Deleting);
        var operation = Operation(instance.Id, ElsaInstanceOperationAction.Restart, ElsaInstanceOperationState.RecoveryRequired);

        Assert.Equal(stored, ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation));
        Assert.NotEqual(ElsaObservedLifecycle.RecoveryRequired,
            ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation));
    }

    [Fact]
    public void Running_still_maps_to_provisioning_and_is_in_progress()
    {
        var instance = Instance(ElsaObservedLifecycle.Unknown);
        var operation = Operation(instance.Id, ElsaInstanceOperationAction.Create, ElsaInstanceOperationState.Running);

        var projected = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation);

        Assert.Equal(ElsaObservedLifecycle.Provisioning, projected);
        Assert.True(ManagedElsaInstanceCustomerProjection.IsKnownInProgress(projected));
        Assert.Equal("instance.provisioning",
            ManagedElsaInstanceCustomerProjection.UnavailableReasonCode(
                canOpen: true, healthy: false, handoffConfigured: false, hasIdentity: false, projected));
    }

    [Theory]
    [InlineData("azure.recovery.auto-resume-exhausted")]
    [InlineData("azure.deployment.failed")]
    [InlineData("azure.deployment.wait-exceeded")]
    public void Recovery_required_reason_codes_project_the_same_non_progress_state(string reasonCode)
    {
        var instance = Instance(ElsaObservedLifecycle.Unknown);
        var operation = Operation(
            instance.Id,
            ElsaInstanceOperationAction.Create,
            ElsaInstanceOperationState.RecoveryRequired,
            reasonCode);

        var projected = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation);
        var applied = ManagedElsaInstanceCustomerProjection.Apply(instance, operation);
        var reason = ManagedElsaInstanceCustomerProjection.UnavailableReason(
            canOpen: true, healthy: false, handoffConfigured: false, hasIdentity: false, projected);
        var reasonCodeOnWire = ManagedElsaInstanceCustomerProjection.UnavailableReasonCode(
            canOpen: true, healthy: false, handoffConfigured: false, hasIdentity: false, projected);

        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, projected);
        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, applied.ObservedLifecycle);
        Assert.Equal(instance.Id, applied.Id);
        Assert.False(ManagedElsaInstanceCustomerProjection.IsKnownInProgress(projected));
        Assert.Equal(ManagedElsaInstanceCustomerProjection.NeedsAttentionLabel,
            ManagedElsaInstanceCustomerProjection.CustomerLabel(projected));
        Assert.Equal(ManagedElsaInstanceCustomerProjection.RecoveryRequiredUnavailableReasonCode, reasonCodeOnWire);
        Assert.Equal("instance.recovery-required", reasonCodeOnWire);
        Assert.Equal(ManagedElsaInstanceCustomerProjection.GenericUnavailableReason, reason);
        Assert.DoesNotContain("Failed", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed", reasonCodeOnWire, StringComparison.Ordinal);
        Assert.DoesNotContain(reasonCode, reason, StringComparison.Ordinal);
        Assert.DoesNotContain(reasonCode, reasonCodeOnWire, StringComparison.Ordinal);
        Assert.DoesNotContain("provisioned", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Setup didn't finish",
            ManagedElsaInstanceCustomerProjection.GenericUnavailableReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Needs_attention_label_is_only_for_recovery_required_and_never_says_failed()
    {
        foreach (var lifecycle in Enum.GetValues<ElsaObservedLifecycle>())
        {
            var label = ManagedElsaInstanceCustomerProjection.CustomerLabel(lifecycle);
            if (lifecycle == ElsaObservedLifecycle.RecoveryRequired)
            {
                Assert.Equal(ManagedElsaInstanceCustomerProjection.NeedsAttentionLabel, label);
                Assert.Equal("Needs attention", label);
                Assert.DoesNotContain("Failed", label, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            Assert.Null(label);
        }
    }

    [Fact]
    public void Recovery_required_is_not_in_progress_and_does_not_use_failed_copy()
    {
        Assert.False(ManagedElsaInstanceCustomerProjection.IsKnownInProgress(ElsaObservedLifecycle.RecoveryRequired));
        Assert.NotEqual(
            ManagedElsaInstanceCustomerProjection.FailedUnavailableReason,
            ManagedElsaInstanceCustomerProjection.UnavailableReason(
                canOpen: true, healthy: false, handoffConfigured: false, hasIdentity: false,
                ElsaObservedLifecycle.RecoveryRequired));
        Assert.NotEqual(
            ManagedElsaInstanceCustomerProjection.ProvisioningUnavailableReason,
            ManagedElsaInstanceCustomerProjection.UnavailableReason(
                canOpen: true, healthy: false, handoffConfigured: false, hasIdentity: false,
                ElsaObservedLifecycle.RecoveryRequired));
        Assert.DoesNotContain(
            "Failed",
            ManagedElsaInstanceCustomerProjection.UnavailableReason(
                canOpen: true, healthy: false, handoffConfigured: false, hasIdentity: false,
                ElsaObservedLifecycle.RecoveryRequired),
            StringComparison.Ordinal);
    }

    public static TheoryData<ElsaObservedLifecycle, bool, string?> CustomerStateContractCases =>
        new()
        {
            { ElsaObservedLifecycle.Pending, true, "instance.provisioning" },
            { ElsaObservedLifecycle.Provisioning, true, "instance.provisioning" },
            { ElsaObservedLifecycle.Updating, true, "instance.provisioning" },
            { ElsaObservedLifecycle.Stopping, true, "instance.provisioning" },
            { ElsaObservedLifecycle.Deleting, true, "instance.provisioning" },
            { ElsaObservedLifecycle.Ready, false, "instance.unavailable" },
            { ElsaObservedLifecycle.Degraded, false, "instance.unavailable" },
            { ElsaObservedLifecycle.Stopped, false, "instance.unavailable" },
            { ElsaObservedLifecycle.Failed, false, "instance.failed" },
            { ElsaObservedLifecycle.Unknown, false, "instance.unknown" },
            { ElsaObservedLifecycle.Deleted, false, "instance.unavailable" },
            { ElsaObservedLifecycle.RecoveryRequired, false, "instance.recovery-required" }
        };

    [Theory]
    [MemberData(nameof(CustomerStateContractCases))]
    public void Each_customer_state_has_a_stable_progress_and_reason_contract(
        ElsaObservedLifecycle lifecycle,
        bool inProgress,
        string? reasonCode)
    {
        Assert.Equal(inProgress, ManagedElsaInstanceCustomerProjection.IsKnownInProgress(lifecycle));
        Assert.Equal(reasonCode, ManagedElsaInstanceCustomerProjection.UnavailableReasonCode(
            canOpen: true, healthy: false, handoffConfigured: true, hasIdentity: true, lifecycle));
        if (lifecycle == ElsaObservedLifecycle.RecoveryRequired)
        {
            Assert.Equal("Needs attention", ManagedElsaInstanceCustomerProjection.CustomerLabel(lifecycle));
            Assert.DoesNotContain("Failed", ManagedElsaInstanceCustomerProjection.CustomerLabel(lifecycle), StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(ManagedElsaInstanceCustomerProjection.CustomerLabel(lifecycle));
        }
    }

    [Fact]
    public void Entitlement_held_still_wins_over_a_recovery_required_projection()
    {
        var instance = Instance(ElsaObservedLifecycle.Unknown);
        var held = Operation(instance.Id, ElsaInstanceOperationAction.Create, ElsaInstanceOperationState.EntitlementHeld);

        var projected = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, held);

        Assert.Equal(ElsaObservedLifecycle.Pending, projected);
        Assert.True(ManagedElsaInstanceCustomerProjection.IsKnownInProgress(projected));
        Assert.NotEqual(ElsaObservedLifecycle.RecoveryRequired, projected);
        Assert.Equal("instance.provisioning",
            ManagedElsaInstanceCustomerProjection.UnavailableReasonCode(
                canOpen: true, healthy: false, handoffConfigured: false, hasIdentity: false, projected));
    }

    [Theory]
    [MemberData(nameof(AllStoredLifecycles))]
    public void Recovery_required_is_never_a_persisted_state_machine_target(ElsaObservedLifecycle stored)
    {
        if (stored == ElsaObservedLifecycle.RecoveryRequired)
            return;

        Assert.False(ElsaInstanceStateMachine.CanTransition(stored, ElsaObservedLifecycle.RecoveryRequired));
    }

    [Theory]
    [InlineData(ManagedElsaReasonCodeCatalog.ProviderSubmissionAccepted)]
    [InlineData(ElsaInstanceProviderReconciliationService.InProgressCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.HealthUnknownCode)]
    public void Healthy_hand_off_reasons_keep_the_normal_in_progress_projection(string reason)
    {
        var instance = Instance(ElsaObservedLifecycle.Unknown);
        var operation = Operation(
            instance.Id,
            ElsaInstanceOperationAction.Create,
            ElsaInstanceOperationState.RecoveryRequired,
            reason);

        var projected = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation, Now);

        Assert.Equal(ElsaObservedLifecycle.Provisioning, projected);
        Assert.True(ManagedElsaInstanceCustomerProjection.IsKnownInProgress(projected));
        Assert.Null(ManagedElsaInstanceCustomerProjection.CustomerLabel(projected));
        Assert.NotEqual(ElsaObservedLifecycle.RecoveryRequired, projected);
    }

    [Fact]
    public void Submission_uncertain_inside_the_window_stays_in_progress()
    {
        var instance = Instance(ElsaObservedLifecycle.Unknown);
        var operation = Operation(
            instance.Id,
            ElsaInstanceOperationAction.Create,
            ElsaInstanceOperationState.RecoveryRequired,
            ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain,
            parkedAt: Now);

        var projected = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(
            instance, operation, Now + ManagedElsaReasonCodeCatalog.SubmissionUncertainHealthyWindow - TimeSpan.FromSeconds(1));

        Assert.Equal(ElsaObservedLifecycle.Provisioning, projected);
        Assert.True(ManagedElsaInstanceCustomerProjection.IsKnownInProgress(projected));
        Assert.NotEqual(ElsaObservedLifecycle.RecoveryRequired, projected);
    }

    [Fact]
    public void Submission_uncertain_at_the_window_projects_recovery_required()
    {
        var instance = Instance(ElsaObservedLifecycle.Unknown);
        var operation = Operation(
            instance.Id,
            ElsaInstanceOperationAction.Create,
            ElsaInstanceOperationState.RecoveryRequired,
            ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain,
            parkedAt: Now);

        var projected = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(
            instance, operation, Now + ManagedElsaReasonCodeCatalog.SubmissionUncertainHealthyWindow);

        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, projected);
        Assert.False(ManagedElsaInstanceCustomerProjection.IsKnownInProgress(projected));
        Assert.Equal(ManagedElsaInstanceCustomerProjection.NeedsAttentionLabel,
            ManagedElsaInstanceCustomerProjection.CustomerLabel(projected));
    }

    [Fact]
    public void Unknown_park_reason_fails_safe_to_recovery_required()
    {
        var instance = Instance(ElsaObservedLifecycle.Provisioning);
        var operation = Operation(
            instance.Id,
            ElsaInstanceOperationAction.Create,
            ElsaInstanceOperationState.RecoveryRequired,
            "provider.recovery.never-seen");

        var projected = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation, Now);

        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, projected);
        Assert.False(ManagedElsaInstanceCustomerProjection.IsKnownInProgress(projected));
    }

    [Fact]
    public void Ready_healthy_human_required_park_is_not_customer_healthy()
    {
        var instance = Instance(ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy);
        var operation = Operation(
            instance.Id,
            ElsaInstanceOperationAction.UpdateIntent,
            ElsaInstanceOperationState.RecoveryRequired,
            ElsaInstanceProviderReconciliationService.AutoResumeExhaustedCode);
        var listed = ManagedElsaInstanceCustomerProjection.Apply(instance, operation, Now);
        var detail = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation, Now);

        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, listed.ObservedLifecycle);
        Assert.Equal(detail, listed.ObservedLifecycle);
        Assert.False(ManagedElsaInstanceCustomerProjection.IsCustomerHealthy(
            instance.DesiredLifecycle, listed.ObservedLifecycle, instance.Health));
        Assert.False(ManagedElsaInstanceCustomerProjection.IsCustomerHealthy(
            instance.DesiredLifecycle, detail, instance.Health));
        Assert.Equal(ManagedElsaInstanceCustomerProjection.NeedsAttentionLabel,
            ManagedElsaInstanceCustomerProjection.CustomerLabel(detail));
    }

    [Fact]
    public void Ready_healthy_hand_off_park_stays_customer_healthy()
    {
        var instance = Instance(ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy);
        var operation = Operation(
            instance.Id,
            ElsaInstanceOperationAction.UpdateIntent,
            ElsaInstanceOperationState.RecoveryRequired,
            ManagedElsaReasonCodeCatalog.ProviderSubmissionAccepted);
        var listed = ManagedElsaInstanceCustomerProjection.Apply(instance, operation, Now);
        var detail = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation, Now);

        Assert.Equal(ElsaObservedLifecycle.Ready, listed.ObservedLifecycle);
        Assert.Equal(detail, listed.ObservedLifecycle);
        Assert.True(ManagedElsaInstanceCustomerProjection.IsCustomerHealthy(
            instance.DesiredLifecycle, listed.ObservedLifecycle, instance.Health));
        Assert.Null(ManagedElsaInstanceCustomerProjection.CustomerLabel(detail));
    }

    [Fact]
    public void Delete_stays_available_because_recovery_required_is_not_failed_or_deleting()
    {
        var instance = Instance(ElsaObservedLifecycle.Provisioning);
        var operation = Operation(instance.Id, ElsaInstanceOperationAction.Restart, ElsaInstanceOperationState.RecoveryRequired);
        var projected = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation);

        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, projected);
        Assert.NotEqual(ElsaObservedLifecycle.Failed, projected);
        Assert.NotEqual(ElsaObservedLifecycle.Deleting, projected);
        Assert.NotEqual(ElsaObservedLifecycle.Deleted, projected);
        Assert.False(ManagedElsaInstanceCustomerProjection.IsKnownInProgress(projected));
        Assert.Equal(ElsaDesiredLifecycle.Running, instance.DesiredLifecycle);
    }

    private static void AssertParkedRecoveryOverride(
        ElsaObservedLifecycle stored,
        ElsaInstanceOperationAction action)
    {
        var instance = Instance(stored);
        var operation = Operation(instance.Id, action, ElsaInstanceOperationState.RecoveryRequired);
        var projected = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation);

        if (stored is ElsaObservedLifecycle.Failed
            or ElsaObservedLifecycle.Deleting
            or ElsaObservedLifecycle.Deleted)
        {
            Assert.Equal(stored, projected);
            return;
        }

        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, projected);
        Assert.False(ManagedElsaInstanceCustomerProjection.IsKnownInProgress(projected));
        Assert.Equal(ManagedElsaInstanceCustomerProjection.NeedsAttentionLabel,
            ManagedElsaInstanceCustomerProjection.CustomerLabel(projected));
    }

    private static ElsaInstance Instance(
        ElsaObservedLifecycle observed,
        ElsaInstanceHealth health = ElsaInstanceHealth.Unknown,
        ElsaDesiredLifecycle desired = ElsaDesiredLifecycle.Running) =>
        ElsaInstance.Hydrate(
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            OrganizationId,
            WorkspaceId,
            "Managed Elsa",
            "managed-elsa",
            new(
                new("commercial", "5.0", "5.0.1"),
                new("server-studio"),
                new("managed", "westeurope", "dedicated", "standard-small", "public", "managed"),
                observed == ElsaObservedLifecycle.Deleted ? ElsaDesiredLifecycle.Deleting : desired),
            observed,
            health,
            4,
            deletedAt: observed == ElsaObservedLifecycle.Deleted ? Now : null);

    private static ElsaInstanceOperationSummary Operation(
        Guid instanceId,
        ElsaInstanceOperationAction action,
        ElsaInstanceOperationState state,
        string? reasonCode = null,
        DateTimeOffset? parkedAt = null) =>
        new(
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            instanceId,
            action,
            state,
            1,
            1,
            Now,
            state is ElsaInstanceOperationState.Running or ElsaInstanceOperationState.RecoveryRequired ? Now : null,
            state is ElsaInstanceOperationState.Succeeded or ElsaInstanceOperationState.Failed ? Now : null,
            null,
            null,
            null,
            state == ElsaInstanceOperationState.Failed ? "provider.reconciliation.failed" : null,
            null,
            null,
            reasonCode,
            RecoveryReason: reasonCode,
            UpdatedAt: parkedAt ?? Now);
}
