using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Safe customer-facing projection used to choose a managed instance. Binding
/// values are null unless persistence can prove that the current endpoint and
/// identity binding agree.
/// </summary>
public sealed record ManagedElsaInstanceSummary(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid InstanceId,
    string Name,
    string Slug,
    ElsaDesiredLifecycle DesiredLifecycle,
    ElsaObservedLifecycle ObservedLifecycle,
    ElsaInstanceHealth Health,
    string? Audience,
    Uri? CallbackUri,
    int? BindingVersion)
{
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public interface IManagedElsaInstanceCatalog
{
    Task<IReadOnlyList<ManagedElsaInstanceSummary>> ListAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);
}
