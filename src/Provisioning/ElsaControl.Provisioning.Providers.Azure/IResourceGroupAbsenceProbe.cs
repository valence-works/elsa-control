namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Fresh presence check after Destroy. Implementations must not mutate.
/// Results are value-free: present, absent, or unknown.
/// </summary>
public interface IResourceGroupAbsenceProbe
{
    Task<ResourceGroupPresence> ObserveAsync(
        string resourceGroupName,
        CancellationToken cancellationToken = default,
        string? subscriptionId = null);
}

public enum ResourceGroupPresence
{
    Unknown = 0,
    Present = 1,
    Absent = 2
}
