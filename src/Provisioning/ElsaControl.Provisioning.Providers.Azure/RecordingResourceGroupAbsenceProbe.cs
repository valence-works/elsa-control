namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Test and CI probe. Does not call Azure. Destroy tests inject a result.
/// </summary>
public sealed class RecordingResourceGroupAbsenceProbe : IResourceGroupAbsenceProbe
{
    public ResourceGroupPresence Result { get; set; } = ResourceGroupPresence.Absent;

    public string? LastName { get; private set; }

    public Task<ResourceGroupPresence> ObserveAsync(
        string resourceGroupName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroupName);
        LastName = resourceGroupName;
        return Task.FromResult(Result);
    }
}
