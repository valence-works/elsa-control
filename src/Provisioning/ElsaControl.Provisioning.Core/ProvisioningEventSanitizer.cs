using System.Text.RegularExpressions;
using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Drops secrets, subscription identifiers, resource identifiers, and endpoints
/// from provider events before they leave the module.
/// </summary>
public static partial class ProvisioningEventSanitizer
{
    public const string DefaultResourceKind = "resource";
    public const string DefaultMessage = "Resource update.";

    public static ProvisioningEvent Sanitize(ProvisioningEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ProvisioningEvent(
            value.OperationId,
            value.Kind,
            SanitizeResourceKind(value.ResourceKind),
            SanitizeMessage(value.Message),
            value.OccurredAt);
    }

    public static string SanitizeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return DefaultMessage;
        if (ContainsSensitiveToken(message))
            return DefaultMessage;
        return message.Trim();
    }

    public static string SanitizeResourceKind(string resourceKind)
    {
        if (string.IsNullOrWhiteSpace(resourceKind) || ContainsSensitiveToken(resourceKind))
            return DefaultResourceKind;
        return resourceKind.Trim();
    }

    public static bool ContainsSensitiveToken(string value) =>
        ContainsSecretOrResourceIdentifier(value)
        || Endpoint().IsMatch(value);

    /// <summary>
    /// Stack config may include an admitted image repository host. It must
    /// never include secret values, subscription paths, or resource IDs.
    /// </summary>
    public static bool ContainsSecretOrResourceIdentifier(string value)
    {
        if (IsSecretLocator(value))
            return false;
        return SubscriptionPath().IsMatch(value)
               || ResourceIdentifier().IsMatch(value)
               || SecretToken().IsMatch(value)
               || UrlCredential().IsMatch(value);
    }

    /// <summary>
    /// <c>secret://name/secret</c> locators are not secret values.
    /// </summary>
    public static bool IsSecretLocator(string value) =>
        value.StartsWith("secret://", StringComparison.OrdinalIgnoreCase)
        && !value.Contains('=', StringComparison.Ordinal)
        && !UrlCredential().IsMatch(value);

    [GeneratedRegex(@"subscriptions[/\\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubscriptionPath();

    [GeneratedRegex(@"[/\\]resourcegroups[/\\]|[/\\]providers[/\\]|resource id", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResourceIdentifier();

    [GeneratedRegex(@"https?://|[\w.-]+\.(azurewebsites|blob\.core|vault\.azure|azurecr)\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Endpoint();

    [GeneratedRegex(@"password|connection string|accountkey|sharedaccesssignature|secret=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretToken();

    [GeneratedRegex(@"[?&]sig=|://[^/\s?#]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredential();
}
