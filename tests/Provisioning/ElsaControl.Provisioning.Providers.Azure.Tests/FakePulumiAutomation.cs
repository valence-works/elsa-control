using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Providers.Azure;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

internal sealed class FakePulumiAutomation : IPulumiAutomation
{
    private readonly object _gate = new();
    private readonly HashSet<string> _stacks = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _cancelRequested = [];
    private readonly List<PulumiAutomationRequest> _runs = [];

    public IReadOnlyList<PulumiAutomationRequest> Runs
    {
        get
        {
            lock (_gate)
                return _runs.ToArray();
        }
    }

    public async Task RunAsync(
        PulumiAutomationRequest request,
        IProviderEventSink events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _runs.Add(request);
            if (_cancelRequested.Contains(request.OperationId))
                throw new OperationCanceledException();
        }

        Assert.True(request.StateBackend.SupportsLocking);
        Assert.StartsWith("azblob://", request.StateBackend.BackendUrl, StringComparison.Ordinal);
        Assert.StartsWith("azurekeyvault://", request.SecretsProvider.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("AZURE_STORAGE_KEY", request.StateBackend.Environment.Keys);

        var resourceType = request.Project == "elsa-platform"
            ? "elsa:azure:platform:SharedEnvironment"
            : "elsa:azure:engine:DedicatedEngine";
        var started = PulumiEngineEventMapper.Map(
            request.OperationId, DateTimeOffset.UtcNow, resourceType, ProvisioningEventKind.ResourceStarted);
        if (started is not null)
            await events.AppendAsync(started, cancellationToken);

        var key = $"{request.Project}/{request.Stack}";
        var existed = false;
        lock (_gate)
        {
            existed = _stacks.Contains(key);
            if (request.Kind == ProvisioningOperationKind.Destroy)
                _stacks.Remove(key);
            else if (request.Kind is ProvisioningOperationKind.Provision or ProvisioningOperationKind.Update)
                _stacks.Add(key);
        }

        if (request.Kind == ProvisioningOperationKind.Destroy && !existed)
        {
            await events.AppendAsync(
                new ProvisioningEvent(
                    request.OperationId,
                    ProvisioningEventKind.AbsenceProved,
                    ProvisioningReasonCodes.AbsenceProved,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        var succeeded = PulumiEngineEventMapper.Map(
            request.OperationId, DateTimeOffset.UtcNow, resourceType, ProvisioningEventKind.ResourceSucceeded);
        if (succeeded is not null)
            await events.AppendAsync(succeeded, cancellationToken);
    }

    public Task CancelAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            _cancelRequested.Add(operationId);
        return Task.CompletedTask;
    }
}
