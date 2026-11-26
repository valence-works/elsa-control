using ElsaControl.Provisioning.Abstractions;
using Pulumi.Automation;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

internal sealed class FakePulumiAutomationClient : IPulumiAutomationClient
{
    public ProvisioningCommand? LastCommand { get; private set; }
    public PulumiWorkspaceConfiguration? LastWorkspace { get; private set; }
    public bool ThrowLockConflict { get; set; }
    public bool Succeeded { get; set; } = true;

    public Task<ProviderRunResult> RunAsync(
        ProvisioningCommand command,
        ProvisioningOperation operation,
        PulumiWorkspaceConfiguration workspace,
        PulumiFn program,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastCommand = command;
        LastWorkspace = workspace;
        if (ThrowLockConflict)
        {
            return Task.FromResult(ProviderRunResult.Fail(
                [
                    new ProvisioningEvent(
                        operation.Id,
                        ProvisioningEventKind.LockConflicted,
                        ProvisioningCapability.ContainerRuntime,
                        "Stack lock conflict.",
                        DateTimeOffset.UtcNow)
                ],
                ProvisioningFailureCodes.StackLocked));
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
        return Task.FromResult(Succeeded
            ? ProviderRunResult.Ok(events)
            : ProviderRunResult.Fail(events, ProvisioningFailureCodes.ProviderFailed));
    }
}
