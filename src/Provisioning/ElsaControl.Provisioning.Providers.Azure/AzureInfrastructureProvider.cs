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
    IProvisioningSecretResolver? secrets = null,
    TimeProvider? timeProvider = null) : IInfrastructureProvider
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IResourceGroupAbsenceProbe _absence = absence ?? new UnknownResourceGroupAbsenceProbe();
    private readonly IProvisioningSecretResolver _secrets = secrets ?? new EmptyProvisioningSecretResolver();

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
        CancellationToken cancellationToken = default) =>
        RunAsync(ProvisioningCommand.Cancel, operation, request, cancellationToken);

    public async Task<ProviderRunResult> DestroyAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(ProvisioningCommand.Destroy, operation, request, cancellationToken)
            .ConfigureAwait(false);
        if (prepared.Result is not null)
            return prepared.Result;

        var result = await automation.RunAsync(
                ProvisioningCommand.Destroy,
                operation,
                prepared.Workspace!,
                prepared.Program!,
                cancellationToken)
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
                "Owned resource group presence is unknown.",
                _timeProvider.GetUtcNow());
            return ProviderRunResult.Fail([..result.Events, unresolved], ProvisioningFailureCodes.ResourceGroupUnknown);
        }

        prepared.Workspace!.EnvironmentVariables.TryGetValue("ARM_SUBSCRIPTION_ID", out var subscription);
        if (string.IsNullOrWhiteSpace(subscription))
            prepared.Workspace.EnvironmentVariables.TryGetValue("AZURE_SUBSCRIPTION_ID", out subscription);
        var presence = await _absence
            .ObserveAsync(groupName, cancellationToken, subscription)
            .ConfigureAwait(false);
        if (presence == ResourceGroupPresence.Absent)
        {
            var receipt = new ProvisioningEvent(
                operation.Id,
                ProvisioningEventKind.ResourceSucceeded,
                "resource-group",
                "Owned resource group is absent.",
                _timeProvider.GetUtcNow());
            return ProviderRunResult.Ok([..result.Events, receipt], result.Outputs, ownedResourcesAbsent: true);
        }

        if (presence == ResourceGroupPresence.Present)
        {
            var present = new ProvisioningEvent(
                operation.Id,
                ProvisioningEventKind.ResourceFailed,
                "resource-group",
                "Owned resource group is still present.",
                _timeProvider.GetUtcNow());
            return ProviderRunResult.Fail([..result.Events, present], ProvisioningFailureCodes.ResourceGroupPresent);
        }

        var unknown = new ProvisioningEvent(
            operation.Id,
            ProvisioningEventKind.ResourceFailed,
            "resource-group",
            "Owned resource group presence is unknown.",
            _timeProvider.GetUtcNow());
        return ProviderRunResult.Fail([..result.Events, unknown], ProvisioningFailureCodes.ResourceGroupUnknown);
    }

    private async Task<ProviderRunResult> RunAsync(
        ProvisioningCommand command,
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(command, operation, request, cancellationToken).ConfigureAwait(false);
        if (prepared.Result is not null)
            return prepared.Result;
        return await automation.RunAsync(command, operation, prepared.Workspace!, prepared.Program!, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(
        ProviderRunResult? Result,
        PulumiWorkspaceConfiguration? Workspace,
        Pulumi.Automation.PulumiFn? Program)> PrepareAsync(
        ProvisioningCommand command,
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (command is not (ProvisioningCommand.Preview or ProvisioningCommand.Provision
            or ProvisioningCommand.Update or ProvisioningCommand.Refresh
            or ProvisioningCommand.Cancel or ProvisioningCommand.Destroy))
        {
            return (NotImplemented(operation, request), null, null);
        }

        var environment = await credentials.ResolveEnvironmentAsync(request.Target.Credentials, cancellationToken)
            .ConfigureAwait(false);
        var secretConfig = await _secrets.ResolveSecretConfigAsync(request.StackConfig, cancellationToken)
            .ConfigureAwait(false);
        var workspace = runner.CreateWorkspace(request.Target, request.StackConfig, environment, secretConfig);
        var program = runner.CreateProgram(request.Target.StackKind);
        return (null, workspace, program);
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
