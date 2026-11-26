namespace ElsaControl.Provisioning.Abstractions;

public sealed record ProvisioningCommand
{
    public ProvisioningCommand(
        ProvisioningOperationKind kind,
        EngineSpec spec,
        ProvisioningTarget target,
        string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        Kind = kind;
        Spec = spec;
        Target = target;
        IdempotencyKey = idempotencyKey;
    }

    public ProvisioningOperationKind Kind { get; init; }

    public EngineSpec Spec { get; init; }

    public ProvisioningTarget Target { get; init; }

    public string IdempotencyKey { get; init; }
}

public abstract record ProvisioningCommandResult
{
    public sealed record Accepted(ProvisioningOperation Operation) : ProvisioningCommandResult;

    public sealed record Replayed(ProvisioningOperation Operation) : ProvisioningCommandResult;

    public sealed record StackLocked(string StackKey, Guid HoldingOperationId) : ProvisioningCommandResult;

    public sealed record Conflict(string ReasonCode) : ProvisioningCommandResult;
}
