namespace ElsaControl.Provisioning.Persistence.EntityFrameworkCore;

internal sealed class ProvisioningOperationEntity
{
    public Guid Id { get; set; }
    public string Command { get; set; } = "";
    public string State { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string StackKind { get; set; } = "";
    public string StackName { get; set; } = "";
    public string LockKey { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureCode { get; set; }
    public string RequestJson { get; set; } = "";
}

internal sealed class ProvisioningEventEntity
{
    public long Id { get; set; }
    public Guid OperationId { get; set; }
    public string Kind { get; set; } = "";
    public string ResourceKind { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class ProvisioningLockEntity
{
    public string LockKey { get; set; } = "";
    public Guid OperationId { get; set; }
}
