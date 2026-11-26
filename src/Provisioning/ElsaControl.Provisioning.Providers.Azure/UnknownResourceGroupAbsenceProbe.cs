namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Fail-closed stand-in. A missing probe must not certify absence.
/// </summary>
public sealed class UnknownResourceGroupAbsenceProbe : IResourceGroupAbsenceProbe
{
    public Task<ResourceGroupPresence> ObserveAsync(
        string resourceGroupName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroupName);
        return Task.FromResult(ResourceGroupPresence.Unknown);
    }
}
