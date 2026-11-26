using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

internal static class ProvisioningOperationStateMachine
{
    public static bool CanStart(ProvisioningOperationStatus status) =>
        status is ProvisioningOperationStatus.Accepted or ProvisioningOperationStatus.Queued;

    public static bool CanCancel(ProvisioningOperationStatus status) =>
        status is ProvisioningOperationStatus.Accepted
            or ProvisioningOperationStatus.Queued
            or ProvisioningOperationStatus.Running;

    public static bool IsTerminal(ProvisioningOperationStatus status) =>
        status is ProvisioningOperationStatus.Succeeded
            or ProvisioningOperationStatus.Failed
            or ProvisioningOperationStatus.Cancelled
            or ProvisioningOperationStatus.StackLocked;

    public static ProvisioningOperationStatus OnStart(ProvisioningOperationStatus status)
    {
        if (!CanStart(status))
            throw new InvalidOperationException($"Cannot start a {status} operation.");
        return ProvisioningOperationStatus.Running;
    }

    public static ProvisioningOperationStatus OnCancelRequested(ProvisioningOperationStatus status)
    {
        if (IsTerminal(status))
            return status;
        if (!CanCancel(status))
            throw new InvalidOperationException($"Cannot cancel a {status} operation.");
        return status == ProvisioningOperationStatus.Running
            ? ProvisioningOperationStatus.Cancelling
            : ProvisioningOperationStatus.Cancelled;
    }

    public static ProvisioningOperationStatus OnSucceeded(ProvisioningOperationStatus status)
    {
        if (status is ProvisioningOperationStatus.Cancelling)
            return ProvisioningOperationStatus.Cancelled;
        if (status is not ProvisioningOperationStatus.Running)
            throw new InvalidOperationException($"Cannot succeed a {status} operation.");
        return ProvisioningOperationStatus.Succeeded;
    }

    public static ProvisioningOperationStatus OnFailed(ProvisioningOperationStatus status)
    {
        if (status is ProvisioningOperationStatus.Cancelling)
            return ProvisioningOperationStatus.Cancelled;
        if (status is not ProvisioningOperationStatus.Running)
            throw new InvalidOperationException($"Cannot fail a {status} operation.");
        return ProvisioningOperationStatus.Failed;
    }
}
