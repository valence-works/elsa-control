using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core.Stores;

namespace ElsaControl.Provisioning.Core.Tests;

public sealed class ProvisioningLiveEventSinkTests
{
    [Fact]
    public async Task Bound_events_are_forwarded_to_the_ledger_in_engine_sequence_order()
    {
        var store = new InMemoryProvisioningStore();
        var ledger = new RecordingLedger();
        var sink = new ProvisioningLiveEventSink(store, store, ledger);
        var (operation, request) = await ClaimAsync(store);

        using (sink.Bind(operation, request))
        {
            await sink.PublishAsync(operation.Id, [
                Event(operation.Id, 3, ProvisioningEventKind.ResourceSucceeded, ProvisioningCapability.ContainerRuntime),
                Event(operation.Id, 1, ProvisioningEventKind.ResourceStarted, "resource-group"),
                Event(operation.Id, 2, ProvisioningEventKind.ResourceSucceeded, "resource-group")
            ]);
        }

        var stored = await store.ListAsync(operation.Id);
        Assert.Equal(new int?[] { 3, 1, 2 }, stored.Select(item => item.EngineSequence).ToArray());
        var written = Assert.Single(ledger.Writes);
        Assert.Equal(new int?[] { 1, 2, 3 }, written.Select(item => item.EngineSequence).ToArray());
    }

    [Fact]
    public async Task A_stale_fence_cannot_write_ledger_rows_or_events()
    {
        var operations = new FenceStore { HoldsFence = false };
        var events = new RecordingEventStore();
        var ledger = new RecordingLedger();
        var sink = new ProvisioningLiveEventSink(operations, events, ledger);
        var operation = Operation();
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.EngineTarget(),
            ProvisioningFixtures.EngineSpec(),
            "sink-fence");

        using (sink.Bind(operation, request))
            await sink.PublishAsync(operation.Id, [Event(operation.Id, 1, ProvisioningEventKind.ResourceStarted, "resource-group")]);

