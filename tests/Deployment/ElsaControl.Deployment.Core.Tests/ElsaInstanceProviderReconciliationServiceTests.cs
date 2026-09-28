using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests;

public sealed class ElsaInstanceProviderReconciliationServiceTests
{
    private static readonly Guid OrganizationId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Commit_envelope_rejects_null_evidence_fingerprint_with_contract_error()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var target = await store.GetTargetAsync(WorkspaceId, accepted.Operation.Id)
            ?? throw new InvalidOperationException("Expected a reconciliation target.");
        var commit = new ElsaInstanceProviderReconciliationCommit(
            WorkspaceId,
            target.Instance.Id,
            target.Operation.Id,
            target.Instance.Version,
            target.Operation.AttemptNumber,
            target.ReconciliationVersion,
            null!,
            target.Instance,
            target.Operation,
            ElsaInstanceProviderReconciliationService.UnknownCode,
            false,
            null,
            null,
            Now);

        var error = Assert.Throws<InvalidOperationException>(commit.Validate);

        Assert.Equal("Provider reconciliation commit envelope is invalid.", error.Message);
    }

    [Theory]
    [InlineData(ElsaInstanceProviderObservationKind.Unknown, ElsaInstanceProviderReconciliationService.UnknownCode)]
    [InlineData(ElsaInstanceProviderObservationKind.Ambiguous, ElsaInstanceProviderReconciliationService.AmbiguousCode)]
    public async Task Uncertain_observation_remains_unknown_and_recovery_required(
        ElsaInstanceProviderObservationKind kind,
        string expectedCode)
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var observation = new ElsaInstanceProviderObservation(
            kind, ElsaObservedLifecycle.Unknown, ElsaInstanceProviderHealthGate.Unknown, "observation-1");

        var result = await Service(store, new RecordingPort(observation)).ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(ElsaObservedLifecycle.Unknown, result.Projection.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Unknown, result.Projection.Health);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, result.Projection.OperationState);
        Assert.Equal(expectedCode, result.DiagnosticCode);
        Assert.False(result.RetrySafe);
    }

    [Fact]
    public async Task Retry_safety_is_explicit_but_does_not_trigger_a_blind_retry()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var evidence = new ElsaInstanceProviderRetryEvidence(
            "https://evidence.example.test/recovery/proof-1",
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var observation = new ElsaInstanceProviderObservation(
            ElsaInstanceProviderObservationKind.Unknown,
            ElsaObservedLifecycle.Unknown,
            ElsaInstanceProviderHealthGate.Unknown,
            "observation-1",
            evidence);

        var result = await Service(store, new RecordingPort(observation)).ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.True(result.RetrySafe);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, result.Projection.OperationState);
        Assert.Equal(ElsaObservedLifecycle.Unknown, result.Projection.ObservedLifecycle);
    }

    [Fact]
    public async Task Confirmed_completed_retry_evidence_auto_resumes_without_an_admin_recover()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var observation = new ElsaInstanceProviderObservation(
            ElsaInstanceProviderObservationKind.Confirmed,
            ElsaObservedLifecycle.Provisioning,
            ElsaInstanceProviderHealthGate.Unknown,
            "observation-late-success",
            OpaqueEvidence(autoResume: true));
        var lifecycle = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var port = new ChargingPort(observation);

        var result = await new ElsaInstanceProviderReconciliationService(
                store, port, new StaticTimeProvider(Now), lifecycle)
            .ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.True(result.RetrySafe);
        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, result.Projection.OperationState);
        var resumed = Assert.Single(store.Operations);
        Assert.Equal(accepted.Operation.Id, resumed.Id);
        Assert.Equal(ElsaInstanceOperationState.Queued, resumed.State);
        Assert.Equal(accepted.Operation.AttemptNumber + 1, resumed.AttemptNumber);
        var recovery = Assert.Single(store.RecoveryRequests);
        Assert.Equal(accepted.Operation.Id, recovery.OperationId);
        Assert.Equal($"auto-resume.{accepted.Operation.Id:N}.1", recovery.IdempotencyKey);
        Assert.Equal(1, port.AutoResumeCount);
        Assert.Equal(["azure.recovery.auto-resume.accepted"], port.Outcomes);
    }

    [Fact]
    public async Task Auto_resume_charges_the_slot_before_recover_and_keeps_it_when_recover_conflicts()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var lifecycle = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var retrySafe = await new ElsaInstanceProviderReconciliationService(
                store,
                new RecordingPort(new ElsaInstanceProviderObservation(
                    ElsaInstanceProviderObservationKind.Confirmed,
                    ElsaObservedLifecycle.Provisioning,
                    ElsaInstanceProviderHealthGate.Unknown,
                    "observation-retry-safe",
                    OpaqueEvidence(autoResume: false))),
                new StaticTimeProvider(Now),
                lifecycle)
            .ReconcileAsync(WorkspaceId, accepted.Operation.Id);
        await lifecycle.RecoverAsync(new ElsaInstanceLifecycleRequest(
            WorkspaceId,
            accepted.Instance.Id,
            retrySafe.Projection.InstanceVersion,
            $"auto-resume.{accepted.Operation.Id:N}.1",
            "auto-resume",
            ActorAccountId: null,
            ExpectedOperationId: accepted.Operation.Id));
        store.MarkRecoveryRequired(accepted.Operation.Id);
        var port = new ChargingPort(
            new ElsaInstanceProviderObservation(
                ElsaInstanceProviderObservationKind.Confirmed,
                ElsaObservedLifecycle.Provisioning,
                ElsaInstanceProviderHealthGate.Unknown,
                "observation-lost-cas",
                OpaqueEvidence(autoResume: true)));

        var result = await new ElsaInstanceProviderReconciliationService(
                store, port, new StaticTimeProvider(Now.AddMinutes(1)), lifecycle)
            .ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.True(result.RetrySafe);
        Assert.Equal(1, port.AutoResumeCount);
        Assert.Equal(1, port.ChargeCalls);
        Assert.Equal(["azure.recovery.auto-resume.conflict"], port.Outcomes);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, Assert.Single(store.Operations).State);
    }

    [Fact]
    public async Task Auto_resume_at_the_third_stop_parks_recovery_required_once_with_exhausted_code()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var lifecycle = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var observation = new ElsaInstanceProviderObservation(
            ElsaInstanceProviderObservationKind.Confirmed,
            ElsaObservedLifecycle.Provisioning,
            ElsaInstanceProviderHealthGate.Unknown,
            "observation-exhausted",
            OpaqueEvidence(autoResume: false))
        {
            ReasonCode = "azure.recovery.auto-resume-exhausted"
        };
        var port = new ChargingPort(observation, initialCount: 3);
        var service = new ElsaInstanceProviderReconciliationService(
            store, port, new StaticTimeProvider(Now), lifecycle);

        var first = await service.ReconcileAsync(WorkspaceId, accepted.Operation.Id);
        var second = await service.ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.RecoveryRequired, first.Outcome);
        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.RecoveryRequired, second.Outcome);
        Assert.Equal("azure.recovery.auto-resume-exhausted", first.DiagnosticCode);
        Assert.Equal(first.DiagnosticCode, second.DiagnosticCode);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, Assert.Single(store.Operations).State);
        Assert.Equal(accepted.Operation.AttemptNumber, Assert.Single(store.Operations).AttemptNumber);
        Assert.Empty(store.RecoveryRequests);
        Assert.Equal(0, port.ChargeCalls);
        Assert.Equal(3, port.AutoResumeCount);
        Assert.Empty(port.Outcomes);
    }

    [Fact]
    public async Task Concurrent_auto_resume_ticks_at_count_two_produce_exactly_one_resume()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var lifecycle = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var barrier = new Barrier(2);
        var port = new ChargingPort(
            new ElsaInstanceProviderObservation(
                ElsaInstanceProviderObservationKind.Confirmed,
                ElsaObservedLifecycle.Provisioning,
                ElsaInstanceProviderHealthGate.Unknown,
                "observation-concurrent-cap",
                OpaqueEvidence(autoResume: true)),
            initialCount: 2,
            claimBarrier: barrier);
        var service = new ElsaInstanceProviderReconciliationService(
            store, port, new StaticTimeProvider(Now), lifecycle);

        var results = await Task.WhenAll(
            Task.Run(() => CaptureAsync(() => service.ReconcileAsync(WorkspaceId, accepted.Operation.Id))),
            Task.Run(() => CaptureAsync(() => service.ReconcileAsync(WorkspaceId, accepted.Operation.Id))));

        Assert.Equal(2, results.Count(x => x.Result is not null));
        Assert.Equal(3, port.AutoResumeCount);
        Assert.Equal(2, port.ChargeCalls);
        Assert.Single(store.RecoveryRequests);
        Assert.Equal($"auto-resume.{accepted.Operation.Id:N}.3", store.RecoveryRequests.Single().IdempotencyKey);
        Assert.Contains("azure.recovery.auto-resume.accepted", port.Outcomes);
        Assert.Contains("azure.recovery.auto-resume-exhausted", port.Outcomes);
        Assert.Equal(ElsaInstanceOperationState.Queued, Assert.Single(store.Operations).State);
    }

    [Fact]
    public async Task Operator_recover_still_works_after_the_auto_resume_cap()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var observation = new ElsaInstanceProviderObservation(
            ElsaInstanceProviderObservationKind.Confirmed,
            ElsaObservedLifecycle.Provisioning,
            ElsaInstanceProviderHealthGate.Unknown,
            "observation-manual-after-cap",
            OpaqueEvidence(autoResume: false));
        var lifecycle = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var port = new ChargingPort(observation, initialCount: 3);

        var result = await new ElsaInstanceProviderReconciliationService(
                store, port, new StaticTimeProvider(Now), lifecycle)
            .ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.True(result.RetrySafe);
        Assert.Equal(0, port.ChargeCalls);
        Assert.Empty(store.RecoveryRequests);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, Assert.Single(store.Operations).State);

        var recovered = await lifecycle.RecoverAsync(new ElsaInstanceLifecycleRequest(
            WorkspaceId,
            accepted.Instance.Id,
            result.Projection.InstanceVersion,
            "admin-recover-after-cap",
            "admin-recover",
            ActorAccountId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ExpectedOperationId: accepted.Operation.Id));

        Assert.Equal(ElsaInstanceOperationState.Queued, recovered.Operation.State);
        Assert.Equal(3, port.AutoResumeCount);
    }

    [Fact]
    public async Task Operator_visible_reason_is_committed_for_in_progress_recovery()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var observation = new ElsaInstanceProviderObservation(
            ElsaInstanceProviderObservationKind.Confirmed,
            ElsaObservedLifecycle.Provisioning,
            ElsaInstanceProviderHealthGate.Unknown,
            "observation-arm-failed")
        {
            ReasonCode = "azure.deployment.failed"
        };

        var result = await Service(store, new RecordingPort(observation)).ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal("azure.deployment.failed", result.DiagnosticCode);
        Assert.False(result.RetrySafe);
    }

    [Fact]
    public async Task Manual_retry_safe_evidence_does_not_auto_resume()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var observation = new ElsaInstanceProviderObservation(
            ElsaInstanceProviderObservationKind.Unknown,
            ElsaObservedLifecycle.Unknown,
            ElsaInstanceProviderHealthGate.Unknown,
            "observation-manual-retry",
            OpaqueEvidence(autoResume: false));
        var lifecycle = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));

        var result = await new ElsaInstanceProviderReconciliationService(
                store, new RecordingPort(observation), new StaticTimeProvider(Now), lifecycle)
            .ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.True(result.RetrySafe);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, Assert.Single(store.Operations).State);
        Assert.Equal(accepted.Operation.AttemptNumber, Assert.Single(store.Operations).AttemptNumber);
        Assert.Empty(store.RecoveryRequests);
    }

    [Fact]
    public async Task Auto_resume_is_a_no_op_when_lifecycle_is_not_wired()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var observation = new ElsaInstanceProviderObservation(
            ElsaInstanceProviderObservationKind.Confirmed,
            ElsaObservedLifecycle.Provisioning,
            ElsaInstanceProviderHealthGate.Unknown,
            "observation-unwired",
            OpaqueEvidence(autoResume: true));

        var result = await Service(store, new RecordingPort(observation))
            .ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.True(result.RetrySafe);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, Assert.Single(store.Operations).State);
        Assert.Empty(store.RecoveryRequests);
    }

    [Fact]
    public async Task Confirmed_healthy_running_state_converges_deterministically()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var observation = new ElsaInstanceProviderObservation(
            ElsaInstanceProviderObservationKind.Confirmed,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceProviderHealthGate.Passed,
            "observation-healthy");

        var result = await Service(store, new RecordingPort(observation)).ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.Converged, result.Outcome);
        Assert.Equal(ElsaObservedLifecycle.Ready, result.Projection.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Healthy, result.Projection.Health);
        Assert.Equal(ElsaInstanceOperationState.Succeeded, result.Projection.OperationState);
        Assert.Equal(ElsaInstanceProviderReconciliationService.ConvergedCode, result.DiagnosticCode);
        Assert.Equal(Now, result.ReconciledAt);
        Assert.Equal(checked(accepted.Instance.Version + 1), result.Projection.InstanceVersion);
        Assert.Equal(result.Projection.InstanceVersion, store.Instances.Single().Version);
    }

    [Fact]
    public async Task Explicit_missing_deployment_projection_clears_stale_endpoint()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var current = new ElsaCurrentDeploymentReference(
            "deployment-1", endpointUri: "https://managed.example.test");
        _ = await Service(store, new RecordingPort(new(
                ElsaInstanceProviderObservationKind.Confirmed,
                ElsaObservedLifecycle.Ready,
                ElsaInstanceProviderHealthGate.Passed,
                "observation-current",
                retryEvidence: null,
                currentDeploymentReference: current)))
            .ReconcileAsync(WorkspaceId, accepted.Operation.Id);
        var projected = store.Instances.Single();
        Assert.Equal(current, projected.CurrentDeploymentReference);

        var restarted = await new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now.AddMinutes(1)))
            .RestartAsync(new(WorkspaceId, projected.Id, projected.Version, "restart-after-endpoint-removal"));
        store.MarkRecoveryRequired(restarted.Operation.Id);
        _ = await Service(store, new RecordingPort(new(
                ElsaInstanceProviderObservationKind.Confirmed,
                ElsaObservedLifecycle.Ready,
                ElsaInstanceProviderHealthGate.Passed,
                "observation-removed",
                retryEvidence: null,
                currentDeploymentReference: null)))
            .ReconcileAsync(WorkspaceId, restarted.Operation.Id);

        Assert.Null(store.Instances.Single().CurrentDeploymentReference);
    }

    [Fact]
    public async Task A_redeploy_without_managed_handoff_does_not_inherit_studio_grants()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        _ = await Service(store, new RecordingPort(Converged(
                "observation-configured",
                new("deployment-1", "attempt-1", "https://managed.example.test", managedHandoff: true, studioGrantsSupported: true))))
            .ReconcileAsync(WorkspaceId, accepted.Operation.Id);
        var configured = store.Instances.Single();
        Assert.True(configured.CurrentDeploymentReference!.ManagedHandoff);
        Assert.True(configured.CurrentDeploymentReference.StudioGrantsSupported);

        var restarted = await new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now.AddMinutes(1)))
            .RestartAsync(new(WorkspaceId, configured.Id, configured.Version, "redeploy-without-handoff"));
        store.MarkRecoveryRequired(restarted.Operation.Id);
        _ = await Service(store, new RecordingPort(Converged(
                "observation-unconfigured",
                new("deployment-2", "attempt-1", "https://managed.example.test"))))
            .ReconcileAsync(WorkspaceId, restarted.Operation.Id);

        Assert.False(store.Instances.Single().CurrentDeploymentReference!.ManagedHandoff);
        Assert.False(store.Instances.Single().CurrentDeploymentReference!.StudioGrantsSupported);

        static ElsaInstanceProviderObservation Converged(string correlationId, ElsaCurrentDeploymentReference deployment) => new(
            ElsaInstanceProviderObservationKind.Confirmed,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceProviderHealthGate.Passed,
            correlationId,
            retryEvidence: null,
            currentDeploymentReference: deployment);
    }

    [Fact]
    public async Task Read_only_deleted_observation_cannot_tombstone_a_deleting_instance()
    {
        var (store, accepted) = await RecoveryTargetAsync(ElsaDesiredLifecycle.Deleting);
        var observation = new ElsaInstanceProviderObservation(
            ElsaInstanceProviderObservationKind.Confirmed,
            ElsaObservedLifecycle.Deleted,
            ElsaInstanceProviderHealthGate.Unknown,
            "observation-deleted");

        var result = await Service(store, new RecordingPort(observation)).ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(ElsaObservedLifecycle.Unknown, result.Projection.ObservedLifecycle);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, result.Projection.OperationState);
        Assert.Null(store.Instances.Single().DeletedAt);
    }

    [Fact]
    public async Task Confirmed_deleted_observation_releases_a_predecessor_for_a_waiting_delete_without_tombstoning()
    {
        var (store, accepted, authority) = await RecoveryTargetWithAuthorityAsync();
        var confirmationId = Guid.NewGuid();
        var actorAccountId = Guid.NewGuid();
        authority.Add(new ActionConfirmation(
            confirmationId,
            WorkspaceId,
            ConfirmationActionType.DeleteManagedInstance,
            accepted.Instance.Id.ToString("D"),
            actorAccountId,
            Now,
            Now.AddMinutes(5),
            null));
        var deletion = await new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now))
            .DeleteAsync(new(
                WorkspaceId,
                accepted.Instance.Id,
                accepted.Instance.Version,
                "delete-behind-recovery-required-create",
                DeleteConfirmationId: confirmationId,
                ActorAccountId: actorAccountId));
        Assert.Equal(ElsaInstanceOperationState.WaitingForPriorOperation, deletion.Operation.State);

        var observation = new ElsaInstanceProviderObservation(
            ElsaInstanceProviderObservationKind.Confirmed,
            ElsaObservedLifecycle.Deleted,
            ElsaInstanceProviderHealthGate.Unknown,
            "observation-predecessor-absent");

        var result = await Service(store, new RecordingPort(observation))
            .ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.Converged, result.Outcome);
        Assert.Equal(ElsaInstanceOperationState.Succeeded, result.Projection.OperationState);
        Assert.Equal(ElsaObservedLifecycle.Unknown, result.Projection.ObservedLifecycle);
        var current = store.Instances.Single();
        Assert.Equal(ElsaDesiredLifecycle.Deleting, current.DesiredLifecycle);
        Assert.Equal(new ElsaLastOperationId(deletion.Operation.Id), current.LastOperationId);
        Assert.Null(current.DeletedAt);
    }

    [Fact]
    public async Task Later_positive_evidence_can_converge_after_an_unknown_observation()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var port = new QueuePort(
            new(ElsaInstanceProviderObservationKind.Unknown, ElsaObservedLifecycle.Unknown,
                ElsaInstanceProviderHealthGate.Unknown, "observation-unknown"),
            new(ElsaInstanceProviderObservationKind.Confirmed, ElsaObservedLifecycle.Ready,
                ElsaInstanceProviderHealthGate.Passed, "observation-converged"));
        var service = Service(store, port);

        var uncertain = await service.ReconcileAsync(WorkspaceId, accepted.Operation.Id);
        var converged = await service.ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.RecoveryRequired, uncertain.Outcome);
        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.Converged, converged.Outcome);
        Assert.Equal(ElsaObservedLifecycle.Ready, converged.Projection.ObservedLifecycle);
        Assert.Equal(2, port.Calls);
    }

    [Theory]
    [InlineData(ElsaInstanceProviderHealthGate.Failed, ElsaInstanceHealth.Degraded, ElsaInstanceProviderReconciliationService.HealthFailedCode)]
    public async Task Ready_report_without_a_passing_health_gate_never_projects_ready(
        ElsaInstanceProviderHealthGate healthGate,
        ElsaInstanceHealth expectedHealth,
        string expectedCode)
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var observation = new ElsaInstanceProviderObservation(
            ElsaInstanceProviderObservationKind.Confirmed,
            ElsaObservedLifecycle.Ready,
            healthGate,
            "observation-unhealthy");

        var result = await Service(store, new RecordingPort(observation)).ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.HealthGateFailed, result.Outcome);
        Assert.Equal(ElsaObservedLifecycle.Degraded, result.Projection.ObservedLifecycle);
        Assert.NotEqual(ElsaObservedLifecycle.Ready, result.Projection.ObservedLifecycle);
        Assert.Equal(expectedHealth, result.Projection.Health);
        Assert.Equal(ElsaInstanceOperationState.Failed, result.Projection.OperationState);
        Assert.Equal(expectedCode, result.DiagnosticCode);
    }

    [Fact]
    public async Task Unknown_health_gate_stays_in_progress_and_recovery_required()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var observation = new ElsaInstanceProviderObservation(
            ElsaInstanceProviderObservationKind.Confirmed,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceProviderHealthGate.Unknown,
            "observation-health-unknown");

        var result = await Service(store, new RecordingPort(observation)).ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(ElsaObservedLifecycle.Provisioning, result.Projection.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Unknown, result.Projection.Health);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, result.Projection.OperationState);
        Assert.Equal(ElsaInstanceProviderReconciliationService.HealthUnknownCode, result.DiagnosticCode);
    }

    [Fact]
    public async Task Confirmed_provisioning_observation_projects_provisioning_without_completing()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var observation = new ElsaInstanceProviderObservation(
            ElsaInstanceProviderObservationKind.Confirmed,
            ElsaObservedLifecycle.Provisioning,
            ElsaInstanceProviderHealthGate.Unknown,
            "observation-provisioning");

        var first = await Service(store, new RecordingPort(observation)).ReconcileAsync(WorkspaceId, accepted.Operation.Id);
        var refresh = await Service(store, new RecordingPort(observation)).ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.RecoveryRequired, first.Outcome);
        Assert.Equal(ElsaObservedLifecycle.Provisioning, first.Projection.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Unknown, first.Projection.Health);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, first.Projection.OperationState);
        Assert.Equal(ElsaInstanceProviderReconciliationService.InProgressCode, first.DiagnosticCode);
        Assert.Equal(ElsaObservedLifecycle.Provisioning, refresh.Projection.ObservedLifecycle);
        Assert.NotEqual(ElsaObservedLifecycle.Ready, refresh.Projection.ObservedLifecycle);
        Assert.Equal(ElsaObservedLifecycle.Provisioning, store.Instances.Single().ObservedLifecycle);
    }


    [Fact]
    public async Task Provider_failure_is_value_free_and_remains_recovery_required()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var result = await Service(store, new ThrowingPort())
            .ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(ElsaObservedLifecycle.Unknown, result.Projection.ObservedLifecycle);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, result.Projection.OperationState);
        Assert.Equal(ElsaInstanceProviderReconciliationService.UnavailableCode, result.DiagnosticCode);
    }

    [Fact]
    public async Task Completed_reconciliation_replays_without_another_provider_read()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var port = new RecordingPort(new(
            ElsaInstanceProviderObservationKind.Confirmed,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceProviderHealthGate.Passed,
            "observation-replay"));
        var service = Service(store, port);

        var first = await service.ReconcileAsync(WorkspaceId, accepted.Operation.Id);
        var replay = await service.ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(1, port.Calls);
        Assert.Equal(first.Projection, replay.Projection);
    }

    [Fact]
    public async Task Concurrent_conflicting_evidence_fails_closed()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var barrier = new Barrier(2);
        var observations = new Queue<ElsaInstanceProviderObservation>(
        [
            new(ElsaInstanceProviderObservationKind.Confirmed, ElsaObservedLifecycle.Ready,
                ElsaInstanceProviderHealthGate.Passed, "observation-a"),
            new(ElsaInstanceProviderObservationKind.Unknown, ElsaObservedLifecycle.Unknown,
                ElsaInstanceProviderHealthGate.Unknown, "observation-b")
        ]);
        var port = new ConcurrentPort(observations, barrier);
        var service = Service(store, port);

        var results = await Task.WhenAll(
            Task.Run(() => CaptureAsync(() => service.ReconcileAsync(WorkspaceId, accepted.Operation.Id))),
            Task.Run(() => CaptureAsync(() => service.ReconcileAsync(WorkspaceId, accepted.Operation.Id))));

        Assert.Single(results, x => x.Result is not null);
        var conflict = Assert.Single(results, x => x.Error is not null).Error;
        Assert.IsType<ElsaInstanceLifecycleConflictException>(conflict);
        Assert.Contains("evidence conflicts", conflict.Message, StringComparison.Ordinal);
        Assert.Single(store.Operations);
        Assert.Single(store.Instances);
    }

    [Fact]
    public async Task Diagnostics_are_stable_and_do_not_include_provider_values()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        const string providerValue = "subscription-secret-123";
        var observation = new ElsaInstanceProviderObservation(
            ElsaInstanceProviderObservationKind.Ambiguous,
            ElsaObservedLifecycle.Unknown,
            ElsaInstanceProviderHealthGate.Unknown,
            providerValue);

        var result = await Service(store, new RecordingPort(observation)).ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.Equal(ElsaInstanceProviderReconciliationService.AmbiguousCode, result.DiagnosticCode);
        Assert.DoesNotContain(providerValue, result.DiagnosticCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mismatched_provider_correlation_fails_closed()
    {
        var (store, accepted) = await RecoveryTargetAsync();
        var observation = new ElsaInstanceProviderObservation(
            ElsaInstanceProviderObservationKind.Confirmed,
            ElsaObservedLifecycle.Ready,
            ElsaInstanceProviderHealthGate.Passed,
            Guid.NewGuid(),
            accepted.Operation.AttemptNumber,
            "wrong-operation");

        var result = await Service(store, new UncorrelatedPort(observation))
            .ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(ElsaInstanceProviderReconciliationService.CorrelationMismatchCode, result.DiagnosticCode);
        Assert.Equal(ElsaObservedLifecycle.Unknown, result.Projection.ObservedLifecycle);
    }

    private static ElsaInstanceProviderRetryEvidence OpaqueEvidence(bool autoResume)
    {
        var digest = "sha256:" + new string('a', 64);
        return new(
            ElsaInstanceProviderRecoveryObservationReference.Create(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                digest),
            digest,
            autoResume);
    }

    private static ElsaInstanceProviderReconciliationService Service(
        InMemoryElsaInstanceLifecycleStore store,
        IElsaInstanceProviderReconciliationPort port) =>
        new(store, port, new StaticTimeProvider(Now));

    private static async Task<(InMemoryElsaInstanceLifecycleStore Store, ElsaInstanceLifecycleAcceptance Accepted)> RecoveryTargetAsync(
        ElsaDesiredLifecycle desiredLifecycle = ElsaDesiredLifecycle.Running)
    {
        var (store, accepted, _) = await RecoveryTargetWithAuthorityAsync(desiredLifecycle);
        return (store, accepted);
    }

    private static async Task<(
        InMemoryElsaInstanceLifecycleStore Store,
        ElsaInstanceLifecycleAcceptance Accepted,
        InMemoryElsaInstanceDeleteConfirmationAuthority Authority)> RecoveryTargetWithAuthorityAsync(
        ElsaDesiredLifecycle desiredLifecycle = ElsaDesiredLifecycle.Running)
    {
        var authority = new InMemoryElsaInstanceDeleteConfirmationAuthority();
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now), authority);
        var lifecycle = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var accepted = await lifecycle.CreateAsync(new(
            OrganizationId,
            WorkspaceId,
            "reconciliation-test",
            "reconciliation-test",
            new(
                new("commercial", "5.0", "5.0.1"),
                new("server-studio"),
                new("managed", "westeurope", "dedicated", "standard-small", "public", "managed")),
            "create-reconciliation-test"));
        store.MarkRecoveryRequired(accepted.Operation.Id);
        if (desiredLifecycle == ElsaDesiredLifecycle.Deleting)
        {
            var ready = new ElsaInstanceProviderObservation(
                ElsaInstanceProviderObservationKind.Confirmed,
                ElsaObservedLifecycle.Ready,
                ElsaInstanceProviderHealthGate.Passed,
                "observation-ready");
            await Service(store, new RecordingPort(ready)).ReconcileAsync(WorkspaceId, accepted.Operation.Id);
            var instance = store.Instances.Single();
            var confirmationId = Guid.NewGuid();
            var actorAccountId = Guid.NewGuid();
            authority.Add(new ActionConfirmation(
                confirmationId,
                WorkspaceId,
                ConfirmationActionType.DeleteManagedInstance,
                instance.Id.ToString("D"),
                actorAccountId,
                Now,
                Now.AddMinutes(5),
                null));
            accepted = await lifecycle.DeleteAsync(new(
                WorkspaceId,
                instance.Id,
                instance.Version,
                "delete-reconciliation-test",
                DeleteConfirmationId: confirmationId,
                ActorAccountId: actorAccountId));
            store.MarkRecoveryRequired(accepted.Operation.Id);
        }
        return (store, accepted, authority);
    }

    private static async Task<(ElsaInstanceProviderReconciliationResult? Result, Exception? Error)> CaptureAsync(
        Func<Task<ElsaInstanceProviderReconciliationResult>> action)
    {
        try
        {
            return (await action(), null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    private sealed class ChargingPort(
        ElsaInstanceProviderObservation observation,
        int initialCount = 0,
        Barrier? claimBarrier = null) : IElsaInstanceProviderReconciliationPort, IElsaInstanceProviderAutoResumePort
    {
        private readonly object _gate = new();

        public int AutoResumeCount { get; private set; } = initialCount;
        public int ChargeCalls { get; private set; }
        public List<string> Outcomes { get; } = [];

        public Task<ElsaInstanceProviderObservation> ObserveAsync(
            ElsaInstanceProviderReconciliationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(observation.Correlate(request));

        public Task<int?> TryChargeAutoResumeAsync(
            Guid workspaceId,
            Guid instanceId,
            Guid lifecycleOperationId,
            CancellationToken cancellationToken = default)
        {
            claimBarrier?.SignalAndWait(cancellationToken);
            lock (_gate)
            {
                ChargeCalls++;
                if (AutoResumeCount >= 3)
                    return Task.FromResult<int?>(null);
                AutoResumeCount++;
                return Task.FromResult<int?>(AutoResumeCount);
            }
        }

        public Task RecordAutoResumeOutcomeAsync(
            Guid workspaceId,
            Guid instanceId,
            Guid lifecycleOperationId,
            string outcomeCode,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
                Outcomes.Add(outcomeCode);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPort(ElsaInstanceProviderObservation observation) : IElsaInstanceProviderReconciliationPort
    {
        public int Calls { get; private set; }

        public Task<ElsaInstanceProviderObservation> ObserveAsync(
            ElsaInstanceProviderReconciliationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(observation.Correlate(request));
        }
    }

    private sealed class ThrowingPort : IElsaInstanceProviderReconciliationPort
    {
        public Task<ElsaInstanceProviderObservation> ObserveAsync(
            ElsaInstanceProviderReconciliationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("secret-provider-message");
    }

    private sealed class UncorrelatedPort(ElsaInstanceProviderObservation observation)
        : IElsaInstanceProviderReconciliationPort
    {
        public Task<ElsaInstanceProviderObservation> ObserveAsync(
            ElsaInstanceProviderReconciliationRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(observation);
    }

    private sealed class QueuePort(params ElsaInstanceProviderObservation[] observations) : IElsaInstanceProviderReconciliationPort
    {
        private readonly Queue<ElsaInstanceProviderObservation> _observations = new(observations);
        public int Calls { get; private set; }

        public Task<ElsaInstanceProviderObservation> ObserveAsync(
            ElsaInstanceProviderReconciliationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_observations.Dequeue().Correlate(request));
        }
    }

    private sealed class ConcurrentPort(
        Queue<ElsaInstanceProviderObservation> observations,
        Barrier barrier) : IElsaInstanceProviderReconciliationPort
    {
        private readonly object _gate = new();

        public Task<ElsaInstanceProviderObservation> ObserveAsync(
            ElsaInstanceProviderReconciliationRequest request,
            CancellationToken cancellationToken = default)
        {
            ElsaInstanceProviderObservation observation;
            lock (_gate)
                observation = observations.Dequeue();
            barrier.SignalAndWait(cancellationToken);
            return Task.FromResult(observation.Correlate(request));
        }
    }

    private sealed class StaticTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
