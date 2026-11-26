namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Provider extension point. Azure is first; AWS, GCP, and on-prem implement
/// this contract later with no change to Core.
/// </summary>
public interface IInfrastructureProvider
{
    string Id { get; }

    IReadOnlySet<string> Capabilities { get; }

    Task ExecuteAsync(
        ProviderRunRequest request,
        IProviderEventSink events,
        CancellationToken cancellationToken = default);

    Task CancelAsync(Guid operationId, CancellationToken cancellationToken = default);
}

public sealed record ProviderRunRequest
{
    public ProviderRunRequest(
        Guid operationId,
        ProvisioningOperationKind kind,
        EngineSpec spec,
        ProvisioningTarget target)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(target);
        OperationId = operationId;
        Kind = kind;
        Spec = spec;
        Target = target;
    }

    public Guid OperationId { get; }

    public ProvisioningOperationKind Kind { get; }

    public EngineSpec Spec { get; }

    public ProvisioningTarget Target { get; }
}

public interface IProviderEventSink
{
    Task AppendAsync(ProvisioningEvent @event, CancellationToken cancellationToken = default);
}
