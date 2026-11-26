namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Module-owned operation state machine. Independent of Control lifecycle,
/// entitlements, and customers.
/// </summary>
public static class ProvisioningOperationStateMachine
{
    private static readonly IReadOnlyDictionary<ProvisioningOperationState, ProvisioningOperationState[]> Allowed =
        new Dictionary<ProvisioningOperationState, ProvisioningOperationState[]>
        {
            [ProvisioningOperationState.Accepted] =
            [
                ProvisioningOperationState.Queued,
                ProvisioningOperationState.LockConflict,
                ProvisioningOperationState.Cancelled
            ],
            [ProvisioningOperationState.Queued] =
            [
                ProvisioningOperationState.Running,
                ProvisioningOperationState.LockConflict,
                ProvisioningOperationState.Cancelled
            ],
            [ProvisioningOperationState.Running] =
            [
                ProvisioningOperationState.Succeeded,
                ProvisioningOperationState.Failed,
                ProvisioningOperationState.Cancelling,
                ProvisioningOperationState.Cancelled
            ],
            [ProvisioningOperationState.Cancelling] =
            [
                ProvisioningOperationState.Cancelled,
                ProvisioningOperationState.Failed
            ],
            [ProvisioningOperationState.Succeeded] = [],
            [ProvisioningOperationState.Failed] = [],
            [ProvisioningOperationState.Cancelled] = [],
            [ProvisioningOperationState.LockConflict] = []
        };

    public static bool CanTransition(ProvisioningOperationState from, ProvisioningOperationState to) =>
        Allowed.TryGetValue(from, out var next) && Array.IndexOf(next, to) >= 0;

    public static ProvisioningOperation Transition(
        ProvisioningOperation operation,
        ProvisioningOperationState to,
        DateTimeOffset now,
        string? failureCode = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!CanTransition(operation.State, to))
        {
            throw new InvalidOperationException(
                $"Cannot transition provisioning operation from {operation.State} to {to}.");
        }

        return operation with
        {
            State = to,
            StartedAt = to == ProvisioningOperationState.Running ? now : operation.StartedAt,
            CompletedAt = IsTerminal(to) ? now : operation.CompletedAt,
            FailureCode = failureCode ?? operation.FailureCode
        };
    }

    public static bool IsTerminal(ProvisioningOperationState state) =>
        state is ProvisioningOperationState.Succeeded
            or ProvisioningOperationState.Failed
            or ProvisioningOperationState.Cancelled
            or ProvisioningOperationState.LockConflict;

    public static bool HoldsStackLock(ProvisioningOperationState state) =>
        state is ProvisioningOperationState.Running or ProvisioningOperationState.Cancelling;
}
