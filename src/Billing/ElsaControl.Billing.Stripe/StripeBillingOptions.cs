using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Billing.Stripe;

public sealed class StripeBillingOptions
{
    public const string ConfigurationSection = "Billing:Stripe";
    public const string ExpectedModeConfigurationKey = ConfigurationSection + ":ExpectedMode";
    public const string TestMode = "test";
    public const string LiveMode = "live";

    public bool Enabled { get; set; }
    /// <summary>
    /// The deployment target's authoritative Stripe account mode. This value is
    /// supplied by the deployment pipeline because ASPNETCORE_ENVIRONMENT is
    /// shared by staging and production hosting.
    /// </summary>
    public string? ExpectedMode { get; set; }
    public string? SecretKey { get; set; }
    public string? WebhookSigningSecret { get; set; }
    public List<StripeWebhookSourceOptions> WebhookSources { get; set; } = [];
    public string? DefaultPriceId { get; set; }
    public string? CheckoutSuccessUrl { get; set; }
    public string? CheckoutCancelUrl { get; set; }
    public string? PortalReturnUrl { get; set; }
    public string? CloudPortalReturnUrl { get; set; }

    public bool IsProviderReady =>
        Enabled && !string.IsNullOrWhiteSpace(SecretKey);

    public IEnumerable<string> ValidateExpectedMode()
    {
        if (!Enabled)
            yield break;

        if (string.IsNullOrWhiteSpace(SecretKey))
        {
            yield return $"{ConfigurationSection}:SecretKey is required when billing is enabled.";
            yield break;
        }

        var expectedMode = NormalizeMode(ExpectedMode);
        if (expectedMode is null)
        {
            yield return $"{ExpectedModeConfigurationKey} must be explicitly set to 'test' or 'live' when billing is enabled.";
            yield break;
        }

        var actualMode = GetKeyMode(SecretKey);
        if (actualMode is null)
        {
            yield return $"{ConfigurationSection}:SecretKey must use a supported Stripe test or live key prefix.";
            yield break;
        }

        if (!string.Equals(expectedMode, actualMode, StringComparison.Ordinal))
            yield return $"{ExpectedModeConfigurationKey} does not match the configured Stripe key mode.";
    }

    public static string? NormalizeMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        TestMode or "staging" => TestMode,
        LiveMode or "production" => LiveMode,
        _ => null
    };

    public static string? GetKeyMode(string? value)
    {
        if (value is null)
            return null;

        return HasValuePrefix(value, "sk_test_") ? TestMode
            : HasValuePrefix(value, "sk_live_") || HasValuePrefix(value, "rk_live_")
                ? LiveMode
                : null;

        static bool HasValuePrefix(string candidate, string prefix) =>
            candidate.StartsWith(prefix, StringComparison.Ordinal) && candidate.Length > prefix.Length;
    }

    public bool IsCheckoutConfigured =>
        IsProviderReady &&
        !string.IsNullOrWhiteSpace(DefaultPriceId) &&
        Uri.TryCreate(CheckoutSuccessUrl, UriKind.Absolute, out var success) &&
        (success.Scheme == Uri.UriSchemeHttp || success.Scheme == Uri.UriSchemeHttps) &&
        Uri.TryCreate(CheckoutCancelUrl, UriKind.Absolute, out var cancel) &&
        (cancel.Scheme == Uri.UriSchemeHttp || cancel.Scheme == Uri.UriSchemeHttps);

    public bool IsWebhookConfigured =>
        Enabled && ResolveWebhookSources().Any(source => !string.IsNullOrWhiteSpace(source.SigningSecret));

    public IReadOnlyList<StripeWebhookSourceOptions> ResolveWebhookSources()
    {
        var sources = new List<StripeWebhookSourceOptions>();
        foreach (var source in WebhookSources)
        {
            var name = NormalizeSourceName(source.Name);
            if (name is null || string.IsNullOrWhiteSpace(source.SigningSecret))
                continue;
            sources.Add(new StripeWebhookSourceOptions
            {
                Name = name,
                SigningSecret = source.SigningSecret,
                ExpectedMode = source.ExpectedMode,
                CloudPriceIds = [.. source.CloudPriceIds]
            });
        }

        if (!sources.Any(source => string.Equals(source.Name, BillingWebhookSources.Control, StringComparison.Ordinal)) &&
            !string.IsNullOrWhiteSpace(WebhookSigningSecret))
        {
            sources.Insert(0, new StripeWebhookSourceOptions
            {
                Name = BillingWebhookSources.Control,
                SigningSecret = WebhookSigningSecret,
                ExpectedMode = ExpectedMode,
                CloudPriceIds = []
            });
        }

        return sources;
    }

    public IEnumerable<string> ValidateWebhookSources()
    {
        if (!Enabled)
            yield break;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in WebhookSources)
        {
            if (string.IsNullOrWhiteSpace(source.Name) && string.IsNullOrWhiteSpace(source.SigningSecret) &&
                source.CloudPriceIds.Count == 0 && string.IsNullOrWhiteSpace(source.ExpectedMode))
                continue;

            var name = NormalizeSourceName(source.Name);
            if (name is null)
            {
                yield return $"{ConfigurationSection}:WebhookSources names must be '{BillingWebhookSources.Control}' or '{BillingWebhookSources.Hub}'.";
                yield break;
            }

            if (!names.Add(name))
            {
                yield return $"{ConfigurationSection}:WebhookSources names must be unique.";
                yield break;
            }

            if (string.IsNullOrWhiteSpace(source.SigningSecret))
            {
                yield return $"{ConfigurationSection}:WebhookSources:{name}:SigningSecret is required.";
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(source.ExpectedMode) && NormalizeMode(source.ExpectedMode) is null)
            {
                yield return $"{ConfigurationSection}:WebhookSources:{name}:ExpectedMode must be 'test' or 'live'.";
                yield break;
            }
        }
    }

    public static string? NormalizeSourceName(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        BillingWebhookSources.Control => BillingWebhookSources.Control,
        BillingWebhookSources.Hub => BillingWebhookSources.Hub,
        _ => null
    };

    public bool AllowsCloudPrice(StripeWebhookSourceOptions source, string? priceReference)
    {
        var allowed = source.CloudPriceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.Ordinal);
        if (allowed.Count == 0)
            return !string.Equals(NormalizeSourceName(source.Name), BillingWebhookSources.Hub, StringComparison.Ordinal);

        return priceReference is not null && allowed.Contains(priceReference);
    }

    public bool IsPortalConfigured =>
        IsProviderReady &&
        Uri.TryCreate(PortalReturnUrl, UriKind.Absolute, out var portal) &&
        (portal.Scheme == Uri.UriSchemeHttp || portal.Scheme == Uri.UriSchemeHttps);

    public bool IsCloudPortalConfigured =>
        IsProviderReady && IsSafeCloudReturnUrl(CloudPortalReturnUrl);

    public bool IsConfigured => IsCheckoutConfigured && IsWebhookConfigured;

    public static bool IsSafeCloudReturnUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsolutePath.Length > 0;
}

public sealed class StripeWebhookSourceOptions
{
    public string? Name { get; set; }
    public string? SigningSecret { get; set; }
    public string? ExpectedMode { get; set; }
    public List<string> CloudPriceIds { get; set; } = [];
}

public sealed class BillingProviderUnavailableException : InvalidOperationException
{
    public BillingProviderUnavailableException(string message)
        : base(message)
    {
    }

    public BillingProviderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
