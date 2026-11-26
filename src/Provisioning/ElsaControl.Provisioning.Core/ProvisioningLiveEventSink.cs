using System.Collections.Concurrent;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core.Stores;
using Microsoft.Extensions.Logging;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Appends sanitised events to the module store and, when the fence is still
/// held, to Control's existing step ledger. Live appends are one conditional
/// write on owner plus token. A stale run cannot write. Ledger failures are
/// logged by type name and never fail the module run.
/// </summary>
public sealed class ProvisioningLiveEventSink(
    IProvisioningOperationStore operations,
    IProvisioningEventStore eventStore,
    ILogger<ProvisioningLiveEventSink> logger,
    IProvisioningStepLedger? ledger = null) : IProvisioningLiveEventSink
{
    private readonly ConcurrentDictionary<Guid, BoundRun> _runs = new();
    private readonly IProvisioningStepLedger _ledger = ledger ?? new NoOpProvisioningStepLedger();
    private readonly ILogger<ProvisioningLiveEventSink> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public IDisposable Bind(ProvisioningOperation operation, ProvisioningRequest request)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        var bound = new BoundRun(operation, request);
        _runs[operation.Id] = bound;
        return bound.DisposeAction(() => _runs.TryRemove(operation.Id, out _));
    }

    public void SetFailureCode(Guid operationId, string? failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode))
            return;
        if (!_runs.TryGetValue(operationId, out var bound))
            return;
        bound.Operation = bound.Operation with { FailureCode = failureCode.Trim() };
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

        if (!await eventStore
                .TryAppendIfFenceHeldAsync(
                    bound.Operation.Id,
                    bound.Operation.FencingToken,
                    events,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            _runs.TryRemove(operationId, out _);
            return;
        }

        await WriteLedgerAsync(bound.Operation, bound.Request, events, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PublishTerminalAsync(
        ProvisioningOperation operation,
        ProvisioningRequest? request,
        IReadOnlyList<ProvisioningEvent> events,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
            return;

        await eventStore.AppendAsync(events, cancellationToken).ConfigureAwait(false);
        var bound = _runs.TryGetValue(operation.Id, out var live) ? live : new BoundRun(operation, request);
        await WriteLedgerAsync(bound.Operation, bound.Request ?? request, events, cancellationToken)
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

        try
        {
            await _ledger.HeartbeatAsync(bound.Operation, bound.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Provisioning step ledger heartbeat failed. OperationId={OperationId} ExceptionType={ExceptionType}",
                operationId,
                exception.GetType().FullName);
        }
    }

    private async Task WriteLedgerAsync(
        ProvisioningOperation operation,
        ProvisioningRequest? request,
        IReadOnlyList<ProvisioningEvent> events,
        CancellationToken cancellationToken)
    {
        var ordered = events
            .OrderBy(item => item.EngineSequence ?? int.MaxValue)
            .ThenBy(item => item.OccurredAt)
            .ToArray();
        try
        {
            await _ledger
                .TryWriteAsync(operation, request, ordered, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Provisioning step ledger write failed. OperationId={OperationId} ExceptionType={ExceptionType}",
                operation.Id,
                exception.GetType().FullName);
        }
    }

    private sealed class BoundRun(ProvisioningOperation operation, ProvisioningRequest? request)
    {
        public ProvisioningOperation Operation { get; set; } = operation;
        public ProvisioningRequest? Request { get; } = request;

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
