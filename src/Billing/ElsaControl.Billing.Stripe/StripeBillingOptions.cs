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

    public bool IsWebhookConfigured => Enabled && !string.IsNullOrWhiteSpace(WebhookSigningSecret);

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
