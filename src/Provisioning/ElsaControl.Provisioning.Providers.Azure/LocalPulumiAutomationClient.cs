using System.Collections.Immutable;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pulumi.Automation;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Automation API client. Uses the pinned CLI in the host image. Tests that
/// must not require the CLI inject a different <see cref="IPulumiAutomationClient"/>.
/// </summary>
public sealed class LocalPulumiAutomationClient(
    ILogger<LocalPulumiAutomationClient>? logger = null,
    IProvisioningLiveEventSink? liveEvents = null)
    : IPulumiAutomationClient
{
    private readonly ILogger<LocalPulumiAutomationClient> _logger =
        logger ?? NullLogger<LocalPulumiAutomationClient>.Instance;

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
            using var stack = await LocalWorkspace.CreateOrSelectStackAsync(args, cancellationToken).ConfigureAwait(false);
            foreach (var pair in workspace.StackConfig)
            {
                await stack.SetConfigAsync(pair.Key, new ConfigValue(pair.Value), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (workspace.SecretStackConfig is not null)
            {
                foreach (var pair in workspace.SecretStackConfig)
                {
                    await stack.SetConfigAsync(pair.Key, new ConfigValue(pair.Value, isSecret: true), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var events = new List<ProvisioningEvent>();
            await using var pump = liveEvents is null
                ? null
                : new ProvisioningLiveEventPump(operation.Id, liveEvents, _logger);
            void OnEvent(Pulumi.Automation.Events.EngineEvent engineEvent)
            {
                try
                {
                    var mapped = PulumiEngineEventMapper.Map(operation.Id, engineEvent, DateTimeOffset.UtcNow).ToList();
                    if (mapped.Count == 0)
                        return;
                    if (pump is null)
                    {
                        events.AddRange(mapped);
                        return;
                    }

                    pump.TryEnqueue(mapped);
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        "Provisioning live event mapping failed. OperationId={OperationId} ExceptionType={ExceptionType}",
                        operation.Id,
                        exception.GetType().FullName);
                }
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
                case ProvisioningCommand.Cancel:
                    await stack.CancelAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case ProvisioningCommand.Destroy:
                    await stack.DestroyAsync(new DestroyOptions { OnEvent = OnEvent }, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    return ProviderRunResult.Fail([], ProvisioningFailureCodes.NotImplemented);
            }

            if (pump is not null)
                await pump.CompleteAsync().ConfigureAwait(false);

            if (command == ProvisioningCommand.Cancel)
            {
                var cancelled = new ProvisioningEvent(
                    operation.Id,
                    ProvisioningEventKind.OperationCancelled,
                    ProvisioningCapability.ContainerRuntime,
                    "Update cancelled.",
                    DateTimeOffset.UtcNow);
                return ProviderRunResult.Ok([cancelled]);
            }

            if (pump is not null)
                return ProviderRunResult.Ok([], outputs);

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
        catch (Exception exception) when (IsNothingToCancel(exception, command))
        {
            var nothing = new ProvisioningEvent(
                operation.Id,
                ProvisioningEventKind.OperationCancelled,
                ProvisioningCapability.ContainerRuntime,
                "Nothing to cancel.",
                DateTimeOffset.UtcNow);
            return new ProviderRunResult(true, [nothing], failureCode: ProvisioningFailureCodes.NothingToCancel);
        }
        catch (Exception exception) when (PulumiLockConflict.Matches(exception))
        {
            _logger.LogError(
                "Provisioning stack lock conflict. OperationId={OperationId} ExceptionType={ExceptionType}",
                operation.Id,
                exception.GetType().FullName);
            var locked = new ProvisioningEvent(
                operation.Id,
                ProvisioningEventKind.LockConflicted,
                ProvisioningCapability.ContainerRuntime,
                "Stack lock conflict.",
                DateTimeOffset.UtcNow);
            return ProviderRunResult.Fail([locked], ProvisioningFailureCodes.StackLocked);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Provisioning provider failed. OperationId={OperationId} ExceptionType={ExceptionType}",
                operation.Id,
                exception.GetType().FullName);
            var failed = new ProvisioningEvent(
                operation.Id,
                ProvisioningEventKind.ResourceFailed,
                ProvisioningCapability.ContainerRuntime,
                "Provider failed.",
                DateTimeOffset.UtcNow);
            return ProviderRunResult.Fail([failed], ProvisioningFailureCodes.ProviderFailed);
        }
    }

    private static bool IsNothingToCancel(Exception exception, ProvisioningCommand command)
    {
        if (command != ProvisioningCommand.Cancel)
            return false;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var name = current.GetType().Name;
            if (name.Contains("NoUpdate", StringComparison.OrdinalIgnoreCase))
                return true;
            if (current.Message.Contains("no update in progress", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
