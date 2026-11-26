using ElsaControl.Provisioning.Abstractions;
using Pulumi.Automation;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Runs one Automation API command. Production uses the local workspace;
/// tests use the file-backend double or a mock. Never returns secret values.
/// </summary>
public interface IPulumiAutomationClient
{
    Task<ProviderRunResult> RunAsync(
        ProvisioningCommand command,
        ProvisioningOperation operation,
        PulumiWorkspaceConfiguration workspace,
        PulumiFn program,
        CancellationToken cancellationToken = default);
}
