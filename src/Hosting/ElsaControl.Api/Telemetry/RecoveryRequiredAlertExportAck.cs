using System.Collections.Concurrent;
using System.Diagnostics;
using ElsaControl.Deployment.Core.Telemetry;
using OpenTelemetry;

namespace ElsaControl.Api.Telemetry;

/// <summary>
/// Records each Azure Monitor batch <see cref="ExportResult"/> against the
/// RecoveryRequired alert identities in that batch. A row is acknowledged
/// only when its batch returned <see cref="ExportResult.Success"/>.
/// </summary>
internal sealed class RecoveryRequiredAlertExportAck
{
    private readonly ConcurrentDictionary<string, ExportResult> _results = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ExportResult>> _waiters = new(StringComparer.Ordinal);

    public void Watch(string dedupeIdentity)
    {
        if (string.IsNullOrWhiteSpace(dedupeIdentity))
            return;

        // A recorded result is authoritative. Never erase Success (or Failure)
        // when a later watch starts — that is the ForceFlush-before-watch race.
        if (_results.ContainsKey(dedupeIdentity))
            return;

        _waiters.GetOrAdd(
            dedupeIdentity,
            static _ => new TaskCompletionSource<ExportResult>(
                TaskCreationOptions.RunContinuationsAsynchronously));
    }

    public void Record(IEnumerable<string> identities, ExportResult result)
    {
        foreach (var identity in identities)
        {
            if (string.IsNullOrWhiteSpace(identity))
                continue;

            _results[identity] = result;
            if (_waiters.TryGetValue(identity, out var waiter))
                waiter.TrySetResult(result);
        }
    }

    public bool WaitForSuccess(string dedupeIdentity, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(dedupeIdentity))
            return false;

        if (TryRead(dedupeIdentity, out var recorded))
        {
            Prune(dedupeIdentity);
            return recorded == ExportResult.Success;
        }

        if (!_waiters.TryGetValue(dedupeIdentity, out var waiter))
            return false;

        var remaining = timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout;
        try
        {
            if (!waiter.Task.Wait(remaining))
                return false;
        }
        catch (AggregateException)
        {
            return false;
        }

        if (!waiter.Task.IsCompletedSuccessfully)
            return false;

        var result = waiter.Task.Result;
        Prune(dedupeIdentity);
        return result == ExportResult.Success;
    }

    private bool TryRead(string dedupeIdentity, out ExportResult result)
    {
        if (_results.TryGetValue(dedupeIdentity, out result))
            return true;
        if (_waiters.TryGetValue(dedupeIdentity, out var waiter) &&
            waiter.Task.IsCompletedSuccessfully)
        {
            result = waiter.Task.Result;
            return true;
        }

        result = default;
        return false;
    }

    private void Prune(string dedupeIdentity)
    {
        _results.TryRemove(dedupeIdentity, out _);
        _waiters.TryRemove(dedupeIdentity, out _);
    }
}

/// <summary>
/// Forwards each batch to the real Azure Monitor exporter and records that
/// batch's <see cref="ExportResult"/> against the alert identities in it.
/// The wrapper is the registered exporter so the SDK sets
/// <see cref="BaseExporter{T}.ParentProvider"/> on it. The inner exporter
/// resolves the same resource from a sibling SDK registration.
/// Exporter retries stay at zero so the outbox owns retry.
/// </summary>
internal sealed class RecoveryRequiredAlertExportAckExporter : BaseExporter<Activity>
{
    private readonly BaseExporter<Activity> _inner;
    private readonly RecoveryRequiredAlertExportAck _ack;
    private readonly bool _ownsInner;

    public RecoveryRequiredAlertExportAckExporter(
        BaseExporter<Activity> inner,
        RecoveryRequiredAlertExportAck ack,
        bool ownsInner = true)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ack = ack ?? throw new ArgumentNullException(nameof(ack));
        _ownsInner = ownsInner;
    }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        // Processor batches are single-pass circular buffers. Copy first so
        // the real exporter still sees the same activities we ack against.
        var count = (int)Math.Clamp(batch.Count, 0, int.MaxValue);
        var items = new Activity[count];
        var identities = new List<string>(count);
        var index = 0;
        foreach (var activity in batch)
        {
            if (index < items.Length)
                items[index++] = activity;
            if (activity.OperationName == ManagedLifecycleTelemetry.RecoveryRequiredEnteredActivityName &&
                activity.GetTagItem(ManagedLifecycleTelemetry.DedupeIdentityTag) is string identity &&
                identity.Length > 0)
                identities.Add(identity);
        }

        ExportResult result;
        try
        {
            result = _inner.Export(new Batch<Activity>(items, index));
        }
        catch
        {
            _ack.Record(identities, ExportResult.Failure);
            throw;
        }

        _ack.Record(identities, result);
        return result;
    }

    protected override bool OnForceFlush(int timeoutMilliseconds) =>
        _inner.ForceFlush(timeoutMilliseconds);

    protected override bool OnShutdown(int timeoutMilliseconds) =>
        _inner.Shutdown(timeoutMilliseconds);

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsInner)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}
