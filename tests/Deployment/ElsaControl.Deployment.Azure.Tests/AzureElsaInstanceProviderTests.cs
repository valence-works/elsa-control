using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureElsaInstanceProviderTests
{
    private static readonly Guid TestInstanceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TestOrganizationId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Theory]
    [InlineData("3.8", "3.8.0-preview.5413")]
    [InlineData("3.10", "3.10.4")]
    [InlineData("4.1", "4.1.0")]
    [InlineData("5.0", "5.0.0")]
    public async Task Submission_preserves_arbitrary_admitted_release_lines_and_stable_correlation(
        string releaseLine,
        string version)
    {
        var workspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var instanceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var operationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var plan = Translate(releaseLine, version);
        var service = new CapturingOperationService(CreateOperation(workspaceId, plan, operationId) with { AttemptNumber = 17 });
        var provider = new AzureElsaInstanceProvider(
            service,
            new CapturingOperationStore(),
            new InMemoryAssignmentStore(),
            options: EnabledOptions());
        var request = CreateSubmission(workspaceId, instanceId, operationId, plan);

        var first = await provider.SubmitAsync(request);
        var second = await provider.SubmitAsync(request);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.CorrelationId, second.CorrelationId);
        Assert.Equal("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", first.PlacementAssignmentId);
        Assert.Equal(first.PlacementAssignmentId, second.PlacementAssignmentId);
        Assert.Equal(2, service.Submissions.Count);
        Assert.Equal(service.Submissions[0].IdempotencyKey, service.Submissions[1].IdempotencyKey);
        Assert.Equal($"elsa-instance-operation:{operationId:D}", service.Submissions[0].IdempotencyKey);
        Assert.All(service.Submissions, submission =>
        {
            Assert.Equal(version, submission.Plan.ElsaVersion);
            Assert.Equal(releaseLine, submission.Plan.ReleaseLine);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Healthy_observation_projects_only_safe_provider_neutral_deployment_identity(bool managedHandoff)
    {
        var workspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var instanceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var operationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var plan = Translate("5.0", "5.0.0");
        var operation = CreateOperation(workspaceId, plan, operationId) with
        {
            Status = AzureProviderOperationStatus.Succeeded,
            AttemptNumber = 2,
            Health = AzureProviderHealth.Healthy,
            Endpoint = "https://runtime.example.test/",
            ManagedHandoff = managedHandoff
        };
        var provider = new AzureElsaInstanceProvider(
            new CapturingOperationService(operation),
            new CapturingOperationStore(operation),
            new InMemoryAssignmentStore(),
            options: EnabledOptions());

        var observation = await provider.ObserveAsync(new(
            workspaceId,
            instanceId,
            operationId,
            2,
            ElsaDesiredLifecycle.Running,
            null,
            null));

        Assert.Equal(ElsaInstanceProviderObservationKind.Confirmed, observation.Kind);
        Assert.Equal(ElsaObservedLifecycle.Ready, observation.ObservedLifecycle);
        Assert.Equal(ElsaInstanceProviderHealthGate.Passed, observation.HealthGate);
        Assert.Equal(operation.OperationIdentity, observation.CorrelationId);
        Assert.Equal(operation.OperationIdentity, observation.CurrentDeploymentReference?.DeploymentId);
        Assert.Equal("attempt-2", observation.CurrentDeploymentReference?.RevisionId);
        Assert.Equal("https://runtime.example.test", observation.CurrentDeploymentReference?.EndpointUri);
        Assert.Equal(managedHandoff, observation.CurrentDeploymentReference?.ManagedHandoff);
    }

    [Fact]
    public async Task Observation_rebinds_a_template_only_scope_rotation_and_keeps_the_assignment()
    {
        var workspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var operationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var assignmentId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var plan = TranslateForInstance("5.0", "5.0.0");
        var operation = CreateOperation(workspaceId, plan, operationId) with
        {
            Status = AzureProviderOperationStatus.Succeeded,
            AttemptNumber = 2,
            Health = AzureProviderHealth.Healthy,
            Endpoint = "https://runtime.example.test/",
            OrganizationId = TestOrganizationId,
            InstanceId = TestInstanceId,
            ProviderAssignmentId = assignmentId,
            ProviderScopeFingerprint = new string('a', 64)
        };
        var assignmentStore = new InMemoryAssignmentStore { AssignmentId = assignmentId };
        await assignmentStore.CreateOrGetAsync(
            new(
                workspaceId, TestOrganizationId, TestInstanceId,
                new string('a', 64), "11111111-1111-1111-1111-111111111111",
                "rg-elsa", operation.TargetKey, operation.Location),
            DateTimeOffset.UtcNow);
        var provider = new AzureElsaInstanceProvider(
            new CapturingOperationService(operation),
            new CapturingOperationStore(operation),
            assignmentStore,
            options: EnabledOptions() with { ProviderScopeFingerprint = new string('b', 64) });

        var observation = await provider.ObserveAsync(new(
            workspaceId,
            TestInstanceId,
            operationId,
            2,
            ElsaDesiredLifecycle.Running,
            null,
            null));

        Assert.Equal(ElsaInstanceProviderObservationKind.Confirmed, observation.Kind);
        Assert.Equal(ElsaObservedLifecycle.Ready, observation.ObservedLifecycle);
        Assert.Equal(assignmentId, (await assignmentStore.GetAsync(workspaceId, assignmentId))!.Id);
        Assert.Equal(new string('b', 64), (await assignmentStore.GetAsync(workspaceId, assignmentId))!.ProviderScopeFingerprint);
        var audit = Assert.Single(await assignmentStore.ListRebindsAsync(workspaceId, assignmentId));
        Assert.Equal("lifecycle-observe", audit.TriggeredBy);
        Assert.Equal(new string('a', 64), audit.FromProviderScopeFingerprint);
        Assert.Equal(new string('b', 64), audit.ToProviderScopeFingerprint);
    }

    [Fact]
    public async Task Observation_refuses_rebind_while_an_operation_is_in_flight()
    {
        var workspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var operationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var assignmentStore = new InMemoryAssignmentStore
        {
            BlockingOperationStatus = AzureProviderOperationStatus.Running
        };
        await assignmentStore.CreateOrGetAsync(
            new(
                workspaceId, TestOrganizationId, TestInstanceId,
                new string('a', 64), "11111111-1111-1111-1111-111111111111",
                "rg-elsa", AzureElsaInstanceProvider.WorkloadName(TestInstanceId), "westeurope"),
            DateTimeOffset.UtcNow);
        var provider = new AzureElsaInstanceProvider(
            new CapturingOperationService(null),
            new CapturingOperationStore(),
            assignmentStore,
            options: EnabledOptions() with { ProviderScopeFingerprint = new string('b', 64) });

        var observation = await provider.ObserveAsync(new(
            workspaceId,
            TestInstanceId,
            operationId,
            1,
            ElsaDesiredLifecycle.Running,
            null,
            null));

        Assert.Equal(ElsaInstanceProviderObservationKind.Unknown, observation.Kind);
        Assert.Equal(AzureProviderAssignmentRebindDiagnostics.OperationsInFlight, observation.CorrelationId);
    }

    [Theory]
    [InlineData(AzureProviderHealth.Healthy, null)]
    [InlineData(AzureProviderHealth.Healthy, "https://runtime.example.test/api")]
    [InlineData(AzureProviderHealth.Degraded, null)]
    [InlineData(AzureProviderHealth.Degraded, "https://runtime.example.test/?token=secret")]
    public async Task Succeeded_healthy_or_degraded_operation_with_invalid_endpoint_is_ambiguous_and_value_free(
        AzureProviderHealth health,
        string? endpoint)
    {
        var workspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var operationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var plan = Translate("5.0", "5.0.0");
        var operation = CreateOperation(workspaceId, plan, operationId) with
        {
            Status = AzureProviderOperationStatus.Succeeded,
            Health = health,
            Endpoint = endpoint
        };
        var provider = new AzureElsaInstanceProvider(
            new CapturingOperationService(operation),
            new CapturingOperationStore(operation),
            new InMemoryAssignmentStore(),
            options: EnabledOptions());

        var request = new ElsaInstanceProviderReconciliationRequest(
            workspaceId,
            TestInstanceId,
            operationId,
            1,
            ElsaDesiredLifecycle.Running,
            null,
            null);
        var first = await provider.ObserveAsync(request);
        var second = await provider.ObserveAsync(request);

        Assert.Equal(ElsaInstanceProviderObservationKind.Ambiguous, first.Kind);
        Assert.Equal(ElsaObservedLifecycle.Unknown, first.ObservedLifecycle);
        Assert.Equal(ElsaInstanceProviderHealthGate.Unknown, first.HealthGate);
        Assert.Equal("provider-operation-endpoint-invalid", first.CorrelationId);
        Assert.Equal(first.CorrelationId, second.CorrelationId);
        Assert.DoesNotContain("secret", first.CorrelationId, StringComparison.OrdinalIgnoreCase);
        Assert.Null(first.CurrentDeploymentReference);
        Assert.False(first.HasCurrentDeploymentProjection);
    }

    [Theory]
    [InlineData(AzureProviderOperationStatus.Succeeded, AzureProviderHealth.Degraded, ElsaObservedLifecycle.Ready, ElsaInstanceProviderHealthGate.Failed)]
    [InlineData(AzureProviderOperationStatus.Succeeded, AzureProviderHealth.Unreachable, ElsaObservedLifecycle.Ready, ElsaInstanceProviderHealthGate.Unknown)]
    [InlineData(AzureProviderOperationStatus.Succeeded, AzureProviderHealth.Failed, ElsaObservedLifecycle.Failed, ElsaInstanceProviderHealthGate.Failed)]
    [InlineData(AzureProviderOperationStatus.Failed, AzureProviderHealth.Failed, ElsaObservedLifecycle.Failed, ElsaInstanceProviderHealthGate.Failed)]
    [InlineData(AzureProviderOperationStatus.Cancelled, AzureProviderHealth.Unknown, ElsaObservedLifecycle.Failed, ElsaInstanceProviderHealthGate.Failed)]
    [InlineData(AzureProviderOperationStatus.Running, AzureProviderHealth.Unknown, ElsaObservedLifecycle.Provisioning, ElsaInstanceProviderHealthGate.Unknown)]
    [InlineData(AzureProviderOperationStatus.RecoveryRequired, AzureProviderHealth.Unknown, ElsaObservedLifecycle.Provisioning, ElsaInstanceProviderHealthGate.Unknown)]
    public async Task Provider_statuses_project_to_safe_observed_lifecycle_and_health(
        AzureProviderOperationStatus status,
        AzureProviderHealth health,
        ElsaObservedLifecycle expectedLifecycle,
        ElsaInstanceProviderHealthGate expectedHealth)
    {
        var workspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var instanceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var operationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var plan = Translate("5.0", "5.0.0");
        var operation = CreateOperation(workspaceId, plan, operationId) with
        {
            Status = status,
            Health = health,
            Endpoint = status == AzureProviderOperationStatus.Succeeded &&
                       health is AzureProviderHealth.Healthy or AzureProviderHealth.Degraded
                ? "https://runtime.example.test/"
                : null
        };
        var provider = new AzureElsaInstanceProvider(
            new CapturingOperationService(operation),
            new CapturingOperationStore(operation),
            new InMemoryAssignmentStore(),
            options: EnabledOptions());

        var observation = await provider.ObserveAsync(new(
            workspaceId,
            instanceId,
            operationId,
            1,
            ElsaDesiredLifecycle.Running,
            null,
            null));

        Assert.Equal(ElsaInstanceProviderObservationKind.Confirmed, observation.Kind);
        Assert.Equal(expectedLifecycle, observation.ObservedLifecycle);
        Assert.Equal(expectedHealth, observation.HealthGate);
        if (status == AzureProviderOperationStatus.Succeeded && expectedLifecycle == ElsaObservedLifecycle.Ready && health != AzureProviderHealth.Unreachable)
            Assert.NotNull(observation.CurrentDeploymentReference);
        else
            Assert.Null(observation.CurrentDeploymentReference);
    }

    [Theory]
    [InlineData(AzureProviderRunnerStep.Workload, AzureProviderOperationPhase.FoundationReady)]
    [InlineData(AzureProviderRunnerStep.Foundation, AzureProviderOperationPhase.FoundationSubmitted)]
    public async Task Recovery_required_confirmed_completed_step_records_auto_resume_evidence(
        AzureProviderRunnerStep completedStep,
        AzureProviderOperationPhase phase)
    {
        var fixture = await CreateObserveFixtureAsync(
            ConfirmedObservation(completedStep),
            attemptedStep: completedStep,
            phase: phase);

        var observation = await fixture.Provider.ObserveAsync(fixture.Request);

        Assert.Equal(ElsaInstanceProviderObservationKind.Confirmed, observation.Kind);
        Assert.Equal(ElsaObservedLifecycle.Provisioning, observation.ObservedLifecycle);
        Assert.NotNull(observation.RetryEvidence);
        Assert.True(observation.RetryEvidence.AutoResume);
        Assert.Equal(0, observation.RetryEvidence.ObservedAutoResumeCount);
        Assert.Equal(1, fixture.Observer.Calls);
        Assert.Equal(1, fixture.ObservationStore.CreateCalls);
        Assert.Equal(1, fixture.OperationStore.ArmClockCalls);
        Assert.Equal(0, fixture.OperationStore.AutoResumeIncrements);
        Assert.Equal(0, fixture.OperationStore.Current!.AutoResumeCount);
        Assert.Equal(AzureNamedDeploymentFreshness.MinimumArmIntervalSeconds,
            fixture.OperationStore.Current.ArmObservationBackoffSeconds);
    }

    [Fact]
    public async Task Auto_resume_charge_is_atomic_and_records_the_outcome()
    {
        var fixture = await CreateObserveFixtureAsync(
            ConfirmedObservation(AzureProviderRunnerStep.Workload),
            attemptedStep: AzureProviderRunnerStep.Workload,
            phase: AzureProviderOperationPhase.FoundationReady);

        Assert.Equal(1, await fixture.Provider.TryChargeAutoResumeAsync(
            fixture.Request.WorkspaceId, fixture.Request.InstanceId, fixture.Request.OperationId, 0));
        Assert.Equal(2, await fixture.Provider.TryChargeAutoResumeAsync(
            fixture.Request.WorkspaceId, fixture.Request.InstanceId, fixture.Request.OperationId, 1));
        Assert.Equal(3, await fixture.Provider.TryChargeAutoResumeAsync(
            fixture.Request.WorkspaceId, fixture.Request.InstanceId, fixture.Request.OperationId, 2));
        Assert.Null(await fixture.Provider.TryChargeAutoResumeAsync(
            fixture.Request.WorkspaceId, fixture.Request.InstanceId, fixture.Request.OperationId, 3));
        Assert.Equal(3, await fixture.Provider.GetAutoResumeCountAsync(
            fixture.Request.WorkspaceId, fixture.Request.InstanceId, fixture.Request.OperationId));
        Assert.Equal(AzureNamedDeploymentFreshness.MaximumAutoResumes, fixture.OperationStore.Current!.AutoResumeCount);

        await fixture.Provider.RecordAutoResumeOutcomeAsync(
            fixture.Request.WorkspaceId, fixture.Request.InstanceId, fixture.Request.OperationId,
            AzureLateSuccessCodes.AutoResumeExhausted);
        Assert.Equal(AzureLateSuccessCodes.AutoResumeExhausted, fixture.OperationStore.Current.LastObservationReasonCode);
        Assert.Contains(AzureLateSuccessCodes.AutoResumeExhausted, fixture.OperationStore.AutoResumeOutcomes);
    }

    [Fact]
    public async Task Recovery_required_running_arm_deployment_writes_no_observation_row()
    {
        var fixture = await CreateObserveFixtureAsync(
            new AzureProviderRecoveryObservation(
                AzureProviderRecoveryObservationKind.InProgress,
                null,
                new(),
                AzureProviderHealth.Unknown,
                null,
                "azure.recovery.workload-in-progress",
                "The retained Azure workload deployment is still running."),
            attemptedStep: AzureProviderRunnerStep.Workload,
            phase: AzureProviderOperationPhase.FoundationReady);

        var observation = await fixture.Provider.ObserveAsync(fixture.Request);

        Assert.Equal(ElsaInstanceProviderObservationKind.Confirmed, observation.Kind);
        Assert.Equal(ElsaObservedLifecycle.Provisioning, observation.ObservedLifecycle);
        Assert.Null(observation.RetryEvidence);
        Assert.Equal(1, fixture.Observer.Calls);
        Assert.Equal(0, fixture.ObservationStore.CreateCalls);
        Assert.Equal(1, fixture.OperationStore.ArmClockCalls);
        Assert.Equal(0, fixture.OperationStore.AutoResumeIncrements);
        Assert.Equal(AzureNamedDeploymentFreshness.MinimumArmIntervalSeconds,
            fixture.OperationStore.Current!.ArmObservationBackoffSeconds);
    }

    [Fact]
    public async Task Recovery_required_rate_limits_arm_reads_until_backoff_elapses()
    {
        var now = DateTimeOffset.Parse("2026-09-24T00:48:18Z");
        var fixture = await CreateObserveFixtureAsync(
            ConfirmedObservation(AzureProviderRunnerStep.Workload),
            attemptedStep: AzureProviderRunnerStep.Workload,
            phase: AzureProviderOperationPhase.FoundationReady,
            lastArmObservedAt: now.AddSeconds(-30),
            backoffSeconds: 60,
            now: now);

        var observation = await fixture.Provider.ObserveAsync(fixture.Request);

        Assert.Null(observation.RetryEvidence);
        Assert.Equal(ManagedElsaReasonCodeCatalog.AzureRecoveryObservationUnavailable, observation.ReasonCode);
        Assert.Equal(0, fixture.Observer.Calls);
        Assert.Equal(0, fixture.ObservationStore.CreateCalls);
        Assert.Equal(0, fixture.OperationStore.ArmClockCalls);
        Assert.Equal(0, fixture.OperationStore.AutoResumeIncrements);
    }

    [Fact]
    public async Task Recovery_required_without_evidence_emits_a_temporary_observation_reason()
    {
        var fixture = await CreateObserveFixtureAsync(
            ConfirmedObservation(AzureProviderRunnerStep.Workload),
            attemptedStep: AzureProviderRunnerStep.Workload,
            phase: AzureProviderOperationPhase.FoundationReady,
            lastArmObservedAt: DateTimeOffset.Parse("2026-09-24T00:48:18Z"),
            backoffSeconds: 60,
            now: DateTimeOffset.Parse("2026-09-24T00:48:18Z"));
        fixture.OperationStore.Current = fixture.OperationStore.Current! with
        {
            LastObservationReasonCode = null
        };

        var observation = await fixture.Provider.ObserveAsync(fixture.Request);

        Assert.Null(observation.RetryEvidence);
        Assert.Equal(ManagedElsaReasonCodeCatalog.AzureRecoveryObservationUnavailable, observation.ReasonCode);
        Assert.Equal(ManagedElsaReasonClass.Temporary,
            ManagedElsaReasonCodeCatalog.Classify(observation.ReasonCode));
    }

    [Fact]
    public async Task Recovery_required_reuses_last_evidence_when_the_arm_read_is_not_due()
    {
        var now = DateTimeOffset.Parse("2026-09-24T00:48:18Z");
        var fixture = await CreateObserveFixtureAsync(
            ConfirmedObservation(AzureProviderRunnerStep.Workload),
            attemptedStep: AzureProviderRunnerStep.Workload,
            phase: AzureProviderOperationPhase.FoundationReady,
            now: now);

        var first = await fixture.Provider.ObserveAsync(fixture.Request);
        Assert.NotNull(first.RetryEvidence);
        Assert.True(first.RetryEvidence.AutoResume);
        Assert.Equal(1, fixture.Observer.Calls);

        fixture.OperationStore.Current = fixture.OperationStore.Current! with
        {
            LastArmObservedAt = now,
            ArmObservationBackoffSeconds = 60,
            LastObservationReasonCode = "azure.recovery.workload-observed"
        };

        var second = await fixture.Provider.ObserveAsync(fixture.Request);

        Assert.NotNull(second.RetryEvidence);
        Assert.Equal(first.RetryEvidence.Reference, second.RetryEvidence.Reference);
        Assert.Equal(first.RetryEvidence.Digest, second.RetryEvidence.Digest);
        Assert.False(second.RetryEvidence.AutoResume);
        Assert.Equal("azure.recovery.workload-observed", second.ReasonCode);
        Assert.Equal(1, fixture.Observer.Calls);
        Assert.Equal(1, fixture.ObservationStore.CreateCalls);
        Assert.Equal(0, fixture.OperationStore.AutoResumeIncrements);
    }

    [Fact]
    public async Task Recovery_required_auto_resume_stops_at_the_cap()
    {
        var fixture = await CreateObserveFixtureAsync(
            ConfirmedObservation(AzureProviderRunnerStep.Workload),
            attemptedStep: AzureProviderRunnerStep.Workload,
            phase: AzureProviderOperationPhase.FoundationReady,
            autoResumeCount: AzureNamedDeploymentFreshness.MaximumAutoResumes);

        var observation = await fixture.Provider.ObserveAsync(fixture.Request);

        Assert.Null(observation.RetryEvidence);
        Assert.Equal(AzureLateSuccessCodes.AutoResumeExhausted, observation.ReasonCode);
        Assert.Equal(AzureLateSuccessCodes.AutoResumeExhausted, fixture.OperationStore.Current!.LastObservationReasonCode);
        Assert.Contains(AzureLateSuccessCodes.AutoResumeExhausted, fixture.OperationStore.AutoResumeOutcomes);
        Assert.Equal(0, fixture.Observer.Calls);
        Assert.Equal(0, fixture.ObservationStore.CreateCalls);
        Assert.Equal(0, fixture.OperationStore.ArmClockCalls);
        Assert.Equal(0, fixture.OperationStore.AutoResumeIncrements);
        Assert.Equal(AzureNamedDeploymentFreshness.MaximumAutoResumes, fixture.OperationStore.Current.AutoResumeCount);
    }

    [Fact]
    public async Task Operator_observe_at_the_cap_still_reads_arm_and_mints_evidence()
    {
        var fixture = await CreateObserveFixtureAsync(
            ConfirmedObservation(AzureProviderRunnerStep.Workload),
            attemptedStep: AzureProviderRunnerStep.Workload,
            phase: AzureProviderOperationPhase.FoundationReady,
            autoResumeCount: AzureNamedDeploymentFreshness.MaximumAutoResumes);

        var observation = await fixture.Provider.ObserveAsync(
            fixture.Request with { OperatorInitiated = true });

        Assert.NotNull(observation.RetryEvidence);
        Assert.False(observation.RetryEvidence.AutoResume);
        Assert.Equal(AzureLateSuccessCodes.AutoResumeExhausted, observation.ReasonCode);
        Assert.Equal(1, fixture.Observer.Calls);
        Assert.Equal(1, fixture.ObservationStore.CreateCalls);
        Assert.Equal(AzureNamedDeploymentFreshness.MaximumAutoResumes, fixture.OperationStore.Current!.AutoResumeCount);
    }

    [Fact]
    public async Task Operator_forced_reads_reuse_a_current_receipt_within_sixty_seconds()
    {
        var now = DateTimeOffset.Parse("2026-09-24T00:48:18Z");
        var fixture = await CreateObserveFixtureAsync(
            ConfirmedObservation(AzureProviderRunnerStep.Workload),
            attemptedStep: AzureProviderRunnerStep.Workload,
            phase: AzureProviderOperationPhase.FoundationReady,
            autoResumeCount: AzureNamedDeploymentFreshness.MaximumAutoResumes,
            now: now);

        var first = await fixture.Provider.ObserveAsync(
            fixture.Request with { OperatorInitiated = true });
        var second = await fixture.Provider.ObserveAsync(
            fixture.Request with { OperatorInitiated = true });

        Assert.NotNull(first.RetryEvidence);
        Assert.NotNull(second.RetryEvidence);
        Assert.Equal(first.RetryEvidence!.Reference, second.RetryEvidence!.Reference);
        Assert.Equal(first.RetryEvidence.Digest, second.RetryEvidence.Digest);
        Assert.False(second.RetryEvidence.AutoResume);
        Assert.Equal(1, fixture.Observer.Calls);
        Assert.Equal(1, fixture.ObservationStore.CreateCalls);
        Assert.Equal(1, fixture.OperationStore.ArmClockCalls);
    }

    [Fact]
    public async Task Operator_forced_read_after_sixty_seconds_reads_arm_again()
    {
        var now = DateTimeOffset.Parse("2026-09-24T00:48:18Z");
        var fixture = await CreateObserveFixtureAsync(
            ConfirmedObservation(AzureProviderRunnerStep.Workload),
            attemptedStep: AzureProviderRunnerStep.Workload,
            phase: AzureProviderOperationPhase.FoundationReady,
            now: now);

        var first = await fixture.Provider.ObserveAsync(
            fixture.Request with { OperatorInitiated = true });
        Assert.Equal(1, fixture.Observer.Calls);

        fixture.OperationStore.Current = fixture.OperationStore.Current! with
        {
            LastArmObservedAt = now.AddSeconds(-AzureNamedDeploymentFreshness.MinimumArmIntervalSeconds)
        };
        var second = await fixture.Provider.ObserveAsync(
            fixture.Request with { OperatorInitiated = true });

        Assert.NotNull(first.RetryEvidence);
        Assert.NotNull(second.RetryEvidence);
        Assert.Equal(2, fixture.Observer.Calls);
        Assert.Equal(2, fixture.OperationStore.ArmClockCalls);
    }

    [Fact]
    public async Task Automatic_observe_stays_on_the_rate_limit_when_the_operator_floor_has_elapsed()
    {
        var now = DateTimeOffset.Parse("2026-09-24T00:48:18Z");
        var fixture = await CreateObserveFixtureAsync(
            ConfirmedObservation(AzureProviderRunnerStep.Workload),
            attemptedStep: AzureProviderRunnerStep.Workload,
            phase: AzureProviderOperationPhase.FoundationReady,
            now: now);

        var first = await fixture.Provider.ObserveAsync(fixture.Request);
        Assert.Equal(1, fixture.Observer.Calls);
        Assert.NotNull(first.RetryEvidence);

        fixture.OperationStore.Current = fixture.OperationStore.Current! with
        {
            LastArmObservedAt = now.AddSeconds(-90),
            ArmObservationBackoffSeconds = AzureNamedDeploymentFreshness.MaximumArmIntervalSeconds
        };
        var automatic = await fixture.Provider.ObserveAsync(fixture.Request);
        var forced = await fixture.Provider.ObserveAsync(
            fixture.Request with { OperatorInitiated = true });

        Assert.NotNull(automatic.RetryEvidence);
        Assert.Equal(first.RetryEvidence!.Reference, automatic.RetryEvidence!.Reference);
        Assert.NotNull(forced.RetryEvidence);
        Assert.Equal(2, fixture.Observer.Calls);
        Assert.Equal(2, fixture.OperationStore.ArmClockCalls);
    }

    [Fact]
    public async Task Recovery_required_stale_receipt_is_reminted_for_the_current_instance_version()
    {
        var now = DateTimeOffset.Parse("2026-09-24T00:48:18Z");
        var fixture = await CreateObserveFixtureAsync(
            ConfirmedObservation(AzureProviderRunnerStep.Workload),
            attemptedStep: AzureProviderRunnerStep.Workload,
            phase: AzureProviderOperationPhase.FoundationReady,
            now: now);

        var first = await fixture.Provider.ObserveAsync(fixture.Request);
        Assert.NotNull(first.RetryEvidence);
        Assert.Equal(1, fixture.ObservationStore.CreateCalls);

        fixture.OperationStore.Current = fixture.OperationStore.Current! with
        {
            LastArmObservedAt = now.AddSeconds(-120),
            ArmObservationBackoffSeconds = AzureNamedDeploymentFreshness.MinimumArmIntervalSeconds
        };
        var reminted = await fixture.Provider.ObserveAsync(
            fixture.Request with { InstanceVersion = fixture.Request.InstanceVersion + 2 });

        Assert.NotNull(reminted.RetryEvidence);
        Assert.Equal(2, fixture.Observer.Calls);
        Assert.Equal(2, fixture.ObservationStore.CreateCalls);
        Assert.Equal(fixture.Request.InstanceVersion + 2, fixture.ObservationStore.LastReceipt!.Observation.ObservedInstanceVersion);
    }

    [Fact]
    public async Task Recovery_required_identical_arm_reads_reuse_the_receipt_without_a_new_row()
    {
        var now = DateTimeOffset.Parse("2026-09-24T00:48:18Z");
        var fixture = await CreateObserveFixtureAsync(
            ConfirmedObservation(AzureProviderRunnerStep.Workload),
            attemptedStep: AzureProviderRunnerStep.Workload,
            phase: AzureProviderOperationPhase.FoundationReady,
            now: now);

        var first = await fixture.Provider.ObserveAsync(fixture.Request);
        Assert.NotNull(first.RetryEvidence);
        Assert.Equal(1, fixture.ObservationStore.CreateCalls);

        fixture.OperationStore.Current = fixture.OperationStore.Current! with
        {
            LastArmObservedAt = now.AddSeconds(-120),
            ArmObservationBackoffSeconds = AzureNamedDeploymentFreshness.MinimumArmIntervalSeconds
        };
        var second = await fixture.Provider.ObserveAsync(
            fixture.Request with { InstanceVersion = fixture.Request.InstanceVersion + 1 });

        Assert.NotNull(second.RetryEvidence);
        Assert.Equal(first.RetryEvidence!.Reference, second.RetryEvidence.Reference);
        Assert.Equal(first.RetryEvidence.Digest, second.RetryEvidence.Digest);
        Assert.True(second.RetryEvidence.AutoResume);
        Assert.Equal(2, fixture.Observer.Calls);
        Assert.Equal(1, fixture.ObservationStore.CreateCalls);
        Assert.Equal(2, fixture.OperationStore.ArmClockCalls);
    }

    [Fact]
    public async Task Recovery_required_arm_failed_is_operator_visible_without_retry_evidence()
    {
        var fixture = await CreateObserveFixtureAsync(
            new AzureProviderRecoveryObservation(
                AzureProviderRecoveryObservationKind.Ambiguous,
                null,
                new(),
                AzureProviderHealth.Unknown,
                null,
                AzureLateSuccessCodes.DeploymentFailed,
                "Azure reported the workload deployment as failed or canceled."),
            attemptedStep: AzureProviderRunnerStep.Workload,
            phase: AzureProviderOperationPhase.FoundationReady);

        var observation = await fixture.Provider.ObserveAsync(fixture.Request);

        Assert.Null(observation.RetryEvidence);
        Assert.Equal(AzureLateSuccessCodes.DeploymentFailed, observation.ReasonCode);
        Assert.Equal(AzureLateSuccessCodes.DeploymentFailed, fixture.OperationStore.Current!.LastObservationReasonCode);
        Assert.Equal(0, fixture.ObservationStore.CreateCalls);
        Assert.Equal(AzureNamedDeploymentFreshness.MinimumArmIntervalSeconds,
            fixture.OperationStore.Current.ArmObservationBackoffSeconds);
    }

    [Fact]
    public async Task Transient_foundation_failure_records_retry_evidence_without_auto_resume_on_first_observe()
    {
        var fixture = await CreateObserveFixtureAsync(
            FailedObservation(AzureProviderRunnerStep.Foundation, AzureTransientArmFailure.ManagedEnvironmentProvisioningErrorCode),
            attemptedStep: AzureProviderRunnerStep.Foundation,
            phase: AzureProviderOperationPhase.Planned);

        var observation = await fixture.Provider.ObserveAsync(fixture.Request);

        Assert.NotNull(observation.RetryEvidence);
        Assert.False(observation.RetryEvidence.AutoResume);
        Assert.Equal(AzureLateSuccessCodes.Retrying, observation.ReasonCode);
        Assert.Equal(1, fixture.ObservationStore.CreateCalls);
        Assert.Equal(AzureNamedDeploymentFreshness.MinimumArmIntervalSeconds,
            fixture.OperationStore.Current!.ArmObservationBackoffSeconds);
    }

    [Fact]
    public async Task Transient_foundation_failure_auto_resumes_after_the_failure_has_been_seen()
    {
        var now = DateTimeOffset.Parse("2026-10-05T00:04:00Z");
        var fixture = await CreateObserveFixtureAsync(
            FailedObservation(AzureProviderRunnerStep.Foundation, AzureTransientArmFailure.ManagedEnvironmentProvisioningErrorCode),
            attemptedStep: AzureProviderRunnerStep.Foundation,
            phase: AzureProviderOperationPhase.Planned,
            lastArmObservedAt: now.AddSeconds(-120),
            backoffSeconds: AzureNamedDeploymentFreshness.MinimumArmIntervalSeconds,
            now: now);
        fixture.OperationStore.Current = fixture.OperationStore.Current! with
        {
            LastObservationReasonCode = AzureLateSuccessCodes.Retrying
        };

        var observation = await fixture.Provider.ObserveAsync(fixture.Request);

        Assert.NotNull(observation.RetryEvidence);
        Assert.True(observation.RetryEvidence.AutoResume);
        Assert.Equal(AzureLateSuccessCodes.Retrying, observation.ReasonCode);
        Assert.Equal(
            AzureNamedDeploymentFreshness.MinimumArmIntervalSeconds,
            fixture.OperationStore.Current!.ArmObservationBackoffSeconds);
    }

    [Fact]
    public async Task Transient_foundation_failure_backoff_grows_from_the_persisted_auto_resume_count()
    {
        var now = DateTimeOffset.Parse("2026-10-05T00:04:00Z");
        var fixture = await CreateObserveFixtureAsync(
            FailedObservation(AzureProviderRunnerStep.Foundation, AzureTransientArmFailure.ManagedEnvironmentProvisioningErrorCode),
            attemptedStep: AzureProviderRunnerStep.Foundation,
            phase: AzureProviderOperationPhase.Planned,
            lastArmObservedAt: now.AddSeconds(-300),
            backoffSeconds: AzureNamedDeploymentFreshness.MinimumArmIntervalSeconds,
            autoResumeCount: 2,
            now: now);
        fixture.OperationStore.Current = fixture.OperationStore.Current! with
        {
            LastObservationReasonCode = AzureLateSuccessCodes.Retrying
        };

        var observation = await fixture.Provider.ObserveAsync(fixture.Request);

        Assert.NotNull(observation.RetryEvidence);
        Assert.True(observation.RetryEvidence.AutoResume);
        Assert.Equal(240, fixture.OperationStore.Current!.ArmObservationBackoffSeconds);
    }

    [Fact]
    public async Task Transient_foundation_failure_at_the_auto_resume_cap_is_exhausted_and_recoverable()
    {
        var now = DateTimeOffset.Parse("2026-10-05T00:04:00Z");
        var fixture = await CreateObserveFixtureAsync(
            FailedObservation(AzureProviderRunnerStep.Foundation, AzureTransientArmFailure.ManagedEnvironmentProvisioningErrorCode),
            attemptedStep: AzureProviderRunnerStep.Foundation,
            phase: AzureProviderOperationPhase.Planned,
            now: now);

        var first = await fixture.Provider.ObserveAsync(fixture.Request);
        Assert.NotNull(first.RetryEvidence);
        fixture.OperationStore.Current = fixture.OperationStore.Current! with
        {
            AutoResumeCount = AzureNamedDeploymentFreshness.MaximumAutoResumes,
            LastArmObservedAt = now.AddSeconds(-120),
            ArmObservationBackoffSeconds = AzureNamedDeploymentFreshness.MinimumArmIntervalSeconds,
            LastObservationReasonCode = AzureLateSuccessCodes.Retrying
        };

        var observation = await fixture.Provider.ObserveAsync(fixture.Request);

        Assert.NotNull(observation.RetryEvidence);
        Assert.False(observation.RetryEvidence.AutoResume);
        Assert.Equal(AzureLateSuccessCodes.AutoResumeExhausted, observation.ReasonCode);
        Assert.Equal(AzureNamedDeploymentFreshness.MaximumAutoResumes, fixture.OperationStore.Current!.AutoResumeCount);
    }

    [Fact]
    public async Task Non_transient_foundation_failure_records_needs_operator_and_retry_evidence()
    {
        var fixture = await CreateObserveFixtureAsync(
            FailedObservation(AzureProviderRunnerStep.Foundation),
            attemptedStep: AzureProviderRunnerStep.Foundation,
            phase: AzureProviderOperationPhase.Planned);

        var observation = await fixture.Provider.ObserveAsync(fixture.Request);

        Assert.NotNull(observation.RetryEvidence);
        Assert.False(observation.RetryEvidence.AutoResume);
        Assert.Equal(AzureLateSuccessCodes.NeedsOperator, observation.ReasonCode);
    }

    [Fact]
    public async Task Recovery_required_retry_safe_mutation_replay_stays_manual()
    {
        var fixture = await CreateObserveFixtureAsync(
            ConfirmedObservation(AzureProviderRunnerStep.AcrPull),
            attemptedStep: AzureProviderRunnerStep.SeedSecrets,
            phase: AzureProviderOperationPhase.AcrPullObserved);

        var observation = await fixture.Provider.ObserveAsync(fixture.Request);

        Assert.NotNull(observation.RetryEvidence);
        Assert.False(observation.RetryEvidence.AutoResume);
        Assert.Equal(1, fixture.ObservationStore.CreateCalls);
        Assert.Equal(0, fixture.OperationStore.AutoResumeIncrements);
    }

    [Theory]
    [InlineData(AzureProviderOperationStatus.Running)]
    [InlineData(AzureProviderOperationStatus.Succeeded)]
    public async Task Recovery_replay_missing_accepted_proof_cannot_project_postclaim_status(
        AzureProviderOperationStatus status)
    {
        var fixture = await CreateRecoveryFixtureAsync(status);

        var result = await fixture.Provider.RecoverAsync(fixture.Request);

        Assert.Equal(ElsaInstanceProviderRecoveryOutcome.Rejected, result.Outcome);
        Assert.Equal(1, fixture.ObservationStore.ReplayCalls);
        Assert.Equal(0, fixture.ObservationStore.StrictCalls);
        Assert.Equal(0, fixture.Observer.Calls);
        Assert.Equal(0, fixture.OperationStore.ClaimRecoveryCalls);
    }

    [Theory]
    [InlineData(AzureProviderOperationStatus.Running)]
    [InlineData(AzureProviderOperationStatus.Succeeded)]
    public async Task Recovery_replay_wrong_accepted_proof_cannot_project_postclaim_status(
        AzureProviderOperationStatus status)
    {
        var fixture = await CreateRecoveryFixtureAsync(status, includeReplayObservation: true, wrongReplayObservation: true);

        var result = await fixture.Provider.RecoverAsync(fixture.Request);

        Assert.Equal(ElsaInstanceProviderRecoveryOutcome.Rejected, result.Outcome);
        Assert.Equal(1, fixture.ObservationStore.ReplayCalls);
        Assert.Equal(0, fixture.ObservationStore.StrictCalls);
        Assert.Equal(0, fixture.Observer.Calls);
        Assert.Equal(0, fixture.OperationStore.ClaimRecoveryCalls);
    }

    [Theory]
    [InlineData(AzureProviderOperationStatus.Running, ElsaInstanceProviderRecoveryOutcome.InProgress, "azure.operation.in-progress")]
    [InlineData(AzureProviderOperationStatus.Succeeded, ElsaInstanceProviderRecoveryOutcome.Succeeded, "azure.operation.no-op")]
    public async Task Recovery_replay_valid_successor_projects_without_remote_observation(
        AzureProviderOperationStatus status,
        ElsaInstanceProviderRecoveryOutcome expectedOutcome,
        string expectedCode)
    {
        var fixture = await CreateRecoveryFixtureAsync(status, includeReplayObservation: true);

        var result = await fixture.Provider.RecoverAsync(fixture.Request);

        Assert.True(expectedOutcome == result.Outcome, $"Expected {expectedOutcome}; got {result.Outcome}: {result.Code}");
        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(1, fixture.ObservationStore.ReplayCalls);
        Assert.Equal(0, fixture.ObservationStore.StrictCalls);
        Assert.Equal(0, fixture.Observer.Calls);
        Assert.Equal(0, fixture.OperationStore.ClaimRecoveryCalls);
    }

    [Fact]
    public async Task Recovery_required_uses_strict_ledger_validation_not_postclaim_replay()
    {
        var fixture = await CreateRecoveryFixtureAsync(
            AzureProviderOperationStatus.RecoveryRequired,
            includeStrictObservation: true);

        var result = await fixture.Provider.RecoverAsync(fixture.Request);

        Assert.Equal(ElsaInstanceProviderRecoveryOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal("azure.recovery.unavailable", result.Code);
        Assert.Equal(0, fixture.ObservationStore.ReplayCalls);
        Assert.Equal(1, fixture.ObservationStore.StrictCalls);
        Assert.Equal(0, fixture.Observer.Calls);
        Assert.Equal(0, fixture.OperationStore.ClaimRecoveryCalls);
    }

    [Fact]
    public async Task Recovery_required_survives_a_template_only_scope_rotation()
    {
        var fixture = await CreateRecoveryFixtureAsync(
            AzureProviderOperationStatus.RecoveryRequired,
            includeStrictObservation: true);
        var rotated = new AzureElsaInstanceProvider(
            new CapturingOperationService(null),
            fixture.OperationStore,
            fixture.AssignmentStore,
            options: EnabledOptions() with { ProviderScopeFingerprint = new string('b', 64) },
            recoveryObserver: fixture.Observer,
            recoveryObservationStore: fixture.ObservationStore);

        var result = await rotated.RecoverAsync(fixture.Request);

        Assert.NotEqual("azure.recovery.operation-unavailable", result.Code);
        Assert.NotEqual("azure.recovery.identity-mismatch", result.Code);
        Assert.NotEqual("azure.recovery.assignment-mismatch", result.Code);
        Assert.Equal("azure.recovery.unavailable", result.Code);
        Assert.Equal(new string('b', 64), (await fixture.AssignmentStore.GetAsync(
            fixture.Request.Submission.WorkspaceId,
            Guid.Parse(fixture.Request.Submission.PlacementAssignmentId!)))!.ProviderScopeFingerprint);
    }

    [Fact]
    public async Task Missing_provider_operation_is_unknown_and_does_not_claim_health()
    {
        var workspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var instanceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var operationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var provider = new AzureElsaInstanceProvider(
            new CapturingOperationService(null),
            new CapturingOperationStore(),
            new InMemoryAssignmentStore(),
            options: EnabledOptions());

        var observation = await provider.ObserveAsync(new(
            workspaceId,
            instanceId,
            operationId,
            1,
            ElsaDesiredLifecycle.Running,
            null,
            null));

        Assert.Equal(ElsaInstanceProviderObservationKind.Unknown, observation.Kind);
        Assert.Equal(ElsaObservedLifecycle.Unknown, observation.ObservedLifecycle);
        Assert.Equal(ElsaInstanceProviderHealthGate.Unknown, observation.HealthGate);
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("target")]
    [InlineData("action")]
    [InlineData("idempotency")]
    [InlineData("scope")]
    public async Task Mismatched_provider_operation_identity_is_ambiguous_and_unknown(string mismatch)
    {
        var workspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var operationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var plan = Translate("5.0", "5.0.0");
        var operation = CreateOperation(workspaceId, plan, operationId) with
        {
            WorkspaceId = mismatch == "workspace" ? Guid.NewGuid() : workspaceId,
            TargetKey = mismatch == "target" ? "different-target" : AzureElsaInstanceProvider.WorkloadName(TestInstanceId),
            Action = mismatch == "action" ? AzureProviderOperationAction.Delete : AzureProviderOperationAction.Reconcile,
            IdempotencyKey = mismatch == "idempotency" ? "elsa-instance-operation:different" : AzureElsaInstanceProvider.IdempotencyKey(operationId)
        };
        var provider = new AzureElsaInstanceProvider(
            new CapturingOperationService(operation),
            new CapturingOperationStore(operation),
            new InMemoryAssignmentStore(),
            options:
            new AzureElsaInstanceProviderOptions
            {
                Enabled = true,
                TemplateFingerprint = new string('b', 64),
                ProviderScopeFingerprint = mismatch == "scope" ? new string('b', 64) : new string('a', 64),
                SubscriptionId = "11111111-1111-1111-1111-111111111111",
                ResourceGroupNamePrefix = "rg-elsa"
            });

        var observation = await provider.ObserveAsync(new(
            workspaceId,
            TestInstanceId,
            operationId,
            1,
            ElsaDesiredLifecycle.Running,
            null,
            null));

        Assert.Equal(ElsaInstanceProviderObservationKind.Ambiguous, observation.Kind);
        Assert.Equal(ElsaObservedLifecycle.Unknown, observation.ObservedLifecycle);
        Assert.Equal(ElsaInstanceProviderHealthGate.Unknown, observation.HealthGate);
        Assert.Equal("provider-operation-correlation-mismatch", observation.CorrelationId);
        Assert.Null(observation.CurrentDeploymentReference);
    }

    [Fact]
    public async Task Disabled_provider_fails_closed_before_submission()
    {
        var plan = Translate("5.0", "5.0.0");
        var service = new CapturingOperationService(CreateOperation(Guid.NewGuid(), plan, Guid.NewGuid()));
        var provider = new AzureElsaInstanceProvider(
            service,
            new CapturingOperationStore(),
            new InMemoryAssignmentStore(),
            options: new AzureElsaInstanceProviderOptions());

        var exception = await Assert.ThrowsAsync<ElsaInstanceProviderSubmissionException>(() => provider.SubmitAsync(
            CreateSubmission(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), plan)));
        Assert.Equal(ElsaInstanceProviderSubmissionFailureKind.Rejected, exception.Kind);
        Assert.Empty(service.Submissions);
    }

    [Fact]
    public async Task Lifecycle_submission_without_an_organization_binding_is_rejected_before_durable_submission()
    {
        var plan = Translate("5.0", "5.0.0");
        var service = new CapturingOperationService(CreateOperation(Guid.NewGuid(), plan, Guid.NewGuid()));
        var provider = new AzureElsaInstanceProvider(service, new CapturingOperationStore(), new InMemoryAssignmentStore(), options: EnabledOptions());

        var exception = await Assert.ThrowsAsync<ElsaInstanceProviderSubmissionException>(() => provider.SubmitAsync(
            CreateSubmission(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), plan) with { OrganizationId = null }));

        Assert.Equal(ElsaInstanceProviderSubmissionFailureKind.Rejected, exception.Kind);
        Assert.Empty(service.Submissions);
    }

    [Fact]
    public async Task Unsafe_extension_plan_is_rejected_before_durable_provider_submission()
    {
        var workspaceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var translated = Translate("5.0", "5.0.0");
        var service = new CapturingOperationService(CreateOperation(workspaceId, translated, operationId));
        var provider = new AzureElsaInstanceProvider(service, new CapturingOperationStore(), new InMemoryAssignmentStore(), options: EnabledOptions());
        var request = CreateSubmission(workspaceId, Guid.NewGuid(), operationId, translated);
        var resolvedPlan = request.Plan;
        request = request with
        {
            Plan = resolvedPlan with
            {
                Packages =
                [
                    resolvedPlan.Packages[0] with
                    {
                        PackageId = "Customer.SecretPackage",
                        ExtensionClass = ResolvedExtensionClass.ArbitraryCustomer
                    }
                ]
            }
        };

        var exception = await Assert.ThrowsAsync<ElsaInstanceProviderSubmissionException>(() => provider.SubmitAsync(request));

        Assert.Equal(ElsaInstanceProviderSubmissionFailureKind.Rejected, exception.Kind);
        Assert.Empty(service.Submissions);
        Assert.DoesNotContain("Customer.SecretPackage", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 1, 500, 1024, true)]
    [InlineData(1, 3, 1000, 2048, true)]
    [InlineData(1, 1, 500, 2048, false)]
    public async Task Submission_carries_the_resolved_capacity_or_is_rejected_without_a_Container_Apps_mapping(
        int minReplicas, int maxReplicas, int cpuMillicores, int memoryMiB, bool mappable)
    {
        var workspaceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var translated = Translate("5.0", "5.0.0");
        var service = new CapturingOperationService(CreateOperation(workspaceId, translated, operationId));
        var provider = new AzureElsaInstanceProvider(service, new CapturingOperationStore(), new InMemoryAssignmentStore(), options: EnabledOptions());
        var request = CreateSubmission(workspaceId, Guid.NewGuid(), operationId, translated);
        request = request with
        {
            Plan = request.Plan with
            {
                Capacity = request.Plan.Capacity with
                {
                    Components = [new("runtime", minReplicas, maxReplicas, cpuMillicores, memoryMiB)]
                }
            }
        };

        if (mappable)
        {
            await provider.SubmitAsync(request);
            Assert.Equal(new AzureWorkloadCapacity(minReplicas, maxReplicas, cpuMillicores, memoryMiB), Assert.Single(service.Submissions).Plan.Capacity);
            return;
        }

        var exception = await Assert.ThrowsAsync<ElsaInstanceProviderSubmissionException>(() => provider.SubmitAsync(request));
        Assert.Equal(ElsaInstanceProviderSubmissionFailureKind.Rejected, exception.Kind);
        Assert.Empty(service.Submissions);
    }

    [Fact]
    public async Task Durable_submission_failure_is_classified_as_outcome_unknown()
    {
        var plan = Translate("5.0", "5.0.0");
        var provider = new AzureElsaInstanceProvider(
            new ThrowingOperationService(),
            new CapturingOperationStore(),
            new InMemoryAssignmentStore(),
            options: EnabledOptions());

        var exception = await Assert.ThrowsAsync<ElsaInstanceProviderSubmissionException>(() => provider.SubmitAsync(
            CreateSubmission(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), plan)));

        Assert.Equal(ElsaInstanceProviderSubmissionFailureKind.OutcomeUnknown, exception.Kind);
    }

    [Fact]
    public void Enabled_provider_requires_a_valid_scope_fingerprint()
    {
        Assert.Throws<ArgumentException>(() => new AzureElsaInstanceProviderOptions { Enabled = true }.Validate());
        Assert.Throws<ArgumentException>(() => new AzureElsaInstanceProviderOptions
        {
            Enabled = true,
            ProviderScopeFingerprint = "not-a-fingerprint"
        }.Validate());

        EnabledOptions().Validate();
    }

    [Theory]
    [InlineData(false, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.ConfirmedAbsent)]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.ConfirmedAbsent)]
    [InlineData(true, AzureProviderOperationStatus.Running, false, ElsaInstanceCleanupObservationKind.InProgress)]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, true, ElsaInstanceCleanupObservationKind.Unknown)]
    [InlineData(false, AzureProviderOperationStatus.Accepted, false, ElsaInstanceCleanupObservationKind.InProgress)]
    [InlineData(false, AzureProviderOperationStatus.Queued, false, ElsaInstanceCleanupObservationKind.InProgress)]
    [InlineData(false, AzureProviderOperationStatus.Running, false, ElsaInstanceCleanupObservationKind.InProgress)]
    [InlineData(false, AzureProviderOperationStatus.RecoveryRequired, false, ElsaInstanceCleanupObservationKind.Unknown)]
    [InlineData(false, AzureProviderOperationStatus.Failed, false, ElsaInstanceCleanupObservationKind.Unknown)]
    [InlineData(false, AzureProviderOperationStatus.Cancelled, false, ElsaInstanceCleanupObservationKind.Unknown)]
    [InlineData(false, AzureProviderOperationStatus.EntitlementHeld, false, ElsaInstanceCleanupObservationKind.Unknown)]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.ConfirmedAbsent, "other-lifecycle")]
    [InlineData(false, AzureProviderOperationStatus.Running, false, ElsaInstanceCleanupObservationKind.Ambiguous, "other-lifecycle")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.ConfirmedAbsent, "wrong-action")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.ConfirmedAbsent, "invalid-retry")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.ConfirmedAbsent, "hashed-retry")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, true, ElsaInstanceCleanupObservationKind.Unknown, "other-lifecycle")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.ConfirmedAbsent, "retry")]
    [InlineData(true, AzureProviderOperationStatus.Running, false, ElsaInstanceCleanupObservationKind.Unknown, null, "missing-group")]
    [InlineData(true, AzureProviderOperationStatus.Running, false, ElsaInstanceCleanupObservationKind.Unknown, null, "wrong-group")]
    [InlineData(true, AzureProviderOperationStatus.Running, false, ElsaInstanceCleanupObservationKind.Unknown, null, "retained-inventory")]
    [InlineData(true, AzureProviderOperationStatus.Running, false, ElsaInstanceCleanupObservationKind.Unknown, null, "endpoint")]
    [InlineData(true, AzureProviderOperationStatus.Running, false, ElsaInstanceCleanupObservationKind.Unknown, null, "wrong-phase")]
    [InlineData(true, AzureProviderOperationStatus.Accepted, false, ElsaInstanceCleanupObservationKind.Unknown)]
    [InlineData(true, AzureProviderOperationStatus.Queued, false, ElsaInstanceCleanupObservationKind.Unknown)]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.Unknown, null, "missing-group")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.Unknown, null, "wrong-group")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.Unknown, null, "retained-inventory")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.Unknown, null, "wrong-phase")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.Unknown, null, "endpoint")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.ConfirmedAbsent, null, null, "retained")]
    [InlineData(true, AzureProviderOperationStatus.Running, false, ElsaInstanceCleanupObservationKind.Ambiguous, null, null, "retained")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.Ambiguous, "other-lifecycle", null, "retained")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.Ambiguous, null, "endpoint", "retained")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.Ambiguous, null, "wrong-phase", "retained")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.Ambiguous, null, "retained-inventory", "retained")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.Ambiguous, null, null, "retained-placement")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.Ambiguous, null, null, "retained-operation-scope")]
    public async Task Cleanup_submits_or_reobserves_delete_and_confirms_only_verified_absence(
        bool alreadyDeleted, AzureProviderOperationStatus status, bool retainedResource, ElsaInstanceCleanupObservationKind expectedKind,
        string? correlation = null, string? observationVariant = null, string? scopeVariant = null)
    {
        var workspaceId = Guid.NewGuid();
        var lifecycleOperationId = Guid.NewGuid();
        var plan = Translate("5.0", "5.0.0");
        plan = plan with { WorkloadName = AzureElsaInstanceProvider.WorkloadName(TestInstanceId) };
        var providerScope = scopeVariant is null ? new string('a', 64) : new string('b', 64);
        var reconcile = CreateOperation(workspaceId, plan, Guid.NewGuid()) with
        {
            OrganizationId = TestOrganizationId,
            InstanceId = TestInstanceId,
            LifecycleAction = ElsaInstanceOperationAction.Reconcile,
            ProviderScopeFingerprint = providerScope,
            SqlWorkflowPackageVersion = plan.SqlWorkflowPackageVersion,
            SqlQuartzPackageVersion = plan.SqlQuartzPackageVersion
        };
        reconcile = reconcile with
        {
            RequestHash = AzureProviderOperationValidation.ComputeRequestHash(
                AzureProviderOperationService.CreateOperationRequest(reconcile)),
            OperationIdentity = AzureProviderOperationValidation.ComputeOperationIdentity(
                AzureProviderOperationService.CreateOperationRequest(reconcile))
        };
        Assert.NotNull(AzureProviderOperationService.TryRestorePlan(reconcile, providerScope: null));
        var delete = reconcile with
        {
            Id = Guid.NewGuid(),
            Action = AzureProviderOperationAction.Delete,
            IdempotencyKey = AzureElsaInstanceProvider.IdempotencyKey(lifecycleOperationId) + ":delete",
            LifecycleAction = ElsaInstanceOperationAction.Delete,
            Status = status,
            Phase = AzureProviderOperationPhase.CleanupVerified,
            Resources = new(),
            Endpoint = null
        };
        delete = correlation switch
        {
            "other-lifecycle" => delete with { IdempotencyKey = AzureElsaInstanceProvider.IdempotencyKey(Guid.NewGuid()) + ":delete" },
            "wrong-action" => delete with { LifecycleAction = ElsaInstanceOperationAction.Reconcile },
            "invalid-retry" => delete with { IdempotencyKey = delete.IdempotencyKey + ":retry:not-an-operation" },
            "hashed-retry" => delete with { IdempotencyKey = "delete-retry:sha256:" + new string('a', 64) },
            "retry" => delete with { IdempotencyKey = delete.IdempotencyKey + ":retry:" + Guid.NewGuid().ToString("N") },
            _ => delete
        };
        var assignmentStore = new InMemoryAssignmentStore
        {
            State = alreadyDeleted ? AzureProviderAssignmentState.Deleted : AzureProviderAssignmentState.Reserved,
            LastOperationId = alreadyDeleted ? delete.Id : null,
            Resources = retainedResource ? new(WorkloadResourceId: "/subscriptions/retained/resourceGroups/retained/providers/Microsoft.App/containerApps/retained") : new()
        };
        var assignment = await assignmentStore.CreateOrGetAsync(new(
            workspaceId,
            TestOrganizationId,
            TestInstanceId,
            providerScope,
            "11111111-1111-1111-1111-111111111111",
            scopeVariant == "retained-placement" ? "rg-other" : "rg-elsa",
            AzureElsaInstanceProvider.WorkloadName(TestInstanceId),
            "westeurope"),
            DateTimeOffset.UtcNow);
        reconcile = reconcile with { ProviderAssignmentId = assignment.Id };
        reconcile = reconcile with
        {
            RequestHash = AzureProviderOperationValidation.ComputeRequestHash(
                AzureProviderOperationService.CreateOperationRequest(reconcile)),
            OperationIdentity = AzureProviderOperationValidation.ComputeOperationIdentity(
                AzureProviderOperationService.CreateOperationRequest(reconcile))
        };
        // The durable provider store preserves the assignment's immutable resource-group
        // authority when cleanup clears every other resource reference. Keep the test double's
        // completed operation shaped like that rehydrated row rather than an impossible all-null
        // resource snapshot.
        delete = delete with
        {
            ProviderAssignmentId = assignment.Id,
            ProviderScopeFingerprint = scopeVariant == "retained-operation-scope" ? new string('c', 64) : providerScope,
            Phase = observationVariant == "wrong-phase" ? AzureProviderOperationPhase.CleanupSubmitted : delete.Phase,
            Resources = observationVariant switch
            {
                "missing-group" => new(),
                "wrong-group" => new AzureProviderResourceReferences("rg-wrong"),
                "retained-inventory" => new AzureProviderResourceReferences(
                    assignment.ResourceGroupName,
                    WorkloadResourceId: "/subscriptions/retained/resourceGroups/retained/providers/Microsoft.App/containerApps/retained"),
                _ => new AzureProviderResourceReferences(assignment.ResourceGroupName)
            },
            Endpoint = observationVariant == "endpoint" ? "https://runtime.example.test" : null
        };
        var service = new CapturingOperationService(reconcile) { DeleteOperation = delete };
        var provider = new AzureElsaInstanceProvider(service, new CapturingOperationStore(reconcile, delete), assignmentStore, options: EnabledOptions());

        var result = await provider.CleanupAsync(new(
            workspaceId, TestInstanceId, lifecycleOperationId, 3, null,
            new ElsaPlacementAssignmentReference(assignment.Id.ToString("D")), null));

        Assert.Equal(expectedKind, result.Kind);
        Assert.Equal(expectedKind == ElsaInstanceCleanupObservationKind.Ambiguous ? "deletion.provider-correlation-invalid" :
            retainedResource ? "deletion.provider-evidence-unavailable" :
            expectedKind == ElsaInstanceCleanupObservationKind.ConfirmedAbsent ? "deletion.provider-confirmed-absent" :
            status is AzureProviderOperationStatus.Failed or AzureProviderOperationStatus.Cancelled ? "deletion.provider-cleanup-failed" :
            "deletion.provider-cleanup-pending", result.DiagnosticCode);
        Assert.Equal(lifecycleOperationId, result.OperationId);
        Assert.Equal(3, result.AttemptNumber);
        if (expectedKind == ElsaInstanceCleanupObservationKind.InProgress)
            Assert.Equal(AzureElsaInstanceProvider.LastProviderProgressAt(delete), result.LastProviderProgressAt);
        if (alreadyDeleted)
        {
            Assert.Empty(service.DeleteSubmissions);
            if (scopeVariant is not null)
            {
                Assert.Empty(await assignmentStore.ListRebindsAsync(workspaceId, assignment.Id));
                Assert.Equal(providerScope,
                    (await assignmentStore.GetAsync(workspaceId, assignment.Id))!.ProviderScopeFingerprint);
            }
        }
        else
        {
            var submission = Assert.Single(service.DeleteSubmissions);
            Assert.Equal(ElsaInstanceOperationAction.Delete, submission.LifecycleAction);
            Assert.Equal(plan.Fingerprint, submission.Plan.Fingerprint);
            Assert.Equal(AzureElsaInstanceProvider.IdempotencyKey(lifecycleOperationId), submission.IdempotencyKey);
        }
    }

    [Fact]
    public void LastProviderProgressAt_is_status_or_phase_progress_never_heartbeat_or_arm_restamp()
    {
        var created = DateTimeOffset.Parse("2026-10-03T10:00:00Z");
        var operation = CreateOperation(Guid.NewGuid(), Translate("5.0", "5.0.0"), Guid.NewGuid()) with
        {
            CreatedAt = created,
            StatusChangedAt = created.AddMinutes(1),
            ProgressChangedAt = created.AddMinutes(5),
            HeartbeatAt = created.AddMinutes(40),
            LastArmObservedAt = created.AddMinutes(40),
            UpdatedAt = created.AddMinutes(40),
            Resources = new(
                ResourceGroupName: "rg-safe",
                FoundationDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-safe/providers/Microsoft.Resources/deployments/foundation",
                AcrPullRoleAssignmentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-reg/providers/Microsoft.ContainerRegistry/registries/reg/providers/Microsoft.Authorization/roleAssignments/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                RegistryResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-reg/providers/Microsoft.ContainerRegistry/registries/reg")
        };

        Assert.Equal(created.AddMinutes(5), AzureElsaInstanceProvider.LastProviderProgressAt(operation));
        Assert.NotEqual(operation.HeartbeatAt, AzureElsaInstanceProvider.LastProviderProgressAt(operation));
        Assert.NotEqual(operation.LastArmObservedAt, AzureElsaInstanceProvider.LastProviderProgressAt(operation));
        Assert.NotEqual(operation.CreatedAt, AzureElsaInstanceProvider.LastProviderProgressAt(operation));

        var shrinking = operation with
        {
            Phase = AzureProviderOperationPhase.CleanupSubmitted,
            Resources = new(WorkloadResourceId: null)
        };
        Assert.NotEqual(
            AzureElsaInstanceProvider.CleanupProgressReceipt(operation),
            AzureElsaInstanceProvider.CleanupProgressReceipt(shrinking));

        var persistedRoleCleared = operation with
        {
            Resources = operation.Resources with { AcrPullRoleAssignmentId = null }
        };
        Assert.NotEqual(
            AzureElsaInstanceProvider.CleanupProgressReceipt(operation),
            AzureElsaInstanceProvider.CleanupProgressReceipt(persistedRoleCleared));

        var metadataOnly = operation with
        {
            HeartbeatAt = created.AddMinutes(50),
            LastArmObservedAt = created.AddMinutes(50),
            UpdatedAt = created.AddMinutes(50)
        };
        Assert.Equal(
            AzureElsaInstanceProvider.CleanupProgressReceipt(operation),
            AzureElsaInstanceProvider.CleanupProgressReceipt(metadataOnly));

        var endpointOnly = operation with
        {
            Endpoint = "https://runtime.example.test/"
        };
        Assert.Equal(
            AzureElsaInstanceProvider.CleanupProgressReceipt(operation),
            AzureElsaInstanceProvider.CleanupProgressReceipt(endpointOnly));
    }

    [Fact]
    public async Task Cleanup_without_a_restorable_correlated_reconcile_plan_fails_closed_without_submission()
    {
        var service = new CapturingOperationService(null);
        var provider = new AzureElsaInstanceProvider(service, new CapturingOperationStore(), new InMemoryAssignmentStore(), options: EnabledOptions());
        var operationId = Guid.NewGuid();

        var result = await provider.CleanupAsync(new(
            Guid.NewGuid(), TestInstanceId, operationId, 1, null,
            new ElsaPlacementAssignmentReference(Guid.NewGuid().ToString("D")), null));

        Assert.Equal(ElsaInstanceCleanupObservationKind.Ambiguous, result.Kind);
        Assert.Equal("deletion.provider-assignment-invalid", result.DiagnosticCode);
        Assert.Empty(service.DeleteSubmissions);
    }

    [Theory]
    [InlineData(true, false, 2, ElsaInstanceCleanupObservationKind.ConfirmedAbsent)]
    [InlineData(false, false, 2, ElsaInstanceCleanupObservationKind.Unknown)]
    [InlineData(true, true, 2, ElsaInstanceCleanupObservationKind.Unknown)]
    [InlineData(true, false, 3, ElsaInstanceCleanupObservationKind.Ambiguous)]
    public async Task Delete_recovery_reuses_exact_cleanup_classifier_for_a_completed_successor(
        bool assignmentDeleted, bool retainedInventory, int lifecycleAttempt,
        ElsaInstanceCleanupObservationKind expectedKind)
    {
        var workspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var lifecycleOperationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var providerOperationId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var assignmentId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var plan = TranslateForInstance("5.0", "5.0.0");
        var resourceGroupName = AzureProviderResourceAssignmentNaming.ResourceGroupName("rg-elsa-proof", TestInstanceId);
        var operation = CreateOperation(workspaceId, plan, providerOperationId) with
        {
            Action = AzureProviderOperationAction.Delete,
            IdempotencyKey = AzureElsaInstanceProvider.IdempotencyKey(lifecycleOperationId) + ":delete",
            Status = AzureProviderOperationStatus.Succeeded,
            Phase = AzureProviderOperationPhase.CleanupVerified,
            CheckpointSequence = 1,
            AttemptNumber = 2,
            Version = 3,
            Resources = new AzureProviderResourceReferences(ResourceGroupName: resourceGroupName),
            Endpoint = null,
            OrganizationId = TestOrganizationId,
            InstanceId = TestInstanceId,
            LifecycleAction = ElsaInstanceOperationAction.Delete,
            ProviderAssignmentId = assignmentId,
            SqlWorkflowPackageVersion = plan.SqlWorkflowPackageVersion,
            SqlQuartzPackageVersion = plan.SqlQuartzPackageVersion
        };
        var operationRequest = AzureProviderOperationService.CreateOperationRequest(operation);
        operation = operation with
        {
            RequestHash = AzureProviderOperationValidation.ComputeRequestHash(operationRequest),
            OperationIdentity = AzureProviderOperationValidation.ComputeOperationIdentity(operationRequest)
        };
        var authority = new AzureProviderDeleteRecoveryAuthority(
            providerOperationId,
            assignmentId,
            2,
            1,
            1,
            2,
            1,
            operation.OperationIdentity,
            operation.RequestHash,
            operation.TargetKey,
            operation.ProviderScopeFingerprint!,
            operation.PlanFingerprint,
            operation.TemplateFingerprint);
        authority.Validate();
        var operationStore = new CapturingOperationStore(completedDelete: operation)
        {
            DeleteRecoveryAuthority = authority
        };
        var assignmentStore = new InMemoryAssignmentStore
        {
            AssignmentId = assignmentId,
            LastOperationId = providerOperationId,
            State = assignmentDeleted ? AzureProviderAssignmentState.Deleted : AzureProviderAssignmentState.Reserved,
            Resources = new AzureProviderResourceReferences(ResourceGroupName: resourceGroupName,
                WorkloadRevisionName: retainedInventory ? "retained-revision" : null)
        };
        await assignmentStore.CreateOrGetAsync(new(
            workspaceId,
            TestOrganizationId,
            TestInstanceId,
            new string('a', 64),
            "11111111-1111-1111-1111-111111111111",
            "rg-elsa-proof",
            operation.TargetKey,
            "westeurope"),
            DateTimeOffset.UtcNow);
        var executor = new AzureProviderExecutor(
            operationStore,
            new NeverCalledRunner());
        var provider = new AzureElsaInstanceProvider(
            new CapturingOperationService(operation),
            operationStore,
            assignmentStore,
            options: EnabledOptions(),
            executor: executor);

        var result = await provider.RecoverDeleteAsync(new(
            new(
                workspaceId,
                TestInstanceId,
                lifecycleOperationId,
                lifecycleAttempt,
                null,
                new ElsaPlacementAssignmentReference(assignmentId.ToString("D")),
                null),
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            1,
            "delete-worker",
            new string('a', 64),
            1));

        Assert.True(expectedKind == result.Kind, result.DiagnosticCode);
        if (expectedKind == ElsaInstanceCleanupObservationKind.ConfirmedAbsent)
            Assert.Equal("deletion.provider-confirmed-absent", result.DiagnosticCode);
    }

    [Fact]
    public async Task Health_probe_asks_the_runtime_probe_for_this_instances_workload_and_current_endpoint()
    {
        var runtime = new RecordingRuntimeHealthProbe(new(ElsaInstanceHealth.Degraded, "azure.health.not-healthy"));

        var result = await HealthProbeProvider(runtime).ProbeAsync(HealthProbeRequest("https://runtime.example.test"));

        Assert.Equal((ElsaInstanceHealth.Degraded, "azure.health.not-healthy"), (result.Health, result.DiagnosticCode));
        Assert.Equal(
            (AzureElsaInstanceProvider.WorkloadName(TestInstanceId), "https://runtime.example.test", TimeSpan.FromSeconds(7)),
            Assert.Single(runtime.Calls));
    }

    [Fact]
    public async Task Health_probe_without_a_current_endpoint_or_a_runtime_probe_is_unknown_without_probing()
    {
        var runtime = new RecordingRuntimeHealthProbe(new(ElsaInstanceHealth.Healthy, "azure.health.healthy"));

        var withoutEndpoint = await HealthProbeProvider(runtime).ProbeAsync(HealthProbeRequest(null));
        var withoutProbe = await HealthProbeProvider(null).ProbeAsync(HealthProbeRequest("https://runtime.example.test"));

        Assert.Equal((ElsaInstanceHealth.Unknown, "azure.health.endpoint-unavailable"), (withoutEndpoint.Health, withoutEndpoint.DiagnosticCode));
        Assert.Equal((ElsaInstanceHealth.Unknown, "azure.health.probe-unavailable"), (withoutProbe.Health, withoutProbe.DiagnosticCode));
        Assert.Empty(runtime.Calls);
    }

    private static AzureElsaInstanceProvider HealthProbeProvider(IAzureRuntimeHealthProbe? runtime) =>
        new(new CapturingOperationService(null), new CapturingOperationStore(), new InMemoryAssignmentStore(),
            options: EnabledOptions(), runtimeHealthProbe: runtime);

    private static ElsaInstanceHealthProbeRequest HealthProbeRequest(string? endpoint) =>
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), TestInstanceId,
            new ElsaCurrentDeploymentReference("deployment-1", "attempt-1", endpoint), TimeSpan.FromSeconds(7));

    private sealed class RecordingRuntimeHealthProbe(ElsaInstanceHealthProbeResult result) : IAzureRuntimeHealthProbe
    {
        public List<(string WorkloadName, string Endpoint, TimeSpan Timeout)> Calls { get; } = [];

        public Task<ElsaInstanceHealthProbeResult> ProbeAsync(
            string workloadName,
            string endpointOrigin,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((workloadName, endpointOrigin, timeout));
            return Task.FromResult(result);
        }
    }

    private static AzureElsaInstanceProviderOptions EnabledOptions() =>
        new()
        {
            Enabled = true,
            TemplateFingerprint = new string('b', 64),
            ProviderScopeFingerprint = new string('a', 64),
            SubscriptionId = "11111111-1111-1111-1111-111111111111",
            ResourceGroupNamePrefix = "rg-elsa"
        };

    private static AzureProviderRecoveryObservation FailedObservation(
        AzureProviderRunnerStep failedStep,
        string? innerErrorCode = null) =>
        new(
            AzureProviderRecoveryObservationKind.Failed,
            failedStep,
            new(),
            AzureProviderHealth.Unknown,
            null,
            AzureLateSuccessCodes.DeploymentFailed,
            "Azure reported the deployment as failed or canceled.",
            innerErrorCode);

    private static AzureProviderRecoveryObservation ConfirmedObservation(AzureProviderRunnerStep completedStep) =>
        new(
            AzureProviderRecoveryObservationKind.Confirmed,
            completedStep,
            new(),
            AzureProviderHealth.Unknown,
            null,
            completedStep == AzureProviderRunnerStep.Workload
                ? "azure.recovery.workload-observed"
                : completedStep == AzureProviderRunnerStep.AcrPull
                    ? "azure.recovery.acr-pull-observed"
                    : "azure.recovery.foundation-observed",
            "The retained Azure checkpoint was observed without mutation.");

    private static async Task<ObserveFixture> CreateObserveFixtureAsync(
        AzureProviderRecoveryObservation observed,
        AzureProviderRunnerStep attemptedStep,
        AzureProviderOperationPhase phase,
        DateTimeOffset? lastArmObservedAt = null,
        int backoffSeconds = 0,
        int autoResumeCount = 0,
        DateTimeOffset? now = null)
    {
        var workspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var operationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var assignmentId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var plan = TranslateForInstance("5.0", "5.0.0");
        var operation = CreateOperation(workspaceId, plan, operationId) with
        {
            Status = AzureProviderOperationStatus.RecoveryRequired,
            Phase = phase,
            AttemptNumber = 2,
            Version = 2,
            OrganizationId = TestOrganizationId,
            InstanceId = TestInstanceId,
            LifecycleAction = ElsaInstanceOperationAction.Reconcile,
            ProviderAssignmentId = assignmentId,
            SqlWorkflowPackageVersion = plan.SqlWorkflowPackageVersion,
            SqlQuartzPackageVersion = plan.SqlQuartzPackageVersion,
            AttemptedStep = attemptedStep,
            LastArmObservedAt = lastArmObservedAt,
            ArmObservationBackoffSeconds = backoffSeconds,
            AutoResumeCount = autoResumeCount
        };
        var operationRequest = AzureProviderOperationService.CreateOperationRequest(operation);
        operation = operation with
        {
            RequestHash = AzureProviderOperationValidation.ComputeRequestHash(operationRequest),
            OperationIdentity = AzureProviderOperationValidation.ComputeOperationIdentity(operationRequest)
        };

        var observationStore = new RecordingRecoveryObservationStore();
        var operationStore = new CapturingOperationStore(operation);
        var observer = new ScriptedRecoveryObserver(observed);
        var assignmentStore = new InMemoryAssignmentStore
        {
            AssignmentId = assignmentId,
            State = AzureProviderAssignmentState.Active,
            LastOperationId = operationId
        };
        var options = EnabledOptions();
        await assignmentStore.CreateOrGetAsync(new(
            workspaceId, TestOrganizationId, TestInstanceId,
            options.ProviderScopeFingerprint!, options.SubscriptionId,
            options.ResourceGroupNamePrefix, operation.TargetKey, operation.Location,
            options.ResourceGroupNamingVersion), DateTimeOffset.UtcNow);
        var provider = new AzureElsaInstanceProvider(
            new CapturingOperationService(operation),
            operationStore,
            assignmentStore,
            timeProvider: now is { } observedAt ? new StaticTimeProvider(observedAt) : null,
            options: options,
            recoveryObserver: observer,
            recoveryObservationStore: observationStore);
        var request = new ElsaInstanceProviderReconciliationRequest(
            workspaceId,
            TestInstanceId,
            operationId,
            operation.AttemptNumber,
            ElsaDesiredLifecycle.Running,
            new ElsaResolvedPlanReference(
                "plan-1",
                1,
                "sha256:" + new string('d', 64),
                $"https://control.example.test/api/workspaces/{workspaceId:D}/instances/{TestInstanceId:D}/resolved-plans/plan-1"),
            null,
            2);
        return new(provider, request, observationStore, operationStore, observer);
    }

    private static async Task<RecoveryFixture> CreateRecoveryFixtureAsync(
        AzureProviderOperationStatus status,
        bool includeReplayObservation = false,
        bool wrongReplayObservation = false,
        bool includeStrictObservation = false)
    {
        var workspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var operationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var assignmentId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var plan = TranslateForInstance("5.0", "5.0.0");
        var isPostClaim = status is AzureProviderOperationStatus.Running or AzureProviderOperationStatus.Succeeded;
        var operation = CreateOperation(workspaceId, plan, operationId) with
        {
            Status = status,
            Phase = AzureProviderOperationPhase.FoundationSubmitted,
            AttemptNumber = isPostClaim ? 2 : 1,
            Version = isPostClaim ? 2 : 1,
            OrganizationId = TestOrganizationId,
            InstanceId = TestInstanceId,
            LifecycleAction = ElsaInstanceOperationAction.Reconcile,
            ProviderAssignmentId = assignmentId,
            SqlWorkflowPackageVersion = plan.SqlWorkflowPackageVersion,
            SqlQuartzPackageVersion = plan.SqlQuartzPackageVersion,
            AttemptedStep = AzureProviderRunnerStep.Foundation
        };
        var operationRequest = AzureProviderOperationService.CreateOperationRequest(operation);
        operation = operation with
        {
            RequestHash = AzureProviderOperationValidation.ComputeRequestHash(operationRequest),
            OperationIdentity = AzureProviderOperationValidation.ComputeOperationIdentity(operationRequest)
        };

        var preClaimOperation = operation with
        {
            Status = AzureProviderOperationStatus.RecoveryRequired,
            AttemptNumber = 1,
            Version = 1
        };
        var observation = CreateProviderRecoveryObservation(preClaimOperation);
        var replayObservation = includeReplayObservation
            ? wrongReplayObservation
                ? observation with { ProviderOperationId = Guid.Parse("77777777-7777-7777-7777-777777777777") }
                : observation
            : null;
        var strictObservation = includeStrictObservation ? observation : null;
        var observationStore = new CapturingRecoveryObservationStore(strictObservation, replayObservation);
        var operationStore = new CapturingOperationStore(operation);
        var observer = new CountingRecoveryObserver();
        var assignmentStore = new InMemoryAssignmentStore
        {
            AssignmentId = assignmentId,
            State = AzureProviderAssignmentState.Active,
            LastOperationId = operationId
        };
        var options = EnabledOptions();
        await assignmentStore.CreateOrGetAsync(new(
            workspaceId, TestOrganizationId, TestInstanceId,
            options.ProviderScopeFingerprint!, options.SubscriptionId,
            options.ResourceGroupNamePrefix, operation.TargetKey, operation.Location,
            options.ResourceGroupNamingVersion), DateTimeOffset.UtcNow);
        var provider = new AzureElsaInstanceProvider(
            new CapturingOperationService(operation),
            operationStore,
            assignmentStore,
            options: EnabledOptions(),
            recoveryObserver: observer,
            recoveryObservationStore: observationStore);
        var submission = CreateSubmission(workspaceId, TestInstanceId, operationId, plan) with
        {
            AttemptNumber = 2,
            PlacementAssignmentId = assignmentId.ToString("D")
        };
        var recordId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var observationDigest = observation.ComputeRecordDigest(recordId);
        var envelope = new ElsaInstanceProviderRecoveryEnvelope(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            TestOrganizationId,
            workspaceId,
            TestInstanceId,
            operationId,
            1,
            1,
            2,
            2,
            $"instance/{TestInstanceId:D}/operations",
            "recovery-key",
            new string('c', 64),
            ElsaInstanceProviderRecoveryObservationReference.Create(recordId, observationDigest),
            observationDigest);
        return new(
            provider,
            new(submission, envelope),
            observationStore,
            operationStore,
            observer,
            assignmentStore);
    }

    private static AzureProviderRecoveryObservationRecord CreateProviderRecoveryObservation(
        AzureProviderOperation operation)
    {
        var uri = $"https://control.example.test/api/workspaces/{operation.WorkspaceId:D}/instances/{TestInstanceId:D}/resolved-plans/plan-1";
        return new(
            operation.OrganizationId!.Value,
            operation.WorkspaceId,
            operation.InstanceId!.Value,
            operation.Id,
            operation.LifecycleAction!.Value,
            1,
            1,
            operation.Id,
            operation.OperationIdentity,
            operation.RequestHash,
            operation.AttemptNumber,
            operation.Version,
            operation.CheckpointSequence,
            operation.ProviderAssignmentId!.Value,
            operation.TargetKey,
            operation.ProviderScopeFingerprint,
            "plan-1",
            1,
            uri,
            "sha256:" + new string('d', 64),
            operation.PlanFingerprint,
            operation.TemplateFingerprint,
            AzureProviderRunnerStep.Foundation,
            AzureProviderOperationPhase.FoundationObserved,
            AzureProviderHealth.Unknown,
            new string('e', 64),
            new string('f', 64),
            DateTimeOffset.Parse("2026-09-06T08:00:00Z"));
    }

    private static AzureWorkloadPlan TranslateForInstance(string releaseLine, string version) =>
        AzureWorkloadPlanTranslator.Translate(
            AzureWorkloadPlanTranslatorTests.CreatePlan(releaseLine, version),
            new(AzureElsaInstanceProvider.WorkloadName(TestInstanceId), "westeurope")).Plan!;

    private static AzureWorkloadPlan Translate(string releaseLine, string version) =>
        AzureWorkloadPlanTranslator.Translate(
            AzureWorkloadPlanTranslatorTests.CreatePlan(releaseLine, version),
            new("workload-a", "westeurope")).Plan!;

    private static ElsaInstanceProviderSubmission CreateSubmission(
        Guid workspaceId,
        Guid instanceId,
        Guid operationId,
        AzureWorkloadPlan plan) =>
        new(
            workspaceId,
            instanceId,
            operationId,
            1,
            ElsaDesiredLifecycle.Running,
            ToResolvedPlan(plan),
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            "westeurope",
            TestOrganizationId,
            ElsaInstanceOperationAction.Reconcile,
            operationId.ToString("D"));

    private static ResolvedElsaApplicationPlan ToResolvedPlan(AzureWorkloadPlan plan) =>
        AzureWorkloadPlanTranslatorTests.CreatePlan(plan.ReleaseLine, plan.ElsaVersion);

    private static AzureProviderOperation CreateOperation(
        Guid workspaceId,
        AzureWorkloadPlan plan,
        Guid operationId) =>
        new(
            operationId,
            workspaceId,
            AzureElsaInstanceProvider.WorkloadName(TestInstanceId),
            AzureProviderOperationAction.Reconcile,
            $"elsa-instance-operation:{operationId:D}",
            new string('a', 64),
            $"azure-operation-{operationId:N}",
            plan.Fingerprint,
            new string('b', 64),
            plan.ElsaVersion,
            plan.ReleaseLine,
            plan.Topology,
            plan.Isolation,
            plan.Location,
            plan.ImageRepository,
            $"sha256:{plan.ImageDigest}",
            plan.ReleaseManifestDigest,
            plan.ReleaseManifestSignatureDigest,
            AzureProviderOperationStatus.Accepted,
            AzureProviderOperationPhase.Planned,
            0,
            0,
            1,
            new(),
            null,
            AzureProviderHealth.Unknown,
            [],
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            plan.ReleaseManifestReference,
            plan.ReleaseManifestSignatureReference,
            plan.SecretReferences,
            ProviderScopeFingerprint: new string('a', 64),
            ProviderAssignmentId: operationId);

    private sealed record ObserveFixture(
        AzureElsaInstanceProvider Provider,
        ElsaInstanceProviderReconciliationRequest Request,
        RecordingRecoveryObservationStore ObservationStore,
        CapturingOperationStore OperationStore,
        ScriptedRecoveryObserver Observer);

    private sealed record RecoveryFixture(
        AzureElsaInstanceProvider Provider,
        ElsaInstanceProviderRecoveryRequest Request,
        CapturingRecoveryObservationStore ObservationStore,
        CapturingOperationStore OperationStore,
        CountingRecoveryObserver Observer,
        InMemoryAssignmentStore AssignmentStore);

    private sealed class StaticTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ScriptedRecoveryObserver(AzureProviderRecoveryObservation observation) : IAzureProviderRecoveryObserver
    {
        public int Calls { get; private set; }

        public Task<AzureProviderRecoveryObservation> ObserveAsync(
            AzureProviderRecoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(observation);
        }
    }

    private sealed class RecordingRecoveryObservationStore : IAzureProviderRecoveryObservationStore
    {
        public int CreateCalls { get; private set; }
        public AzureProviderRecoveryObservationReceipt? LastReceipt { get; private set; }

        public Task<AzureProviderRecoveryObservationReceipt> CreateOrGetAsync(
            AzureProviderRecoveryObservationRecord observation,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            var recordId = Guid.Parse("88888888-8888-8888-8888-888888888888");
            var digest = observation.ComputeRecordDigest(recordId);
            LastReceipt = new AzureProviderRecoveryObservationReceipt(
                recordId,
                ElsaInstanceProviderRecoveryObservationReference.Create(recordId, digest),
                digest,
                observation);
            return Task.FromResult(LastReceipt);
        }

        public Task<AzureProviderRecoveryObservationReceipt?> GetLatestReceiptForAttemptAsync(
            Guid workspaceId,
            Guid lifecycleOperationId,
            int observedLifecycleAttemptNumber,
            Guid providerOperationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                LastReceipt is { } receipt &&
                receipt.Observation.WorkspaceId == workspaceId &&
                receipt.Observation.LifecycleOperationId == lifecycleOperationId &&
                receipt.Observation.ObservedLifecycleAttemptNumber == observedLifecycleAttemptNumber &&
                receipt.Observation.ProviderOperationId == providerOperationId
                    ? receipt
                    : null);

        public Task<AzureProviderRecoveryObservationReceipt?> GetLatestReceiptForOperationAsync(
            Guid workspaceId,
            Guid lifecycleOperationId,
            Guid providerOperationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                LastReceipt is { } receipt &&
                receipt.Observation.WorkspaceId == workspaceId &&
                receipt.Observation.LifecycleOperationId == lifecycleOperationId &&
                receipt.Observation.ProviderOperationId == providerOperationId
                    ? receipt
                    : null);

        public Task<AzureProviderRecoveryObservationRecord?> GetAndValidateRecordedAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid instanceId,
            Guid lifecycleOperationId,
            int observedLifecycleAttemptNumber,
            string reference,
            string digest,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureProviderRecoveryObservationRecord?>(null);

        public Task<AzureProviderRecoveryObservationRecord?> GetAndValidateForAcceptedRecoveryAsync(
            AzureProviderRecoveryObservationBinding binding,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureProviderRecoveryObservationRecord?>(null);

        public Task<AzureProviderRecoveryObservationRecord?> GetAndValidateForAcceptedRecoveryReplayAsync(
            AzureProviderRecoveryObservationBinding binding,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureProviderRecoveryObservationRecord?>(null);
    }

    private sealed class CapturingRecoveryObservationStore(
        AzureProviderRecoveryObservationRecord? strictObservation,
        AzureProviderRecoveryObservationRecord? replayObservation) : IAzureProviderRecoveryObservationStore
    {
        public int StrictCalls { get; private set; }
        public int ReplayCalls { get; private set; }

        public Task<AzureProviderRecoveryObservationReceipt> CreateOrGetAsync(
            AzureProviderRecoveryObservationRecord observation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AzureProviderRecoveryObservationRecord?> GetAndValidateRecordedAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid instanceId,
            Guid lifecycleOperationId,
            int observedLifecycleAttemptNumber,
            string reference,
            string digest,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureProviderRecoveryObservationRecord?>(null);

        public Task<AzureProviderRecoveryObservationRecord?> GetAndValidateForAcceptedRecoveryAsync(
            AzureProviderRecoveryObservationBinding binding,
            CancellationToken cancellationToken = default)
        {
            StrictCalls++;
            return Task.FromResult(strictObservation);
        }

        public Task<AzureProviderRecoveryObservationRecord?> GetAndValidateForAcceptedRecoveryReplayAsync(
            AzureProviderRecoveryObservationBinding binding,
            CancellationToken cancellationToken = default)
        {
            ReplayCalls++;
            return Task.FromResult(replayObservation);
        }
    }

    private sealed class CountingRecoveryObserver : IAzureProviderRecoveryObserver
    {
        public int Calls { get; private set; }

        public Task<AzureProviderRecoveryObservation> ObserveAsync(
            AzureProviderRecoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AzureProviderRecoveryObservation(
                AzureProviderRecoveryObservationKind.Unknown,
                null,
                new(),
                AzureProviderHealth.Unknown,
                null,
                "provider.recovery.unknown",
                "The retained provider state remains uncertain."));
        }
    }

    private sealed class CapturingOperationService(AzureProviderOperation? operation) : IAzureProviderOperationService, IAzureProviderOperationReplayService
    {
        public List<AzureProviderOperationSubmission> Submissions { get; } = [];
        public List<AzureProviderOperationSubmission> DeleteSubmissions { get; } = [];
        public AzureProviderOperation? DeleteOperation { get; init; }

        public Task<AzureProviderOperation> SubmitAsync(
            Guid workspaceId,
            AzureProviderOperationSubmission submission,
            CancellationToken cancellationToken = default)
        {
            Submissions.Add(submission);
            return Task.FromResult(operation!);
        }

        public Task<AzureProviderOperationSubmissionResult> SubmitWithReplayAsync(
            Guid workspaceId,
            AzureProviderOperationSubmission submission,
            CancellationToken cancellationToken = default)
        {
            Submissions.Add(submission);
            return Task.FromResult(new AzureProviderOperationSubmissionResult(operation!, Replayed: Submissions.Count > 1));
        }

        public Task<AzureProviderOperation> SubmitDeleteAsync(
            Guid workspaceId,
            AzureProviderOperationSubmission submission,
            CancellationToken cancellationToken = default)
        {
            DeleteSubmissions.Add(submission);
            return Task.FromResult(DeleteOperation ?? operation!);
        }

        public Task<AzureProviderOperationStatusResponse?> GetStatusAsync(
            Guid workspaceId,
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureProviderOperationStatusResponse?>(null);
    }

    private sealed class ThrowingOperationService : IAzureProviderOperationService
    {
        public Task<AzureProviderOperation> SubmitAsync(Guid workspaceId, AzureProviderOperationSubmission submission, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("durable provider outcome unavailable");

        public Task<AzureProviderOperation> SubmitDeleteAsync(Guid workspaceId, AzureProviderOperationSubmission submission, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AzureProviderOperationStatusResponse?> GetStatusAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureProviderOperationStatusResponse?>(null);
    }

    private sealed class CapturingOperationStore(AzureProviderOperation? operation = null, AzureProviderOperation? completedDelete = null) : IAzureProviderOperationStore, IAzureProviderDeleteRecoveryStore
    {
        public int ClaimRecoveryCalls { get; private set; }
        public int ArmClockCalls { get; private set; }
        public int AutoResumeIncrements { get; private set; }
        public AzureProviderOperation? Current { get; set; } = operation;
        public AzureProviderDeleteRecoveryAuthority? DeleteRecoveryAuthority { get; init; }

        public Task<AzureProviderOperation> CreateOrGetAsync(AzureProviderOperationRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<AzureProviderOperationCreateResult> CreateOrGetWithResultAsync(AzureProviderOperationRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            new(await CreateOrGetAsync(request, now, cancellationToken), Replayed: false);
        public Task<AzureProviderOperation?> GetAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new[] { completedDelete, Current }.FirstOrDefault(candidate =>
                candidate?.WorkspaceId == workspaceId && candidate.Id == operationId));
        public Task<AzureProviderDeleteRecoveryAuthority?> GetDeleteRecoveryAuthorityAsync(
            Guid workspaceId,
            Guid recoveryRequestId,
            Guid instanceId,
            Guid lifecycleOperationId,
            CancellationToken cancellationToken = default) => Task.FromResult(DeleteRecoveryAuthority);
        public Task<AzureProviderOperation?> ClaimDeleteRecoveryAsync(
            AzureProviderDeleteRecoveryClaimRequest request,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) => Task.FromResult<AzureProviderOperation?>(null);
        public Task<IReadOnlyList<AzureProviderOperation>> ListRunnableAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AzureProviderOperation>>([]);
        public Task<AzureProviderOperation?> GetLatestReconcileAsync(Guid workspaceId, string targetKey, string? providerScopeFingerprint, CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task RecordArmObservationClockAsync(Guid workspaceId, Guid operationId, DateTimeOffset observedAt, int backoffSeconds, CancellationToken cancellationToken = default) =>
            RecordArmObservationClockAsync(workspaceId, operationId, observedAt, backoffSeconds, null, cancellationToken);

        public Task RecordArmObservationClockAsync(Guid workspaceId, Guid operationId, DateTimeOffset observedAt, int backoffSeconds, string? reasonCode, CancellationToken cancellationToken = default)
        {
            ArmClockCalls++;
            if (Current is not null)
                Current = Current with
                {
                    LastArmObservedAt = observedAt,
                    ArmObservationBackoffSeconds = backoffSeconds,
                    LastObservationReasonCode = reasonCode ?? Current.LastObservationReasonCode
                };
            return Task.CompletedTask;
        }
        public Task<AzureProviderOperation?> IncrementAutoResumeCountAsync(
            Guid workspaceId,
            Guid operationId,
            int expectedCount,
            CancellationToken cancellationToken = default)
        {
            if (Current is null ||
                Current.AutoResumeCount != expectedCount ||
                Current.AutoResumeCount >= AzureNamedDeploymentFreshness.MaximumAutoResumes)
                return Task.FromResult<AzureProviderOperation?>(null);
            AutoResumeIncrements++;
            Current = Current with { AutoResumeCount = Current.AutoResumeCount + 1 };
            return Task.FromResult<AzureProviderOperation?>(Current);
        }
        public List<string> AutoResumeOutcomes { get; } = [];
        public Task RecordAutoResumeOutcomeAsync(Guid workspaceId, Guid operationId, string reasonCode, CancellationToken cancellationToken = default)
        {
            AutoResumeOutcomes.Add(reasonCode);
            if (Current is not null)
                Current = Current with { LastObservationReasonCode = reasonCode };
            return Task.CompletedTask;
        }
        public Task<AzureProviderOperation?> MarkUnrestorableAsync(Guid workspaceId, Guid operationId, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) => Task.FromResult<AzureProviderOperation?>(null);
        public Task<AzureProviderOperation?> ClaimAsync(Guid workspaceId, Guid operationId, string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) => Task.FromResult<AzureProviderOperation?>(null);
        public Task<AzureProviderOperation?> ClaimRecoveryAsync(Guid workspaceId, Guid operationId, string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            ClaimRecoveryCalls++;
            return Task.FromResult<AzureProviderOperation?>(null);
        }
        public Task<AzureProviderOperation?> HeartbeatAsync(Guid workspaceId, Guid operationId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) => Task.FromResult<AzureProviderOperation?>(null);
        public Task<AzureProviderOperation?> CheckpointAsync(Guid workspaceId, Guid operationId, string leaseToken, AzureProviderCheckpoint checkpoint, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) => Task.FromResult<AzureProviderOperation?>(null);
        public Task<AzureProviderOperation?> FinalizeAsync(Guid workspaceId, Guid operationId, string leaseToken, AzureProviderOperationStatus status, string code, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) => Task.FromResult<AzureProviderOperation?>(null);
        public Task<int> RecoverStaleAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<IReadOnlyList<AzureProviderOperationTransition>> ListTransitionsAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AzureProviderOperationTransition>>([]);
    }

    private sealed class NeverCalledRunner : IAzureProviderRunner
    {
        public Task<AzureProviderRunnerResult> RunAsync(
            AzureProviderRunnerCommand command,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The replay path must not invoke the provider runner.");
    }

    private sealed class InMemoryAssignmentStore : IAzureProviderResourceAssignmentStore
    {
        private AzureProviderResourceAssignment? _assignment;
        private readonly List<AzureProviderAssignmentRebindRecord> _rebinds = [];
        public Guid AssignmentId { get; init; } = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        public AzureProviderAssignmentState State { get; init; } = AzureProviderAssignmentState.Reserved;
        public Guid? LastOperationId { get; init; }
        public AzureProviderResourceReferences Resources { get; init; } = new();
        public AzureProviderOperationStatus? BlockingOperationStatus { get; init; }

        public Task<AzureProviderResourceAssignment> CreateOrGetAsync(
            AzureProviderResourceAssignmentRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            _assignment ??= new(
                AssignmentId,
                request.WorkspaceId,
                request.OrganizationId,
                request.InstanceId,
                request.ProviderScopeFingerprint,
                request.NamingVersion,
                request.SubscriptionId,
                AzureProviderResourceAssignmentNaming.ResourceGroupName(request.ResourceGroupNamePrefix, request.InstanceId, request.NamingVersion),
                request.WorkloadName,
                new string('f', 64),
                request.Location,
                State,
                Resources with { ResourceGroupName = AzureProviderResourceAssignmentNaming.ResourceGroupName(request.ResourceGroupNamePrefix, request.InstanceId, request.NamingVersion) },
                LastOperationId,
                1,
                now,
                now);
            return RebindIfNeededAsync(
                _assignment,
                request.ProviderScopeFingerprint,
                request.SubscriptionId,
                request.ResourceGroupNamePrefix,
                request.NamingVersion,
                request.Rebind,
                now);
        }

        public Task<AzureProviderResourceAssignment?> GetAsync(
            Guid workspaceId,
            Guid assignmentId,
            CancellationToken cancellationToken = default) => Task.FromResult(_assignment);

        public async Task<AzureProviderResourceAssignment?> RebindToCurrentScopeAsync(
            AzureProviderAssignmentScopeAuthority authority,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            _assignment is null || _assignment.State == AzureProviderAssignmentState.Deleted
                ? null
                : await RebindIfNeededAsync(
                    _assignment,
                    authority.ProviderScopeFingerprint,
                    authority.SubscriptionId,
                    authority.ResourceGroupNamePrefix,
                    authority.NamingVersion,
                    authority.Rebind,
                    now);

        public Task<bool> HasRebindLineageAsync(
            Guid workspaceId,
            Guid assignmentId,
            string fromProviderScopeFingerprint,
            string toProviderScopeFingerprint,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(fromProviderScopeFingerprint, toProviderScopeFingerprint, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(true);
            return Task.FromResult(_rebinds.Any(rebind =>
                rebind.AssignmentId == assignmentId &&
                string.Equals(rebind.FromProviderScopeFingerprint, fromProviderScopeFingerprint, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(rebind.ToProviderScopeFingerprint, toProviderScopeFingerprint, StringComparison.OrdinalIgnoreCase)));
        }

        public Task<IReadOnlyList<AzureProviderAssignmentRebindRecord>> ListRebindsAsync(
            Guid workspaceId,
            Guid assignmentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AzureProviderAssignmentRebindRecord>>(
                _rebinds.Where(rebind => rebind.AssignmentId == assignmentId).ToArray());

        private Task<AzureProviderResourceAssignment> RebindIfNeededAsync(
            AzureProviderResourceAssignment assignment,
            string providerScopeFingerprint,
            string subscriptionId,
            string resourceGroupNamePrefix,
            int namingVersion,
            AzureProviderAssignmentRebindContext? rebind,
            DateTimeOffset now)
        {
            if (string.Equals(assignment.ProviderScopeFingerprint, providerScopeFingerprint, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(assignment);

            var expectedGroup = AzureProviderResourceAssignmentNaming.ResourceGroupName(
                resourceGroupNamePrefix, assignment.InstanceId, namingVersion);
            if (!string.Equals(assignment.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(assignment.ResourceGroupName, expectedGroup, StringComparison.Ordinal))
                throw new AzureProviderAssignmentRebindException(
                    AzureProviderAssignmentRebindDiagnostics.PlacementMismatch,
                    "The Azure provider assignment cannot be rebound across a placement change.");
            if (BlockingOperationStatus is AzureProviderOperationStatus.Accepted or AzureProviderOperationStatus.Queued
                or AzureProviderOperationStatus.EntitlementHeld or AzureProviderOperationStatus.Running)
                throw new AzureProviderAssignmentRebindException(
                    AzureProviderAssignmentRebindDiagnostics.OperationsInFlight,
                    "The Azure provider assignment cannot be rebound until in-flight operations drain.");

            var from = assignment.ProviderScopeFingerprint;
            _assignment = assignment with
            {
                ProviderScopeFingerprint = providerScopeFingerprint,
                OwnershipKey = AzureProviderResourceAssignmentNaming.OwnershipKey(
                    assignment.Id, assignment.InstanceId, providerScopeFingerprint),
                Version = assignment.Version + 1,
                UpdatedAt = now
            };
            var trigger = rebind ?? new AzureProviderAssignmentRebindContext("azure-provider-assignment-store");
            _rebinds.Add(new(
                Guid.NewGuid(),
                assignment.Id,
                assignment.WorkspaceId,
                assignment.InstanceId,
                from,
                providerScopeFingerprint,
                trigger.TriggeredBy,
                trigger.TriggerOperationId,
                now));
            return Task.FromResult(_assignment);
        }
    }
}
