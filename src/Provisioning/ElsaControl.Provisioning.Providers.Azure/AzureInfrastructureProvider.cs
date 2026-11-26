using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Azure <see cref="IInfrastructureProvider"/>. Realization is Pulumi C# through
/// the Automation API. Operations that are not implemented fail closed.
/// </summary>
public sealed class AzureInfrastructureProvider(
    AzurePulumiAutomationRunner runner,
    ICredentialResolver credentials,
    IPulumiAutomationClient automation,
    IResourceGroupAbsenceProbe? absence = null,
    TimeProvider? timeProvider = null) : IInfrastructureProvider
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IResourceGroupAbsenceProbe _absence = absence ?? new UnknownResourceGroupAbsenceProbe();

    public string ProviderId => AzurePulumiAutomationRunner.ProviderId;

    public IReadOnlySet<string> Capabilities { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ProvisioningCapability.ContainerRuntime,
        ProvisioningCapability.ManagedSql,
        ProvisioningCapability.Secrets,
        ProvisioningCapability.Ingress,
        ProvisioningCapability.Scale,
        ProvisioningCapability.SharedEnvironment,
        ProvisioningCapability.SharedRegistry
    };

    public PulumiWorkspaceConfiguration CreateWorkspace(
        ProvisioningTarget target,
        IReadOnlyDictionary<string, string> stackConfig) =>
        runner.CreateWorkspace(target, stackConfig, new Dictionary<string, string>(StringComparer.Ordinal));

    public Task<ProviderRunResult> PreviewAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        RunAsync(ProvisioningCommand.Preview, operation, request, cancellationToken);

    public Task<ProviderRunResult> ProvisionAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        RunAsync(ProvisioningCommand.Provision, operation, request, cancellationToken);

    public Task<ProviderRunResult> UpdateAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        RunAsync(ProvisioningCommand.Update, operation, request, cancellationToken);

    public Task<ProviderRunResult> RefreshAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        RunAsync(ProvisioningCommand.Refresh, operation, request, cancellationToken);

    public Task<ProviderRunResult> CancelAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        var nothing = new ProvisioningEvent(
            operation.Id,
            ProvisioningEventKind.OperationCancelled,
            ResourceKind(request),
            "Nothing to cancel.",
            now);
        return Task.FromResult(new ProviderRunResult(
            true,
            [nothing],
            failureCode: ProvisioningFailureCodes.NothingToCancel));
    }

    public async Task<ProviderRunResult> DestroyAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(ProvisioningCommand.Destroy, operation, request, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
            return result;

        string groupName;
        try
        {
            groupName = ProvisioningEngineResourceGroup.Resolve(operation.StackName, request.StackConfig);
        }
        catch (InvalidOperationException)
        {
            var unresolved = new ProvisioningEvent(
                operation.Id,
                ProvisioningEventKind.ResourceFailed,
                "resource-group",
                "Owned resource group is not absent.",
                _timeProvider.GetUtcNow());
            return ProviderRunResult.Fail([..result.Events, unresolved], ProvisioningFailureCodes.ResourceGroupPresent);
        }

        var presence = await _absence.ObserveAsync(groupName, cancellationToken).ConfigureAwait(false);
        if (presence != ResourceGroupPresence.Absent)
        {
            var failed = new ProvisioningEvent(
                operation.Id,
                ProvisioningEventKind.ResourceFailed,
                "resource-group",
                "Owned resource group is not absent.",
                _timeProvider.GetUtcNow());
            return ProviderRunResult.Fail([..result.Events, failed], ProvisioningFailureCodes.ResourceGroupPresent);
        }

        var receipt = new ProvisioningEvent(
            operation.Id,
            ProvisioningEventKind.ResourceSucceeded,
            "resource-group",
            "Owned resource group is absent.",
            _timeProvider.GetUtcNow());
        return ProviderRunResult.Ok([..result.Events, receipt], result.Outputs, ownedResourcesAbsent: true);
    }

    private async Task<ProviderRunResult> RunAsync(
        ProvisioningCommand command,
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (command is not (ProvisioningCommand.Preview or ProvisioningCommand.Provision
            or ProvisioningCommand.Update or ProvisioningCommand.Refresh or ProvisioningCommand.Destroy))
        {
            return NotImplemented(operation, request);
        }

        var environment = await credentials.ResolveEnvironmentAsync(request.Target.Credentials, cancellationToken)
            .ConfigureAwait(false);
        var workspace = runner.CreateWorkspace(request.Target, request.StackConfig, environment);
        var program = runner.CreateProgram(request.Target.StackKind);
        return await automation.RunAsync(command, operation, workspace, program, cancellationToken)
            .ConfigureAwait(false);
    }

    private ProviderRunResult NotImplemented(ProvisioningOperation operation, ProvisioningRequest request)
    {
        var failed = new ProvisioningEvent(
            operation.Id,
            ProvisioningEventKind.ResourceFailed,
            ResourceKind(request),
            "Provider operation is not implemented.",
            _timeProvider.GetUtcNow());
        return ProviderRunResult.Fail([failed], ProvisioningFailureCodes.NotImplemented);
    }

    private static string ResourceKind(ProvisioningRequest request) =>
        request.Target.StackKind == ProvisioningStackKind.Platform
            ? ProvisioningCapability.SharedEnvironment
            : ProvisioningCapability.ContainerRuntime;
}