        Assert.Empty(events.Items);
        Assert.Empty(ledger.Writes);
    }

    [Fact]
    public async Task The_worker_publishes_through_the_bound_sink_while_the_fence_is_held()
    {
        var store = new InMemoryProvisioningStore();
        var ledger = new RecordingLedger();
        var sink = new ProvisioningLiveEventSink(store, store, ledger);
        var worker = new ProvisioningOperationWorker(
            store,
            store,
            new FakeInfrastructureProvider(),
            liveEvents: sink);
        var service = new ProvisioningService(store, store);
        await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget(),
            ProvisioningFixtures.PlatformSpec(),
            "sink-worker"));

        Assert.Equal(1, await worker.ProcessOnceAsync());
        var published = ledger.Writes.SelectMany(item => item).ToArray();
        Assert.Contains(published, item => item.Kind == ProvisioningEventKind.OperationStarted);
        Assert.Contains(published, item => item.Kind == ProvisioningEventKind.ResourceSucceeded);
    }

    [Fact]
    public async Task Duplicate_engine_sequences_are_still_published_to_the_ledger_once_the_ledger_dedupes()
    {
        var store = new InMemoryProvisioningStore();
        var ledger = new RecordingLedger { Result = ProvisioningStepLedgerResult.Duplicate };
        var sink = new ProvisioningLiveEventSink(store, store, ledger);
        var (operation, request) = await ClaimAsync(store);
        var started = Event(operation.Id, 1, ProvisioningEventKind.ResourceStarted, "resource-group");

        using (sink.Bind(operation, request))
        {
            await sink.PublishAsync(operation.Id, [started]);
            await sink.PublishAsync(operation.Id, [started]);
        }

        Assert.Equal(2, ledger.Writes.Count);
        Assert.Equal(2, (await store.ListAsync(operation.Id)).Count);
    }

    private static async Task<(ProvisioningOperation Operation, ProvisioningRequest Request)> ClaimAsync(
        InMemoryProvisioningStore store)
    {
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.EngineTarget(),
            ProvisioningFixtures.EngineSpec(),
            "sink-live");
        var enqueued = await store.CreateOrGetAsync(request, "hash", DateTimeOffset.UtcNow);
        var claimed = await store.ClaimAsync(enqueued.OperationId, DateTimeOffset.UtcNow);
        return (claimed!, request);
    }

    private static ProvisioningEvent Event(
        Guid operationId,
        int sequence,
        ProvisioningEventKind kind,
        string resourceKind) =>
        new(operationId, kind, resourceKind, "Resource started.", DateTimeOffset.UtcNow, sequence);

    private static ProvisioningOperation Operation() =>
        new(
            Guid.NewGuid(),
            ProvisioningCommand.Provision,
            ProvisioningOperationState.Running,
            FakeInfrastructureProvider.Id,
            ProvisioningStackKind.Engine,
            ProvisioningFixtures.EngineName(),
            "sink-op",
            "hash",
            DateTimeOffset.UtcNow)
        {
            FencingToken = 1
        };

    private sealed class RecordingLedger : IProvisioningStepLedger
    {
        public ProvisioningStepLedgerResult Result { get; init; } = ProvisioningStepLedgerResult.Written;
        public List<IReadOnlyList<ProvisioningEvent>> Writes { get; } = [];

        public Task<ProvisioningStepLedgerResult> TryWriteAsync(
            ProvisioningOperation operation,
            ProvisioningRequest? request,
            IReadOnlyList<ProvisioningEvent> events,
            CancellationToken cancellationToken = default)
        {
            Writes.Add(events);
            return Task.FromResult(Result);
        }

        public Task HeartbeatAsync(
            ProvisioningOperation operation,
            ProvisioningRequest? request,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingEventStore : IProvisioningEventStore
    {
        public List<ProvisioningEvent> Items { get; } = [];

        public Task AppendAsync(IReadOnlyList<ProvisioningEvent> events, CancellationToken cancellationToken = default)
        {
            Items.AddRange(events);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProvisioningEvent>> ListAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProvisioningEvent>>(Items.Where(item => item.OperationId == operationId).ToArray());
    }

    private sealed class FenceStore : IProvisioningOperationStore
    {
        public bool HoldsFence { get; set; }

        public Task<bool> HoldsFenceAsync(Guid operationId, long fencingToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(HoldsFence);

        public Task<ProvisioningEnqueueResult> CreateOrGetAsync(ProvisioningRequest request, string requestHash, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ProvisioningOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProvisioningOperation?>(null);
        public Task<IReadOnlyList<ProvisioningOperation>> ListRunnableAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProvisioningOperation>>([]);
        public Task<ProvisioningOperation?> ClaimAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProvisioningOperation?>(null);
        public Task<ProvisioningOperation?> CompleteAsync(Guid operationId, ProvisioningOperationState state, DateTimeOffset now, string? failureCode, CancellationToken cancellationToken = default, bool stateLockMayBeHeld = false, long? fencingToken = null) =>
            Task.FromResult<ProvisioningOperation?>(null);
        public Task<ProvisioningRequest?> GetRequestAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProvisioningRequest?>(null);
        public Task<ProvisioningOperation?> FindRunningAsync(ProvisioningStackKind kind, string stackName, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProvisioningOperation?>(null);
        public Task<ProvisioningOperation?> FindQueuedAsync(ProvisioningStackKind kind, string stackName, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProvisioningOperation?>(null);
        public Task<bool> RenewLeaseAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public Task RequestCancelAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<bool> IsCancelRequestedAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public Task<IReadOnlyList<ProvisioningOperation>> ListStateLockPendingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProvisioningOperation>>([]);
        public Task<bool> MarkStateLockClearedAsync(Guid operationId, CancellationToken cancellationToken = default, long? fencingToken = null) =>
            Task.FromResult(false);
        public Task<IReadOnlyList<ProvisioningOperation>> FailInterruptedAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProvisioningOperation>>([]);
    }
}
