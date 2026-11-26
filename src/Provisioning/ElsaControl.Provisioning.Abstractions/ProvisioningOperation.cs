namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Durable operation envelope. Carries no provider command body, no customer
/// identity, and no secret values.
/// </summary>
public sealed record ProvisioningOperation
{
    public ProvisioningOperation(
        Guid id,
        ProvisioningOperationKind kind,
        ProvisioningOperationStatus status,
        string idempotencyKey,
        string requestHash,
        string stackKey,
        string providerId,
        DateTimeOffset acceptedAt,
        int attemptNumber = 1,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        string? reasonCode = null,
        Guid? holdingOperationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(stackKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        Id = id;
        Kind = kind;
        Status = status;
        IdempotencyKey = idempotencyKey;
        RequestHash = requestHash;
        StackKey = stackKey;
        ProviderId = providerId;
        AcceptedAt = acceptedAt;
        AttemptNumber = attemptNumber;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        ReasonCode = reasonCode;
        HoldingOperationId = holdingOperationId;
    }

    public Guid Id { get; init; }

    public ProvisioningOperationKind Kind { get; init; }

    public ProvisioningOperationStatus Status { get; init; }

    public string IdempotencyKey { get; init; }

    public string RequestHash { get; init; }

    public string StackKey { get; init; }

    public string ProviderId { get; init; }

    public DateTimeOffset AcceptedAt { get; init; }

    public int AttemptNumber { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? ReasonCode { get; init; }

    public Guid? HoldingOperationId { get; init; }
}
