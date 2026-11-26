namespace ElsaControl.Provisioning.Abstractions;

public sealed record ProviderRunResult
{
    public ProviderRunResult(
        bool succeeded,
        IReadOnlyList<ProvisioningEvent> events,
        IReadOnlyDictionary<string, string>? outputs = null,
        string? failureCode = null,
        bool ownedResourcesAbsent = false)
    {
        ArgumentNullException.ThrowIfNull(events);
        Succeeded = succeeded;
        Events = events;
        Outputs = outputs ?? new Dictionary<string, string>(StringComparer.Ordinal);
        FailureCode = string.IsNullOrWhiteSpace(failureCode) ? null : failureCode.Trim();
        OwnedResourcesAbsent = ownedResourcesAbsent;
    }

    public bool Succeeded { get; }
    public IReadOnlyList<ProvisioningEvent> Events { get; }
    public IReadOnlyDictionary<string, string> Outputs { get; }
    public string? FailureCode { get; }

    /// <summary>
    /// Set after Destroy when a fresh presence check shows the owned resource
    /// group is gone. Never carries the group name into customer text.
    /// </summary>
    public bool OwnedResourcesAbsent { get; }

    public static ProviderRunResult Ok(
        IReadOnlyList<ProvisioningEvent> events,
        IReadOnlyDictionary<string, string>? outputs = null,
        bool ownedResourcesAbsent = false) =>
        new(true, events, outputs, ownedResourcesAbsent: ownedResourcesAbsent);

    public static ProviderRunResult Fail(IReadOnlyList<ProvisioningEvent> events, string failureCode) =>
        new(false, events, failureCode: failureCode);
}
