using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureManagedElsaProvisioningProgressProjectorTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset AcceptedAt = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Queued_create_has_request_stage_and_no_provider_details()
    {
        var result = Project(lifecycleState: ElsaInstanceOperationState.Queued);

        Assert.Equal(ManagedElsaProvisioningProgressStates.Queued, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.RequestAccepted, result.CurrentStage);
        Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Current, result.Stages[0].Status);
        Assert.All(result.Stages.Skip(1), stage => Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Pending, stage.Status));
        Assert.Contains(result.Activity, entry => entry.MessageCode == "request.accepted");
    }

    [Theory]
    [InlineData(AzureProviderOperationPhase.Planned, "request-accepted")]
    [InlineData(AzureProviderOperationPhase.FoundationSubmitted, "hosting-foundation")]
    [InlineData(AzureProviderOperationPhase.FoundationObserved, "hosting-foundation")]
    [InlineData(AzureProviderOperationPhase.AcrPullObserved, "configuration")]
    [InlineData(AzureProviderOperationPhase.SeedSecretsObserved, "configuration")]
    [InlineData(AzureProviderOperationPhase.SqlFirewallReady, "configuration")]
    [InlineData(AzureProviderOperationPhase.SqlBootstrapReady, "configuration")]
    [InlineData(AzureProviderOperationPhase.FoundationReady, "configuration")]
    [InlineData(AzureProviderOperationPhase.WorkloadSubmitted, "runtime-deployment")]
    [InlineData(AzureProviderOperationPhase.WorkloadReady, "runtime-deployment")]
    [InlineData(AzureProviderOperationPhase.HealthVerified, "health-verification")]
    [InlineData(AzureProviderOperationPhase.TrafficPromoted, "traffic-routing")]
    public void Known_provider_phases_map_to_stable_stages(AzureProviderOperationPhase phase, string expectedStage)
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.Running,
            provider: Provider(phase: phase),
            transitions: [Transition(1, phase)]);

        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, result.State);
        Assert.Equal(expectedStage, result.CurrentStage);

        var currentIndex = result.Stages.Select((stage, index) => (stage, index)).Single(item => item.stage.Code == expectedStage).index;
        Assert.All(result.Stages.Take(currentIndex), stage => Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Completed, stage.Status));
        Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Current, result.Stages[currentIndex].Status);
        Assert.All(result.Stages.Skip(currentIndex + 1), stage => Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Pending, stage.Status));
    }

    [Fact]
    public void Later_phase_cannot_be_regressed_by_an_older_transition()
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.Running,
            provider: Provider(phase: AzureProviderOperationPhase.HealthVerified),
            transitions:
            [
                Transition(3, AzureProviderOperationPhase.HealthVerified, AcceptedAt.AddMinutes(3)),
                Transition(2, AzureProviderOperationPhase.WorkloadReady, AcceptedAt.AddMinutes(2)),
                Transition(1, AzureProviderOperationPhase.Planned, AcceptedAt.AddMinutes(1))
            ]);

        Assert.Equal(ManagedElsaProvisioningProgressStages.HealthVerification, result.CurrentStage);
        Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Completed, result.Stages[3].Status);
        Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Current, result.Stages[4].Status);
        Assert.Equal(new[] { 1L, 2L, 3L }, result.Activity.Select(entry => entry.Sequence).ToArray());
    }

    [Fact]
    public void Duplicate_transitions_are_coalesced_and_ordered_by_durable_sequence()
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.Running,
            provider: Provider(phase: AzureProviderOperationPhase.WorkloadSubmitted),
            transitions:
            [
                Transition(4, AzureProviderOperationPhase.WorkloadSubmitted, AcceptedAt.AddMinutes(4)),
                Transition(2, AzureProviderOperationPhase.WorkloadSubmitted, AcceptedAt.AddMinutes(2)),
                Transition(3, AzureProviderOperationPhase.WorkloadSubmitted, AcceptedAt.AddMinutes(3)),
                Transition(1, AzureProviderOperationPhase.FoundationReady, AcceptedAt.AddMinutes(1))
            ]);

        Assert.Equal(new[] { 0L, 1L, 2L }, result.Activity.Select(entry => entry.Sequence).ToArray());
        Assert.Single(result.Activity, entry => entry.MessageCode == "runtime.deploying");
        Assert.Equal(1, result.Activity.Count(entry => entry.MessageCode == "foundation.ready"));
        Assert.Equal(AcceptedAt.AddMinutes(1), result.Activity[1].OccurredAt);
    }

    [Fact]
    public void Recovery_required_is_stale_and_blocks_the_last_known_stage()
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.Running,
            provider: Provider(AzureProviderOperationStatus.RecoveryRequired, AzureProviderOperationPhase.WorkloadSubmitted),
            transitions: [Transition(1, AzureProviderOperationPhase.WorkloadSubmitted)]);

        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressDiagnostics.RequiresAttention, result.DiagnosticCode);
        Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Blocked, result.Stages[3].Status);
        Assert.Contains(result.Activity, entry => entry.Status == ManagedElsaProvisioningProgressActivityStatuses.Blocked);
    }

    [Fact]
    public void Lifecycle_recovery_with_running_provider_keeps_progress_active()
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            provider: Provider(AzureProviderOperationStatus.Running, AzureProviderOperationPhase.WorkloadSubmitted),
            transitions: [Transition(1, AzureProviderOperationPhase.WorkloadSubmitted)]);

        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.RuntimeDeployment, result.CurrentStage);
        Assert.DoesNotContain(result.Activity, entry => entry.Status == ManagedElsaProvisioningProgressActivityStatuses.Blocked);
    }

    [Fact]
    public void Lifecycle_recovery_without_provider_remains_stale()
    {
        var result = Project(lifecycleState: ElsaInstanceOperationState.RecoveryRequired);

        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressDiagnostics.RequiresAttention, result.DiagnosticCode);
        Assert.Contains(result.Activity, entry => entry.Status == ManagedElsaProvisioningProgressActivityStatuses.Blocked);
    }

    [Theory]
    [InlineData(ElsaInstanceOperationState.Failed, "provisioning.failed")]
    [InlineData(ElsaInstanceOperationState.Cancelled, "provisioning.cancelled")]
    public void Lifecycle_failure_and_cancellation_win_over_provider_state(ElsaInstanceOperationState state, string diagnostic)
    {
        var result = Project(
            lifecycleState: state,
            provider: Provider(AzureProviderOperationStatus.Succeeded, AzureProviderOperationPhase.TrafficPromoted),
            transitions: [Transition(1, AzureProviderOperationPhase.TrafficPromoted)]);

        Assert.Equal(ManagedElsaProvisioningProgressStates.Failed, result.State);
        Assert.Equal(diagnostic, result.DiagnosticCode);
        Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Blocked, result.Stages[5].Status);
    }

    [Fact]
    public void Ready_makes_every_stage_complete_and_stops_at_terminal_state()
    {
        var completedAt = AcceptedAt.AddMinutes(8);
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.Succeeded,
            completedAt: completedAt,
            observedLifecycle: ElsaObservedLifecycle.Ready,
            provider: Provider(AzureProviderOperationStatus.Succeeded, AzureProviderOperationPhase.TrafficPromoted, completedAt),
            transitions:
            [
                Transition(1, AzureProviderOperationPhase.Planned),
                Transition(2, AzureProviderOperationPhase.FoundationReady, AcceptedAt.AddMinutes(1)),
                Transition(3, AzureProviderOperationPhase.WorkloadReady, AcceptedAt.AddMinutes(4)),
                Transition(4, AzureProviderOperationPhase.HealthVerified, AcceptedAt.AddMinutes(6)),
                Transition(5, AzureProviderOperationPhase.TrafficPromoted, AcceptedAt.AddMinutes(7))
            ]);

        Assert.Equal(ManagedElsaProvisioningProgressStates.Ready, result.State);
        Assert.Null(result.CurrentStage);
        Assert.All(result.Stages, stage => Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Completed, stage.Status));
        Assert.Equal(completedAt, result.CompletedAt);
        Assert.Contains(result.Activity, entry => entry.MessageCode == "engine.ready");
    }

    [Fact]
    public void Unknown_phase_is_active_without_echoing_internal_values()
    {
        var unknownPhase = (AzureProviderOperationPhase)999;
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.Running,
            provider: Provider(phase: unknownPhase),
            transitions: [Transition(1, unknownPhase)]);

        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, result.State);
        Assert.Null(result.CurrentStage);
        Assert.DoesNotContain(result.Activity, entry => entry.Sequence > 0);
        Assert.DoesNotContain("999", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Unavailable_history_is_unknown_and_redacted()
    {
        var result = AzureManagedElsaProvisioningProgressProjector.Project(
            new AzureManagedElsaProvisioningProgressProjectionInput(null, null, null));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Unavailable, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressDiagnostics.HistoryUnavailable, result.DiagnosticCode);
        Assert.All(result.Stages, stage => Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Unknown, stage.Status));
    }

    [Fact]
    public void Unavailable_history_preserves_only_the_safe_lifecycle_acceptance_signal()
    {
        var lifecycle = Lifecycle(ElsaInstanceOperationState.Running);
        var result = AzureManagedElsaProvisioningProgressProjector.Project(
            new AzureManagedElsaProvisioningProgressProjectionInput(
                Topology(ElsaObservedLifecycle.Provisioning),
                lifecycle,
                ProviderOperation: null,
                Transitions: null,
                HistoryUnavailable: true));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Unavailable, result.State);
        Assert.Equal(AcceptedAt, result.StartedAt);
        Assert.Equal("request.accepted", Assert.Single(result.Activity).MessageCode);
        Assert.All(result.Stages, stage =>
            Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Unknown, stage.Status));
    }

    [Fact]
    public void Serialized_projection_contains_only_allowlisted_customer_values()
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.Running,
            provider: Provider(phase: AzureProviderOperationPhase.FoundationSubmitted),
            transitions: [Transition(1, AzureProviderOperationPhase.FoundationSubmitted)]);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("hosting-foundation", json);
        Assert.DoesNotContain("provider-operation-id", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resource-group", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("target-secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider.internal.failure", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Provider().Id.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
    }

    private static ManagedElsaProvisioningProgress Project(
        ElsaInstanceOperationState lifecycleState,
        DateTimeOffset? completedAt = null,
        ElsaObservedLifecycle observedLifecycle = ElsaObservedLifecycle.Provisioning,
        AzureProviderOperation? provider = null,
        IReadOnlyList<AzureProviderOperationTransition>? transitions = null) =>
        AzureManagedElsaProvisioningProgressProjector.Project(
            new AzureManagedElsaProvisioningProgressProjectionInput(
                Topology(observedLifecycle),
                Lifecycle(lifecycleState, completedAt),
                provider,
                transitions));

    private static ElsaInstanceLifecycleTopologySnapshot Topology(ElsaObservedLifecycle observedLifecycle) =>
        new(InstanceId, 1, ElsaDesiredLifecycle.Running, observedLifecycle, null, Array.Empty<ElsaInstanceLifecycleTopologyOperation>());

    private static ElsaInstanceLifecycleTopologyOperation Lifecycle(
        ElsaInstanceOperationState state,
        DateTimeOffset? completedAt = null) =>
        new(Guid.Parse("33333333-3333-3333-3333-333333333333"), ElsaInstanceOperationAction.Create, state, 1, 1,
            AcceptedAt, AcceptedAt.AddSeconds(1), completedAt, null, "provider.internal.failure", null, null, null);

    private static AzureProviderOperationTransition Transition(
        long sequence,
        AzureProviderOperationPhase phase,
        DateTimeOffset? occurredAt = null,
        AzureProviderOperationStatus status = AzureProviderOperationStatus.Running) =>
        new(Guid.NewGuid(), Guid.Parse("44444444-4444-4444-4444-444444444444"), sequence, status, phase,
            "provider.internal.code", "provider.internal.message", occurredAt ?? AcceptedAt.AddSeconds(sequence));

    private static AzureProviderOperation Provider(
        AzureProviderOperationStatus status = AzureProviderOperationStatus.Running,
        AzureProviderOperationPhase phase = AzureProviderOperationPhase.Planned,
        DateTimeOffset? completedAt = null) =>
        new(Guid.Parse("55555555-5555-5555-5555-555555555555"), WorkspaceId, "provider-target-key",
            AzureProviderOperationAction.Reconcile, "provider-idempotency-key", "request-hash", "provider-operation-id",
            "plan-fingerprint", "template-fingerprint", "3.8.1", "3.8", "topology", "isolated", "westeurope",
            "provider/image", "sha256:provider-image", null, null, status, phase, 1, 1, 1, new(), "https://provider.example",
            AzureProviderHealth.Healthy, [new AzureProviderDiagnostic("provider.internal.failure", "provider.internal.message")],
            "worker-id", AcceptedAt.AddMinutes(1), AcceptedAt.AddSeconds(30), AcceptedAt, AcceptedAt.AddMinutes(1), completedAt,
            InstanceId: InstanceId, LifecycleAction: ElsaInstanceOperationAction.Create);
}
