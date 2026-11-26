using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core.Tests;

public sealed class ProvisioningOperationStateMachineTests
{
    [Theory]
    [InlineData(ProvisioningOperationState.Accepted, ProvisioningOperationState.Queued, true)]
    [InlineData(ProvisioningOperationState.Queued, ProvisioningOperationState.Running, true)]
    [InlineData(ProvisioningOperationState.Running, ProvisioningOperationState.Succeeded, true)]
    [InlineData(ProvisioningOperationState.Running, ProvisioningOperationState.Cancelling, true)]
    [InlineData(ProvisioningOperationState.Running, ProvisioningOperationState.Cancelled, true)]
    [InlineData(ProvisioningOperationState.Cancelling, ProvisioningOperationState.Cancelled, true)]
    [InlineData(ProvisioningOperationState.Queued, ProvisioningOperationState.LockConflict, true)]
    [InlineData(ProvisioningOperationState.Succeeded, ProvisioningOperationState.Running, false)]
    [InlineData(ProvisioningOperationState.Failed, ProvisioningOperationState.Queued, false)]
    public void Allowed_transitions_are_closed(ProvisioningOperationState from, ProvisioningOperationState to, bool allowed) =>
        Assert.Equal(allowed, ProvisioningOperationStateMachine.CanTransition(from, to));

    [Fact]
    public void Transition_stamps_running_and_terminal_times()
    {
        var accepted = new ProvisioningOperation(
            Guid.NewGuid(),
            ProvisioningCommand.Provision,
            ProvisioningOperationState.Queued,
            FakeInfrastructureProvider.Id,
            ProvisioningStackKind.Engine,
            "acme-prod",
            "key-1",
            "hash-1",
            DateTimeOffset.UtcNow);
        var now = DateTimeOffset.Parse("2026-11-26T12:00:00Z");
        var running = ProvisioningOperationStateMachine.Transition(accepted, ProvisioningOperationState.Running, now);
        Assert.Equal(now, running.StartedAt);
        Assert.Null(running.CompletedAt);

        var succeeded = ProvisioningOperationStateMachine.Transition(
            running,
            ProvisioningOperationState.Succeeded,
            now.AddMinutes(1));
        Assert.Equal(now.AddMinutes(1), succeeded.CompletedAt);
        Assert.True(succeeded.IsTerminal);
    }
}
