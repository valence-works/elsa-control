using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Fail-closed stand-in when the provisioning worker is off. Never reports
/// success for work it did not do.
/// </summary>
public sealed class UnconfiguredInfrastructureProvider : IInfrastructureProvider
{
    public const string Id = "unconfigured";

    public string ProviderId => Id;

    public IReadOnlySet<string> Capabilities { get; } = new HashSet<string>(StringComparer.Ordinal);

    public Task<ProviderRunResult> PreviewAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        NotImplemented(operation, request, cancellationToken);

    public Task<ProviderRunResult> ProvisionAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        NotImplemented(operation, request, cancellationToken);

    public Task<ProviderRunResult> UpdateAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        NotImplemented(operation, request, cancellationToken);

    public Task<ProviderRunResult> RefreshAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        NotImplemented(operation, request, cancellationToken);

    public Task<ProviderRunResult> CancelAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        NotImplemented(operation, request, cancellationToken);

    public Task<ProviderRunResult> DestroyAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default) =>
        NotImplemented(operation, request, cancellationToken);

    private static Task<ProviderRunResult> NotImplemented(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var failed = new ProvisioningEvent(
            operation.Id,
            ProvisioningEventKind.ResourceFailed,
            ProvisioningCapability.ContainerRuntime,
            "Provider is not configured.",
            DateTimeOffset.UtcNow);
        return Task.FromResult(ProviderRunResult.Fail([failed], ProvisioningFailureCodes.NotImplemented));
    }
}
