using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
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

    public void BeginWatch(string dedupeIdentity)
    {
        if (string.IsNullOrWhiteSpace(dedupeIdentity))
            return;

        _results.TryRemove(dedupeIdentity, out _);
        _waiters[dedupeIdentity] = new TaskCompletionSource<ExportResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Record(IEnumerable<string> identities, ExportResult result)
    {
        foreach (var identity in identities)
        {
            if (string.IsNullOrWhiteSpace(identity))
                continue;

            _results[identity] = result;
            if (_waiters.TryRemove(identity, out var waiter))
                waiter.TrySetResult(result);
        }
    }

    public bool TryTakeSuccess(string dedupeIdentity)
    {
        if (string.IsNullOrWhiteSpace(dedupeIdentity))
            return false;
        if (_results.TryGetValue(dedupeIdentity, out var recorded))
            return recorded == ExportResult.Success;
        if (_waiters.TryGetValue(dedupeIdentity, out var waiter) &&
            waiter.Task.IsCompletedSuccessfully)
            return waiter.Task.Result == ExportResult.Success;
        return false;
    }
}

/// <summary>
/// Forwards each batch to the real Azure Monitor exporter and records that
/// batch's <see cref="ExportResult"/> against the alert identities in it.
/// Exporter retries stay at zero so the outbox owns retry.
/// </summary>
internal sealed class RecoveryRequiredAlertExportAckExporter : BaseExporter<Activity>
{
    private static readonly MethodInfo? SetParentProviderMethod =
        typeof(BaseExporter<Activity>).GetMethod(
            "SetParentProvider",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private readonly BaseExporter<Activity> _inner;
    private readonly RecoveryRequiredAlertExportAck _ack;

    public RecoveryRequiredAlertExportAckExporter(
        BaseExporter<Activity> inner,
        RecoveryRequiredAlertExportAck ack)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ack = ack ?? throw new ArgumentNullException(nameof(ack));
    }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        EnsureInnerParentProvider();
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

    protected override bool OnForceFlush(int timeoutMilliseconds)
    {
        EnsureInnerParentProvider();
        return _inner.ForceFlush(timeoutMilliseconds);
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        EnsureInnerParentProvider();
        return _inner.Shutdown(timeoutMilliseconds);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    private void EnsureInnerParentProvider()
    {
        if (ParentProvider is null || ReferenceEquals(_inner.ParentProvider, ParentProvider))
            return;
        SetParentProviderMethod?.Invoke(_inner, [ParentProvider]);
    }
}
