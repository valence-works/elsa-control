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
            failureCode: null,
            recoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationInProgress,
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

    [Fact]
    public void Accepted_handoff_without_provider_is_queued()
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: null,
            recoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionAccepted);

        Assert.Equal(ManagedElsaProvisioningProgressStates.Queued, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.RequestAccepted, result.CurrentStage);
        Assert.Null(result.DiagnosticCode);
        Assert.DoesNotContain(result.Activity, entry => entry.Status == ManagedElsaProvisioningProgressActivityStatuses.Blocked);
    }

    [Theory]
    [InlineData(null, AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionUncertain)]
    [InlineData("provider.internal.failure", null)]
    [InlineData("provider.internal.failure", AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionAccepted)]
    public void Uncertain_or_failed_recovery_without_provider_stays_stale(string? failureCode, string? recoveryReason)
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: failureCode,
            recoveryReason: recoveryReason);

        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressDiagnostics.RequiresAttention, result.DiagnosticCode);
        Assert.Contains(result.Activity, entry => entry.Status == ManagedElsaProvisioningProgressActivityStatuses.Blocked);
    }

    [Theory]
    [InlineData(AzureProviderOperationStatus.Accepted)]
    [InlineData(AzureProviderOperationStatus.Queued)]
    public void Accepted_handoff_with_provider_accepted_or_queued_is_active_at_request_accepted(
        AzureProviderOperationStatus status)
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: null,
            recoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionAccepted,
            provider: Provider(status));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.RequestAccepted, result.CurrentStage);
        Assert.Null(result.DiagnosticCode);
        Assert.DoesNotContain(result.Activity, entry => entry.Status == ManagedElsaProvisioningProgressActivityStatuses.Blocked);
    }

    [Fact]
    public void Accepted_handoff_with_provider_succeeded_before_ready_is_active_at_health_verification()
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: null,
            recoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionAccepted,
            observedLifecycle: ElsaObservedLifecycle.Provisioning,
            provider: Provider(AzureProviderOperationStatus.Succeeded, AzureProviderOperationPhase.WorkloadReady, AcceptedAt.AddMinutes(8)));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.HealthVerification, result.CurrentStage);
        Assert.Null(result.DiagnosticCode);
        Assert.DoesNotContain(result.Activity, entry => entry.Status == ManagedElsaProvisioningProgressActivityStatuses.Blocked);
    }

    [Fact]
    public void Succeeded_before_ready_never_moves_the_stage_backwards()
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: null,
            recoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionAccepted,
            observedLifecycle: ElsaObservedLifecycle.Provisioning,
            provider: Provider(AzureProviderOperationStatus.Succeeded, AzureProviderOperationPhase.TrafficPromoted, AcceptedAt.AddMinutes(8)));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.TrafficRouting, result.CurrentStage);
    }

    [Theory]
    [InlineData(AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionAccepted, ManagedElsaProvisioningProgressStates.Queued, "request-accepted")]
    [InlineData(AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationInProgress, ManagedElsaProvisioningProgressStates.Queued, "request-accepted")]
    [InlineData(AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationHealthUnknown, ManagedElsaProvisioningProgressStates.Active, "health-verification")]
    public void Healthy_continuation_reasons_without_provider_stay_queued_or_active(
        string recoveryReason,
        string expectedState,
        string expectedStage)
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: null,
            recoveryReason: recoveryReason);

        Assert.Equal(expectedState, result.State);
        Assert.Equal(expectedStage, result.CurrentStage);
        Assert.Null(result.DiagnosticCode);
    }

    [Theory]
    [InlineData(AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationUnavailable)]
    [InlineData(AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationUnknown)]
    public void Transient_reasons_hold_the_last_known_stage(string recoveryReason)
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: null,
            recoveryReason: recoveryReason,
            provider: Provider(AzureProviderOperationStatus.Running, AzureProviderOperationPhase.WorkloadSubmitted),
            transitions: [Transition(1, AzureProviderOperationPhase.WorkloadSubmitted)]);

        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.RuntimeDeployment, result.CurrentStage);
        Assert.Null(result.DiagnosticCode);
        Assert.DoesNotContain(result.Activity, entry => entry.Status == ManagedElsaProvisioningProgressActivityStatuses.Blocked);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationAmbiguous)]
    [InlineData(null, AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationCorrelationMismatch)]
    [InlineData(null, AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationRetrySafe)]
    [InlineData(null, AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionUncertain)]
    [InlineData(null, "provider.not-a-real-reason")]
    [InlineData("provider.internal.failure", AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionAccepted)]
    [InlineData("provider.submission.uncertain", null)]
    public void Definite_problem_reasons_and_any_failure_code_are_immediately_stale(
        string? failureCode,
        string? recoveryReason)
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: failureCode,
            recoveryReason: recoveryReason,
            provider: Provider(AzureProviderOperationStatus.Running, AzureProviderOperationPhase.WorkloadSubmitted),
            transitions: [Transition(1, AzureProviderOperationPhase.WorkloadSubmitted)]);

        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressDiagnostics.RequiresAttention, result.DiagnosticCode);
        Assert.Contains(result.Activity, entry => entry.Status == ManagedElsaProvisioningProgressActivityStatuses.Blocked);
    }

    [Fact]
    public void Health_unknown_during_verification_stays_active_until_the_progress_bound()
    {
        var succeededAt = AcceptedAt.AddMinutes(5);
        var provider = Provider(
            AzureProviderOperationStatus.Succeeded,
            AzureProviderOperationPhase.HealthVerified,
            completedAt: succeededAt,
            createdAt: AcceptedAt,
            statusChangedAt: succeededAt);

        var beforeBound = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: null,
            recoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationHealthUnknown,
            observedLifecycle: ElsaObservedLifecycle.Provisioning,
            provider: provider,
            timeProvider: new FixedTimeProvider(succeededAt.AddMinutes(9).AddSeconds(59)));
        var atBound = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: null,
            recoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationHealthUnknown,
            observedLifecycle: ElsaObservedLifecycle.Provisioning,
            provider: provider,
            timeProvider: new FixedTimeProvider(succeededAt.AddMinutes(10)));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, beforeBound.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.HealthVerification, beforeBound.CurrentStage);
        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, atBound.State);
    }

    [Fact]
    public void Succeeded_before_ready_with_old_created_at_stays_active_from_status_changed_at()
    {
        // In-flight rows backfill StatusChangedAt from UpdatedAt. A 25-minute
        // CreatedAt must not make health-verification stale at deploy.
        var now = AcceptedAt.AddMinutes(25);
        var succeededAt = now.AddMinutes(-2);
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: null,
            recoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationHealthUnknown,
            observedLifecycle: ElsaObservedLifecycle.Provisioning,
            provider: Provider(
                AzureProviderOperationStatus.Succeeded,
                AzureProviderOperationPhase.HealthVerified,
                completedAt: succeededAt,
                createdAt: AcceptedAt,
                updatedAt: succeededAt,
                statusChangedAt: succeededAt),
            timeProvider: new FixedTimeProvider(now));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.HealthVerification, result.CurrentStage);
        Assert.Null(result.DiagnosticCode);
    }

    [Theory]
    [InlineData(AzureProviderOperationStatus.Accepted, 9, 59, ManagedElsaProvisioningProgressStates.Active)]
    [InlineData(AzureProviderOperationStatus.Queued, 9, 59, ManagedElsaProvisioningProgressStates.Active)]
    [InlineData(AzureProviderOperationStatus.Accepted, 10, 0, ManagedElsaProvisioningProgressStates.Stale)]
    [InlineData(AzureProviderOperationStatus.Queued, 10, 0, ManagedElsaProvisioningProgressStates.Stale)]
    [InlineData(AzureProviderOperationStatus.Accepted, 10, 1, ManagedElsaProvisioningProgressStates.Stale)]
    [InlineData(AzureProviderOperationStatus.Queued, 10, 1, ManagedElsaProvisioningProgressStates.Stale)]
    public void Accepted_or_queued_provider_flips_to_stale_at_exactly_ten_minutes(
        AzureProviderOperationStatus status,
        int minutes,
        int seconds,
        string expectedState)
    {
        // Inclusive bound: elapsed == 10:00 is stale. 9:59 stays active.
        var createdAt = AcceptedAt;
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: null,
            recoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionAccepted,
            provider: Provider(status, createdAt: createdAt, statusChangedAt: createdAt),
            timeProvider: new FixedTimeProvider(createdAt.AddMinutes(minutes).AddSeconds(seconds)));

        Assert.Equal(expectedState, result.State);
        if (expectedState == ManagedElsaProvisioningProgressStates.Stale)
        {
            Assert.Equal(ManagedElsaProvisioningProgressDiagnostics.RequiresAttention, result.DiagnosticCode);
            Assert.Contains(result.Activity, entry => entry.Status == ManagedElsaProvisioningProgressActivityStatuses.Blocked);
        }
        else
        {
            Assert.Equal(ManagedElsaProvisioningProgressStages.RequestAccepted, result.CurrentStage);
            Assert.Null(result.DiagnosticCode);
        }
    }

    [Fact]
    public void Unavailable_is_active_at_9_59_and_stale_at_inclusive_10_00()
    {
        var createdAt = AcceptedAt;
        var provider = Provider(
            AzureProviderOperationStatus.Queued,
            AzureProviderOperationPhase.Planned,
            createdAt: createdAt,
            statusChangedAt: createdAt);

        var before = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: null,
            recoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationUnavailable,
            provider: provider,
            timeProvider: new FixedTimeProvider(createdAt.AddMinutes(9).AddSeconds(59)));
        var atBound = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: null,
            recoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationUnavailable,
            provider: provider,
            timeProvider: new FixedTimeProvider(createdAt.AddMinutes(10)));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, before.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.RequestAccepted, before.CurrentStage);
        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, atBound.State);
        Assert.Equal(ManagedElsaProvisioningProgressDiagnostics.RequiresAttention, atBound.DiagnosticCode);
    }

    [Fact]
    public void Reason_rewritten_every_five_seconds_with_queued_unchanged_is_stale_at_10_01()
    {
        // Reconcile rewrites RecoveryReason about every 5s. That write is not
        // provider progress. Heartbeats and UpdatedAt also do not reset the clock.
        var createdAt = AcceptedAt;
        var provider = Provider(
            AzureProviderOperationStatus.Queued,
            AzureProviderOperationPhase.Planned,
            createdAt: createdAt,
            heartbeatAt: createdAt.AddMinutes(9),
            updatedAt: createdAt.AddMinutes(9),
            statusChangedAt: createdAt);

        for (var elapsed = TimeSpan.FromSeconds(5); elapsed < TimeSpan.FromMinutes(10); elapsed += TimeSpan.FromSeconds(5))
        {
            var during = Project(
                lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
                failureCode: null,
                recoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationInProgress,
                provider: provider,
                timeProvider: new FixedTimeProvider(createdAt + elapsed));
            Assert.Equal(ManagedElsaProvisioningProgressStates.Active, during.State);
        }

        var justPast = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: null,
            recoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationInProgress,
            provider: provider,
            timeProvider: new FixedTimeProvider(createdAt.AddMinutes(10).AddSeconds(1)));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, justPast.State);
    }

    [Fact]
    public void Running_provider_stays_active_for_twenty_five_minutes_while_reasons_alternate()
    {
        var started = AcceptedAt;
        foreach (var (reason, elapsed) in new (string Reason, TimeSpan Elapsed)[]
                 {
                     (AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationInProgress, TimeSpan.FromMinutes(5)),
                     (AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationUnavailable, TimeSpan.FromMinutes(12)),
                     (AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationInProgress, TimeSpan.FromMinutes(20)),
                     (AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationUnavailable, TimeSpan.FromMinutes(25))
                 })
        {
            var result = Project(
                lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
                failureCode: null,
                recoveryReason: reason,
                provider: Provider(
                    AzureProviderOperationStatus.Running,
                    AzureProviderOperationPhase.FoundationSubmitted,
                    createdAt: started,
                    heartbeatAt: started.AddMinutes(24),
                    statusChangedAt: started),
                timeProvider: new FixedTimeProvider(started + elapsed));

            Assert.Equal(ManagedElsaProvisioningProgressStates.Active, result.State);
            Assert.Equal(ManagedElsaProvisioningProgressStages.HostingFoundation, result.CurrentStage);
        }
    }

    [Fact]
    public void Finished_create_is_never_made_stale_by_the_progress_bound()
    {
        var createdAt = AcceptedAt;
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.Succeeded,
            completedAt: createdAt.AddMinutes(8),
            observedLifecycle: ElsaObservedLifecycle.Ready,
            failureCode: null,
            provider: Provider(
                AzureProviderOperationStatus.Succeeded,
                AzureProviderOperationPhase.TrafficPromoted,
                createdAt.AddMinutes(8),
                createdAt,
                heartbeatAt: createdAt),
            timeProvider: new FixedTimeProvider(createdAt.AddMinutes(15)));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Ready, result.State);
        Assert.Null(result.DiagnosticCode);
    }

    [Theory]
    [InlineData(AzureProviderOperationStatus.Failed, "provisioning.failed")]
    [InlineData(AzureProviderOperationStatus.Cancelled, "provisioning.cancelled")]
    public void Accepted_handoff_with_provider_failure_or_cancellation_is_failed(
        AzureProviderOperationStatus status,
        string diagnostic)
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: null,
            recoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionAccepted,
            provider: Provider(status, AzureProviderOperationPhase.WorkloadSubmitted));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Failed, result.State);
        Assert.Equal(diagnostic, result.DiagnosticCode);
        Assert.Contains(result.Activity, entry => entry.Status == ManagedElsaProvisioningProgressActivityStatuses.Blocked);
    }

    [Fact]
    public void Accepted_handoff_with_provider_succeeded_and_ready_is_ready()
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.RecoveryRequired,
            failureCode: null,
            recoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionAccepted,
            observedLifecycle: ElsaObservedLifecycle.Ready,
            provider: Provider(AzureProviderOperationStatus.Succeeded, AzureProviderOperationPhase.TrafficPromoted, AcceptedAt.AddMinutes(8)));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Ready, result.State);
        Assert.Null(result.CurrentStage);
        Assert.Contains(result.Activity, entry => entry.MessageCode == "engine.ready");
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
    public void Delete_accepted_without_provider_is_stale_at_exactly_ten_minutes()
    {
        var acceptedAt = AcceptedAt;
        var atBound = Project(
            lifecycleState: ElsaInstanceOperationState.Accepted,
            action: ElsaInstanceOperationAction.Delete,
            failureCode: null,
            timeProvider: new FixedTimeProvider(acceptedAt.AddMinutes(10)));
        var justPast = Project(
            lifecycleState: ElsaInstanceOperationState.Accepted,
            action: ElsaInstanceOperationAction.Delete,
            failureCode: null,
            timeProvider: new FixedTimeProvider(acceptedAt.AddMinutes(10).AddSeconds(1)));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, atBound.State);
        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, justPast.State);
        Assert.Equal(ManagedElsaProvisioningProgressDiagnostics.RequiresAttention, justPast.DiagnosticCode);
    }

    [Fact]
    public void Waiting_create_is_never_clock_stale_on_its_own()
    {
        var organizationId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var recentDeleteAcceptedAt = AcceptedAt.AddMinutes(24);
        var delete = Lifecycle(
            ElsaInstanceOperationState.Accepted,
            failureCode: null,
            action: ElsaInstanceOperationAction.Delete,
            id: Guid.Parse("77777777-7777-7777-7777-777777777777"),
            organizationId: organizationId,
            acceptedAt: recentDeleteAcceptedAt);
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.WaitingForPriorOperation,
            failureCode: null,
            action: ElsaInstanceOperationAction.Create,
            organizationId: organizationId,
            blockingOperationId: delete.Id,
            topologyOperations: [delete],
            timeProvider: new FixedTimeProvider(AcceptedAt.AddMinutes(25)));

        Assert.Equal(ManagedElsaProvisioningProgressStates.WaitingForPriorOperation, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.WaitingForDelete, result.CurrentStage);
        Assert.Equal(delete.Id, result.BlockingOperationId);
        Assert.Equal(ManagedElsaProvisioningProgressStages.RequestAccepted, result.BlockingOperationStage);
        Assert.Null(result.StaleReason);
        Assert.NotEqual(ManagedElsaProvisioningProgressStates.Queued, result.State);
        Assert.NotEqual(ManagedElsaProvisioningProgressStates.Active, result.State);
    }

    [Fact]
    public void Waiting_delete_stays_not_stale_while_create_blocker_is_still_running()
    {
        var organizationId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var create = Lifecycle(
            ElsaInstanceOperationState.RecoveryRequired,
            failureCode: null,
            action: ElsaInstanceOperationAction.Create,
            id: Guid.Parse("77777777-7777-7777-7777-777777777777"),
            organizationId: organizationId,
            recoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionAccepted);
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.WaitingForPriorOperation,
            failureCode: null,
            action: ElsaInstanceOperationAction.Delete,
            organizationId: organizationId,
            blockingOperationId: create.Id,
            topologyOperations: [create],
            blockingProvider: Provider(
                status: AzureProviderOperationStatus.Running,
                phase: AzureProviderOperationPhase.FoundationSubmitted,
                createdAt: AcceptedAt,
                statusChangedAt: AcceptedAt),
            timeProvider: new FixedTimeProvider(AcceptedAt.AddMinutes(15)));

        Assert.Equal(ManagedElsaProvisioningProgressStates.WaitingForPriorOperation, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.WaitingForPriorOperation, result.CurrentStage);
        Assert.Equal(create.Id, result.BlockingOperationId);
        Assert.Equal(ManagedElsaProvisioningProgressStages.HostingFoundation, result.BlockingOperationStage);
        Assert.Null(result.StaleReason);
        Assert.NotEqual(ManagedElsaProvisioningProgressStates.Stale, result.State);
    }

    [Fact]
    public void Waiting_delete_is_stale_when_create_blocker_has_no_provider_progress()
    {
        var organizationId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var create = Lifecycle(
            ElsaInstanceOperationState.Accepted,
            failureCode: null,
            action: ElsaInstanceOperationAction.Create,
            id: Guid.Parse("77777777-7777-7777-7777-777777777777"),
            organizationId: organizationId);
        var waiting = Project(
            lifecycleState: ElsaInstanceOperationState.WaitingForPriorOperation,
            failureCode: null,
            action: ElsaInstanceOperationAction.Delete,
            organizationId: organizationId,
            blockingOperationId: create.Id,
            topologyOperations: [create],
            timeProvider: new FixedTimeProvider(AcceptedAt.AddMinutes(10)));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, waiting.State);
        Assert.Equal(ManagedElsaProvisioningProgressStaleReasons.BlockingOperationStale, waiting.StaleReason);
        Assert.Equal(create.Id, waiting.BlockingOperationId);
        Assert.Equal(ManagedElsaProvisioningProgressStages.RequestAccepted, waiting.BlockingOperationStage);
    }

    [Fact]
    public void Serialized_waiting_snapshot_exposes_blocker_and_stale_reason()
    {
        var organizationId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var create = Lifecycle(
            ElsaInstanceOperationState.Accepted,
            failureCode: null,
            action: ElsaInstanceOperationAction.Create,
            id: Guid.Parse("77777777-7777-7777-7777-777777777777"),
            organizationId: organizationId);
        var waiting = Project(
            lifecycleState: ElsaInstanceOperationState.WaitingForPriorOperation,
            failureCode: null,
            action: ElsaInstanceOperationAction.Delete,
            organizationId: organizationId,
            blockingOperationId: create.Id,
            topologyOperations: [create],
            timeProvider: new FixedTimeProvider(AcceptedAt.AddMinutes(10)));

        var json = JsonSerializer.Serialize(waiting, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"staleReason\":\"blocking-operation-stale\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"blockingOperationId\":\"{create.Id:D}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"blockingOperationStage\":\"request-accepted\"", json, StringComparison.Ordinal);
        var queued = Project(lifecycleState: ElsaInstanceOperationState.Queued, failureCode: null);
        var queuedJson = JsonSerializer.Serialize(queued, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("staleReason", queuedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("blockingOperationId", queuedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("blockingOperationStage", queuedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_is_never_shown_as_entitlement_held_and_keeps_the_ordinary_clock()
    {
        var before = Project(
            lifecycleState: ElsaInstanceOperationState.EntitlementHeld,
            action: ElsaInstanceOperationAction.Delete,
            failureCode: null,
            timeProvider: new FixedTimeProvider(AcceptedAt.AddMinutes(9).AddSeconds(59)));
        var atBound = Project(
            lifecycleState: ElsaInstanceOperationState.EntitlementHeld,
            action: ElsaInstanceOperationAction.Delete,
            failureCode: null,
            timeProvider: new FixedTimeProvider(AcceptedAt.AddMinutes(10)));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Queued, before.State);
        Assert.NotEqual(ManagedElsaProvisioningProgressStates.EntitlementHeld, before.State);
        Assert.NotEqual(ManagedElsaProvisioningProgressStages.EntitlementHeld, before.CurrentStage);
        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, atBound.State);
        Assert.NotEqual(ManagedElsaProvisioningProgressStates.EntitlementHeld, atBound.State);
        Assert.Equal(ManagedElsaProvisioningProgressDiagnostics.RequiresAttention, atBound.DiagnosticCode);
    }

    [Fact]
    public void Entitlement_held_is_exempt_from_the_progress_clock()
    {
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.EntitlementHeld,
            failureCode: null,
            timeProvider: new FixedTimeProvider(AcceptedAt.AddMinutes(10)));

        Assert.Equal(ManagedElsaProvisioningProgressStates.EntitlementHeld, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.EntitlementHeld, result.CurrentStage);
        Assert.Null(result.DiagnosticCode);
        Assert.NotEqual(ManagedElsaProvisioningProgressStates.Queued, result.State);
        Assert.NotEqual(ManagedElsaProvisioningProgressStates.Active, result.State);
        Assert.NotEqual(ManagedElsaProvisioningProgressStates.Stale, result.State);
    }

    [Fact]
    public void Waiting_for_prior_operation_is_exempt_from_the_progress_clock()
    {
        var organizationId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var update = Lifecycle(
            ElsaInstanceOperationState.Running,
            failureCode: null,
            action: ElsaInstanceOperationAction.UpdateIntent,
            id: Guid.Parse("88888888-8888-8888-8888-888888888888"),
            organizationId: organizationId);
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.WaitingForPriorOperation,
            failureCode: null,
            organizationId: organizationId,
            blockingOperationId: update.Id,
            topologyOperations: [update],
            timeProvider: new FixedTimeProvider(AcceptedAt.AddMinutes(25)));

        Assert.Equal(ManagedElsaProvisioningProgressStates.WaitingForPriorOperation, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.WaitingForUpdate, result.CurrentStage);
        Assert.NotEqual(ManagedElsaProvisioningProgressStates.Stale, result.State);
    }

    [Fact]
    public void Delete_accepted_and_waiting_create_are_both_stale_at_exactly_ten_minutes()
    {
        var organizationId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var clock = new FixedTimeProvider(AcceptedAt.AddMinutes(10));
        var delete = Lifecycle(
            ElsaInstanceOperationState.Accepted,
            failureCode: null,
            action: ElsaInstanceOperationAction.Delete,
            id: Guid.Parse("77777777-7777-7777-7777-777777777777"),
            organizationId: organizationId);
        var deletion = Project(
            lifecycleState: ElsaInstanceOperationState.Accepted,
            action: ElsaInstanceOperationAction.Delete,
            failureCode: null,
            organizationId: organizationId,
            id: delete.Id,
            timeProvider: clock);
        var waiting = Project(
            lifecycleState: ElsaInstanceOperationState.WaitingForPriorOperation,
            failureCode: null,
            organizationId: organizationId,
            blockingOperationId: delete.Id,
            topologyOperations: [delete],
            timeProvider: clock);

        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, deletion.State);
        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, waiting.State);
        Assert.Equal(ManagedElsaProvisioningProgressStaleReasons.BlockingOperationStale, waiting.StaleReason);
        Assert.Equal(delete.Id, waiting.BlockingOperationId);
        Assert.Equal(ManagedElsaProvisioningProgressStages.RequestAccepted, waiting.BlockingOperationStage);
    }

    [Fact]
    public void Foreign_or_unresolvable_blocker_id_fails_closed_without_leaking_tenant_data()
    {
        var ownerOrg = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var foreignOrg = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var foreign = Lifecycle(
            ElsaInstanceOperationState.Accepted,
            failureCode: "foreign.tenant.secret",
            action: ElsaInstanceOperationAction.Delete,
            id: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            organizationId: foreignOrg);
        var unresolvable = Project(
            lifecycleState: ElsaInstanceOperationState.WaitingForPriorOperation,
            failureCode: null,
            organizationId: ownerOrg,
            blockingOperationId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            topologyOperations: [],
            timeProvider: new FixedTimeProvider(AcceptedAt.AddMinutes(1)));
        var foreignBlocker = Project(
            lifecycleState: ElsaInstanceOperationState.WaitingForPriorOperation,
            failureCode: null,
            organizationId: ownerOrg,
            blockingOperationId: foreign.Id,
            topologyOperations: [foreign],
            timeProvider: new FixedTimeProvider(AcceptedAt.AddMinutes(1)));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, unresolvable.State);
        Assert.Equal(ManagedElsaProvisioningProgressStaleReasons.BlockingOperationUnresolvable, unresolvable.StaleReason);
        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, foreignBlocker.State);
        Assert.Equal(ManagedElsaProvisioningProgressStaleReasons.BlockingOperationUnresolvable, foreignBlocker.StaleReason);
        Assert.Null(foreignBlocker.BlockingOperationId);
        var json = JsonSerializer.Serialize(foreignBlocker);
        Assert.DoesNotContain(foreign.Id.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("foreign.tenant.secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(foreignOrg.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Waiting_chain_resolves_one_level_only()
    {
        var organizationId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var acceptedReconcile = Lifecycle(
            ElsaInstanceOperationState.Accepted,
            failureCode: null,
            action: ElsaInstanceOperationAction.Reconcile,
            id: Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            organizationId: organizationId);
        var waitingDelete = Lifecycle(
            ElsaInstanceOperationState.WaitingForPriorOperation,
            failureCode: null,
            action: ElsaInstanceOperationAction.Delete,
            id: Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            organizationId: organizationId,
            blockingOperationId: acceptedReconcile.Id);
        var result = Project(
            lifecycleState: ElsaInstanceOperationState.WaitingForPriorOperation,
            failureCode: null,
            organizationId: organizationId,
            blockingOperationId: waitingDelete.Id,
            topologyOperations: [acceptedReconcile, waitingDelete],
            timeProvider: new FixedTimeProvider(AcceptedAt.AddMinutes(10)));

        Assert.Equal(ManagedElsaProvisioningProgressStates.WaitingForPriorOperation, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.WaitingForDelete, result.CurrentStage);
        Assert.Equal(waitingDelete.Id, result.BlockingOperationId);
        Assert.Equal(ManagedElsaProvisioningProgressStages.WaitingForPriorOperation, result.BlockingOperationStage);
        Assert.Null(result.StaleReason);
        Assert.NotEqual(ManagedElsaProvisioningProgressStates.Stale, result.State);
    }

    [Fact]
    public void Non_failed_customer_facing_tokens_do_not_mention_failure_or_time_estimates()
    {
        var forbidden = new[]
        {
            "failed", "error", "lost", "minute", "minutes", "hour", "hours", "10:00", "ten minute",
            "soon", "second", "moment", "shortly", "within",
            "subscription", "billing", "payment"
        };
        var allowedFailedTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            ManagedElsaProvisioningProgressStates.Failed,
            ManagedElsaProvisioningProgressDiagnostics.Failed,
            ManagedElsaProvisioningProgressActivityStatuses.Blocked
        };
        string[] tokens =
        [
            ManagedElsaProvisioningProgressStates.Queued,
            ManagedElsaProvisioningProgressStates.Active,
            ManagedElsaProvisioningProgressStates.WaitingForPriorOperation,
            ManagedElsaProvisioningProgressStates.EntitlementHeld,
            ManagedElsaProvisioningProgressStates.Stale,
            ManagedElsaProvisioningProgressStates.Ready,
            ManagedElsaProvisioningProgressStates.Unavailable,
            ..ManagedElsaProvisioningProgressStages.Ordered,
            ManagedElsaProvisioningProgressStages.WaitingForPriorOperation,
            ManagedElsaProvisioningProgressStages.WaitingForDelete,
            ManagedElsaProvisioningProgressStages.WaitingForUpdate,
            ManagedElsaProvisioningProgressStages.EntitlementHeld,
            ManagedElsaProvisioningProgressDiagnostics.RequiresAttention,
            ManagedElsaProvisioningProgressDiagnostics.Cancelled,
            ManagedElsaProvisioningProgressDiagnostics.HistoryUnavailable,
            "request.accepted",
            "foundation.preparing",
            "foundation.ready",
            "configuration.registry-ready",
            "configuration.secrets-ready",
            "configuration.database-ready",
            "runtime.deploying",
            "runtime.deployed",
            "health.verifying",
            "health.verified",
            "traffic.routing",
            "traffic.routed",
            "engine.ready",
            ManagedElsaProvisioningProgressCopy.EntitlementHeldCreate,
            ManagedElsaProvisioningProgressCopy.EntitlementHeldChange
        ];

        Assert.All(tokens, token =>
        {
            if (allowedFailedTokens.Contains(token))
                return;
            Assert.All(forbidden, fragment =>
                Assert.DoesNotContain(fragment, token, StringComparison.OrdinalIgnoreCase));
        });
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
        IReadOnlyList<AzureProviderOperationTransition>? transitions = null,
        string? failureCode = "provider.internal.failure",
        string? recoveryReason = null,
        TimeProvider? timeProvider = null,
        ElsaInstanceOperationAction action = ElsaInstanceOperationAction.Create,
        Guid? id = null,
        Guid organizationId = default,
        Guid? blockingOperationId = null,
        IReadOnlyList<ElsaInstanceLifecycleTopologyOperation>? topologyOperations = null,
        AzureProviderOperation? blockingProvider = null,
        IReadOnlyList<AzureProviderOperationTransition>? blockingTransitions = null) =>
        AzureManagedElsaProvisioningProgressProjector.Project(
            new AzureManagedElsaProvisioningProgressProjectionInput(
                Topology(observedLifecycle, topologyOperations),
                Lifecycle(lifecycleState, completedAt, failureCode, action, id, organizationId, blockingOperationId),
                provider,
                transitions,
                RecoveryReason: recoveryReason,
                BlockingProviderOperation: blockingProvider,
                BlockingTransitions: blockingTransitions),
            timeProvider ?? new FixedTimeProvider(AcceptedAt));

    private static ElsaInstanceLifecycleTopologySnapshot Topology(
        ElsaObservedLifecycle observedLifecycle,
        IReadOnlyList<ElsaInstanceLifecycleTopologyOperation>? operations = null) =>
        new(InstanceId, 1, ElsaDesiredLifecycle.Running, observedLifecycle, null,
            operations ?? Array.Empty<ElsaInstanceLifecycleTopologyOperation>());

    private static ElsaInstanceLifecycleTopologyOperation Lifecycle(
        ElsaInstanceOperationState state,
        DateTimeOffset? completedAt = null,
        string? failureCode = "provider.internal.failure",
        ElsaInstanceOperationAction action = ElsaInstanceOperationAction.Create,
        Guid? id = null,
        Guid organizationId = default,
        Guid? blockingOperationId = null,
        DateTimeOffset? acceptedAt = null,
        string? recoveryReason = null) =>
        new(id ?? Guid.Parse("33333333-3333-3333-3333-333333333333"), action, state, 1, 1,
            acceptedAt ?? AcceptedAt, (acceptedAt ?? AcceptedAt).AddSeconds(1), completedAt, null, failureCode, null, null, null,
            RecoveryReason: recoveryReason, OrganizationId: organizationId, BlockingOperationId: blockingOperationId);

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
        DateTimeOffset? completedAt = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? heartbeatAt = null,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? statusChangedAt = null)
    {
        var created = createdAt ?? AcceptedAt;
        var heartbeat = heartbeatAt ?? (status == AzureProviderOperationStatus.Running ? created.AddSeconds(30) : null);
        return new(Guid.Parse("55555555-5555-5555-5555-555555555555"), WorkspaceId, "provider-target-key",
            AzureProviderOperationAction.Reconcile, "provider-idempotency-key", "request-hash", "provider-operation-id",
            "plan-fingerprint", "template-fingerprint", "3.8.1", "3.8", "topology", "isolated", "westeurope",
            "provider/image", "sha256:provider-image", null, null, status, phase, 1, 1, 1, new(), "https://provider.example",
            AzureProviderHealth.Healthy, [new AzureProviderDiagnostic("provider.internal.failure", "provider.internal.message")],
            "worker-id", AcceptedAt.AddMinutes(1), heartbeat, created, updatedAt ?? AcceptedAt.AddMinutes(1), completedAt,
            InstanceId: InstanceId, LifecycleAction: ElsaInstanceOperationAction.Create,
            StatusChangedAt: statusChangedAt ?? created);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
