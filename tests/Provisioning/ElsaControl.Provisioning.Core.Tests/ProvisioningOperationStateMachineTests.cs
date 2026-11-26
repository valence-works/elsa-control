using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;

namespace ElsaControl.Provisioning.Core.Tests;

public sealed class ProvisioningOperationStateMachineTests
{
    [Fact]
    public void Queued_starts_then_succeeds()
    {
        var running = ProvisioningOperationStateMachine.OnStart(ProvisioningOperationStatus.Queued);
        Assert.Equal(ProvisioningOperationStatus.Running, running);
        Assert.Equal(ProvisioningOperationStatus.Succeeded, ProvisioningOperationStateMachine.OnSucceeded(running));
    }

    [Fact]
    public void Running_cancel_goes_through_cancelling()
    {
        Assert.Equal(
            ProvisioningOperationStatus.Cancelling,
            ProvisioningOperationStateMachine.OnCancelRequested(ProvisioningOperationStatus.Running));
        Assert.Equal(
            ProvisioningOperationStatus.Cancelled,
            ProvisioningOperationStateMachine.OnCancelRequested(ProvisioningOperationStatus.Queued));
    }

    [Fact]
    public void Terminal_states_cannot_start()
    {
        Assert.False(ProvisioningOperationStateMachine.CanStart(ProvisioningOperationStatus.Succeeded));
        Assert.True(ProvisioningOperationStateMachine.IsTerminal(ProvisioningOperationStatus.StackLocked));
    }
}
