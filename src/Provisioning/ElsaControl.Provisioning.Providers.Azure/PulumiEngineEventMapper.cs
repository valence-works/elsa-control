using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using Pulumi.Automation.Events;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Maps Pulumi engine events to sanitised provider-neutral events. Resource
/// identifiers, endpoints, secrets, and raw diagnostic text never leave this
/// mapper. Hub-facing step names are applied later by the step ledger.
/// </summary>
public static class PulumiEngineEventMapper
{
    public const string ResourceStartedMessage = "Resource started.";
    public const string ResourceSucceededMessage = "Resource succeeded.";
    public const string ResourceFailedMessage = "Resource failed.";
    public const string UpdateCancelledMessage = "Update cancelled.";
    public const string ApplyCompletedMessage = "Apply completed.";

    public static IEnumerable<ProvisioningEvent> Map(
        Guid operationId,
        EngineEvent engineEvent,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(engineEvent);
        return Map(
            operationId,
            new PulumiEngineEventFacts(
                engineEvent.Sequence,
                engineEvent.CancelEvent is not null,
                engineEvent.ResourcePreEvent?.Metadata?.Type,
                engineEvent.ResourceOutputsEvent?.Metadata?.Type,
                engineEvent.ResourceOperationFailedEvent?.Metadata?.Type,
                engineEvent.DiagnosticEvent is { } diagnostic
                    && string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase),
                engineEvent.SummaryEvent is not null),
            now);
    }

    public static IEnumerable<ProvisioningEvent> Map(
        Guid operationId,
        PulumiEngineEventFacts facts,
        DateTimeOffset now)
    {
        if (facts.Sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(facts));

        if (facts.Cancelled)
        {
            yield return Event(
                operationId,
                ProvisioningEventKind.OperationCancelled,
                ProvisioningCapability.ContainerRuntime,
                UpdateCancelledMessage,
                now,
                facts.Sequence);
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(facts.PreResourceType))
        {
            yield return Event(
                operationId,
                ProvisioningEventKind.ResourceStarted,
                ResourceKind(facts.PreResourceType),
                ResourceStartedMessage,
                now,
                facts.Sequence);
        }

        if (!string.IsNullOrWhiteSpace(facts.OutputsResourceType))
        {
            yield return Event(
                operationId,
                ProvisioningEventKind.ResourceSucceeded,
                ResourceKind(facts.OutputsResourceType),
                ResourceSucceededMessage,
                now,
                facts.Sequence);
        }

        if (facts.DiagnosticError || !string.IsNullOrWhiteSpace(facts.FailedResourceType))
        {
            yield return Event(
                operationId,
                ProvisioningEventKind.ResourceFailed,
                string.IsNullOrWhiteSpace(facts.FailedResourceType)
                    ? ProvisioningCapability.ContainerRuntime
                    : ResourceKind(facts.FailedResourceType),
                ResourceFailedMessage,
                now,
                facts.Sequence);
        }

        if (facts.Summary)
        {
            yield return Event(
                operationId,
                ProvisioningEventKind.ResourceSucceeded,
                ProvisioningCapability.ContainerRuntime,
                ApplyCompletedMessage,
                now,
                facts.Sequence);
        }
    }

    public static string ResourceKind(string? type)
    {
        if (string.IsNullOrWhiteSpace(type) || ProvisioningEventSanitizer.ContainsSensitiveToken(type))
            return ProvisioningEventSanitizer.DefaultResourceKind;

        var value = type.Trim();
        var slash = value.LastIndexOf(':');
        var name = slash >= 0 ? value[(slash + 1)..] : value;
        var urnName = name.LastIndexOf("::", StringComparison.Ordinal);
        if (urnName >= 0)
            name = name[(urnName + 2)..];
        return name.ToLowerInvariant() switch
        {
            "resourcegroup" => "resource-group",
            "userassignedidentity" => "identity",
            "vault" or "secret" or "randompassword" or "randombytes" => ProvisioningCapability.Secrets,
            "server" or "database" or "backupshorttermretentionpolicy" => ProvisioningCapability.ManagedSql,
            "managedenvironment" => ProvisioningCapability.SharedEnvironment,
            "containerapp" => ProvisioningCapability.ContainerRuntime,
            "workspace" or "diagnosticsetting" => "observability",
            "roleassignment" => "role-assignment",
            "firewallrule" => "network",
            _ => ProvisioningEventSanitizer.DefaultResourceKind
        };
    }

    private static ProvisioningEvent Event(
        Guid operationId,
        ProvisioningEventKind kind,
        string resourceKind,
        string message,
        DateTimeOffset now,
        int sequence) =>
        ProvisioningEventSanitizer.Sanitize(
            new ProvisioningEvent(operationId, kind, resourceKind, message, now, sequence));
}

/// <summary>
/// The customer-safe facts extracted from one Pulumi engine event. Tests use
/// this instead of constructing Automation API types.
/// </summary>
public readonly record struct PulumiEngineEventFacts(
    int Sequence,
    bool Cancelled,
    string? PreResourceType,
    string? OutputsResourceType,
    string? FailedResourceType,
    bool DiagnosticError,
    bool Summary);
