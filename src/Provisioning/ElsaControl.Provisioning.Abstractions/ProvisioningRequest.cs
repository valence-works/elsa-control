namespace ElsaControl.Provisioning.Abstractions;

public sealed record ProvisioningRequest
{
    public ProvisioningRequest(
        ProvisioningCommand command,
        EngineSpec spec,
        ProvisioningTarget target,
        string idempotencyKey,
        IReadOnlyDictionary<string, string>? stackConfig = null)
    {
        if (!Enum.IsDefined(command))
            throw new ArgumentOutOfRangeException(nameof(command));
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        Command = command;
        Spec = spec;
        Target = target;
        IdempotencyKey = idempotencyKey.Trim();
        StackConfig = stackConfig ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public ProvisioningCommand Command { get; }
    public EngineSpec Spec { get; }
    public ProvisioningTarget Target { get; }
    public string IdempotencyKey { get; }
    public IReadOnlyDictionary<string, string> StackConfig { get; }
}
