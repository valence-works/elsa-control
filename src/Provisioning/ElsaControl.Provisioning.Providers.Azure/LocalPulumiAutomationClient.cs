using System.Collections.Immutable;
using ElsaControl.Provisioning.Abstractions;
using Pulumi.Automation;
using Pulumi.Automation.Commands.Exceptions;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Automation API client. Uses the pinned CLI in the host image. Tests that
/// must not require the CLI inject a different <see cref="IPulumiAutomationClient"/>.
/// </summary>
public sealed class LocalPulumiAutomationClient : IPulumiAutomationClient
{
    public async Task<ProviderRunResult> RunAsync(
        ProvisioningCommand command,
        ProvisioningOperation operation,
        PulumiWorkspaceConfiguration workspace,
        PulumiFn program,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(program);
        cancellationToken.ThrowIfCancellationRequested();

        var options = workspace.ToLocalWorkspaceOptions(program);
        var args = new InlineProgramArgs(workspace.ProjectName, workspace.StackName, program)
        {
            ProjectSettings = options.ProjectSettings,
            StackSettings = options.StackSettings,
            EnvironmentVariables = options.EnvironmentVariables,
            SecretsProvider = options.SecretsProvider
        };

        try
        {
            var stack = await LocalWorkspace.CreateOrSelectStackAsync(args, cancellationToken).ConfigureAwait(false);
            foreach (var pair in workspace.StackConfig)
            {
                await stack.SetConfigAsync(pair.Key, new ConfigValue(pair.Value), cancellationToken)
                    .ConfigureAwait(false);
            }

            var events = new List<ProvisioningEvent>();
            void OnEvent(Pulumi.Automation.Events.EngineEvent engineEvent)
            {
                events.AddRange(PulumiEngineEventMapper.Map(operation.Id, engineEvent, DateTimeOffset.UtcNow));
            }

            Dictionary<string, string>? outputs = null;
            switch (command)
            {
                case ProvisioningCommand.Preview:
                    await stack.PreviewAsync(new PreviewOptions { OnEvent = OnEvent }, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case ProvisioningCommand.Provision:
                case ProvisioningCommand.Update:
                    var up = await stack.UpAsync(new UpOptions { OnEvent = OnEvent }, cancellationToken)
                        .ConfigureAwait(false);
                    outputs = SafeOutputs(up.Outputs);
                    break;
                case ProvisioningCommand.Refresh:
                    await stack.RefreshAsync(new RefreshOptions { OnEvent = OnEvent }, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case ProvisioningCommand.Destroy:
                    await stack.DestroyAsync(new DestroyOptions { OnEvent = OnEvent }, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    return ProviderRunResult.Fail([], ProvisioningFailureCodes.NotImplemented);
            }

            if (events.Count == 0)
            {
                events.Add(new ProvisioningEvent(
                    operation.Id,
                    ProvisioningEventKind.ResourceSucceeded,
                    operation.StackKind == ProvisioningStackKind.Platform
                        ? ProvisioningCapability.SharedEnvironment
                        : ProvisioningCapability.ContainerRuntime,
                    command == ProvisioningCommand.Preview ? "Preview completed." : "Apply completed.",
                    DateTimeOffset.UtcNow));
            }

            return ProviderRunResult.Ok(events, outputs);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ConcurrentUpdateException)
        {
            var locked = new ProvisioningEvent(
                operation.Id,
                ProvisioningEventKind.LockConflicted,
                ProvisioningCapability.ContainerRuntime,
                "Stack lock conflict.",
                DateTimeOffset.UtcNow);
            return ProviderRunResult.Fail([locked], ProvisioningFailureCodes.StackLocked);
        }
        catch (Exception)
        {
            var failed = new ProvisioningEvent(
                operation.Id,
                ProvisioningEventKind.ResourceFailed,
                ProvisioningCapability.ContainerRuntime,
                "Provider failed.",
                DateTimeOffset.UtcNow);
            return ProviderRunResult.Fail([failed], ProvisioningFailureCodes.ProviderFailed);
        }
    }

    private static Dictionary<string, string> SafeOutputs(IImmutableDictionary<string, OutputValue> outputs)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in outputs)
        {
            if (pair.Value.IsSecret)
                continue;
            var text = pair.Value.Value?.ToString();
            if (string.IsNullOrWhiteSpace(text))
                continue;
            if (ElsaControl.Provisioning.Core.ProvisioningEventSanitizer.ContainsSensitiveToken(pair.Key)
                || ElsaControl.Provisioning.Core.ProvisioningEventSanitizer.ContainsSensitiveToken(text))
            {
                continue;
            }

            result[pair.Key] = text;
        }

        return result;
    }
}
