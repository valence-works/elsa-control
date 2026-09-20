using ElsaControl.Billing.Stripe;

namespace ElsaControl.Api.OrganizationBilling;

/// <summary>
/// Resolves the Stripe Customer Portal return URL for Hosted Cloud customers.
/// Only HTTPS Elsa Cloud dashboard URLs that stay under the configured origin
/// and path are accepted. Browser-supplied hosts cannot override the server
/// configuration.
/// </summary>
public static class HostedBillingReturnUrls
{
    public static bool TryResolve(string? configuredUrl, string? requestedUrl, out string returnUrl)
    {
        returnUrl = "";
        if (!StripeBillingOptions.IsSafeCloudReturnUrl(configuredUrl))
            return false;

        if (string.IsNullOrWhiteSpace(requestedUrl))
        {
            returnUrl = configuredUrl!;
            return true;
        }

        if (!StripeBillingOptions.IsSafeCloudReturnUrl(requestedUrl))
            return false;

        var configured = new Uri(configuredUrl!, UriKind.Absolute);
        var requested = new Uri(requestedUrl, UriKind.Absolute);
        if (!string.Equals(configured.Scheme, requested.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(configured.IdnHost, requested.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            configured.Port != requested.Port)
            return false;

        var configuredPath = TrimTrailingSlash(configured.AbsolutePath);
        var requestedPath = TrimTrailingSlash(requested.AbsolutePath);
        if (requestedPath != configuredPath &&
            !requestedPath.StartsWith(configuredPath + "/", StringComparison.Ordinal))
            return false;

        returnUrl = requested.GetLeftPart(UriPartial.Path);
        return true;
    }

    private static string TrimTrailingSlash(string path) =>
        path.Length > 1 ? path.TrimEnd('/') : path;
}
