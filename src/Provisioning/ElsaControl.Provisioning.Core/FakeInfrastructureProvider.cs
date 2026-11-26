using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// In-process fake used by Core and Control unit tests. It never talks to a cloud.
/// </summary>
public sealed class FakeInfrastructureProvider : IInfrastructureProvider
{
    private readonly object _gate = new();
    private readonly HashSet<string> _stacks = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _cancelRequested = [];
    private readonly Dictionary<Guid, TaskCompletionSource> _holds = [];

    public string Id => "fake";

    public IReadOnlySet<string> Capabilities { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ProvisioningCapabilities.ContainerRuntime,
        ProvisioningCapabilities.Database,
        ProvisioningCapabilities.Secrets,
        ProvisioningCapabilities.Ingress,
        ProvisioningCapabilities.Identity,
        ProvisioningCapabilities.Platform
    };

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// When set, <see cref="ExecuteAsync"/> waits until <see cref="Release"/> or cancel.
    /// Used to contend a second Provision against a running one.
    /// </summary>
    public bool HoldExecutions { get; set; }

    public Task Release(Guid operationId)
    {
        lock (_gate)
        {
            if (_holds.TryGetValue(operationId, out var source))
                source.TrySetResult();
        }

        return Task.CompletedTask;
    }

    public async Task WaitUntilHeldAsync(Guid operationId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_holds.ContainsKey(operationId))
                    return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException("Provider did not hold the execution in time.");
    }

    public async Task ExecuteAsync(
        ProviderRunRequest request,
        IProviderEventSink events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);
        var now = DateTimeOffset.UtcNow;
        var stackKey = request.Target.Stack.Key;

        if (request.Kind == ProvisioningOperationKind.Cancel)
            return;

        await EmitCapabilityAsync(request, events, now, started: true, cancellationToken)
            .ConfigureAwait(false);

        if (HoldExecutions)
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
                _holds[request.OperationId] = hold;
            using (cancellationToken.Register(() => hold.TrySetCanceled(cancellationToken)))
                await hold.Task.ConfigureAwait(false);
        }
        else if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (_cancelRequested.Contains(request.OperationId))
                throw new OperationCanceledException();
        }

        if (request.Kind == ProvisioningOperationKind.Destroy)
        {
            var existed = false;
            lock (_gate)
                existed = _stacks.Remove(stackKey);
            if (!existed)
            {
                await events.AppendAsync(
                    new ProvisioningEvent(
                        request.OperationId,
                        ProvisioningEventKind.AbsenceProved,
                        ProvisioningReasonCodes.AbsenceProved,
                        DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        else if (request.Kind is ProvisioningOperationKind.Provision or ProvisioningOperationKind.Update)
        {
            lock (_gate)
                _stacks.Add(stackKey);
        }

        await EmitCapabilityAsync(request, events, DateTimeOffset.UtcNow, started: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task CancelAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _cancelRequested.Add(operationId);
            if (_holds.TryGetValue(operationId, out var hold))
                hold.TrySetCanceled();
        }

        return Task.CompletedTask;
    }

    private static async Task EmitCapabilityAsync(
        ProviderRunRequest request,
        IProviderEventSink events,
        DateTimeOffset occurredAt,
        bool started,
        CancellationToken cancellationToken)
    {
        var capability = request.Target.Stack.Project == ProvisioningProjects.Platform
            ? ProvisioningCapabilities.Platform
            : ProvisioningCapabilities.ContainerRuntime;
        var kind = started ? ProvisioningEventKind.ResourceStarted : ProvisioningEventKind.ResourceSucceeded;
        var code = started ? ProvisioningReasonCodes.ResourceStarted : ProvisioningReasonCodes.ResourceSucceeded;
        await events.AppendAsync(
            new ProvisioningEvent(request.OperationId, kind, code, occurredAt, capability),
            cancellationToken).ConfigureAwait(false);

        if (request.Spec.Database is not null)
        {
            await events.AppendAsync(
                new ProvisioningEvent(request.OperationId, kind, code, occurredAt, ProvisioningCapabilities.Database),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
