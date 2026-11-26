using System.Collections.Concurrent;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core.Stores;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Appends sanitised events to the module store and, when the fence is still
/// held, to Control's existing step ledger. A stale run cannot write.
/// </summary>
public sealed class ProvisioningLiveEventSink(
    IProvisioningOperationStore operations,
    IProvisioningEventStore eventStore,
    IProvisioningStepLedger? ledger = null) : IProvisioningLiveEventSink
{
    private readonly ConcurrentDictionary<Guid, BoundRun> _runs = new();
    private readonly IProvisioningStepLedger _ledger = ledger ?? new NoOpProvisioningStepLedger();

    public IDisposable Bind(ProvisioningOperation operation, ProvisioningRequest request)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        var bound = new BoundRun(operation, request);
        _runs[operation.Id] = bound;
        return bound.DisposeAction(() => _runs.TryRemove(operation.Id, out _));
    }

    public async Task PublishAsync(
        Guid operationId,
        IReadOnlyList<ProvisioningEvent> events,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
            return;
        if (!_runs.TryGetValue(operationId, out var bound))
        {
            await eventStore.AppendAsync(events, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await operations
                .HoldsFenceAsync(bound.Operation.Id, bound.Operation.FencingToken, cancellationToken)
                .ConfigureAwait(false))
        {
            _runs.TryRemove(operationId, out _);
            return;
        }

        await eventStore.AppendAsync(events, cancellationToken).ConfigureAwait(false);
        var ordered = events
            .OrderBy(item => item.EngineSequence ?? int.MaxValue)
            .ThenBy(item => item.OccurredAt)
            .ToArray();
        await _ledger
            .TryWriteAsync(bound.Operation, bound.Request, ordered, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task HeartbeatAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_runs.TryGetValue(operationId, out var bound))
            return;
        if (!await operations
                .HoldsFenceAsync(bound.Operation.Id, bound.Operation.FencingToken, cancellationToken)
                .ConfigureAwait(false))
        {
            _runs.TryRemove(operationId, out _);
            return;
        }

        await _ledger.HeartbeatAsync(bound.Operation, bound.Request, cancellationToken).ConfigureAwait(false);
    }

    private sealed record BoundRun(ProvisioningOperation Operation, ProvisioningRequest Request)
    {
        public IDisposable DisposeAction(Action release) => new Binder(release);

        private sealed class Binder(Action release) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    release();
            }
        }
    }
}
