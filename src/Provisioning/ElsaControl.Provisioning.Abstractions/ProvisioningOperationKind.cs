namespace ElsaControl.Provisioning.Abstractions;

public enum ProvisioningOperationKind
{
    Preview,
    Provision,
    Update,
    Refresh,
    Cancel,
    Destroy
}

public enum ProvisioningOperationStatus
{
    Accepted,
    Queued,
    Running,
    Cancelling,
    Succeeded,
    Failed,
    Cancelled,
    StackLocked
}
