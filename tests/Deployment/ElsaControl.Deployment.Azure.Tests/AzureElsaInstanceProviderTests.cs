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
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.Ambiguous, "other-lifecycle")]
    [InlineData(false, AzureProviderOperationStatus.Running, false, ElsaInstanceCleanupObservationKind.Ambiguous, "other-lifecycle")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.Ambiguous, "wrong-action")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.Ambiguous, "invalid-retry")]
    [InlineData(true, AzureProviderOperationStatus.Succeeded, false, ElsaInstanceCleanupObservationKind.Ambiguous, "hashed-retry")]
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
    public async Task Cleanup_submits_or_reobserves_delete_and_confirms_only_verified_absence(
        bool alreadyDeleted, AzureProviderOperationStatus status, bool retainedResource, ElsaInstanceCleanupObservationKind expectedKind,
        string? correlation = null, string? observationVariant = null)
    {
        var workspaceId = Guid.NewGuid();
        var lifecycleOperationId = Guid.NewGuid();
        var plan = Translate("5.0", "5.0.0");
        plan = plan with { WorkloadName = AzureElsaInstanceProvider.WorkloadName(TestInstanceId) };
        var reconcile = CreateOperation(workspaceId, plan, Guid.NewGuid()) with
        {
            OrganizationId = TestOrganizationId,
            InstanceId = TestInstanceId,
            LifecycleAction = ElsaInstanceOperationAction.Reconcile,
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
        Assert.NotNull(AzureProviderOperationService.TryRestorePlan(reconcile));
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
            new string('a', 64),
            "11111111-1111-1111-1111-111111111111",
            "rg-elsa",
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
        if (alreadyDeleted)
            Assert.Empty(service.DeleteSubmissions);
        else
        {
            var submission = Assert.Single(service.DeleteSubmissions);
            Assert.Equal(ElsaInstanceOperationAction.Delete, submission.LifecycleAction);
            Assert.Equal(plan.Fingerprint, submission.Plan.Fingerprint);
            Assert.Equal(AzureElsaInstanceProvider.IdempotencyKey(lifecycleOperationId), submission.IdempotencyKey);
        }
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

    private static AzureElsaInstanceProviderOptions EnabledOptions() =>
        new()
        {
            Enabled = true,
            TemplateFingerprint = new string('b', 64),
            ProviderScopeFingerprint = new string('a', 64),
            SubscriptionId = "11111111-1111-1111-1111-111111111111",
            ResourceGroupNamePrefix = "rg-elsa"
        };

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

    private sealed record RecoveryFixture(
        AzureElsaInstanceProvider Provider,
        ElsaInstanceProviderRecoveryRequest Request,
        CapturingRecoveryObservationStore ObservationStore,
        CapturingOperationStore OperationStore,
        CountingRecoveryObserver Observer,
        InMemoryAssignmentStore AssignmentStore);

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
        public AzureProviderDeleteRecoveryAuthority? DeleteRecoveryAuthority { get; init; }

        public Task<AzureProviderOperation> CreateOrGetAsync(AzureProviderOperationRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<AzureProviderOperationCreateResult> CreateOrGetWithResultAsync(AzureProviderOperationRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            new(await CreateOrGetAsync(request, now, cancellationToken), Replayed: false);
        public Task<AzureProviderOperation?> GetAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new[] { completedDelete, operation }.FirstOrDefault(candidate =>
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
        public Task<AzureProviderOperation?> GetLatestReconcileAsync(Guid workspaceId, string targetKey, string? providerScopeFingerprint, CancellationToken cancellationToken = default) => Task.FromResult(operation);
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
            _assignment is null
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
