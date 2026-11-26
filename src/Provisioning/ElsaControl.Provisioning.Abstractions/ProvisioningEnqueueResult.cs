namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Immediate result of enqueueing a command. The provider has not run yet.
/// </summary>
public sealed record ProvisioningEnqueueResult(
    Guid OperationId,
    ProvisioningOperationState State,
    bool Replayed,
    bool LockConflict);
