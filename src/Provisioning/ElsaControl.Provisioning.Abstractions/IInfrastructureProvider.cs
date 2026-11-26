namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Provider extension point. Azure is first; AWS, GCP, and on-prem plug in later
/// with no change to Core.
/// </summary>
public interface IInfrastructureProvider
{
    string ProviderId { get; }

    IReadOnlySet<string> Capabilities { get; }

    Task<ProviderRunResult> PreviewAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default);

    Task<ProviderRunResult> ProvisionAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default);

    Task<ProviderRunResult> UpdateAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default);

    Task<ProviderRunResult> RefreshAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default);

    Task<ProviderRunResult> CancelAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default);

    Task<ProviderRunResult> DestroyAsync(
        ProvisioningOperation operation,
        ProvisioningRequest request,
        CancellationToken cancellationToken = default);
}
