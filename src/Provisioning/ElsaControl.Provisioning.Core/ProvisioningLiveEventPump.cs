using System.Threading.Channels;
using ElsaControl.Provisioning.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Bounded, non-blocking buffer between Pulumi's synchronous engine-event
/// callback and Control's async ledger writes. The callback must never wait
/// on the database or throw; a sink failure is logged as a type name only.
/// </summary>
public sealed class ProvisioningLiveEventPump : IAsyncDisposable
{
    public const int DefaultCapacity = 256;

    private readonly Guid _operationId;
    private readonly IProvisioningLiveEventSink _sink;
    private readonly ILogger _logger;
    private readonly Channel<IReadOnlyList<ProvisioningEvent>> _channel;
    private readonly Task _drain;
    private readonly CancellationTokenSource _drainCts = new();

    public ProvisioningLiveEventPump(
        Guid operationId,
        IProvisioningLiveEventSink sink,
        ILogger? logger = null,
        int capacity = DefaultCapacity)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("Operation ID is required.", nameof(operationId));
        ArgumentNullException.ThrowIfNull(sink);
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _operationId = operationId;
        _sink = sink;
        _logger = logger ?? NullLogger.Instance;
        _channel = Channel.CreateBounded<IReadOnlyList<ProvisioningEvent>>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
        _drain = DrainAsync();
    }

    /// <summary>
    /// Enqueue mapped events. Never throws, never blocks.
    /// </summary>
    public bool TryEnqueue(IReadOnlyList<ProvisioningEvent> events)
    {
        try
        {
            if (events is null || events.Count == 0)
                return true;
            return _channel.Writer.TryWrite(events);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Provisioning live event enqueue failed. OperationId={OperationId} ExceptionType={ExceptionType}",
                _operationId,
                exception.GetType().FullName);
            return false;
        }
    }

    public async Task CompleteAsync()
    {
        _channel.Writer.TryComplete();
        await _drain.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _drain.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Provisioning live event drain failed. OperationId={OperationId} ExceptionType={ExceptionType}",
                _operationId,
                exception.GetType().FullName);
        }

        _drainCts.Dispose();
    }

    public static IReadOnlyList<ProvisioningEvent> Coalesce(
        IReadOnlyList<IReadOnlyList<ProvisioningEvent>> batches)
    {
        var lastByStage = new Dictionary<(ProvisioningEventKind Kind, string ResourceKind), ProvisioningEvent>();
        var order = new List<(ProvisioningEventKind Kind, string ResourceKind)>();
        foreach (var batch in batches)
        {
            foreach (var value in batch)
            {
                var key = (value.Kind, value.ResourceKind);
                if (lastByStage.TryAdd(key, value))
                    order.Add(key);
                else
                    lastByStage[key] = value;
            }
        }

        return order.Select(key => lastByStage[key]).ToArray();
    }

    private async Task DrainAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_drainCts.Token).ConfigureAwait(false))
            {
                var pending = new List<IReadOnlyList<ProvisioningEvent>>();
                while (_channel.Reader.TryRead(out var batch))
                    pending.Add(batch);
                if (pending.Count == 0)
                    continue;

                try
                {
                    await _sink
                        .PublishAsync(_operationId, Coalesce(pending), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        "Provisioning live event publish failed. OperationId={OperationId} ExceptionType={ExceptionType}",
                        _operationId,
                        exception.GetType().FullName);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host is stopping; remaining engine events are dropped.
        }
    }
}
