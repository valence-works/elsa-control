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
    void Send(RecoveryRequiredAlertDispatch item);
}

public readonly record struct RecoveryRequiredAlertDispatch(
    Guid WorkspaceId,
    Guid InstanceId,
    Guid OperationId,
    int AttemptNumber,
    Guid? RunId = null);

/// <summary>
/// Exponential backoff for undelivered outbox rows. The first retry waits
/// one second; later retries double up to five minutes.
/// </summary>
public static class RecoveryRequiredAlertBackoff
{
    public static readonly TimeSpan Initial = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Cap = TimeSpan.FromMinutes(5);

    public static TimeSpan Delay(int deliveryAttempts)
    {
        if (deliveryAttempts < 1)
            deliveryAttempts = 1;
        var seconds = Math.Min(Cap.TotalSeconds, Initial.TotalSeconds * Math.Pow(2, deliveryAttempts - 1));
        return TimeSpan.FromSeconds(seconds);
    }
}

public sealed class ActivityRecoveryRequiredAlertSender : IRecoveryRequiredAlertSender
{
    public void Send(RecoveryRequiredAlertDispatch item) =>
        ManagedLifecycleRecoveryRequiredAlert.RecordEntered(
            item.WorkspaceId,
            item.InstanceId,
            item.OperationId,
            item.AttemptNumber,
            item.RunId);
}
