using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Azure <see cref="IInfrastructureProvider"/>. Realization is Pulumi C# through
/// the Automation API. This skeleton maps commands onto workspace configuration
/// and sanitised engine events; full apply is #1014.
/// </summary>
public sealed class AzureInfrastructureProvider(
    AzurePulumiAutomationRunner runner,
    ICredentialResolver credentials,
    TimeProvider? timeProvider = null) : IInfrastructureProvider
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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
        CompleteAsync(operation, request, mutate: false, cancellationToken);

    public Task<ProviderRunResult> ProvisionAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(operation, request, mutate: true, cancellationToken);

    public Task<ProviderRunResult> UpdateAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(operation, request, mutate: true, cancellationToken);

    public Task<ProviderRunResult> RefreshAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(operation, request, mutate: false, cancellationToken);

    public Task CancelAsync(ProvisioningOperation operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(operation);
        return Task.CompletedTask;
    }

    public Task<ProviderRunResult> DestroyAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(operation, request, mutate: true, cancellationToken);

    private async Task<ProviderRunResult> CompleteAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        bool mutate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var environment = await credentials.ResolveEnvironmentAsync(request.Target.Credentials, cancellationToken)
            .ConfigureAwait(false);
        var workspace = runner.CreateWorkspace(request.Target, request.StackConfig, environment);
        _ = workspace.ToLocalWorkspaceOptions(runner.CreateProgram(request.Target.StackKind));

        var now = _timeProvider.GetUtcNow();
        var resourceKind = request.Target.StackKind == ProvisioningStackKind.Platform
            ? ProvisioningCapability.SharedEnvironment
            : ProvisioningCapability.ContainerRuntime;
        var started = mutate ? "Apply started." : "Preview started.";
        var completed = mutate ? "Apply completed." : "Preview completed.";
        var events = new[]
        {
            new ProvisioningEvent(operation.Id, ProvisioningEventKind.ResourceStarted, resourceKind, started, now),
            new ProvisioningEvent(operation.Id, ProvisioningEventKind.ResourceSucceeded, resourceKind, completed, now)
        };
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["project"] = workspace.ProjectName,
            ["stack"] = workspace.StackName
        };
        if (request.Target.StackKind == ProvisioningStackKind.Engine)
            outputs["platformStack"] = request.Target.PlatformStackName ?? string.Empty;
        return ProviderRunResult.Ok(events, outputs);
    }
}
