using System.Text.RegularExpressions;
using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Drops anything that looks like a cloud identifier, endpoint, or secret from
/// events before they are stored. Known reason codes and capability tokens pass.
/// </summary>
public static partial class ProvisioningEventSanitizer
{
    private static readonly HashSet<string> AllowedReasonCodes = new(StringComparer.Ordinal)
    {
        ProvisioningReasonCodes.OperationQueued,
        ProvisioningReasonCodes.OperationStarted,
        ProvisioningReasonCodes.OperationSucceeded,
        ProvisioningReasonCodes.OperationFailed,
        ProvisioningReasonCodes.OperationCancelled,
        ProvisioningReasonCodes.ResourceStarted,
        ProvisioningReasonCodes.ResourceSucceeded,
        ProvisioningReasonCodes.ResourceFailed,
        ProvisioningReasonCodes.StackLocked,
        ProvisioningReasonCodes.IdempotencyConflict,
        ProvisioningReasonCodes.ProviderUnavailable,
        ProvisioningReasonCodes.AbsenceProved,
        ProvisioningReasonCodes.CancelRequested
    };

    private static readonly HashSet<string> AllowedCapabilities = new(StringComparer.Ordinal)
    {
        ProvisioningCapabilities.ContainerRuntime,
        ProvisioningCapabilities.Database,
        ProvisioningCapabilities.Secrets,
        ProvisioningCapabilities.Ingress,
        ProvisioningCapabilities.Identity,
        ProvisioningCapabilities.Platform
    };

    public static ProvisioningEvent Sanitize(ProvisioningEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var reason = AllowedReasonCodes.Contains(@event.ReasonCode)
            ? @event.ReasonCode
            : ProvisioningReasonCodes.OperationFailed;
        var capability = @event.Capability is not null && AllowedCapabilities.Contains(@event.Capability)
            ? @event.Capability
            : null;
        if (LooksUnsafe(@event.ReasonCode) || LooksUnsafe(@event.Capability))
        {
            reason = ProvisioningReasonCodes.OperationFailed;
            capability = null;
        }

        return new ProvisioningEvent(@event.OperationId, @event.Kind, reason, @event.OccurredAt, capability);
    }

    public static bool LooksUnsafe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return ResourceIdRegex().IsMatch(value)
               || EndpointRegex().IsMatch(value)
               || SecretAssignmentRegex().IsMatch(value);
    }

    [GeneratedRegex(@"/subscriptions/|/resourcegroups/|/providers/microsoft\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResourceIdRegex();

    [GeneratedRegex(@"https?://|\.azurecontainerapps\.io|\.vault\.azure\.net|\.database\.windows\.net|\.blob\.core\.windows\.net", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EndpointRegex();

    [GeneratedRegex(@"(password|secret|token|key|connectionstring)\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentRegex();
}
