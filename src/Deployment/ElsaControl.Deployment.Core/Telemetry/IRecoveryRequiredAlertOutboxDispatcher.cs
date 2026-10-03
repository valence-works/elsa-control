namespace ElsaControl.Deployment.Core.Telemetry;

/// <summary>
/// Sends durable RecoveryRequired outbox rows after catalog commit.
/// A crash between commit and send leaves <c>SentAt</c> empty so the next
/// tick still delivers.
/// </summary>
public interface IRecoveryRequiredAlertOutboxDispatcher
{
    Task<int> DispatchPendingAsync(int limit = 32, CancellationToken cancellationToken = default);
}

public interface IRecoveryRequiredAlertSender
{
    /// <returns>
    /// <see langword="true"/> when the transport acknowledged delivery
    /// for this row's persisted identity. Creating an in-memory activity
    /// is not an acknowledgement.
    /// </returns>
    bool Send(RecoveryRequiredAlertDispatch item);
}

/// <summary>
/// Confirms the Azure Monitor exporter accepted the RecoveryRequired span
/// for a specific persisted identity. A successful <c>ForceFlush</c> is
/// not an acknowledgement.
/// </summary>
public interface IRecoveryRequiredAlertTransportAck
{
    /// <summary>
    /// Registers interest in this identity before the span is emitted so a
    /// concurrent flush cannot record Success that a later watch would erase.
    /// </summary>
    void Watch(string dedupeIdentity)
    {
    }

    bool TryAcknowledge(string dedupeIdentity);
}

/// <summary>
/// Coalescing nudge so catalog commit can ask the dispatcher to run now
/// without sending the span itself.
/// </summary>
public interface IRecoveryRequiredAlertDispatchSignal
{
    void Notify();
    Task WaitAsync(CancellationToken cancellationToken = default);
}

public sealed class RecoveryRequiredAlertDispatchSignal : IRecoveryRequiredAlertDispatchSignal
{
    public static RecoveryRequiredAlertDispatchSignal Instance { get; } = new();

    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken = default) =>
        _signal.WaitAsync(cancellationToken);
}

public readonly record struct RecoveryRequiredAlertDispatch(
    Guid WorkspaceId,
    Guid InstanceId,
    Guid OperationId,
    int AttemptNumber,
    Guid? RunId,
    string DedupeIdentity);

/// <summary>
/// Exponential backoff for undelivered outbox rows. The first retry waits
/// one second; later retries double up to five minutes. A per-row claim
/// lease covers the send timeout plus margin so two dispatch loops do not
/// send the same row.
/// </summary>
public static class RecoveryRequiredAlertBackoff
{
    public static readonly TimeSpan Initial = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Cap = TimeSpan.FromMinutes(5);
    /// <summary>
    /// Single timeout budget. Export, flush/await, and the lease send window
    /// are derived from this value so export ≤ wait &lt; lease.
    /// </summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ExportTimeout = AckTimeout;
    public static readonly TimeSpan WaitTimeout = AckTimeout;
    public static readonly TimeSpan SendTimeout = WaitTimeout;
    public static readonly TimeSpan LeaseMargin = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan LeaseDuration = SendTimeout + LeaseMargin;

    public static TimeSpan Delay(int deliveryAttempts)
    {
        if (deliveryAttempts < 1)
            deliveryAttempts = 1;
        var seconds = Math.Min(Cap.TotalSeconds, Initial.TotalSeconds * Math.Pow(2, deliveryAttempts - 1));
        return TimeSpan.FromSeconds(seconds);
    }
}

public sealed class ActivityRecoveryRequiredAlertSender(
    IRecoveryRequiredAlertTransportAck? ack = null) : IRecoveryRequiredAlertSender
{
    public bool Send(RecoveryRequiredAlertDispatch item)
    {
        if (string.IsNullOrWhiteSpace(item.DedupeIdentity))
            return false;

        ack?.Watch(item.DedupeIdentity);
        ManagedLifecycleRecoveryRequiredAlert.RecordEntered(
            item.WorkspaceId,
            item.InstanceId,
            item.OperationId,
            item.AttemptNumber,
            item.RunId,
            item.DedupeIdentity);
        return ack?.TryAcknowledge(item.DedupeIdentity) ?? false;
    }
}
