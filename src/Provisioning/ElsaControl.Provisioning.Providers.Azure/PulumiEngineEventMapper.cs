using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using Pulumi.Automation.Events;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Maps Pulumi engine events to sanitised provider-neutral events. Resource
/// identifiers, endpoints, and secrets never leave this mapper.
/// </summary>
public static class PulumiEngineEventMapper
{
    public static IEnumerable<ProvisioningEvent> Map(
        Guid operationId,
        EngineEvent engineEvent,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(engineEvent);
        if (engineEvent.ResourcePreEvent is { } started)
        {
            yield return new ProvisioningEvent(
                operationId,
                ProvisioningEventKind.ResourceStarted,
                ResourceKind(started.Metadata.Type),
                "Resource started.",
                now);
        }

        if (engineEvent.ResourceOutputsEvent is { } succeeded)
        {
            yield return new ProvisioningEvent(
                operationId,
                ProvisioningEventKind.ResourceSucceeded,
                ResourceKind(succeeded.Metadata.Type),
                "Resource succeeded.",
                now);
        }

        if (engineEvent.DiagnosticEvent is { } diagnostic
            && string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase))
        {
            yield return new ProvisioningEvent(
                operationId,
                ProvisioningEventKind.ResourceFailed,
                ProvisioningCapability.ContainerRuntime,
                ProvisioningEventSanitizer.SanitizeMessage("Resource failed."),
                now);
        }
    }

    public static string ResourceKind(string? type)
    {
        if (string.IsNullOrWhiteSpace(type) || ProvisioningEventSanitizer.ContainsSensitiveToken(type))
            return ProvisioningEventSanitizer.DefaultResourceKind;
        var slash = type.LastIndexOf(':');
        var name = slash >= 0 ? type[(slash + 1)..] : type;
        return name.ToLowerInvariant() switch
        {
            "resourcegroup" => "resource-group",
            "userassignedidentity" => "identity",
            "vault" => ProvisioningCapability.Secrets,
            "server" or "database" => ProvisioningCapability.ManagedSql,
            "managedenvironment" => ProvisioningCapability.SharedEnvironment,
            "containerapp" => ProvisioningCapability.ContainerRuntime,
            "workspace" => "observability",
            "roleassignment" => "role-assignment",
            "firewallrule" => "network",
            _ => ProvisioningEventSanitizer.DefaultResourceKind
        };
    }
}
