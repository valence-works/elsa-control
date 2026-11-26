namespace ElsaControl.Provisioning.Abstractions;

public enum ProvisioningOperationState
{
    Accepted = 0,
    Queued = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelling = 5,
    Cancelled = 6,
    LockConflict = 7
}
