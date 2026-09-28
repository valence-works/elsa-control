using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureManagedElsaProvisioningProgressReaderTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid InstanceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset AcceptedAt = DateTimeOffset.Parse("2026-09-21T10:00:00Z");

    [Fact]
    public async Task Missing_scoped_instance_returns_not_found_without_reading_provider_history()
    {
        var instances = new InstanceStore { Topology = null };
        var providers = new ProviderStore();
        var reader = Reader(instances, providers);

        Assert.Null(await reader.ReadAsync(WorkspaceId, InstanceId));
        Assert.Equal(0, providers.CallCount);
    }

    [Fact]
    public async Task Active_create_is_correlated_server_side_and_can_report_queued_before_provider_submission()
    {
        var create = Lifecycle(Guid.NewGuid(), ElsaInstanceOperationState.Queued);
        var instances = new InstanceStore { Topology = Topology([create], create.Id) };
        var providers = new ProviderStore();
        var reader = Reader(instances, providers);

        var result = Assert.IsType<ManagedElsaProvisioningProgress>(await reader.ReadAsync(WorkspaceId, InstanceId));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Queued, result.State);
        Assert.Equal(create.Id, providers.LifecycleOperationId);
        Assert.Equal(7, providers.ExpectedInstanceVersion);
        Assert.Equal(WorkspaceId, providers.WorkspaceId);
        Assert.Equal(InstanceId, providers.InstanceId);
    }

    [Fact]
    public async Task Terminal_create_is_recovered_from_last_operation_and_remains_ready_without_provider_history()
    {
        var operationId = Guid.NewGuid();
        var instances = new InstanceStore
        {
            Topology = Topology([], operationId, ElsaObservedLifecycle.Ready),
            LastOperation = new ElsaInstanceOperationSummary(
                operationId,
                InstanceId,
                ElsaInstanceOperationAction.Create,
                ElsaInstanceOperationState.Succeeded,
                1,
                1,
                AcceptedAt,
                AcceptedAt.AddSeconds(1),
                AcceptedAt.AddMinutes(8),
                null,
                null,
                null,
                null,
                ElsaObservedLifecycle.Ready,
                ElsaInstanceHealth.Healthy)
        };
        var providers = new ProviderStore();
        var reader = Reader(instances, providers);

        var result = Assert.IsType<ManagedElsaProvisioningProgress>(await reader.ReadAsync(WorkspaceId, InstanceId));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Ready, result.State);
        Assert.Equal(operationId, providers.LifecycleOperationId);
        Assert.All(result.Stages, stage =>
            Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Completed, stage.Status));
    }

    [Fact]
    public async Task Ambiguous_lifecycle_or_provider_history_fails_closed_without_losing_acceptance_time()
    {
        var first = Lifecycle(Guid.NewGuid(), ElsaInstanceOperationState.Running);
        var second = Lifecycle(Guid.NewGuid(), ElsaInstanceOperationState.Queued);
        var multipleLifecycleReader = Reader(
            new InstanceStore { Topology = Topology([first, second], first.Id) },
            new ProviderStore());

        var multiple = Assert.IsType<ManagedElsaProvisioningProgress>(await multipleLifecycleReader.ReadAsync(
            WorkspaceId,
            InstanceId));
        Assert.Equal(ManagedElsaProvisioningProgressStates.Unavailable, multiple.State);
        Assert.Equal(AcceptedAt, multiple.StartedAt);

        var providerStore = new ProviderStore
        {
            Snapshot = new AzureManagedElsaProvisioningOperationSnapshot(null, [], IsAmbiguous: true)
        };
        var providerReader = Reader(
            new InstanceStore { Topology = Topology([first], first.Id) },
            providerStore);
        var provider = Assert.IsType<ManagedElsaProvisioningProgress>(await providerReader.ReadAsync(
            WorkspaceId,
            InstanceId));
        Assert.Equal(ManagedElsaProvisioningProgressStates.Unavailable, provider.State);
        Assert.Equal("request.accepted", Assert.Single(provider.Activity).MessageCode);
    }

    [Fact]
    public async Task Topology_drift_is_preserved_for_the_api_to_map_to_retryable_conflict()
    {
        var reader = Reader(
            new InstanceStore { ThrowTopologyChanged = true },
            new ProviderStore());

        await Assert.ThrowsAsync<ElsaInstanceLifecycleTopologyChangedException>(() =>
            reader.ReadAsync(WorkspaceId, InstanceId));
    }

    [Fact]
    public async Task Deleted_instance_is_concealed_without_reading_provider_history()
    {
        var providers = new ProviderStore();
        var reader = Reader(
            new InstanceStore { Topology = Topology([], null, ElsaObservedLifecycle.Deleted) },
            providers);

        Assert.Null(await reader.ReadAsync(WorkspaceId, InstanceId));
        Assert.Equal(0, providers.CallCount);
    }

    [Fact]
    public async Task Accepted_handoff_sequence_never_reports_stale()
    {
        var create = Lifecycle(
            Guid.NewGuid(),
            ElsaInstanceOperationState.RecoveryRequired,
            AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionAccepted);
        var instances = new InstanceStore { Topology = Topology([create], create.Id) };
        var clock = new FixedTimeProvider(AcceptedAt.AddSeconds(5));

        var missingProvider = Assert.IsType<ManagedElsaProvisioningProgress>(
            await Reader(instances, new ProviderStore(), clock).ReadAsync(WorkspaceId, InstanceId));
        var accepted = Assert.IsType<ManagedElsaProvisioningProgress>(
            await Reader(
                    instances,
                    new ProviderStore
                    {
                        Snapshot = new AzureManagedElsaProvisioningOperationSnapshot(
                            Provider(AzureProviderOperationStatus.Accepted, AzureProviderOperationPhase.Planned),
                            [])
                    },
                    clock)
                .ReadAsync(WorkspaceId, InstanceId));
        var queued = Assert.IsType<ManagedElsaProvisioningProgress>(
            await Reader(
                    instances,
                    new ProviderStore
                    {
                        Snapshot = new AzureManagedElsaProvisioningOperationSnapshot(
                            Provider(AzureProviderOperationStatus.Queued, AzureProviderOperationPhase.Planned),
                            [])
                    },
                    clock)
                .ReadAsync(WorkspaceId, InstanceId));
        var running = Assert.IsType<ManagedElsaProvisioningProgress>(
            await Reader(
                    instances,
                    new ProviderStore
                    {
                        Snapshot = new AzureManagedElsaProvisioningOperationSnapshot(
                            Provider(AzureProviderOperationStatus.Running, AzureProviderOperationPhase.WorkloadSubmitted),
                            [Transition(1, AzureProviderOperationPhase.WorkloadSubmitted)])
                    },
                    clock)
                .ReadAsync(WorkspaceId, InstanceId));
        var succeededBeforeReady = Assert.IsType<ManagedElsaProvisioningProgress>(
            await Reader(
                    instances,
                    new ProviderStore
                    {
                        Snapshot = new AzureManagedElsaProvisioningOperationSnapshot(
                            Provider(AzureProviderOperationStatus.Succeeded, AzureProviderOperationPhase.TrafficPromoted),
                            [Transition(1, AzureProviderOperationPhase.TrafficPromoted)])
                    },
                    clock)
                .ReadAsync(WorkspaceId, InstanceId));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Queued, missingProvider.State);
        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, accepted.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.RequestAccepted, accepted.CurrentStage);
        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, queued.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.RequestAccepted, queued.CurrentStage);
        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, running.State);
        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, succeededBeforeReady.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.TrafficRouting, succeededBeforeReady.CurrentStage);
        Assert.All(
            new[] { missingProvider, accepted, queued, running, succeededBeforeReady },
            snapshot => Assert.NotEqual(ManagedElsaProvisioningProgressStates.Stale, snapshot.State));
    }

    [Theory]
    [InlineData(AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionAccepted, "queued")]
    [InlineData(AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationInProgress, "queued")]
    [InlineData(AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationHealthUnknown, "active")]
    [InlineData(AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationUnavailable, "active")]
    [InlineData(AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationUnknown, "active")]
    [InlineData(AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationAmbiguous, "stale")]
    [InlineData(AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationCorrelationMismatch, "stale")]
    [InlineData(AzureManagedElsaProvisioningProgressProjector.ProviderReconciliationRetrySafe, "stale")]
    [InlineData(AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionUncertain, "stale")]
    [InlineData("provider.not-a-real-reason", "stale")]
    public async Task Reader_passes_the_current_run_reason_into_the_projector(
        string recoveryReason,
        string expectedState)
    {
        var create = Lifecycle(Guid.NewGuid(), ElsaInstanceOperationState.RecoveryRequired, recoveryReason);
        var reader = Reader(
            new InstanceStore { Topology = Topology([create], create.Id) },
            new ProviderStore());

        var result = Assert.IsType<ManagedElsaProvisioningProgress>(await reader.ReadAsync(WorkspaceId, InstanceId));

        Assert.Equal(expectedState, result.State);
    }

    [Fact]
    public async Task Reader_uses_store_written_uncertain_failure_code_as_stale()
    {
        var create = new ElsaInstanceLifecycleTopologyOperation(
            Guid.NewGuid(),
            ElsaInstanceOperationAction.Create,
            ElsaInstanceOperationState.RecoveryRequired,
            1,
            1,
            AcceptedAt,
            AcceptedAt.AddSeconds(1),
            null,
            null,
            "provider.submission.uncertain",
            null,
            null,
            null);
        var reader = Reader(
            new InstanceStore { Topology = Topology([create], create.Id) },
            new ProviderStore());

        var result = Assert.IsType<ManagedElsaProvisioningProgress>(await reader.ReadAsync(WorkspaceId, InstanceId));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressDiagnostics.RequiresAttention, result.DiagnosticCode);
    }

    [Fact]
    public async Task Waiting_create_names_the_blocking_delete_and_is_never_clock_stale()
    {
        var organizationId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var delete = new ElsaInstanceLifecycleTopologyOperation(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            ElsaInstanceOperationAction.Delete,
            ElsaInstanceOperationState.Accepted,
            1,
            1,
            AcceptedAt.AddMinutes(9),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            OrganizationId: organizationId);
        var create = new ElsaInstanceLifecycleTopologyOperation(
            Guid.NewGuid(),
            ElsaInstanceOperationAction.Create,
            ElsaInstanceOperationState.WaitingForPriorOperation,
            1,
            1,
            AcceptedAt,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            OrganizationId: organizationId,
            BlockingOperationId: delete.Id);
        var reader = Reader(
            new InstanceStore { Topology = Topology([create, delete], create.Id) },
            new ProviderStore(),
            new FixedTimeProvider(AcceptedAt.AddMinutes(10)));

        var result = Assert.IsType<ManagedElsaProvisioningProgress>(await reader.ReadAsync(WorkspaceId, InstanceId));

        Assert.Equal(ManagedElsaProvisioningProgressStates.WaitingForPriorOperation, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.WaitingForDelete, result.CurrentStage);
        Assert.Equal(delete.Id, result.BlockingOperationId);
        Assert.Null(result.StaleReason);
    }

    [Fact]
    public async Task Reader_still_projects_the_create_when_a_delete_is_waiting_on_it()
    {
        var organizationId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var create = new ElsaInstanceLifecycleTopologyOperation(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            ElsaInstanceOperationAction.Create,
            ElsaInstanceOperationState.RecoveryRequired,
            1,
            1,
            AcceptedAt,
            AcceptedAt.AddSeconds(1),
            null,
            null,
            null,
            null,
            null,
            null,
            RecoveryReason: AzureManagedElsaProvisioningProgressProjector.ProviderSubmissionAccepted,
            OrganizationId: organizationId);
        var delete = new ElsaInstanceLifecycleTopologyOperation(
            Guid.NewGuid(),
            ElsaInstanceOperationAction.Delete,
            ElsaInstanceOperationState.WaitingForPriorOperation,
            1,
            1,
            AcceptedAt,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            OrganizationId: organizationId,
            BlockingOperationId: create.Id);
        var reader = Reader(
            new InstanceStore { Topology = Topology([delete, create], delete.Id) },
            new ProviderStore
            {
                Snapshot = new AzureManagedElsaProvisioningOperationSnapshot(
                    Provider(AzureProviderOperationStatus.Running, AzureProviderOperationPhase.FoundationSubmitted),
                    [])
            },
            new FixedTimeProvider(AcceptedAt.AddMinutes(15)));

        var result = Assert.IsType<ManagedElsaProvisioningProgress>(await reader.ReadAsync(WorkspaceId, InstanceId));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, result.State);
        Assert.NotEqual(ManagedElsaProvisioningProgressStates.Stale, result.State);
        Assert.Null(result.StaleReason);
    }

    [Fact]
    public async Task Reader_fails_closed_when_the_blocker_id_is_unresolvable()
    {
        var create = new ElsaInstanceLifecycleTopologyOperation(
            Guid.NewGuid(),
            ElsaInstanceOperationAction.Create,
            ElsaInstanceOperationState.WaitingForPriorOperation,
            1,
            1,
            AcceptedAt,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            OrganizationId: Guid.Parse("66666666-6666-6666-6666-666666666666"),
            BlockingOperationId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var reader = Reader(
            new InstanceStore { Topology = Topology([create], create.Id) },
            new ProviderStore());

        var result = Assert.IsType<ManagedElsaProvisioningProgress>(await reader.ReadAsync(WorkspaceId, InstanceId));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Stale, result.State);
        Assert.Equal(ManagedElsaProvisioningProgressStaleReasons.BlockingOperationUnresolvable, result.StaleReason);
    }

    [Fact]
    public async Task Unknown_and_non_monotonic_provider_mappings_emit_protected_warning_events()
    {
        var lifecycle = Lifecycle(Guid.NewGuid(), ElsaInstanceOperationState.Running);
        var logger = new RecordingLogger();
        var providers = new ProviderStore
        {
            Snapshot = new AzureManagedElsaProvisioningOperationSnapshot(
                Provider(AzureProviderOperationPhase.WorkloadReady),
                [
                    Transition(1, AzureProviderOperationPhase.HealthVerified),
                    Transition(2, AzureProviderOperationPhase.WorkloadReady),
                    Transition(3, (AzureProviderOperationPhase)999)
                ])
        };
        var reader = new AzureManagedElsaProvisioningProgressReader(
            new InstanceStore { Topology = Topology([lifecycle], lifecycle.Id) },
            providers,
            logger);

        _ = await reader.ReadAsync(WorkspaceId, InstanceId);

        Assert.Contains(logger.EventIds, id => id == 53001);
        Assert.Contains(logger.EventIds, id => id == 53002);
        Assert.All(logger.Messages, message => Assert.DoesNotContain("999", message, StringComparison.Ordinal));
    }

    private static AzureManagedElsaProvisioningProgressReader Reader(
        IManagedElsaInstanceApiStore instances,
        IAzureManagedElsaProvisioningOperationStore providers,
        TimeProvider? timeProvider = null) =>
        new(instances, providers, NullLogger<AzureManagedElsaProvisioningProgressReader>.Instance, timeProvider ?? new FixedTimeProvider(AcceptedAt));

    private static AzureProviderOperation Provider(
        AzureProviderOperationPhase phase) =>
        Provider(AzureProviderOperationStatus.Running, phase);

    private static AzureProviderOperation Provider(
        AzureProviderOperationStatus status,
        AzureProviderOperationPhase phase) =>
        new(Guid.NewGuid(), WorkspaceId, "safe-target", AzureProviderOperationAction.Reconcile, "safe-idempotency", "hash",
            "provider-operation", "plan", "template", "3.8.1", "3.8", "topology", "isolated", "westeurope",
            "image", "digest", null, null, status, phase, 1, 1, 1, new(), null,
            AzureProviderHealth.Unknown, [], null, null, null, AcceptedAt, AcceptedAt, null,
            InstanceId: InstanceId, LifecycleAction: ElsaInstanceOperationAction.Create);

    private static AzureProviderOperationTransition Transition(long sequence, AzureProviderOperationPhase phase) =>
        new(Guid.NewGuid(), Guid.NewGuid(), sequence, AzureProviderOperationStatus.Running, phase, "private", "private", AcceptedAt.AddSeconds(sequence));

    private static ElsaInstanceLifecycleTopologySnapshot Topology(
        IReadOnlyList<ElsaInstanceLifecycleTopologyOperation> operations,
        Guid? lastOperationId,
        ElsaObservedLifecycle observed = ElsaObservedLifecycle.Provisioning) =>
        new(InstanceId, 7, ElsaDesiredLifecycle.Running, observed, lastOperationId, operations);

    private static ElsaInstanceLifecycleTopologyOperation Lifecycle(
        Guid id,
        ElsaInstanceOperationState state,
        string? recoveryReason = null) =>
        new(
            id,
            ElsaInstanceOperationAction.Create,
            state,
            1,
            1,
            AcceptedAt,
            state == ElsaInstanceOperationState.Queued ? null : AcceptedAt.AddSeconds(1),
            null,
            null,
            null,
            null,
            null,
            null,
            RecoveryReason: recoveryReason);

    private sealed class ProviderStore : IAzureManagedElsaProvisioningOperationStore
    {
        public AzureManagedElsaProvisioningOperationSnapshot? Snapshot { get; init; }
        public int CallCount { get; private set; }
        public Guid WorkspaceId { get; private set; }
        public Guid InstanceId { get; private set; }
        public Guid? LifecycleOperationId { get; private set; }
        public int ExpectedInstanceVersion { get; private set; }

        public Task<AzureManagedElsaProvisioningOperationSnapshot?> GetCreateProvisioningAsync(
            Guid workspaceId,
            Guid instanceId,
            Guid? lifecycleOperationId,
            int expectedInstanceVersion,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            WorkspaceId = workspaceId;
            InstanceId = instanceId;
            LifecycleOperationId = lifecycleOperationId;
            ExpectedInstanceVersion = expectedInstanceVersion;
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class InstanceStore : IManagedElsaInstanceApiStore
    {
        public ElsaInstanceLifecycleTopologySnapshot? Topology { get; init; }
        public ElsaInstanceOperationSummary? LastOperation { get; init; }
        public bool ThrowTopologyChanged { get; init; }

        public Task<ElsaInstanceLifecycleTopologySnapshot?> GetLifecycleTopologyAsync(
            Guid workspaceId,
            Guid instanceId,
            CancellationToken cancellationToken = default) =>
            ThrowTopologyChanged
                ? throw new ElsaInstanceLifecycleTopologyChangedException()
                : Task.FromResult(Topology);

        public Task<ElsaInstanceOperationSummary?> GetOperationAsync(
            Guid workspaceId,
            Guid instanceId,
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(LastOperation?.Id == operationId ? LastOperation : null);

        public Task<ElsaInstanceOperationPage> ListOperationsAsync(
            Guid workspaceId,
            Guid instanceId,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default,
            Guid organizationId = default) =>
            throw new NotSupportedException();

        public Task<ElsaInstancePage> ListInstancesAsync(Guid workspaceId, int page, int pageSize, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> SlugExistsAsync(Guid workspaceId, string slug, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, ElsaInstanceOperationSummary>> GetActiveOperationsAsync(Guid workspaceId, IReadOnlyCollection<Guid> instanceIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ElsaInstanceIntentRevisionSummary>> ListRevisionsAsync(Guid workspaceId, Guid instanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ElsaInstanceResolvedPlanSummary?> GetResolvedPlanAsync(Guid workspaceId, Guid instanceId, string planId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ElsaInstanceDeploymentSummary>> ListDeploymentsAsync(Guid workspaceId, Guid instanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ElsaInstanceAuditEventSummary>> ListAuditAsync(
            Guid workspaceId, Guid instanceId, CancellationToken cancellationToken = default,
            int? limit = null, Guid organizationId = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingLogger : ILogger<AzureManagedElsaProvisioningProgressReader>
    {
        public List<int> EventIds { get; } = [];
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            EventIds.Add(eventId.Id);
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
