using ElsaControl.Provisioning.Abstractions;
using Pulumi.Automation;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

internal sealed class FakePulumiAutomationClient : IPulumiAutomationClient
{
    public ProvisioningCommand? LastCommand { get; private set; }
    public PulumiWorkspaceConfiguration? LastWorkspace { get; private set; }
    public bool ThrowLockConflict { get; set; }
    public bool Succeeded { get; set; } = true;
    public TimeSpan Stall { get; set; }
    public List<ProvisioningCommand> Commands { get; } = [];
    public TaskCompletionSource Started { get; } = new();

    public async Task<ProviderRunResult> RunAsync(
        ProvisioningCommand command,
        ProvisioningOperation operation,
        PulumiWorkspaceConfiguration workspace,
        PulumiFn program,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastCommand = command;
        LastWorkspace = workspace;
        Commands.Add(command);
        if (Stall > TimeSpan.Zero
            && command is ProvisioningCommand.Provision or ProvisioningCommand.Update)
        {
            Started.TrySetResult();
            await Task.Delay(Stall, cancellationToken).ConfigureAwait(false);
        }

        if (command == ProvisioningCommand.Cancel)
        {
            return new ProviderRunResult(
                true,
                [
                    new ProvisioningEvent(
                        operation.Id,
                        ProvisioningEventKind.OperationCancelled,
                        ProvisioningCapability.ContainerRuntime,
                        "Nothing to cancel.",
                        DateTimeOffset.UtcNow)
                ],
                failureCode: ProvisioningFailureCodes.NothingToCancel);
        }

        if (ThrowLockConflict)
        {
            return ProviderRunResult.Fail(
                [
                    new ProvisioningEvent(
                        operation.Id,
                        ProvisioningEventKind.LockConflicted,
                        ProvisioningCapability.ContainerRuntime,
                        "Stack lock conflict.",
                        DateTimeOffset.UtcNow)
                ],
                ProvisioningFailureCodes.StackLocked);
        }

        var events = new[]
        {
            new ProvisioningEvent(
                operation.Id,
                ProvisioningEventKind.ResourceSucceeded,
                operation.StackKind == ProvisioningStackKind.Platform
                    ? ProvisioningCapability.SharedEnvironment
                    : ProvisioningCapability.ContainerRuntime,
                command == ProvisioningCommand.Destroy ? "Destroy completed." : "Apply completed.",
                DateTimeOffset.UtcNow)
        };
        return Succeeded
            ? ProviderRunResult.Ok(events)
            : ProviderRunResult.Fail(events, ProvisioningFailureCodes.ProviderFailed);
    }
}
