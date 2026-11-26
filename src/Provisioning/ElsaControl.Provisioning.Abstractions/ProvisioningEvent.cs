namespace ElsaControl.Provisioning.Abstractions;

public enum ProvisioningEventKind
{
    OperationQueued,
    OperationStarted,
    ResourceStarted,
    ResourceSucceeded,
    ResourceFailed,
    OperationSucceeded,
    OperationFailed,
    OperationCancelled,
    AbsenceProved
}

/// <summary>
/// Provider-neutral, sanitised event. Capability names and reason codes only.
/// No subscription identifiers, resource identifiers, endpoints, or raw logs.
/// </summary>
public sealed record ProvisioningEvent
{
    public ProvisioningEvent(
        Guid operationId,
        ProvisioningEventKind kind,
        string reasonCode,
        DateTimeOffset occurredAt,
        string? capability = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        OperationId = operationId;
        Kind = kind;
        ReasonCode = reasonCode;
        OccurredAt = occurredAt;
        Capability = capability;
    }

    public Guid OperationId { get; }

    public ProvisioningEventKind Kind { get; }

    public string ReasonCode { get; }

    public DateTimeOffset OccurredAt { get; }

    /// <summary>
    /// Capability token such as <c>container-runtime</c>, <c>database</c>,
    /// <c>secrets</c>, <c>ingress</c>, <c>identity</c>, or <c>platform</c>.
    /// </summary>
    public string? Capability { get; }
}

public static class ProvisioningCapabilities
{
    public const string ContainerRuntime = "container-runtime";
    public const string Database = "database";
    public const string Secrets = "secrets";
    public const string Ingress = "ingress";
    public const string Identity = "identity";
    public const string Platform = "platform";
}

public static class ProvisioningReasonCodes
{
    public const string OperationQueued = "provisioning.operation.queued";
    public const string OperationStarted = "provisioning.operation.started";
    public const string OperationSucceeded = "provisioning.operation.succeeded";
    public const string OperationFailed = "provisioning.operation.failed";
    public const string OperationCancelled = "provisioning.operation.cancelled";
    public const string ResourceStarted = "provisioning.resource.started";
    public const string ResourceSucceeded = "provisioning.resource.succeeded";
    public const string ResourceFailed = "provisioning.resource.failed";
    public const string StackLocked = "provisioning.stack.locked";
    public const string IdempotencyConflict = "provisioning.idempotency.conflict";
    public const string ProviderUnavailable = "provisioning.provider.unavailable";
    public const string AbsenceProved = "provisioning.absence.proved";
    public const string CancelRequested = "provisioning.cancel.requested";
}
