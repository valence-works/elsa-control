using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Providers.Azure;

internal static class PulumiEngineEventMapper
{
    public static string? CapabilityFor(string? resourceType) => resourceType switch
    {
        "elsa:azure:platform:SharedEnvironment" => ProvisioningCapabilities.Platform,
        "elsa:azure:engine:DedicatedEngine" => ProvisioningCapabilities.ContainerRuntime,
        _ => null
    };

    public static ProvisioningEvent? Map(
        Guid operationId,
        DateTimeOffset occurredAt,
        string? resourceType,
        ProvisioningEventKind kind)
    {
        var capability = CapabilityFor(resourceType);
        if (capability is null)
            return null;
        var code = kind switch
        {
            ProvisioningEventKind.ResourceStarted => ProvisioningReasonCodes.ResourceStarted,
            ProvisioningEventKind.ResourceSucceeded => ProvisioningReasonCodes.ResourceSucceeded,
            ProvisioningEventKind.ResourceFailed => ProvisioningReasonCodes.ResourceFailed,
            _ => ProvisioningReasonCodes.OperationFailed
        };
        return new ProvisioningEvent(operationId, kind, code, occurredAt, capability);
    }
}
