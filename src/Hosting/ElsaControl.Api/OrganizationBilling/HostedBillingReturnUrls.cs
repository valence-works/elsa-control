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

        var configured = new Uri(configuredUrl!, UriKind.Absolute);
        if (!TryCreateRequestedUri(configured, requestedUrl, out var requested) ||
            !StripeBillingOptions.IsSafeCloudReturnUrl(requested.AbsoluteUri))
            return false;

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

    private static bool TryCreateRequestedUri(Uri configured, string requestedUrl, out Uri requested)
    {
        if (ContainsUnsafeEscapedPathSyntax(requestedUrl))
        {
            requested = null!;
            return false;
        }

        if (Uri.TryCreate(requestedUrl, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            requested = absolute;
            return true;
        }

        if (!requestedUrl.StartsWith("/", StringComparison.Ordinal) ||
            requestedUrl.StartsWith("//", StringComparison.Ordinal) ||
            requestedUrl.Contains('\\'))
        {
            requested = null!;
            return false;
        }

        if (Uri.TryCreate(new Uri(configured.GetLeftPart(UriPartial.Authority)), requestedUrl, out var relative))
        {
            requested = relative;
            return true;
        }

        requested = null!;
        return false;
    }

    private static bool ContainsUnsafeEscapedPathSyntax(string value) =>
        value.Contains("%25", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("%5c", StringComparison.OrdinalIgnoreCase);

    private static string TrimTrailingSlash(string path) =>
        path.Length > 1 ? path.TrimEnd('/') : path;
}
