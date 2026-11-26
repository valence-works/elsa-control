using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Providers.Azure.Programs;
using Pulumi.Automation;
using Pulumi.Automation.Events;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Hosts the <c>elsa-platform</c> and <c>elsa-engine</c> programs in-process
/// through the Automation API. Blob backend, Key Vault secrets provider, and
/// workload-identity environment come from pluggable seams.
/// </summary>
public sealed class PulumiAutomationRunner : IPulumiAutomation
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, WorkspaceStack> _running = [];

    public async Task RunAsync(
        PulumiAutomationRequest request,
        IProviderEventSink events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);
        if (!request.StateBackend.SupportsLocking)
            throw new InvalidOperationException("The Azure Blob backend must enable locking.");

        var program = request.Project == ElsaPlatformStack.ProjectName
            ? PulumiFn.Create<ElsaPlatformStack>()
            : PulumiFn.Create<ElsaEngineStack>();
        var args = new InlineProgramArgs(request.Project, request.Stack, program)
        {
            ProjectSettings = new ProjectSettings(request.Project, new ProjectRuntime(ProjectRuntimeName.Dotnet))
            {
                Backend = new ProjectBackend { Url = request.StateBackend.BackendUrl }
            },
            EnvironmentVariables = Merge(request.StateBackend.Environment, request.Credential.Environment),
            SecretsProvider = request.SecretsProvider.Uri
        };

        var stack = await LocalWorkspace.CreateOrSelectStackAsync(args, cancellationToken).ConfigureAwait(false);
        foreach (var (key, value) in request.Config)
            await stack.SetConfigAsync(key, new ConfigValue(value), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

        lock (_gate)
            _running[request.OperationId] = stack;
        try
        {
            var options = CreateOptions(request.OperationId, events);
            switch (request.Kind)
            {
                case ProvisioningOperationKind.Preview:
                    await stack.PreviewAsync(new PreviewOptions { OnEvent = options }, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case ProvisioningOperationKind.Refresh:
                    await stack.RefreshAsync(new RefreshOptions { OnEvent = options }, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case ProvisioningOperationKind.Destroy:
                    await stack.DestroyAsync(new DestroyOptions { OnEvent = options }, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    await stack.UpAsync(new UpOptions { OnEvent = options }, cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            lock (_gate)
                _running.Remove(request.OperationId);
        }
    }

    public async Task CancelAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        WorkspaceStack? stack;
        lock (_gate)
            _running.TryGetValue(operationId, out stack);
        if (stack is null)
            return;
        await stack.CancelAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Action<EngineEvent> CreateOptions(Guid operationId, IProviderEventSink events) =>
        engineEvent =>
        {
            var occurredAt = DateTimeOffset.UtcNow;
            ProvisioningEvent? mapped = null;
            if (engineEvent.ResourcePreEvent?.Metadata.Type is { } startedType)
            {
                mapped = PulumiEngineEventMapper.Map(
                    operationId, occurredAt, startedType, ProvisioningEventKind.ResourceStarted);
            }
            else if (engineEvent.ResourceOutputsEvent?.Metadata.Type is { } succeededType)
            {
                mapped = PulumiEngineEventMapper.Map(
                    operationId, occurredAt, succeededType, ProvisioningEventKind.ResourceSucceeded);
            }
            else if (engineEvent.ResourceOperationFailedEvent?.Metadata.Type is { } failedType)
            {
                mapped = PulumiEngineEventMapper.Map(
                    operationId, occurredAt, failedType, ProvisioningEventKind.ResourceFailed);
            }

            if (mapped is not null)
                events.AppendAsync(mapped, CancellationToken.None).GetAwaiter().GetResult();
        };

    private static Dictionary<string, string?> Merge(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in left)
            merged[key] = value;
        foreach (var (key, value) in right)
            merged[key] = value;
        return merged;
    }
}
