using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureProviderOperationStepLedgerTests
{
    [Fact]
    public async Task Ordered_events_checkpoint_the_existing_ledger_and_advance_hub_status()
    {
        var providerOperation = Operation();
        var store = new LedgerStore(providerOperation);
        var ledger = new AzureProviderOperationStepLedger(store);
        var (operation, request) = Bound(providerOperation.WorkspaceId, providerOperation.Id);

        var result = await ledger.TryWriteAsync(operation, request, [
            Event(operation.Id, 2, ProvisioningEventKind.ResourceSucceeded, "resource-group"),
            Event(operation.Id, 1, ProvisioningEventKind.ResourceStarted, "resource-group"),
            Event(operation.Id, 3, ProvisioningEventKind.ResourceStarted, ProvisioningCapability.ContainerRuntime),
            Event(operation.Id, 4, ProvisioningEventKind.ResourceSucceeded, ProvisioningCapability.ContainerRuntime)
        ]);

        Assert.Equal(ProvisioningStepLedgerResult.Written, result);
        Assert.Equal(AzureProviderOperationPhase.WorkloadReady, store.Operation.Phase);
        Assert.Equal(
            new[]
            {
                PulumiStepLedgerMapper.FoundationPreparingCode,
                PulumiStepLedgerMapper.FoundationReadyCode,
                PulumiStepLedgerMapper.RuntimeDeployingCode,
                PulumiStepLedgerMapper.RuntimeDeployedCode
            },
            store.Transitions.Select(item => item.Code).ToArray());
        var progress = AzureManagedElsaProvisioningProgressProjector.Project(
            new AzureManagedElsaProvisioningProgressProjectionInput(
                Topology: null,
                LifecycleOperation: Lifecycle(),
                ProviderOperation: store.Operation,
                Transitions: store.Transitions));
        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, progress.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.RuntimeDeployment, progress.CurrentStage);
        var json = System.Text.Json.JsonSerializer.Serialize(progress);
        Assert.DoesNotContain("subscription", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("azure-native", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_same_engine_sequence_can_record_started_and_succeeded()
    {
        var providerOperation = Operation();
        var store = new LedgerStore(providerOperation);
        var ledger = new AzureProviderOperationStepLedger(store);
        var (operation, request) = Bound(providerOperation.WorkspaceId, providerOperation.Id);

        var result = await ledger.TryWriteAsync(operation, request, [
            Event(operation.Id, 4, ProvisioningEventKind.ResourceStarted, "resource-group"),
            Event(operation.Id, 4, ProvisioningEventKind.ResourceSucceeded, "resource-group")
        ]);

        Assert.Equal(ProvisioningStepLedgerResult.Written, result);
        Assert.Equal(
            new[]
            {
                PulumiStepLedgerMapper.FoundationPreparingCode,
                PulumiStepLedgerMapper.FoundationReadyCode
            },
            store.Transitions.Select(item => item.Code).ToArray());
    }

    [Fact]
    public async Task Duplicate_engine_sequences_do_not_write_a_second_row()
    {
        var providerOperation = Operation();
        var store = new LedgerStore(providerOperation);
        var ledger = new AzureProviderOperationStepLedger(store);
        var (operation, request) = Bound(providerOperation.WorkspaceId, providerOperation.Id);
        var started = Event(operation.Id, 1, ProvisioningEventKind.ResourceStarted, "resource-group");

        Assert.Equal(ProvisioningStepLedgerResult.Written, await ledger.TryWriteAsync(operation, request, [started]));
        Assert.Equal(ProvisioningStepLedgerResult.Duplicate, await ledger.TryWriteAsync(operation, request, [started]));
        Assert.Single(store.Transitions);
        Assert.Equal(AzureProviderOperationPhase.FoundationSubmitted, store.Operation.Phase);
    }

    [Fact]
    public async Task A_lost_azure_lease_is_reported_as_a_stale_fence()
    {
        var providerOperation = Operation();
        var store = new LedgerStore(providerOperation) { RejectClaim = true };
        var ledger = new AzureProviderOperationStepLedger(store);
        var (operation, request) = Bound(providerOperation.WorkspaceId, providerOperation.Id);

        var result = await ledger.TryWriteAsync(
            operation,
            request,
            [Event(operation.Id, 1, ProvisioningEventKind.ResourceStarted, "resource-group")]);

        Assert.Equal(ProvisioningStepLedgerResult.StaleFence, result);
        Assert.Empty(store.Transitions);
        Assert.Equal(AzureProviderOperationStatus.Accepted, store.Operation.Status);
    }

    [Fact]
    public async Task Operation_succeeded_queues_workload_ready_with_the_engine_endpoint()
    {
        var providerOperation = Operation();
        var store = new LedgerStore(providerOperation);
        var ledger = new AzureProviderOperationStepLedger(store);
        var (operation, request) = Bound(providerOperation.WorkspaceId, providerOperation.Id);

        var result = await ledger.TryWriteAsync(
            operation,
            request,
            [
                Event(operation.Id, 1, ProvisioningEventKind.ResourceStarted, "resource-group"),
                Event(operation.Id, 2, ProvisioningEventKind.ResourceSucceeded, "resource-group"),
                Event(operation.Id, 3, ProvisioningEventKind.ResourceSucceeded, ProvisioningCapability.ContainerRuntime),
                Event(operation.Id, 4, ProvisioningEventKind.OperationSucceeded, ProvisioningCapability.ContainerRuntime)
            ],
            StackOutputs());

        Assert.Equal(ProvisioningStepLedgerResult.Written, result);
        Assert.Equal(AzureProviderOperationStatus.Queued, store.Operation.Status);
        Assert.Equal(AzureProviderOperationPhase.WorkloadReady, store.Operation.Phase);
        Assert.Equal(AzureProviderHealth.Unknown, store.Operation.Health);
        Assert.Equal("https://runtime.example.test", store.Operation.Endpoint);
        Assert.Equal("engine-app--candidate", store.Operation.Resources.WorkloadRevisionName);
        Assert.Null(store.Operation.LeaseExpiresAt);
        Assert.Null(store.Operation.WorkerId);
        Assert.DoesNotContain(store.Transitions, item => item.Code == PulumiStepLedgerMapper.HealthVerifiedCode);
        Assert.DoesNotContain(store.Transitions, item => item.Code == PulumiStepLedgerMapper.TrafficRoutedCode);
        Assert.DoesNotContain(store.Transitions, item => item.Code == PulumiStepLedgerMapper.OperationSucceededCode);
    }

    [Fact]
    public async Task Delete_operation_succeeded_finalizes_cleanup()
    {
        var providerOperation = Operation() with { Action = AzureProviderOperationAction.Delete };
        var store = new LedgerStore(providerOperation);
        var ledger = new AzureProviderOperationStepLedger(store);
        var (operation, request) = Bound(providerOperation.WorkspaceId, providerOperation.Id);

        var result = await ledger.TryWriteAsync(
            operation,
            request,
            [
                Event(
                    operation.Id,
                    1,
                    ProvisioningEventKind.ResourceSucceeded,
                    PulumiStepLedgerMapper.OwnedResourceGroupAbsentKind,
                    PulumiStepLedgerMapper.ResourceGroupAbsentMessage),
                Event(operation.Id, 2, ProvisioningEventKind.OperationSucceeded, ProvisioningCapability.ContainerRuntime)
            ]);

        Assert.Equal(ProvisioningStepLedgerResult.Written, result);
        Assert.Equal(AzureProviderOperationStatus.Succeeded, store.Operation.Status);
        Assert.Equal(AzureProviderOperationPhase.CleanupVerified, store.Operation.Phase);
        Assert.Equal(new AzureProviderResourceReferences(), store.Operation.Resources);
        Assert.Contains(store.Transitions, item => item.Code == PulumiStepLedgerMapper.CleanupSucceededCode);
    }

    [Fact]
    public async Task Delete_does_not_finalize_without_the_resource_group_absent_receipt()
    {
        var providerOperation = Operation() with { Action = AzureProviderOperationAction.Delete };
        var store = new LedgerStore(providerOperation);
        var ledger = new AzureProviderOperationStepLedger(store);
        var (operation, request) = Bound(providerOperation.WorkspaceId, providerOperation.Id);

        var result = await ledger.TryWriteAsync(
            operation,
            request,
            [Event(operation.Id, 1, ProvisioningEventKind.OperationSucceeded, ProvisioningCapability.ContainerRuntime)]);

        Assert.Equal(ProvisioningStepLedgerResult.Ignored, result);
        Assert.Equal(AzureProviderOperationStatus.Accepted, store.Operation.Status);
        Assert.Empty(store.Transitions);
    }

    [Fact]
    public async Task Operation_succeeded_does_not_finalize_when_a_readiness_checkpoint_is_rejected()
    {
        var providerOperation = Operation();
        var store = new LedgerStore(providerOperation) { ThrowOnCheckpoint = true };
        var ledger = new AzureProviderOperationStepLedger(store);
        var (operation, request) = Bound(providerOperation.WorkspaceId, providerOperation.Id);

        var result = await ledger.TryWriteAsync(
            operation,
            request,
            [Event(operation.Id, 1, ProvisioningEventKind.OperationSucceeded, ProvisioningCapability.ContainerRuntime)]);

        Assert.Equal(ProvisioningStepLedgerResult.StaleFence, result);
        Assert.Equal(AzureProviderOperationStatus.Running, store.Operation.Status);
        Assert.Null(store.Operation.CompletedAt);
    }

    [Fact]
    public async Task A_second_process_can_finalize_a_running_step_after_reclaim()
    {
        var providerOperation = Operation();
        var store = new LedgerStore(providerOperation);
        var first = new AzureProviderOperationStepLedger(store);
        var (operation, request) = Bound(providerOperation.WorkspaceId, providerOperation.Id);

        Assert.Equal(
            ProvisioningStepLedgerResult.Written,
            await first.TryWriteAsync(
                operation,
                request,
                [Event(operation.Id, 1, ProvisioningEventKind.ResourceStarted, "resource-group")]));
        Assert.Equal(AzureProviderOperationStatus.Running, store.Operation.Status);
        Assert.Equal(AzureProviderOperationStepLedger.WorkerId, store.Operation.WorkerId);

        var second = new AzureProviderOperationStepLedger(store);
        var result = await second.TryWriteAsync(
            operation,
            request,
            [Event(operation.Id, 2, ProvisioningEventKind.OperationFailed, ProvisioningCapability.ContainerRuntime)]);

        Assert.Equal(ProvisioningStepLedgerResult.Written, result);
        Assert.Equal(AzureProviderOperationStatus.Failed, store.Operation.Status);
        Assert.Contains(store.Transitions, item => item.Code == ManagedElsaReasonCodeCatalog.AzureDeploymentFailed);
    }

    [Fact]
    public async Task Terminal_events_evict_in_memory_dedupe_so_a_reopened_step_can_write()
    {
        var providerOperation = Operation();
        var store = new LedgerStore(providerOperation);
        var ledger = new AzureProviderOperationStepLedger(store);
        var (operation, request) = Bound(providerOperation.WorkspaceId, providerOperation.Id);
        var started = Event(operation.Id, 1, ProvisioningEventKind.ResourceStarted, "resource-group");
        var failed = Event(operation.Id, 2, ProvisioningEventKind.OperationFailed, ProvisioningCapability.ContainerRuntime);

        Assert.Equal(ProvisioningStepLedgerResult.Written, await ledger.TryWriteAsync(operation, request, [started]));
        Assert.Equal(ProvisioningStepLedgerResult.Duplicate, await ledger.TryWriteAsync(operation, request, [started]));
        Assert.Equal(ProvisioningStepLedgerResult.Written, await ledger.TryWriteAsync(operation, request, [failed]));
        Assert.Equal(AzureProviderOperationStatus.Failed, store.Operation.Status);

        store.Reopen();
        Assert.Equal(ProvisioningStepLedgerResult.Written, await ledger.TryWriteAsync(operation, request, [started]));
        Assert.Equal(AzureProviderOperationPhase.FoundationSubmitted, store.Operation.Phase);
        Assert.Equal(AzureProviderOperationStatus.Running, store.Operation.Status);
    }

    [Fact]
    public async Task A_rejected_terminal_write_clears_in_memory_lease_and_dedupe()
    {
        var providerOperation = Operation();
        var store = new LedgerStore(providerOperation);
        var ledger = new AzureProviderOperationStepLedger(store);
        var (operation, request) = Bound(providerOperation.WorkspaceId, providerOperation.Id);
        var started = Event(operation.Id, 1, ProvisioningEventKind.ResourceStarted, "resource-group");
        var failed = Event(operation.Id, 2, ProvisioningEventKind.OperationFailed, ProvisioningCapability.ContainerRuntime);

        Assert.Equal(ProvisioningStepLedgerResult.Written, await ledger.TryWriteAsync(operation, request, [started]));
        store.RejectFinalize = true;
        Assert.Equal(ProvisioningStepLedgerResult.StaleFence, await ledger.TryWriteAsync(operation, request, [failed]));

        store.RejectFinalize = false;
        store.Reopen();
        Assert.Equal(ProvisioningStepLedgerResult.Written, await ledger.TryWriteAsync(operation, request, [started]));
        Assert.Equal(AzureProviderOperationPhase.FoundationSubmitted, store.Operation.Phase);
        Assert.Equal(AzureProviderOperationStatus.Running, store.Operation.Status);
    }

    [Fact]
    public async Task A_store_throw_on_a_terminal_write_clears_in_memory_lease_and_dedupe()
    {
        var providerOperation = Operation();
        var store = new LedgerStore(providerOperation);
        var ledger = new AzureProviderOperationStepLedger(store);
        var (operation, request) = Bound(providerOperation.WorkspaceId, providerOperation.Id);
        var started = Event(operation.Id, 1, ProvisioningEventKind.ResourceStarted, "resource-group");
        var failed = Event(operation.Id, 2, ProvisioningEventKind.OperationFailed, ProvisioningCapability.ContainerRuntime);

        Assert.Equal(ProvisioningStepLedgerResult.Written, await ledger.TryWriteAsync(operation, request, [started]));
        store.ThrowOnWrite = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.TryWriteAsync(operation, request, [failed]));

        store.ThrowOnWrite = false;
        store.Reopen();
        Assert.Equal(ProvisioningStepLedgerResult.Written, await ledger.TryWriteAsync(operation, request, [started]));
        Assert.Equal(AzureProviderOperationPhase.FoundationSubmitted, store.Operation.Phase);
        Assert.Equal(AzureProviderOperationStatus.Running, store.Operation.Status);
    }

    [Fact]
    public async Task Operation_failed_with_no_pulumi_events_finalizes_the_ledger()
    {
        var providerOperation = Operation();
        var store = new LedgerStore(providerOperation);
        var ledger = new AzureProviderOperationStepLedger(store);
        var (operation, request) = Bound(providerOperation.WorkspaceId, providerOperation.Id);

        var result = await ledger.TryWriteAsync(
            operation,
            request,
            [Event(operation.Id, 1, ProvisioningEventKind.OperationFailed, ProvisioningCapability.ContainerRuntime)]);

        Assert.Equal(ProvisioningStepLedgerResult.Written, result);
        Assert.Equal(AzureProviderOperationStatus.Failed, store.Operation.Status);
        Assert.Contains(store.Transitions, item => item.Code == ManagedElsaReasonCodeCatalog.AzureDeploymentFailed);
    }

    [Fact]
    public async Task Operation_failed_stamps_FromPulumi_failure_class()
    {
        var providerOperation = Operation();
        var store = new LedgerStore(providerOperation);
        var ledger = new AzureProviderOperationStepLedger(store);
        var (operation, request) = Bound(providerOperation.WorkspaceId, providerOperation.Id);
        operation = operation with { FailureCode = AzureTransientArmFailure.QuotaExceeded };

        var result = await ledger.TryWriteAsync(
            operation,
            request,
            [Event(operation.Id, 1, ProvisioningEventKind.OperationFailed, ProvisioningCapability.ContainerRuntime)]);

        Assert.Equal(ProvisioningStepLedgerResult.Written, result);
        Assert.Equal(AzureProviderOperationStatus.Failed, store.Operation.Status);
        Assert.Equal(ElsaInstanceOperationFailureClass.Terminal, store.LastFailureClass);
        Assert.Equal(
            ElsaInstanceOperationFailureClass.Terminal,
            AzureTransientArmFailure.FromPulumi(AzureTransientArmFailure.QuotaExceeded));
    }

    [Fact]
    public async Task An_engine_error_observation_leaves_the_operation_running()
    {
        var providerOperation = Operation();
        var store = new LedgerStore(providerOperation);
        var ledger = new AzureProviderOperationStepLedger(store);
        var (operation, request) = Bound(providerOperation.WorkspaceId, providerOperation.Id);

        await ledger.TryWriteAsync(
            operation,
            request,
            [
                Event(operation.Id, 1, ProvisioningEventKind.ResourceStarted, "resource-group"),
                Event(operation.Id, 2, ProvisioningEventKind.ResourceFailed, ProvisioningCapability.ContainerRuntime)
            ]);

        Assert.NotEqual(AzureProviderOperationStatus.Failed, store.Operation.Status);
        Assert.DoesNotContain(store.Transitions, item => item.Code == ManagedElsaReasonCodeCatalog.AzureDeploymentFailed);
    }

    [Fact]
    public async Task A_failure_finalizes_the_ledger_with_the_phase_zero_reason()
    {
        var providerOperation = Operation();
        var store = new LedgerStore(providerOperation);
        var ledger = new AzureProviderOperationStepLedger(store);
        var (operation, request) = Bound(providerOperation.WorkspaceId, providerOperation.Id);

        var result = await ledger.TryWriteAsync(
            operation,
            request,
            [
                Event(operation.Id, 1, ProvisioningEventKind.ResourceStarted, "resource-group"),
                Event(operation.Id, 2, ProvisioningEventKind.OperationFailed, ProvisioningCapability.ContainerRuntime, "Operation failed.")
            ]);

        Assert.Equal(ProvisioningStepLedgerResult.Written, result);
        Assert.Equal(AzureProviderOperationStatus.Failed, store.Operation.Status);
        Assert.Contains(store.Transitions, item => item.Code == ManagedElsaReasonCodeCatalog.AzureDeploymentFailed);
        Assert.All(store.Transitions, item =>
        {
            Assert.True(AzureProviderOperationValidation.IsSafeCode(item.Code));
            Assert.False(ProvisioningEventSanitizer.ContainsSensitiveToken(item.Message));
        });
    }

    private static (ProvisioningOperation Operation, ProvisioningRequest Request) Bound(
        Guid workspaceId,
        Guid providerOperationId)
    {
        var operation = new ProvisioningOperation(
            Guid.NewGuid(),
            ProvisioningCommand.Provision,
            ProvisioningOperationState.Running,
            "azure",
            ProvisioningStackKind.Engine,
            ProvisioningStackName.ForEngine(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")),
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
        string resourceKind,
        string? message = null) =>
        new(
            operationId,
            kind,
            resourceKind,
            message ?? kind switch
            {
                ProvisioningEventKind.ResourceFailed or ProvisioningEventKind.OperationFailed => "Resource failed.",
                ProvisioningEventKind.ResourceSucceeded or ProvisioningEventKind.OperationSucceeded => "Resource succeeded.",
                ProvisioningEventKind.OperationCancelled => "Operation cancelled.",
                _ => "Resource started."
            },
            DateTimeOffset.UtcNow,
            sequence);

    private static IReadOnlyDictionary<string, string> StackOutputs() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["engineEndpoint"] = "https://runtime.example.test",
            ["workloadRevisionName"] = "engine-app--candidate",
            ["ownedResourceGroupName"] = "rg-engine-test"
        };

    private static AzureProviderOperation Operation() =>
        new(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "workload-a",
            AzureProviderOperationAction.Reconcile,
            "request-1",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
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
            InstanceId: Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            LifecycleAction: ElsaInstanceOperationAction.Create);

    private static ElsaInstanceLifecycleTopologyOperation Lifecycle(
        ElsaInstanceOperationState state = ElsaInstanceOperationState.Running) =>
        new(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            ElsaInstanceOperationAction.Create,
            state,
            1,
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            state == ElsaInstanceOperationState.Succeeded ? DateTimeOffset.UtcNow : null,
            null,
            null,
            null,
            null,
            null);

    private sealed class LedgerStore(AzureProviderOperation operation) : IAzureProviderOperationStore
    {
        private string? _leaseToken;
        public AzureProviderOperation Operation { get; private set; } = operation;
        public List<AzureProviderOperationTransition> Transitions { get; } = [];
        public ElsaInstanceOperationFailureClass? LastFailureClass { get; private set; }
        public bool RejectClaim { get; init; }
        public bool RejectFinalize { get; set; }
        public bool ThrowOnWrite { get; set; }
        public bool ThrowOnCheckpoint { get; set; }

        public void Reopen()
        {
            _leaseToken = null;
            Operation = Operation with
            {
                Status = AzureProviderOperationStatus.Accepted,
                WorkerId = null,
                LeaseExpiresAt = null,
                CompletedAt = null
            };
        }

        public Task<AzureProviderOperationCreateResult> CreateOrGetWithResultAsync(
            AzureProviderOperationRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AzureProviderOperationCreateResult(Operation, false));

        public Task<AzureProviderOperation> CreateOrGetAsync(
            AzureProviderOperationRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Operation);

        public Task<AzureProviderOperation?> GetAsync(
            Guid workspaceId,
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureProviderOperation?>(
                Operation.WorkspaceId == workspaceId && Operation.Id == operationId ? Operation : null);

        public Task<IReadOnlyList<AzureProviderOperation>> ListRunnableAsync(
            DateTimeOffset now,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AzureProviderOperation>>([Operation]);

        public Task<AzureProviderOperation?> GetLatestReconcileAsync(
            Guid workspaceId,
            string targetKey,
            string? providerScopeFingerprint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureProviderOperation?>(null);

        public Task<AzureProviderOperation?> MarkUnrestorableAsync(
            Guid workspaceId,
            Guid operationId,
            DateTimeOffset now,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureProviderOperation?>(null);

        public Task<AzureProviderOperation?> ClaimAsync(
            Guid workspaceId,
            Guid operationId,
            string workerId,
            string leaseToken,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            var ownExpiredRunning = Operation.Status == AzureProviderOperationStatus.Running
                && string.Equals(Operation.WorkerId, workerId, StringComparison.Ordinal)
                && Operation.LeaseExpiresAt is { } expires
                && expires <= now;
            if (RejectClaim
                || (!ownExpiredRunning
                    && Operation.Status is not (AzureProviderOperationStatus.Accepted or AzureProviderOperationStatus.Queued))
                || expectedVersion.HasValue && expectedVersion.Value != Operation.Version)
            {
                return Task.FromResult<AzureProviderOperation?>(null);
            }

            _leaseToken = leaseToken;
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

        public Task<AzureProviderOperation?> ClaimRecoveryAsync(
            Guid workspaceId,
            Guid operationId,
            string workerId,
            string leaseToken,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            ClaimAsync(workspaceId, operationId, workerId, leaseToken, leaseDuration, now, expectedVersion, cancellationToken);

        public Task<AzureProviderOperation?> HeartbeatAsync(
            Guid workspaceId,
            Guid operationId,
            string leaseToken,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            if (_leaseToken != leaseToken)
                return Task.FromResult<AzureProviderOperation?>(null);
            Operation = Operation with
            {
                Version = Operation.Version + 1,
                UpdatedAt = now,
                HeartbeatAt = now,
                LeaseExpiresAt = now.Add(leaseDuration)
            };
            return Task.FromResult<AzureProviderOperation?>(Operation);
        }

        public Task<AzureProviderOperation?> CheckpointAsync(
            Guid workspaceId,
            Guid operationId,
            string leaseToken,
            AzureProviderCheckpoint checkpoint,
            DateTimeOffset now,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            AzureProviderOperationValidation.ValidateCheckpoint(checkpoint);
            if (ThrowOnWrite || ThrowOnCheckpoint)
                throw new InvalidOperationException("The Azure provider operation store failed.");
            if (_leaseToken != leaseToken
                || Operation.Status != AzureProviderOperationStatus.Running
                || expectedVersion.HasValue && expectedVersion.Value != Operation.Version)
            {
                return Task.FromResult<AzureProviderOperation?>(null);
            }

            if (AzureProviderOperationPhaseOrdering.Compare(checkpoint.Phase, Operation.Phase) < 0)
                throw new InvalidOperationException("Checkpoint phase cannot move backwards.");
            if (Operation.Phase == checkpoint.Phase
                && Transitions.LastOrDefault()?.Code == checkpoint.Code)
            {
                return Task.FromResult<AzureProviderOperation?>(Operation);
            }

            Operation = Operation with
            {
                Phase = checkpoint.Phase,
                AttemptedStep = checkpoint.AttemptedStep,
                Health = checkpoint.Health == AzureProviderHealth.Unknown ? Operation.Health : checkpoint.Health,
                Endpoint = checkpoint.Endpoint ?? Operation.Endpoint,
                Resources = checkpoint.ReplaceResources
                    ? checkpoint.Resources
                    : MergeResources(Operation.Resources, checkpoint.Resources),
                CheckpointSequence = Operation.CheckpointSequence + 1,
                Version = Operation.Version + 1,
                UpdatedAt = now
            };
            Transitions.Add(new(
                Guid.NewGuid(),
                Operation.Id,
                Operation.Version,
                Operation.Status,
                Operation.Phase,
                checkpoint.Code,
                checkpoint.Message,
                now));
            return Task.FromResult<AzureProviderOperation?>(Operation);
        }

        public Task<AzureProviderOperation?> FinalizeAsync(
            Guid workspaceId,
            Guid operationId,
            string leaseToken,
            AzureProviderOperationStatus status,
            string code,
            DateTimeOffset now,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            AzureProviderOperationValidation.ValidateCode(code);
            if (ThrowOnWrite)
                throw new InvalidOperationException("The Azure provider operation store failed.");
            if (RejectFinalize
                || _leaseToken != leaseToken
                || Operation.Status != AzureProviderOperationStatus.Running
                || expectedVersion.HasValue && expectedVersion.Value != Operation.Version)
            {
                return Task.FromResult<AzureProviderOperation?>(null);
            }

            Operation = Operation with
            {
                Status = status,
                Version = Operation.Version + 1,
                UpdatedAt = now,
                CompletedAt = now,
                WorkerId = null,
                LeaseExpiresAt = null
            };
            Transitions.Add(new(
                Guid.NewGuid(),
                Operation.Id,
                Operation.Version,
                status,
                Operation.Phase,
                code,
                code,
                now));
            _leaseToken = null;
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
            if (_leaseToken != leaseToken
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
            _leaseToken = null;
            return Task.FromResult<AzureProviderOperation?>(Operation);
        }

        public Task<int> RecoverStaleAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<AzureProviderOperationTransition>> ListTransitionsAsync(
            Guid workspaceId,
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AzureProviderOperationTransition>>(Transitions);

        public Task RecordArmObservationClockAsync(
            Guid workspaceId,
            Guid operationId,
            DateTimeOffset observedAt,
            int backoffSeconds,
            string? reasonCode,
            ElsaInstanceOperationFailureClass? failureClass,
            CancellationToken cancellationToken = default)
        {
            LastFailureClass = failureClass;
            return Task.CompletedTask;
        }

        private static AzureProviderResourceReferences MergeResources(
            AzureProviderResourceReferences current,
            AzureProviderResourceReferences? incoming)
        {
            if (incoming is null)
                return current;
            return new(
                incoming.ResourceGroupName ?? current.ResourceGroupName,
                incoming.FoundationDeploymentId ?? current.FoundationDeploymentId,
                incoming.WorkloadDeploymentId ?? current.WorkloadDeploymentId,
                incoming.WorkloadResourceId ?? current.WorkloadResourceId,
                incoming.WorkloadRevisionName ?? current.WorkloadRevisionName,
                incoming.StableTrafficRevisionName ?? current.StableTrafficRevisionName,
                incoming.WorkloadIdentityResourceId ?? current.WorkloadIdentityResourceId,
                incoming.WorkloadIdentityClientId ?? current.WorkloadIdentityClientId,
                incoming.WorkloadIdentityPrincipalId ?? current.WorkloadIdentityPrincipalId,
                incoming.KeyVaultResourceId ?? current.KeyVaultResourceId,
                incoming.KeyVaultUri ?? current.KeyVaultUri,
                incoming.SqlServerResourceId ?? current.SqlServerResourceId,
                incoming.SqlServerFqdn ?? current.SqlServerFqdn,
                incoming.ContainerAppsEnvironmentResourceId ?? current.ContainerAppsEnvironmentResourceId,
                incoming.RegistryResourceId ?? current.RegistryResourceId,
                incoming.AcrPullDeploymentId ?? current.AcrPullDeploymentId,
                incoming.AcrPullRoleAssignmentId ?? current.AcrPullRoleAssignmentId);
        }
    }
}
