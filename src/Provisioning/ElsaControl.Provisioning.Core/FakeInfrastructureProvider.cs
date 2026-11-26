using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// In-process provider for Control and Core tests. Disposable platform and
/// engine stacks live in memory, including a StackReference-like read of
/// platform outputs and a contended per-stack lock.
/// </summary>
public sealed class FakeInfrastructureProvider : IInfrastructureProvider, ICredentialResolver, IStateBackend, ISecretsProvider
{
    public const string Id = "fake";

    private readonly object _gate = new();
    private readonly Dictionary<string, FakeStack> _stacks = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _cancelled = [];
    private readonly TimeProvider _timeProvider;

    public FakeInfrastructureProvider(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string ProviderId => Id;
    public string Kind => InMemoryKind;
    public string BackendUrl => "memory://provisioning";
    public bool LockingEnabled => true;
    public string SecretsProviderUrl => "memory://secrets";
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

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

    public Task<IReadOnlyDictionary<string, string>> ResolveEnvironmentAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.Kind, CredentialReference.InMemoryKind, StringComparison.Ordinal)
            && !string.Equals(reference.Kind, Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fake provider cannot resolve that credential kind.");
        }

        return Task.FromResult(EnvironmentVariables);
    }

    public Task<ProviderRunResult> PreviewAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        RunAsync(operation, request, mutate: false, cancellationToken);

    public Task<ProviderRunResult> ProvisionAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        RunAsync(operation, request, mutate: true, cancellationToken);

    public Task<ProviderRunResult> UpdateAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        RunAsync(operation, request, mutate: true, cancellationToken);

    public Task<ProviderRunResult> RefreshAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        RunAsync(operation, request, mutate: false, cancellationToken);

    public Task<ProviderRunResult> CancelAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
            _cancelled.Add(operation.Id);

        // Cancel never destroys. Idle and post-success stacks both return
        // a value-free nothing-to-cancel result.
        var nothing = Event(
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

    public Task<ProviderRunResult> DestroyAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            _stacks.Remove(StackKey(operation.StackKind, operation.StackName));
            var events = new[]
            {
                Event(operation.Id, ProvisioningEventKind.ResourceStarted, ResourceKind(request), "Destroy started.", now),
                Event(operation.Id, ProvisioningEventKind.ResourceSucceeded, ResourceKind(request), "Destroy completed.", now)
            };
            return Task.FromResult(ProviderRunResult.Ok(events, ownedResourcesAbsent: true));
        }
    }

    public bool StackExists(string stackName)
    {
        var canonical = ProvisioningStackName.Canonicalize(stackName);
        lock (_gate)
            return _stacks.Keys.Any(key => key.EndsWith("/" + canonical, StringComparison.Ordinal));
    }

    public IReadOnlyDictionary<string, string>? GetOutputs(string stackName)
    {
        var canonical = ProvisioningStackName.Canonicalize(stackName);
        lock (_gate)
        {
            var match = _stacks.FirstOrDefault(pair => pair.Key.EndsWith("/" + canonical, StringComparison.Ordinal));
            return match.Value?.Outputs;
        }
    }

    private Task<ProviderRunResult> RunAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        bool mutate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var missing = request.Spec.RequiredCapabilities.Where(capability => !Capabilities.Contains(capability)).ToArray();
        if (missing.Length > 0)
        {
            var failed = Event(
                operation.Id,
                ProvisioningEventKind.ResourceFailed,
                ProvisioningCapability.ContainerRuntime,
                "Required capability is not available.",
                _timeProvider.GetUtcNow());
            return Task.FromResult(ProviderRunResult.Fail([failed], ProvisioningFailureCodes.CapabilityMissing));
        }

        lock (_gate)
        {
            if (_cancelled.Contains(operation.Id))
            {
                var cancelled = Event(
                    operation.Id,
                    ProvisioningEventKind.OperationCancelled,
                    ResourceKind(request),
                    "Operation cancelled.",
                    _timeProvider.GetUtcNow());
                return Task.FromResult(ProviderRunResult.Fail([cancelled], ProvisioningFailureCodes.Cancelled));
            }

            IReadOnlyDictionary<string, string>? platformOutputs = null;
            if (request.Target.StackKind == ProvisioningStackKind.Engine)
            {
                var platformKey = StackKey(ProvisioningStackKind.Platform, request.Target.PlatformStackName!);
                if (!_stacks.TryGetValue(platformKey, out var platform))
                {
                    var missingPlatform = Event(
                        operation.Id,
                        ProvisioningEventKind.ResourceFailed,
                        ProvisioningCapability.SharedEnvironment,
                        "Platform stack is absent.",
                        _timeProvider.GetUtcNow());
                    return Task.FromResult(ProviderRunResult.Fail([missingPlatform], ProvisioningFailureCodes.PlatformAbsent));
                }

                platformOutputs = platform.Outputs;
            }

            var outputs = BuildOutputs(request, platformOutputs);
            if (mutate)
                _stacks[StackKey(operation.StackKind, operation.StackName)] = new FakeStack(operation.StackName, request.Target.StackKind, outputs);

            var now = _timeProvider.GetUtcNow();
            var events = new[]
            {
                Event(operation.Id, ProvisioningEventKind.ResourceStarted, ResourceKind(request), "Apply started.", now),
                Event(operation.Id, ProvisioningEventKind.ResourceSucceeded, ResourceKind(request), "Apply completed.", now)
            };
            return Task.FromResult(ProviderRunResult.Ok(events, outputs));
        }
    }

    private static IReadOnlyDictionary<string, string> BuildOutputs(
        ProvisioningRequest request,
        IReadOnlyDictionary<string, string>? platformOutputs)
    {
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in request.StackConfig)
            outputs[$"config.{pair.Key}"] = pair.Value;

        if (request.Target.StackKind == ProvisioningStackKind.Platform)
        {
            outputs["environmentName"] = request.Spec.EndpointKind ?? request.Target.StackName;
            outputs["containerEnvironmentKind"] = "shared-container-environment";
            outputs["registryKind"] = "shared-registry";
            return outputs;
        }

        outputs["engineName"] = request.Target.StackName;
        outputs["endpointKind"] = request.Spec.EndpointKind ?? "https-ingress";
        outputs["databaseKind"] = request.Spec.DatabaseKind ?? "managed-sql";
        outputs["secretsKind"] = request.Spec.SecretsKind ?? "managed-secrets";
        outputs["runtimeKind"] = "container-runtime";
        if (platformOutputs is not null
            && platformOutputs.TryGetValue("containerEnvironmentKind", out var environmentKind))
        {
            outputs["platformEnvironmentKind"] = environmentKind;
        }

        return outputs;
    }

    private static string ResourceKind(ProvisioningRequest request) =>
        request.Target.StackKind == ProvisioningStackKind.Platform
            ? ProvisioningCapability.SharedEnvironment
            : ProvisioningCapability.ContainerRuntime;

    private static ProvisioningEvent Event(
        Guid operationId,
        ProvisioningEventKind kind,
        string resourceKind,
        string message,
        DateTimeOffset occurredAt) =>
        new(operationId, kind, resourceKind, message, occurredAt);

    private static string StackKey(ProvisioningStackKind kind, string stackName) =>
        ProvisioningStackLockKey.Format(kind, ProvisioningStackName.Canonicalize(stackName));

    private const string InMemoryKind = "in-memory";

    private sealed record FakeStack(
        string Name,
        ProvisioningStackKind Kind,
        IReadOnlyDictionary<string, string> Outputs);
}
