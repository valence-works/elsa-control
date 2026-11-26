namespace ElsaControl.Provisioning.Abstractions;

public sealed record ProvisioningOperation
{
    public ProvisioningOperation(
        Guid id,
        ProvisioningCommand command,
        ProvisioningOperationState state,
        string providerId,
        ProvisioningStackKind stackKind,
        string stackName,
        string idempotencyKey,
        string requestHash,
        DateTimeOffset acceptedAt,
        int attemptNumber = 1,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        string? failureCode = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Operation ID is required.", nameof(id));
        if (!Enum.IsDefined(command))
            throw new ArgumentOutOfRangeException(nameof(command));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        if (!Enum.IsDefined(stackKind))
            throw new ArgumentOutOfRangeException(nameof(stackKind));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stackName);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        if (attemptNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));

        Id = id;
        Command = command;
        State = state;
        ProviderId = providerId.Trim();
        StackKind = stackKind;
        StackName = stackName.Trim();
        IdempotencyKey = idempotencyKey.Trim();
        RequestHash = requestHash.Trim();
        AcceptedAt = acceptedAt;
        AttemptNumber = attemptNumber;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        FailureCode = string.IsNullOrWhiteSpace(failureCode) ? null : failureCode.Trim();
    }

    public Guid Id { get; }
    public ProvisioningCommand Command { get; }
    public ProvisioningOperationState State { get; init; }
    public string ProviderId { get; }
    public ProvisioningStackKind StackKind { get; }
    public string StackName { get; }
    public string IdempotencyKey { get; }
    public string RequestHash { get; }
    public DateTimeOffset AcceptedAt { get; }
    public int AttemptNumber { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? FailureCode { get; init; }
    public bool CancelRequested { get; init; }
    public bool StateLockMayBeHeld { get; init; }
    public string? LeaseOwner { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public long FencingToken { get; init; }

    public bool IsTerminal =>
        State is ProvisioningOperationState.Succeeded
            or ProvisioningOperationState.Failed
            or ProvisioningOperationState.Cancelled
            or ProvisioningOperationState.LockConflict;
}
