namespace ElsaControl.Provisioning.Abstractions;

public enum ProvisioningEventKind
{
    OperationQueued = 0,
    OperationStarted = 1,
    ResourceStarted = 2,
    ResourceSucceeded = 3,
    ResourceFailed = 4,
    OperationSucceeded = 5,
    OperationFailed = 6,
    OperationCancelled = 7,
    LockConflicted = 8
}
