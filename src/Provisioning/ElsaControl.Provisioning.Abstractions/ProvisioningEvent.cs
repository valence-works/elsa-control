namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// A provider-neutral engine event. Messages and resource kinds are sanitised:
/// no secrets, subscription identifiers, resource identifiers, or endpoints.
/// </summary>
public sealed record ProvisioningEvent
{
    public ProvisioningEvent(
        Guid operationId,
        ProvisioningEventKind kind,
        string resourceKind,
        string message,
        DateTimeOffset occurredAt)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("Operation ID is required.", nameof(operationId));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        OperationId = operationId;
        Kind = kind;
        ResourceKind = resourceKind.Trim();
        Message = message.Trim();
        OccurredAt = occurredAt;
    }

    public Guid OperationId { get; }
    public ProvisioningEventKind Kind { get; }
    public string ResourceKind { get; }
    public string Message { get; }
    public DateTimeOffset OccurredAt { get; }
}
