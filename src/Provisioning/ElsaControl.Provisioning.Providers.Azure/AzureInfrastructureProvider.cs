using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Providers.Azure.Programs;

namespace ElsaControl.Provisioning.Providers.Azure;

public sealed class AzureInfrastructureProvider(
    IPulumiAutomation automation,
    IStateBackend stateBackend,
    ISecretsProvider secretsProvider,
    ICredentialResolver credentials) : IInfrastructureProvider
{
    public string Id => "azure";

    public IReadOnlySet<string> Capabilities { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ProvisioningCapabilities.ContainerRuntime,
        ProvisioningCapabilities.Database,
        ProvisioningCapabilities.Secrets,
        ProvisioningCapabilities.Ingress,
        ProvisioningCapabilities.Identity,
        ProvisioningCapabilities.Platform
    };

    public async Task ExecuteAsync(
        ProviderRunRequest request,
        IProviderEventSink events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);
        if (request.Kind == ProvisioningOperationKind.Cancel)
        {
            await automation.CancelAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var credential = await credentials.ResolveAsync(request.Target.Credential, cancellationToken)
            .ConfigureAwait(false);
        var pulumiRequest = new PulumiAutomationRequest(
            request.OperationId,
            request.Kind,
            request.Target.Stack.Project,
            request.Target.Stack.Name,
            StackConfig(request),
            stateBackend,
            secretsProvider,
            credential);
        await automation.RunAsync(pulumiRequest, events, cancellationToken).ConfigureAwait(false);
    }

    public Task CancelAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        automation.CancelAsync(operationId, cancellationToken);

    internal static IReadOnlyDictionary<string, string> StackConfig(ProviderRunRequest request)
    {
        var spec = request.Spec;
        var config = new Dictionary<string, string>(spec.StackConfig, StringComparer.Ordinal);
        if (request.Target.Stack.Project == ElsaPlatformStack.ProjectName)
        {
            config["environmentName"] = request.Target.Stack.Name;
            return config;
        }

        config["engineName"] = request.Target.Stack.Name;
        config["runtimeImage"] = spec.RuntimeImage;
        if (request.Target.PlatformStack is { } platform)
            config["platformStack"] = $"{platform.Project}/{platform.Name}";
        if (spec.Database is not null)
            config["databaseEngine"] = spec.Database.Engine;
        return config;
    }
}
