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
    /// <see langword="true"/> when the transport acknowledged delivery.
    /// Creating an in-memory activity is not an acknowledgement.
    /// </returns>
    bool Send(RecoveryRequiredAlertDispatch item);
}

/// <summary>
/// Confirms the Azure Monitor exporter accepted the RecoveryRequired span.
/// </summary>
public interface IRecoveryRequiredAlertTransportAck
{
    bool TryAcknowledge();
}

/// <summary>
/// Confirms a direct email transport accepted the alert when a recipient
/// is configured. The Azure Monitor action group is the production email
/// path; this seam exists for hosts that send mail themselves.
/// </summary>
public interface IRecoveryRequiredAlertEmailTransport
{
    bool TryAccept(RecoveryRequiredAlertDispatch item);
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
/// one second; later retries double up to five minutes. A claim lease
/// keeps two dispatch loops from sending the same row.
/// </summary>
public static class RecoveryRequiredAlertBackoff
{
    public static readonly TimeSpan Initial = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Cap = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    public static TimeSpan Delay(int deliveryAttempts)
    {
        if (deliveryAttempts < 1)
            deliveryAttempts = 1;
        var seconds = Math.Min(Cap.TotalSeconds, Initial.TotalSeconds * Math.Pow(2, deliveryAttempts - 1));
        return TimeSpan.FromSeconds(seconds);
    }
}

public sealed class ActivityRecoveryRequiredAlertSender(
    IRecoveryRequiredAlertTransportAck? ack = null,
    IRecoveryRequiredAlertEmailTransport? email = null) : IRecoveryRequiredAlertSender
{
    public bool Send(RecoveryRequiredAlertDispatch item)
    {
        if (string.IsNullOrWhiteSpace(item.DedupeIdentity))
            return false;

        ManagedLifecycleRecoveryRequiredAlert.RecordEntered(
            item.WorkspaceId,
            item.InstanceId,
            item.OperationId,
            item.AttemptNumber,
            item.RunId,
            item.DedupeIdentity);
        var flushed = ack?.TryAcknowledge() ?? false;
        var emailed = email?.TryAccept(item) ?? false;
        return flushed || emailed;
    }
}
