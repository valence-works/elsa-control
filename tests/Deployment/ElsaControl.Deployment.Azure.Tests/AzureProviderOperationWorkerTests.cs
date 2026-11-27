using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureProviderOperationWorkerTests
{
    [Fact]
    public async Task Unrestorable_plan_is_marked_terminal_instead_of_being_polled_forever()
    {
        var operation = Operation();
        var store = new WorkerStore(operation);
        var executor = new AzureProviderExecutor(store, new NeverCalledRunner());
        var worker = new AzureProviderOperationWorker(store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider());

        var processed = await worker.ProcessOnceAsync();

        Assert.Equal(0, processed);
        Assert.Equal(1, store.MarkUnrestorableCount);
        Assert.Equal(AzureProviderOperationStatus.Failed, store.Operation.Status);
        Assert.Contains(store.Transitions, transition => transition.Code == "azure.plan.unrestorable");
    }

    [Fact]
    public async Task Embedded_only_evidence_digest_is_marked_terminal_instead_of_reaching_execution()
    {
        var digest = "sha256:" + new string('f', 64);
        var operation = WithComputedMetadata(Operation() with
        {
            ReleaseManifestReference = $"oci://evidence.example/manifest@{digest}",
            ReleaseManifestSignatureReference = $"oci://evidence.example/signature@{digest}"
        });
        var store = new WorkerStore(operation);
        var executor = new AzureProviderExecutor(store, new NeverCalledRunner());
        var worker = new AzureProviderOperationWorker(store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider());

        var processed = await worker.ProcessOnceAsync();

        Assert.Equal(0, processed);
        Assert.Equal(1, store.MarkUnrestorableCount);
        Assert.Equal(AzureProviderOperationStatus.Failed, store.Operation.Status);
    }

    [Fact]
    public async Task Persisted_request_hash_mismatch_is_marked_terminal_before_execution()
    {
        var digest = "sha256:" + new string('f', 64);
        var operation = WithComputedMetadata(Operation() with
        {
            ReleaseManifestDigest = digest,
            ReleaseManifestSignatureDigest = digest,
            ReleaseManifestReference = "oci://evidence.example/manifest",
            ReleaseManifestSignatureReference = "oci://evidence.example/signature"
        });
        operation = operation with { RequestHash = new string('x', 64) };
        var store = new WorkerStore(operation);
        var executor = new AzureProviderExecutor(store, new NeverCalledRunner());
        var worker = new AzureProviderOperationWorker(store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider());

        var processed = await worker.ProcessOnceAsync();

        Assert.Equal(0, processed);
        Assert.Equal(1, store.MarkUnrestorableCount);
        Assert.Equal(AzureProviderOperationStatus.Failed, store.Operation.Status);
    }

    [Fact]
    public async Task Persisted_unsafe_resource_references_are_marked_terminal_before_execution()
    {
        var operation = Operation() with
        {
            Resources = new AzureProviderResourceReferences(WorkloadResourceId: "https://provider.example.test/resource?token=value")
        };
        var store = new WorkerStore(operation);
        var executor = new AzureProviderExecutor(store, new NeverCalledRunner());
        var worker = new AzureProviderOperationWorker(store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider());

        var processed = await worker.ProcessOnceAsync();

        Assert.Equal(0, processed);
        Assert.Equal(1, store.MarkUnrestorableCount);
        Assert.Equal(AzureProviderOperationStatus.Failed, store.Operation.Status);
    }

    [Fact]
    public async Task A_malformed_operation_does_not_starve_later_runnable_operations()
    {
        var digest = "sha256:" + new string('f', 64);
        var malformed = WithComputedMetadata(Operation() with
        {
            TargetKey = "malformed",
            ReleaseManifestReference = $"oci://evidence.example/manifest@{digest}",
            ReleaseManifestSignatureReference = $"oci://evidence.example/signature@{digest}"
        });
        var later = WithComputedMetadata(Operation() with
        {
            Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            TargetKey = "later",
            ReleaseManifestDigest = digest,
            ReleaseManifestSignatureDigest = digest,
            ReleaseManifestReference = "oci://evidence.example/manifest",
            ReleaseManifestSignatureReference = "oci://evidence.example/signature"
        });
        var store = new MultiOperationWorkerStore(malformed, later);
        var runner = new CompletingRunner();
        var executor = new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(later));
        var worker = new AzureProviderOperationWorker(store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider());

        var processed = await worker.ProcessOnceAsync();

        Assert.Equal(1, processed);
        Assert.Equal(1, store.MarkUnrestorableCount);
        Assert.Equal(AzureProviderOperationStatus.Failed, store.Operations[0].Status);
        Assert.Equal(AzureProviderOperationStatus.Succeeded, store.Operations[1].Status);
        Assert.Equal(1, store.Operations[1].AttemptNumber);
        Assert.Equal(9, runner.Steps.Count);
    }

    [Fact]
    public async Task Asynchronous_executor_failure_after_claim_is_not_misclassified_as_unrestorable()
    {
        var digest = "sha256:" + new string('f', 64);
        var operation = WithComputedMetadata(Operation() with
        {
            ReleaseManifestDigest = digest,
            ReleaseManifestSignatureDigest = digest,
            ReleaseManifestReference = "oci://evidence.example/manifest",
            ReleaseManifestSignatureReference = "oci://evidence.example/signature"
        });
        var store = new WorkerStore(operation, failAfterClaim: true);
        var executor = new AzureProviderExecutor(store, new NeverCalledRunner());
        var worker = new AzureProviderOperationWorker(store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ProcessOnceAsync());

        Assert.Equal(0, store.MarkUnrestorableCount);
        Assert.Equal(AzureProviderOperationStatus.Running, store.Operation.Status);
    }

    [Fact]
    public async Task Recovery_required_operations_wait_for_explicit_provider_observation()
    {
        var digest = "sha256:" + new string('f', 64);
        var operation = WithComputedMetadata(Operation() with
        {
            Status = AzureProviderOperationStatus.RecoveryRequired,
            AttemptNumber = 1,
            ReleaseManifestDigest = digest,
            ReleaseManifestSignatureDigest = digest,
            ReleaseManifestReference = "oci://evidence.example/manifest",
            ReleaseManifestSignatureReference = "oci://evidence.example/signature",
            SecretReferences = new Dictionary<string, string>
            {
                ["database:connectionstring"] = "secret://vault/database",
                ["identity:signingkey"] = "secret://vault/signingkey",
                ["admin:password"] = "secret://vault/admin"
            }
        });
        var store = new WorkerStore(operation, execute: true);
        var runner = new CompletingRunner();
        var executor = new AzureProviderExecutor(store, runner);
        var worker = new AzureProviderOperationWorker(store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider());

        var processed = await worker.ProcessOnceAsync();

        Assert.Equal(0, processed);
        Assert.Equal(0, store.MarkUnrestorableCount);
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, store.Operation.Status);
        Assert.Equal(1, store.Operation.AttemptNumber);
        Assert.Empty(runner.Steps);
        Assert.Empty(runner.Commands);
        Assert.Equal(operation.RequestHash, store.Operation.RequestHash);
    }

    [Fact]
    public async Task Accepted_operations_still_run_the_bicep_executor()
    {
        var digest = "sha256:" + new string('f', 64);
        var operation = WithComputedMetadata(Operation() with
        {
            ReleaseManifestDigest = digest,
            ReleaseManifestSignatureDigest = digest,
            ReleaseManifestReference = "oci://evidence.example/manifest",
            ReleaseManifestSignatureReference = "oci://evidence.example/signature"
        });
        var store = new WorkerStore(operation, execute: true);
        var runner = new CompletingRunner();
        var executor = new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(operation));
        var worker = new AzureProviderOperationWorker(store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider());

        var processed = await worker.ProcessOnceAsync();

        Assert.Equal(1, processed);
        Assert.NotEmpty(runner.Steps);
    }

    [Fact]
    public async Task Pulumi_owned_create_dispatches_and_never_calls_bicep()
    {
        var operation = RunnableOperation();
        var store = new WorkerStore(operation, execute: true);
        var runner = new CompletingRunner();
        var executor = new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(operation));
        var dispatcher = new RecordingDispatcher();
        var worker = new AzureProviderOperationWorker(
            store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider(),
            dispatcher, new StaticBackendStore(ElsaInstanceProvisioningBackend.Pulumi));

        var processed = await worker.ProcessOnceAsync();

        Assert.Equal(1, processed);
        Assert.Single(dispatcher.Operations);
        Assert.Equal(AzureProviderOperationAction.Reconcile, dispatcher.Operations[0].Action);
        Assert.Equal(ElsaInstanceOperationAction.Create, dispatcher.Operations[0].LifecycleAction);
        Assert.Empty(runner.Steps);
        Assert.Equal(AzureProviderOperationStatus.Accepted, store.Operation.Status);
        Assert.Null(store.Operation.WorkerId);
        Assert.Null(store.Operation.LeaseExpiresAt);
        Assert.Equal(AzureProviderOperationPhase.Planned, store.Operation.Phase);
        Assert.Equal(AzureProviderHealth.Unknown, store.Operation.Health);
    }

    [Fact]
    public async Task Pulumi_owned_delete_dispatches_destroy_and_never_calls_bicep()
    {
        var operation = WithComputedMetadata(RunnableOperation() with { Action = AzureProviderOperationAction.Delete });
        var store = new WorkerStore(operation, execute: true);
        var runner = new CompletingRunner();
        var executor = new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(operation));
        var dispatcher = new RecordingDispatcher();
        var worker = new AzureProviderOperationWorker(
            store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider(),
            dispatcher, new StaticBackendStore(ElsaInstanceProvisioningBackend.Pulumi));

        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Equal(AzureProviderOperationAction.Delete, Assert.Single(dispatcher.Operations).Action);
        Assert.Empty(runner.Steps);
    }

    [Fact]
    public async Task Pulumi_owned_retry_dispatches_provision_and_never_calls_bicep()
    {
        var operation = WithComputedMetadata(RunnableOperation() with { LifecycleAction = ElsaInstanceOperationAction.Retry });
        var store = new WorkerStore(operation, execute: true);
        var runner = new CompletingRunner();
        var executor = new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(operation));
        var dispatcher = new RecordingDispatcher();
        var worker = new AzureProviderOperationWorker(
            store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider(),
            dispatcher, new StaticBackendStore(ElsaInstanceProvisioningBackend.Pulumi));

        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Equal(AzureProviderOperationAction.Reconcile, Assert.Single(dispatcher.Operations).Action);
        Assert.Equal(ElsaInstanceOperationAction.Retry, dispatcher.Operations[0].LifecycleAction);
        Assert.Empty(runner.Steps);
    }

    [Fact]
    public async Task Pulumi_owned_retry_after_ready_fails_closed_without_pulumi_or_bicep()
    {
        var operation = WithComputedMetadata(RunnableOperation() with { LifecycleAction = ElsaInstanceOperationAction.Retry });
        await AssertPulumiRejectedAsync(operation, reachedReady: true);
    }

    [Theory]
    [InlineData(ElsaInstanceOperationAction.Reconcile)]
    [InlineData(ElsaInstanceOperationAction.UpdateIntent)]
    [InlineData(ElsaInstanceOperationAction.Start)]
    [InlineData(ElsaInstanceOperationAction.Stop)]
    [InlineData(ElsaInstanceOperationAction.Restart)]
    [InlineData(ElsaInstanceOperationAction.ApproveMinorUpgrade)]
    [InlineData(ElsaInstanceOperationAction.MajorMigration)]
    [InlineData(ElsaInstanceOperationAction.Recover)]
    public async Task Pulumi_owned_unsupported_operation_fails_closed_without_pulumi_or_bicep(
        ElsaInstanceOperationAction action)
    {
        var operation = WithComputedMetadata(RunnableOperation() with { LifecycleAction = action });
        await AssertPulumiRejectedAsync(operation);
    }

    [Fact]
    public async Task Pulumi_owned_reconcile_without_lifecycle_action_fails_closed_without_pulumi_or_bicep()
    {
        var operation = RunnableOperation() with { LifecycleAction = null };
        var store = new WorkerStore(operation, execute: true);
        var runner = new CompletingRunner();
        var dispatcher = new RecordingDispatcher();
        var worker = new AzureProviderOperationWorker(
            store,
            new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(operation)),
            new PersistedAzureProviderPlanSource(),
            new FixedTimeProvider(),
            dispatcher,
            new StaticBackendStore(ElsaInstanceProvisioningBackend.Pulumi));

        Assert.Equal(0, await worker.ProcessOnceAsync());
        Assert.Empty(dispatcher.Operations);
        Assert.Empty(runner.Steps);
        Assert.Equal(1, store.MarkUnrestorableCount);
        Assert.Equal(AzureProviderOperationStatus.Failed, store.Operation.Status);
    }

    [Fact]
    public async Task Bicep_owned_delete_never_calls_pulumi_even_with_dispatcher()
    {
        var operation = WithComputedMetadata(RunnableOperation() with { Action = AzureProviderOperationAction.Delete });
        var store = new WorkerStore(operation, execute: true);
        var runner = new CompletingRunner();
        var executor = new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(operation));
        var dispatcher = new RecordingDispatcher();
        var worker = new AzureProviderOperationWorker(
            store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider(),
            dispatcher, new StaticBackendStore(ElsaInstanceProvisioningBackend.Bicep));

        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Empty(dispatcher.Operations);
        Assert.NotEmpty(runner.Steps);
    }

    [Fact]
    public async Task Dispatch_failure_on_pulumi_instance_fails_closed_without_bicep()
    {
        var operation = RunnableOperation();
        var store = new WorkerStore(operation, execute: true);
        var runner = new CompletingRunner();
        var executor = new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(operation));
        var worker = new AzureProviderOperationWorker(
            store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider(),
            new FailingDispatcher(), new StaticBackendStore(ElsaInstanceProvisioningBackend.Pulumi));

        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Empty(runner.Steps);
        Assert.Equal(AzureProviderOperationStatus.Failed, store.Operation.Status);
        Assert.Contains(store.Transitions, transition => transition.Code == ManagedElsaReasonCodeCatalog.ProviderDispatchFailed);
    }

    [Fact]
    public async Task Pulumi_owned_create_holds_entitlement_without_dispatching()
    {
        var operation = RunnableOperation();
        var store = new WorkerStore(operation, execute: true);
        var runner = new CompletingRunner();
        var executor = new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(operation));
        var dispatcher = new RecordingDispatcher();
        var worker = new AzureProviderOperationWorker(
            store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider(),
            dispatcher, new StaticBackendStore(ElsaInstanceProvisioningBackend.Pulumi), new DenyingGate());

        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Empty(dispatcher.Operations);
        Assert.Empty(runner.Steps);
        Assert.Equal(AzureProviderOperationStatus.EntitlementHeld, store.Operation.Status);
        Assert.Contains(store.Transitions, transition => transition.Code == ElsaInstanceCommercialOperation.EntitlementRequired);
    }

    [Fact]
    public async Task Pulumi_owned_delete_skips_the_commercial_gate()
    {
        var operation = WithComputedMetadata(RunnableOperation() with { Action = AzureProviderOperationAction.Delete });
        var store = new WorkerStore(operation, execute: true);
        var runner = new CompletingRunner();
        var executor = new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(operation));
        var dispatcher = new RecordingDispatcher();
        var worker = new AzureProviderOperationWorker(
            store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider(),
            dispatcher, new StaticBackendStore(ElsaInstanceProvisioningBackend.Pulumi), new DenyingGate());

        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Equal(AzureProviderOperationAction.Delete, Assert.Single(dispatcher.Operations).Action);
        Assert.Empty(runner.Steps);
        Assert.Equal(AzureProviderOperationStatus.Accepted, store.Operation.Status);
        Assert.Null(store.Operation.WorkerId);
    }

    [Fact]
    public async Task Missing_dispatcher_on_pulumi_instance_fails_closed_without_bicep()
    {
        var operation = RunnableOperation();
        var store = new WorkerStore(operation, execute: true);
        var runner = new CompletingRunner();
        var executor = new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(operation));
        var worker = new AzureProviderOperationWorker(
            store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider(),
            pulumi: null, backends: new StaticBackendStore(ElsaInstanceProvisioningBackend.Pulumi));

        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Empty(runner.Steps);
        Assert.Equal(AzureProviderOperationStatus.Failed, store.Operation.Status);
        Assert.Contains(store.Transitions, transition => transition.Code == ManagedElsaReasonCodeCatalog.ProviderDispatchFailed);
    }

    [Fact]
    public async Task Unknown_owner_fails_closed_without_bicep()
    {
        var operation = RunnableOperation();
        var store = new WorkerStore(operation, execute: true);
        var runner = new CompletingRunner();
        var executor = new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(operation));
        var dispatcher = new RecordingDispatcher();
        var worker = new AzureProviderOperationWorker(
            store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider(),
            dispatcher, new StaticBackendStore(ElsaInstanceProvisioningBackend.Unknown));

        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Empty(dispatcher.Operations);
        Assert.Empty(runner.Steps);
        Assert.Equal(AzureProviderOperationStatus.Failed, store.Operation.Status);
        Assert.Contains(store.Transitions, transition => transition.Code == ManagedElsaReasonCodeCatalog.ProviderDispatchFailed);
    }

    [Fact]
    public async Task Two_queued_pulumi_creates_stay_accepted_across_a_stale_sweep()
    {
        var first = RunnableOperation();
        var second = WithComputedMetadata(RunnableOperation() with
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaab"),
            TargetKey = "workload-b",
            IdempotencyKey = "request-2"
        });
        var store = new MultiOperationWorkerStore(first, second);
        var runner = new CompletingRunner();
        var executor = new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(first));
        var dispatcher = new RecordingDispatcher();
        var now = DateTimeOffset.UtcNow;
        var worker = new AzureProviderOperationWorker(
            store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider(),
            dispatcher, new StaticBackendStore(ElsaInstanceProvisioningBackend.Pulumi));

        Assert.Equal(2, await worker.ProcessOnceAsync());
        Assert.Equal(2, dispatcher.Operations.Count);
        Assert.Empty(runner.Steps);
        Assert.All(store.Operations, operation =>
        {
            Assert.Equal(AzureProviderOperationStatus.Accepted, operation.Status);
            Assert.Null(operation.WorkerId);
            Assert.Null(operation.LeaseExpiresAt);
        });

        Assert.Equal(0, await store.RecoverStaleAsync(now.AddSeconds(31)));
        Assert.All(store.Operations, operation =>
            Assert.Equal(AzureProviderOperationStatus.Accepted, operation.Status));
        Assert.DoesNotContain(
            store.Operations,
            operation => operation.Status == AzureProviderOperationStatus.RecoveryRequired);
    }

    [Fact]
    public async Task Dispatched_pulumi_create_is_not_re_enqueued_on_a_new_worker_scope()
    {
        var operation = RunnableOperation();
        var store = new WorkerStore(operation, execute: true);
        var runner = new CompletingRunner();
        var dispatcher = new RecordingDispatcher();
        var backends = new StaticBackendStore(ElsaInstanceProvisioningBackend.Pulumi);
        var first = new AzureProviderOperationWorker(
            store,
            new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(operation)),
            new PersistedAzureProviderPlanSource(),
            new FixedTimeProvider(),
            dispatcher,
            backends);

        Assert.Equal(1, await first.ProcessOnceAsync());
        Assert.Single(dispatcher.Operations);

        var second = new AzureProviderOperationWorker(
            store,
            new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(operation)),
            new PersistedAzureProviderPlanSource(),
            new FixedTimeProvider(),
            dispatcher,
            backends);
        Assert.Equal(1, await second.ProcessOnceAsync());
        Assert.Single(dispatcher.Operations);
        Assert.Empty(runner.Steps);
        Assert.Equal(AzureProviderOperationStatus.Accepted, store.Operation.Status);
    }

    [Fact]
    public async Task Pulumi_apply_success_reaches_ready_through_health_then_observation()
    {
        var lifecycleOperationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var instanceId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var appName = ProvisioningEngineContainerApp.ForInstance(instanceId);
        var resourceGroup = ProvisioningEngineResourceGroup.ForInstance(instanceId);
        var endpoint = ProvisioningEngineIngress.OriginForInstance(
            instanceId,
            ProvisioningEngineIngress.MockEnvironmentDomain);
        var revision = appName + "--candidate";
        var operation = WithComputedMetadata(RunnableOperation() with
        {
            Status = AzureProviderOperationStatus.Queued,
            Phase = AzureProviderOperationPhase.WorkloadReady,
            Endpoint = endpoint,
            Resources = new AzureProviderResourceReferences(
                ResourceGroupName: resourceGroup,
                WorkloadRevisionName: revision,
                WorkloadResourceId: $"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/{resourceGroup}/providers/Microsoft.App/containerApps/{appName}"),
            IdempotencyKey = AzureProviderOperationValidation.LifecycleIdempotencyKey(lifecycleOperationId),
            TargetKey = AzureElsaInstanceProvider.WorkloadName(instanceId),
            InstanceId = instanceId,
            ProviderScopeFingerprint = new string('a', 64)
        });
        var store = new WorkerStore(operation, execute: true);
        var runner = new CompletingRunner();
        var executor = new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(operation));
        var dispatcher = new RecordingDispatcher();
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("--retry") && args[^1].EndsWith("/health", StringComparison.Ordinal), "Healthy");
        process.Success(
            args => args.Contains("containerapp") && args.Contains("show") && args.Contains(appName) && args.Contains(resourceGroup),
            """[{"latestRevision":true,"weight":100}]""");
        var worker = new AzureProviderOperationWorker(
            store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider(),
            dispatcher, new StaticBackendStore(ElsaInstanceProvisioningBackend.Pulumi),
            commercialGate: null,
            readiness: new AzurePulumiReadinessVerifier(ReadinessOptions(), ReadinessScope(), store, process));

        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Empty(dispatcher.Operations);
        Assert.Empty(runner.Steps);
        Assert.Contains(
            process.Calls,
            args => args.Contains("--retry")
                    && args.Contains("30")
                    && args.Contains("--retry-all-errors")
                    && args.Contains("--retry-delay")
                    && args.Contains("--max-time"));
        Assert.DoesNotContain(process.Calls, args => args.Contains("traffic") && args.Contains("set"));
        Assert.Equal(AzureProviderOperationStatus.Succeeded, store.Operation.Status);
        Assert.Equal(AzureProviderOperationPhase.TrafficPromoted, store.Operation.Phase);
        Assert.Equal(AzureProviderHealth.Healthy, store.Operation.Health);
        Assert.Equal(endpoint, store.Operation.Endpoint);

        var provider = new AzureElsaInstanceProvider(
            new UnusedOperationService(),
            store,
            new OperationAssignmentStore(store.Operation),
            options: new AzureElsaInstanceProviderOptions
            {
                Enabled = true,
                TemplateFingerprint = new string('b', 64),
                ProviderScopeFingerprint = new string('a', 64),
                SubscriptionId = "11111111-1111-1111-1111-111111111111",
                ResourceGroupNamePrefix = "rg-elsa"
            });
        var observation = await provider.ObserveAsync(new(
            store.Operation.WorkspaceId,
            instanceId,
            lifecycleOperationId,
            store.Operation.AttemptNumber,
            ElsaDesiredLifecycle.Running,
            null,
            null));

        Assert.Equal(ElsaInstanceProviderObservationKind.Confirmed, observation.Kind);
        Assert.Equal(ElsaObservedLifecycle.Ready, observation.ObservedLifecycle);
        Assert.Equal(ElsaInstanceProviderHealthGate.Passed, observation.HealthGate);
        Assert.Equal(endpoint, observation.CurrentDeploymentReference?.EndpointUri);
    }

    [Fact]
    public async Task Pulumi_stack_outputs_through_the_ledger_reach_ready()
    {
        var lifecycleOperationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var instanceId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var appName = ProvisioningEngineContainerApp.ForInstance(instanceId);
        var resourceGroup = ProvisioningEngineResourceGroup.ForInstance(instanceId);
        var endpoint = ProvisioningEngineIngress.OriginForInstance(
            instanceId,
            ProvisioningEngineIngress.MockEnvironmentDomain);
        var revision = appName + "--candidate";
        var resourceId = $"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/{resourceGroup}/providers/Microsoft.App/containerApps/{appName}";
        var operation = WithComputedMetadata(RunnableOperation() with
        {
            IdempotencyKey = AzureProviderOperationValidation.LifecycleIdempotencyKey(lifecycleOperationId),
            TargetKey = AzureElsaInstanceProvider.WorkloadName(instanceId),
            InstanceId = instanceId,
            ProviderScopeFingerprint = new string('a', 64)
        });
        var store = new WorkerStore(operation, execute: true);
        var ledger = new AzureProviderOperationStepLedger(store);
        var (moduleOperation, request) = Bound(operation.WorkspaceId, operation.Id, instanceId);
        Assert.Equal(
            ProvisioningStepLedgerResult.Written,
            await ledger.TryWriteAsync(
                moduleOperation,
                request,
                [
                    Event(moduleOperation.Id, 1, ProvisioningEventKind.OperationSucceeded, ProvisioningCapability.ContainerRuntime)
                ],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["engineEndpoint"] = endpoint,
                    ["workloadRevisionName"] = revision,
                    ["workloadResourceId"] = resourceId,
                    ["ownedResourceGroupName"] = resourceGroup
                }));
        Assert.Equal(AzureProviderOperationStatus.Queued, store.Operation.Status);
        Assert.Equal(AzureProviderOperationPhase.WorkloadReady, store.Operation.Phase);
        Assert.Equal(endpoint, store.Operation.Endpoint);

        var runner = new CompletingRunner();
        var dispatcher = new RecordingDispatcher();
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("--retry") && args[^1].EndsWith("/health", StringComparison.Ordinal), "Healthy");
        process.Success(
            args => args.Contains("containerapp") && args.Contains("show") && args.Contains(appName) && args.Contains(resourceGroup),
            """[{"latestRevision":true,"weight":100}]""");
        var worker = new AzureProviderOperationWorker(
            store,
            new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(store.Operation)),
            new PersistedAzureProviderPlanSource(),
            new FixedTimeProvider(),
            dispatcher,
            new StaticBackendStore(ElsaInstanceProvisioningBackend.Pulumi),
            commercialGate: null,
            readiness: new AzurePulumiReadinessVerifier(ReadinessOptions(), ReadinessScope(), store, process));

        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Empty(dispatcher.Operations);
        Assert.Empty(runner.Steps);
        Assert.Equal(AzureProviderOperationStatus.Succeeded, store.Operation.Status);
        Assert.Equal(AzureProviderOperationPhase.TrafficPromoted, store.Operation.Phase);
        Assert.Equal(AzureProviderHealth.Healthy, store.Operation.Health);

        var observation = await new AzureElsaInstanceProvider(
            new UnusedOperationService(),
            store,
            new OperationAssignmentStore(store.Operation),
            options: new AzureElsaInstanceProviderOptions
            {
                Enabled = true,
                TemplateFingerprint = new string('b', 64),
                ProviderScopeFingerprint = new string('a', 64),
                SubscriptionId = "11111111-1111-1111-1111-111111111111",
                ResourceGroupNamePrefix = "rg-elsa"
            }).ObserveAsync(new(
            store.Operation.WorkspaceId,
            instanceId,
            lifecycleOperationId,
            store.Operation.AttemptNumber,
            ElsaDesiredLifecycle.Running,
            null,
            null));
        Assert.Equal(ElsaObservedLifecycle.Ready, observation.ObservedLifecycle);
        Assert.Equal(endpoint, observation.CurrentDeploymentReference?.EndpointUri);
    }

    [Fact]
    public async Task Pulumi_revision_label_host_fails_closed_before_health()
    {
        var instanceId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var appName = ProvisioningEngineContainerApp.ForInstance(instanceId);
        await AssertEndpointRejectedAsync(
            instanceId,
            ProvisioningEngineIngress.RevisionOrigin(
                appName,
                "candidate",
                ProvisioningEngineIngress.MockEnvironmentDomain));
    }

    [Fact]
    public async Task Pulumi_non_container_apps_host_fails_closed_before_health()
    {
        await AssertEndpointRejectedAsync(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            "https://runtime.example.test");
    }

    private static AzureProviderOperation RunnableOperation()
    {
        var digest = "sha256:" + new string('f', 64);
        return WithComputedMetadata(Operation() with
        {
            ReleaseManifestDigest = digest,
            ReleaseManifestSignatureDigest = digest,
            ReleaseManifestReference = "oci://evidence.example/manifest",
            ReleaseManifestSignatureReference = "oci://evidence.example/signature"
        });
    }

    private static AzureProviderOperation Operation()
    {
        var operation = new AzureProviderOperation(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "workload-a",
            AzureProviderOperationAction.Reconcile,
            "request-1",
            new('a', 64),
            new('b', 64),
            new('c', 64),
            new('d', 64),
            "3.8.0",
            "3.8",
            "combined",
            "Dedicated",
            "westeurope",
            "valenceruntimeimages.azurecr.io/runtime-combined",
            "sha256:" + new string('e', 64),
            null,
            null,
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
            null,
            null,
            new Dictionary<string, string>(),
            SqlWorkflowPackageVersion: "3.8.0-preview.5413",
            SqlQuartzPackageVersion: "3.8.0-preview.5413");
        return WithComputedMetadata(operation with
        {
            OrganizationId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            InstanceId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            LifecycleAction = ElsaInstanceOperationAction.Create,
            ProviderAssignmentId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee")
        });
    }

    private static async Task AssertEndpointRejectedAsync(Guid instanceId, string endpoint)
    {
        var appName = ProvisioningEngineContainerApp.ForInstance(instanceId);
        var resourceGroup = ProvisioningEngineResourceGroup.ForInstance(instanceId);
        var operation = WithComputedMetadata(RunnableOperation() with
        {
            Status = AzureProviderOperationStatus.Queued,
            Phase = AzureProviderOperationPhase.WorkloadReady,
            Endpoint = endpoint,
            Resources = new AzureProviderResourceReferences(
                ResourceGroupName: resourceGroup,
                WorkloadRevisionName: appName + "--candidate",
                WorkloadResourceId: $"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/{resourceGroup}/providers/Microsoft.App/containerApps/{appName}"),
            InstanceId = instanceId,
            ProviderScopeFingerprint = new string('a', 64)
        });
        var store = new WorkerStore(operation, execute: true);
        var runner = new CompletingRunner();
        var dispatcher = new RecordingDispatcher();
        var process = new FakeCommandProcess();
        var worker = new AzureProviderOperationWorker(
            store,
            new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(operation)),
            new PersistedAzureProviderPlanSource(),
            new FixedTimeProvider(),
            dispatcher,
            new StaticBackendStore(ElsaInstanceProvisioningBackend.Pulumi),
            commercialGate: null,
            readiness: new AzurePulumiReadinessVerifier(ReadinessOptions(), ReadinessScope(), store, process));

        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Empty(dispatcher.Operations);
        Assert.Empty(runner.Steps);
        Assert.Empty(process.Calls);
        Assert.Equal(AzureProviderOperationStatus.Failed, store.Operation.Status);
        Assert.Contains(
            store.Transitions,
            transition => transition.Code == ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadOutputsInvalid);
    }

    private static async Task AssertPulumiRejectedAsync(
        AzureProviderOperation operation,
        bool reachedReady = false)
    {
        var store = new WorkerStore(operation, execute: true, reachedReady: reachedReady);
        var runner = new CompletingRunner();
        var executor = new AzureProviderExecutor(store, runner, assignmentStore: new OperationAssignmentStore(operation));
        var dispatcher = new RecordingDispatcher();
        var phase = store.Operation.Phase;
        var health = store.Operation.Health;
        var endpoint = store.Operation.Endpoint;
        var resources = store.Operation.Resources;
        var worker = new AzureProviderOperationWorker(
            store, executor, new PersistedAzureProviderPlanSource(), new FixedTimeProvider(),
            dispatcher, new StaticBackendStore(ElsaInstanceProvisioningBackend.Pulumi));

        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Empty(dispatcher.Operations);
        Assert.Empty(runner.Steps);
        Assert.Equal(AzureProviderOperationStatus.Failed, store.Operation.Status);
        Assert.Contains(
            store.Transitions,
            transition => transition.Code == ManagedElsaReasonCodeCatalog.ProviderPulumiOperationUnsupported);
        Assert.Equal(phase, store.Operation.Phase);
        Assert.Equal(health, store.Operation.Health);
        Assert.Equal(endpoint, store.Operation.Endpoint);
        Assert.Equal(resources, store.Operation.Resources);
    }

    private static AzureProviderRunnerOptions ReadinessOptions()
    {
        var tool = OperatingSystem.IsWindows() ? @"C:\Windows\System32\cmd.exe" : "/bin/sh";
        return new AzureProviderRunnerOptions
        {
            AzureCliPath = tool,
            CurlPath = tool,
            ObservationAttempts = 1,
            ObservationDelay = TimeSpan.Zero
        };
    }

    private static AzureProviderTargetScope ReadinessScope() => new(
        "11111111-1111-1111-1111-111111111111",
        "proof-rg",
        "22222222-2222-2222-2222-222222222222",
        "registry-rg",
        "valenceruntimeimages",
        "westeurope");

    private static (ProvisioningOperation Operation, ProvisioningRequest Request) Bound(
        Guid workspaceId,
        Guid providerOperationId,
        Guid instanceId)
    {
        var operation = new ProvisioningOperation(
            Guid.NewGuid(),
            ProvisioningCommand.Provision,
            ProvisioningOperationState.Running,
            "azure",
            ProvisioningStackKind.Engine,
            ProvisioningStackName.ForEngine(instanceId),
            "pulumi-ledger",
            "hash",
            DateTimeOffset.UtcNow)
        {
            FencingToken = 1
        };
        var request = new ProvisioningRequest(
            ProvisioningCommand.Provision,
            new EngineSpec(
                "elsa-server:test",
                new HashSet<string>(StringComparer.Ordinal) { ProvisioningCapability.ContainerRuntime }),
            new ProvisioningTarget(
                "azure",
                ProvisioningStackKind.Engine,
                operation.StackName,
                new CredentialReference(CredentialReference.AzureWorkloadIdentityKind, "workload-identity"),
                new StateBackendReference(StateBackendReference.AzureBlobKind, "stelsaplatform/pulumi-state"),
                new SecretsProviderReference(SecretsProviderReference.AzureKeyVaultKind, "kv-elsa-platform/pulumi-secrets"),
                "staging"),
            "pulumi-ledger",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PulumiStepLedgerMapper.ControlWorkspaceIdKey] = workspaceId.ToString("D"),
                [PulumiStepLedgerMapper.ControlProviderOperationIdKey] = providerOperationId.ToString("D")
            });
        return (operation, request);
    }

    private static ProvisioningEvent Event(
        Guid operationId,
        int sequence,
        ProvisioningEventKind kind,
        string resourceKind) =>
        new(
            operationId,
            kind,
            resourceKind,
            "Resource succeeded.",
            DateTimeOffset.UtcNow,
            sequence);

    private static AzureProviderOperation WithComputedMetadata(AzureProviderOperation operation)
    {
        var request = new AzureProviderOperationRequest(
            operation.WorkspaceId,
            operation.TargetKey,
            operation.Action,
            operation.IdempotencyKey,
            operation.PlanFingerprint,
            operation.TemplateFingerprint,
            operation.ElsaVersion,
            operation.ReleaseLine,
            operation.Topology,
            operation.Isolation,
            operation.Location,
            operation.ImageRepository,
            operation.ImageDigest,
            operation.ReleaseManifestDigest,
            operation.ReleaseManifestSignatureDigest,
            operation.ReleaseManifestReference,
            operation.ReleaseManifestSignatureReference,
            operation.SafeSecretReferences,
            operation.ProviderScopeFingerprint,
            operation.SqlWorkflowPackageVersion,
            operation.SqlQuartzPackageVersion,
            operation.OrganizationId,
            operation.InstanceId,
            operation.LifecycleAction,
            operation.ProviderAssignmentId);
        return operation with
        {
            RequestHash = AzureProviderOperationValidation.ComputeRequestHash(request),
            OperationIdentity = AzureProviderOperationValidation.ComputeOperationIdentity(request)
        };
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
    }

    private sealed class NeverCalledRunner : IAzureProviderRunner
    {
        public Task<AzureProviderRunnerResult> RunAsync(AzureProviderRunnerCommand command, CancellationToken cancellationToken = default) =>
            throw new Xunit.Sdk.XunitException("The provider runner must not be called for an unrestorable plan.");
    }

    private sealed class CompletingRunner : IAzureProviderRunner
    {
        public List<AzureProviderRunnerStep> Steps { get; } = [];
        public List<AzureProviderRunnerCommand> Commands { get; } = [];

        public Task<AzureProviderRunnerResult> RunAsync(AzureProviderRunnerCommand command, CancellationToken cancellationToken = default)
        {
            Steps.Add(command.Step);
            Commands.Add(command);
            var phase = command.Step switch
            {
                AzureProviderRunnerStep.Foundation => AzureProviderOperationPhase.FoundationSubmitted,
                AzureProviderRunnerStep.AcrPull or AzureProviderRunnerStep.SeedSecrets => AzureProviderOperationPhase.FoundationSubmitted,
                AzureProviderRunnerStep.SqlBootstrap => AzureProviderOperationPhase.FoundationReady,
                AzureProviderRunnerStep.SqlFirewallCreate => AzureProviderOperationPhase.SqlFirewallReady,
                AzureProviderRunnerStep.SqlBootstrapScript => AzureProviderOperationPhase.SqlBootstrapReady,
                AzureProviderRunnerStep.SqlFirewallCleanup => AzureProviderOperationPhase.FoundationReady,
                AzureProviderRunnerStep.Workload => AzureProviderOperationPhase.WorkloadReady,
                AzureProviderRunnerStep.Health => AzureProviderOperationPhase.HealthVerified,
                AzureProviderRunnerStep.Promotion => AzureProviderOperationPhase.TrafficPromoted,
                _ => AzureProviderOperationPhase.CleanupVerified
            };
            var hasEndpoint = command.Step is AzureProviderRunnerStep.Health or AzureProviderRunnerStep.Promotion;
            var resources = CompleteResources();
            return Task.FromResult(new AzureProviderRunnerResult(
                AzureProviderRunnerOutcome.Completed,
                phase,
                resources,
                hasEndpoint ? AzureProviderHealth.Healthy : AzureProviderHealth.Unknown,
                hasEndpoint ? "https://workload.example.test" : null,
                [],
                "azure.step.completed",
                "Completed."));
        }

        private static AzureProviderResourceReferences CompleteResources() => new(
            ResourceGroupName: "test",
            FoundationDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/test/providers/Microsoft.Resources/deployments/foundation",
            WorkloadDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/test/providers/Microsoft.Resources/deployments/workload",
            WorkloadResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/test/providers/Microsoft.App/containerApps/workload-a",
            WorkloadRevisionName: "workload-a--candidate",
            StableTrafficRevisionName: "workload-a--stable",
            WorkloadIdentityResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/workload-a",
            WorkloadIdentityClientId: "22222222-2222-2222-2222-222222222222",
            WorkloadIdentityPrincipalId: "33333333-3333-3333-3333-333333333333",
            KeyVaultResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/test/providers/Microsoft.KeyVault/vaults/workload-a",
            KeyVaultUri: "https://workload-a.vault.azure.net/",
            SqlServerResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/test/providers/Microsoft.Sql/servers/workload-a",
            SqlServerFqdn: "workload-a.database.windows.net",
            ContainerAppsEnvironmentResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/test/providers/Microsoft.App/managedEnvironments/workload-a",
            RegistryResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/registry/providers/Microsoft.ContainerRegistry/registries/registry",
            AcrPullDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/registry/providers/Microsoft.Resources/deployments/acr-pull",
            AcrPullRoleAssignmentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/registry/providers/Microsoft.ContainerRegistry/registries/registry/providers/Microsoft.Authorization/roleAssignments/44444444-4444-4444-4444-444444444444");
    }

    private sealed class WorkerStore(
        AzureProviderOperation operation,
        bool failAfterClaim = false,
        bool execute = false,
        bool reachedReady = false) : IAzureProviderOperationStore
    {
        public AzureProviderOperation Operation { get; private set; } = operation;
        public List<AzureProviderOperationTransition> Transitions { get; } = [];
        public int MarkUnrestorableCount { get; private set; }
        private readonly bool _execute = execute;

        public async Task<AzureProviderOperationCreateResult> CreateOrGetWithResultAsync(
            AzureProviderOperationRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            new(await CreateOrGetAsync(request, now, cancellationToken), Replayed: false);

        public Task<AzureProviderOperation> CreateOrGetAsync(AzureProviderOperationRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(Operation);

        public Task<AzureProviderOperation?> GetAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureProviderOperation?>(Operation);

        public Task<IReadOnlyList<AzureProviderOperation>> ListRunnableAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AzureProviderOperation>>(Operation.Status is AzureProviderOperationStatus.Accepted or AzureProviderOperationStatus.Queued ? [Operation] : []);

        public Task<bool> HasReachedReadyAsync(
            Guid workspaceId,
            Guid instanceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(reachedReady);

        public Task<AzureProviderOperation?> GetLatestReconcileAsync(Guid workspaceId, string targetKey, string? providerScopeFingerprint, CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureProviderOperation?>(
                Operation.Action == AzureProviderOperationAction.Reconcile
                && string.Equals(Operation.TargetKey, targetKey, StringComparison.OrdinalIgnoreCase)
                    ? Operation
                    : null);

        public Task<AzureProviderOperation?> MarkUnrestorableAsync(Guid workspaceId, Guid operationId, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            MarkUnrestorableCount++;
            if (Operation.Status is not (AzureProviderOperationStatus.Accepted or AzureProviderOperationStatus.Queued or AzureProviderOperationStatus.RecoveryRequired) || expectedVersion.HasValue && expectedVersion.Value != Operation.Version)
                return Task.FromResult<AzureProviderOperation?>(null);

            Operation = Operation with { Status = AzureProviderOperationStatus.Failed, CompletedAt = now, UpdatedAt = now, Version = Operation.Version + 1 };
            Transitions.Add(new(Operation.Id, Operation.Id, Transitions.Count + 1, Operation.Status, Operation.Phase, "azure.plan.unrestorable", "azure.plan.unrestorable", now));
            return Task.FromResult<AzureProviderOperation?>(Operation);
        }

        public Task<AzureProviderOperation?> ClaimAsync(Guid workspaceId, Guid operationId, string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            if (!failAfterClaim)
                return _execute
                    ? ClaimCore(workerId, leaseToken, leaseDuration, now, expectedVersion, allowRecovery: false)
                    : Task.FromResult<AzureProviderOperation?>(null);

            Operation = Operation with { Status = AzureProviderOperationStatus.Running, Version = Operation.Version + 1 };
            return Task.FromException<AzureProviderOperation?>(new InvalidOperationException("The persisted claim failed after ownership was recorded."));
        }

        public Task<AzureProviderOperation?> ClaimRecoveryAsync(Guid workspaceId, Guid operationId, string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
            _execute
                ? ClaimCore(workerId, leaseToken, leaseDuration, now, expectedVersion, allowRecovery: true)
                : Task.FromResult<AzureProviderOperation?>(null);

        public Task<AzureProviderOperation?> HeartbeatAsync(Guid workspaceId, Guid operationId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
            _execute
                ? Task.FromResult<AzureProviderOperation?>(Operation = Operation with { Version = Operation.Version + 1, UpdatedAt = now, HeartbeatAt = now, LeaseExpiresAt = now.Add(leaseDuration) })
                : Task.FromResult<AzureProviderOperation?>(null);

        public Task<AzureProviderOperation?> CheckpointAsync(Guid workspaceId, Guid operationId, string leaseToken, AzureProviderCheckpoint checkpoint, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
            _execute && Operation.Status == AzureProviderOperationStatus.Running &&
            (!expectedVersion.HasValue || expectedVersion.Value == Operation.Version)
                ? Task.FromResult<AzureProviderOperation?>(Operation = Operation with
                {
                    Phase = checkpoint.Phase,
                    Resources = checkpoint.Resources,
                    Endpoint = checkpoint.Endpoint,
                    Health = checkpoint.Health,
                    Diagnostics = checkpoint.Diagnostics,
                    CheckpointSequence = Operation.CheckpointSequence + 1,
                    Version = Operation.Version + 1,
                    UpdatedAt = now
                })
                : Task.FromResult<AzureProviderOperation?>(null);

        public Task<AzureProviderOperation?> FinalizeAsync(Guid workspaceId, Guid operationId, string leaseToken, AzureProviderOperationStatus status, string code, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            if (!_execute
                || Operation.Status != AzureProviderOperationStatus.Running
                || expectedVersion.HasValue && expectedVersion.Value != Operation.Version)
            {
                return Task.FromResult<AzureProviderOperation?>(null);
            }

            Operation = Operation with
            {
                Status = status,
                CompletedAt = status is AzureProviderOperationStatus.RecoveryRequired or AzureProviderOperationStatus.EntitlementHeld ? null : now,
                Version = Operation.Version + 1,
                UpdatedAt = now,
                WorkerId = null,
                LeaseExpiresAt = null
            };
            Transitions.Add(new(
                Operation.Id,
                Operation.Id,
                Transitions.Count + 1,
                status,
                Operation.Phase,
                code,
                code,
                now));
            return Task.FromResult<AzureProviderOperation?>(Operation);
        }

        public Task<AzureProviderOperation?> ReleaseToQueueAsync(
            Guid workspaceId,
            Guid operationId,
            string leaseToken,
            DateTimeOffset now,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            if (!_execute
                || Operation.Status != AzureProviderOperationStatus.Running
                || expectedVersion.HasValue && expectedVersion.Value != Operation.Version)
            {
                return Task.FromResult<AzureProviderOperation?>(null);
            }

            Operation = Operation with
            {
                Status = AzureProviderOperationStatus.Queued,
                Version = Operation.Version + 1,
                UpdatedAt = now,
                WorkerId = null,
                LeaseExpiresAt = null,
                CompletedAt = null
            };
            return Task.FromResult<AzureProviderOperation?>(Operation);
        }

        public Task<int> RecoverStaleAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            if (Operation.Status != AzureProviderOperationStatus.Running
                || Operation.LeaseExpiresAt is not { } expires
                || expires > now)
            {
                return Task.FromResult(0);
            }

            Operation = Operation with
            {
                Status = AzureProviderOperationStatus.RecoveryRequired,
                WorkerId = null,
                LeaseExpiresAt = null,
                Version = Operation.Version + 1,
                UpdatedAt = now
            };
            return Task.FromResult(1);
        }

        public Task<IReadOnlyList<AzureProviderOperationTransition>> ListTransitionsAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AzureProviderOperationTransition>>(Transitions);

        private Task<AzureProviderOperation?> ClaimCore(
            string workerId,
            string leaseToken,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            long? expectedVersion,
            bool allowRecovery)
        {
            if (Operation.Status is not (AzureProviderOperationStatus.Accepted or AzureProviderOperationStatus.Queued or AzureProviderOperationStatus.RecoveryRequired) ||
                allowRecovery && Operation.Status != AzureProviderOperationStatus.RecoveryRequired ||
                !allowRecovery && Operation.Status == AzureProviderOperationStatus.RecoveryRequired ||
                expectedVersion.HasValue && expectedVersion.Value != Operation.Version)
                return Task.FromResult<AzureProviderOperation?>(null);

            Operation = Operation with
            {
                Status = AzureProviderOperationStatus.Running,
                AttemptNumber = Operation.AttemptNumber + 1,
                Version = Operation.Version + 1,
                WorkerId = workerId,
                LeaseExpiresAt = now.Add(leaseDuration),
                HeartbeatAt = now,
                UpdatedAt = now
            };
            return Task.FromResult<AzureProviderOperation?>(Operation);
        }
    }

    private sealed class MultiOperationWorkerStore(params AzureProviderOperation[] operations) : IAzureProviderOperationStore
    {
        public List<AzureProviderOperation> Operations { get; } = operations.ToList();
        public int MarkUnrestorableCount { get; private set; }

        public async Task<AzureProviderOperationCreateResult> CreateOrGetWithResultAsync(
            AzureProviderOperationRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            new(await CreateOrGetAsync(request, now, cancellationToken), Replayed: false);

        public Task<AzureProviderOperation> CreateOrGetAsync(AzureProviderOperationRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(Operations.Single(operation => operation.TargetKey == request.TargetKey));

        public Task<AzureProviderOperation?> GetAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Operations.SingleOrDefault(operation => operation.WorkspaceId == workspaceId && operation.Id == operationId));

        public Task<IReadOnlyList<AzureProviderOperation>> ListRunnableAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AzureProviderOperation>>(Operations.Where(operation => operation.Status is AzureProviderOperationStatus.Accepted or AzureProviderOperationStatus.Queued).ToList());

        public Task<bool> HasReachedReadyAsync(
            Guid workspaceId,
            Guid instanceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<AzureProviderOperation?> GetLatestReconcileAsync(Guid workspaceId, string targetKey, string? providerScopeFingerprint, CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureProviderOperation?>(null);

        public Task<AzureProviderOperation?> MarkUnrestorableAsync(Guid workspaceId, Guid operationId, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            MarkUnrestorableCount++;
            var index = Operations.FindIndex(operation => operation.WorkspaceId == workspaceId && operation.Id == operationId &&
                operation.Status is AzureProviderOperationStatus.Accepted or AzureProviderOperationStatus.Queued or AzureProviderOperationStatus.RecoveryRequired &&
                (!expectedVersion.HasValue || operation.Version == expectedVersion.Value));
            if (index < 0)
                return Task.FromResult<AzureProviderOperation?>(null);

            var updated = Operations[index] with
            {
                Status = AzureProviderOperationStatus.Failed,
                CompletedAt = now,
                UpdatedAt = now,
                Version = Operations[index].Version + 1
            };
            Operations[index] = updated;
            return Task.FromResult<AzureProviderOperation?>(updated);
        }

        public Task<AzureProviderOperation?> ClaimAsync(Guid workspaceId, Guid operationId, string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
            ClaimCoreAsync(workspaceId, operationId, workerId, leaseToken, leaseDuration, now, expectedVersion, allowRecovery: false);

        public Task<AzureProviderOperation?> ClaimRecoveryAsync(Guid workspaceId, Guid operationId, string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
            ClaimCoreAsync(workspaceId, operationId, workerId, leaseToken, leaseDuration, now, expectedVersion, allowRecovery: true);

        public Task<AzureProviderOperation?> HeartbeatAsync(Guid workspaceId, Guid operationId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureProviderOperation?>(Operations.SingleOrDefault(operation => operation.Id == operationId));

        public Task<AzureProviderOperation?> CheckpointAsync(Guid workspaceId, Guid operationId, string leaseToken, AzureProviderCheckpoint checkpoint, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            var index = Operations.FindIndex(operation => operation.Id == operationId && operation.Status == AzureProviderOperationStatus.Running &&
                (!expectedVersion.HasValue || operation.Version == expectedVersion.Value));
            if (index < 0)
                return Task.FromResult<AzureProviderOperation?>(null);

            var updated = Operations[index] with
            {
                Phase = checkpoint.Phase,
                Resources = MergeResources(Operations[index].Resources, checkpoint.Resources),
                Endpoint = checkpoint.Endpoint,
                Health = checkpoint.Health,
                Diagnostics = checkpoint.Diagnostics,
                CheckpointSequence = Operations[index].CheckpointSequence + 1,
                UpdatedAt = now,
                Version = Operations[index].Version + 1
            };
            Operations[index] = updated;
            return Task.FromResult<AzureProviderOperation?>(updated);
        }

        public Task<AzureProviderOperation?> FinalizeAsync(Guid workspaceId, Guid operationId, string leaseToken, AzureProviderOperationStatus status, string code, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            var index = Operations.FindIndex(operation => operation.Id == operationId && operation.Status == AzureProviderOperationStatus.Running &&
                (!expectedVersion.HasValue || operation.Version == expectedVersion.Value));
            if (index < 0)
                return Task.FromResult<AzureProviderOperation?>(null);

            var updated = Operations[index] with
            {
                Status = status,
                CompletedAt = status == AzureProviderOperationStatus.RecoveryRequired ? null : now,
                UpdatedAt = now,
                Version = Operations[index].Version + 1,
                WorkerId = null,
                LeaseExpiresAt = null
            };
            Operations[index] = updated;
            return Task.FromResult<AzureProviderOperation?>(updated);
        }

        public Task<int> RecoverStaleAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            var recovered = 0;
            for (var index = 0; index < Operations.Count; index++)
            {
                var operation = Operations[index];
                if (operation.Status != AzureProviderOperationStatus.Running
                    || operation.LeaseExpiresAt is not { } expires
                    || expires > now)
                {
                    continue;
                }

                Operations[index] = operation with
                {
                    Status = AzureProviderOperationStatus.RecoveryRequired,
                    WorkerId = null,
                    LeaseExpiresAt = null,
                    Version = operation.Version + 1,
                    UpdatedAt = now
                };
                recovered++;
            }

            return Task.FromResult(recovered);
        }

        public Task<IReadOnlyList<AzureProviderOperationTransition>> ListTransitionsAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AzureProviderOperationTransition>>([]);

        private static AzureProviderResourceReferences MergeResources(
            AzureProviderResourceReferences current,
            AzureProviderResourceReferences incoming) =>
            new(
                incoming.ResourceGroupName ?? current.ResourceGroupName,
                incoming.FoundationDeploymentId ?? current.FoundationDeploymentId,
                incoming.WorkloadDeploymentId ?? current.WorkloadDeploymentId,
                incoming.WorkloadResourceId ?? current.WorkloadResourceId,
                incoming.WorkloadRevisionName ?? current.WorkloadRevisionName,
                incoming.StableTrafficRevisionName ?? current.StableTrafficRevisionName);

        private Task<AzureProviderOperation?> ClaimCoreAsync(Guid workspaceId, Guid operationId, string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion, bool allowRecovery)
        {
            var index = Operations.FindIndex(operation => operation.WorkspaceId == workspaceId && operation.Id == operationId &&
                (operation.Status == AzureProviderOperationStatus.Accepted || operation.Status == AzureProviderOperationStatus.Queued ||
                 allowRecovery && operation.Status == AzureProviderOperationStatus.RecoveryRequired) &&
                (!expectedVersion.HasValue || operation.Version == expectedVersion.Value));
            if (index < 0)
                return Task.FromResult<AzureProviderOperation?>(null);

            var updated = Operations[index] with
            {
                Status = AzureProviderOperationStatus.Running,
                AttemptNumber = Operations[index].AttemptNumber + 1,
                Version = Operations[index].Version + 1,
                WorkerId = workerId,
                LeaseExpiresAt = now.Add(leaseDuration),
                HeartbeatAt = now,
                UpdatedAt = now
            };
            Operations[index] = updated;
            return Task.FromResult<AzureProviderOperation?>(updated);
        }
    }

    private sealed class OperationAssignmentStore(AzureProviderOperation operation) : IAzureProviderResourceAssignmentStore
    {
        public Task<AzureProviderResourceAssignment> CreateOrGetAsync(AzureProviderResourceAssignmentRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AzureProviderResourceAssignment?> GetAsync(Guid workspaceId, Guid assignmentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureProviderResourceAssignment?>(new(
                assignmentId,
                operation.WorkspaceId,
                operation.OrganizationId!.Value,
                operation.InstanceId!.Value,
                operation.ProviderScopeFingerprint!,
                1,
                "11111111-1111-1111-1111-111111111111",
                "rg-elsa-test",
                operation.TargetKey,
                new string('f', 64),
                operation.Location,
                AzureProviderAssignmentState.Reserved,
                new(),
                null,
                1,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
    }

    private sealed class StaticBackendStore(ElsaInstanceProvisioningBackend backend) : IElsaInstanceProvisioningBackendStore
    {
        public Task<ElsaInstanceProvisioningBackend> GetBackendAsync(
            Guid workspaceId,
            Guid instanceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(backend);
    }

    private sealed class RecordingDispatcher : IAzurePulumiProvisioningDispatcher
    {
        public bool CanDispatch => true;
        public List<AzureProviderOperation> Operations { get; } = [];
        public bool HasDispatched(Guid operationId) => Operations.Exists(operation => operation.Id == operationId);

        public Task<AzurePulumiDispatchResult> DispatchAsync(
            AzureProviderOperation operation,
            AzureWorkloadPlan plan,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(operation);
            return Task.FromResult(new AzurePulumiDispatchResult(true, Guid.NewGuid()));
        }
    }

    private sealed class DenyingGate : IElsaInstanceCommercialGate
    {
        public Task<ElsaInstanceCommercialGateDecision> EvaluateAsync(
            Guid organizationId,
            ElsaInstanceOperationAction action,
            int? activeInstanceCount = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ElsaInstanceCommercialGateDecision(
                false,
                ElsaInstanceCommercialOperation.EntitlementRequired,
                "Managed hosting is not enabled for this organization."));
    }

    private sealed class FailingDispatcher : IAzurePulumiProvisioningDispatcher
    {
        public bool CanDispatch => false;

        public Task<AzurePulumiDispatchResult> DispatchAsync(
            AzureProviderOperation operation,
            AzureWorkloadPlan plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AzurePulumiDispatchResult(false));
    }

    private sealed class FakeCommandProcess : IAzureCommandProcess
    {
        private readonly Queue<Response> _responses = new();
        public List<string[]> Calls { get; } = [];

        public void Success(Func<string[], bool> matcher, string output = "") =>
            _responses.Enqueue(new(matcher, output));

        public Task<AzureCommandProcessResult<T>> ExecuteAsync<T>(
            AzureCommandProcessRequest request,
            AzureCommandOutputProjector<T> outputProjector,
            CancellationToken cancellationToken = default)
            where T : AzureCommandSafeOutput
        {
            var args = request.Arguments.Select(x => x.Value).ToArray();
            Calls.Add(args);
            var response = _responses.Count > 0
                ? _responses.Dequeue()
                : throw new InvalidOperationException("Unexpected Azure command.");
            Assert.True(response.Matcher(args), $"Unexpected command: {string.Join(' ', args)}");
            return Task.FromResult(new AzureCommandProcessResult<T>(
                AzureCommandProcessStatus.Succeeded,
                AzureCommandProcessFailureKind.None,
                0,
                outputProjector(response.Output.AsMemory()),
                "test.command.succeeded",
                "The test command completed."));
        }

        private sealed record Response(Func<string[], bool> Matcher, string Output);
    }

    private sealed class UnusedOperationService : IAzureProviderOperationService
    {
        public Task<AzureProviderOperation> SubmitAsync(
            Guid workspaceId,
            AzureProviderOperationSubmission submission,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AzureProviderOperation> SubmitDeleteAsync(
            Guid workspaceId,
            AzureProviderOperationSubmission submission,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AzureProviderOperationStatusResponse?> GetStatusAsync(
            Guid workspaceId,
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
