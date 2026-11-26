namespace ElsaControl.Provisioning.Abstractions;

public sealed record ProviderRunResult
{
    public ProviderRunResult(
        bool succeeded,
        IReadOnlyList<ProvisioningEvent> events,
        IReadOnlyDictionary<string, string>? outputs = null,
        string? failureCode = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        Succeeded = succeeded;
        Events = events;
        Outputs = outputs ?? new Dictionary<string, string>(StringComparer.Ordinal);
        FailureCode = string.IsNullOrWhiteSpace(failureCode) ? null : failureCode.Trim();
    }

    public bool Succeeded { get; }
    public IReadOnlyList<ProvisioningEvent> Events { get; }
    public IReadOnlyDictionary<string, string> Outputs { get; }
    public string? FailureCode { get; }

    public static ProviderRunResult Ok(
        IReadOnlyList<ProvisioningEvent> events,
        IReadOnlyDictionary<string, string>? outputs = null) =>
        new(true, events, outputs);

    public static ProviderRunResult Fail(IReadOnlyList<ProvisioningEvent> events, string failureCode) =>
        new(false, events, failureCode: failureCode);
}
