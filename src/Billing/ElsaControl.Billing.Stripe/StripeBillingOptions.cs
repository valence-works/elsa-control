namespace ElsaControl.Billing.Stripe;

public sealed class StripeBillingOptions
{
    public const string ConfigurationSection = "Billing:Stripe";

    public bool Enabled { get; set; }
    public string? SecretKey { get; set; }
    public string? WebhookSigningSecret { get; set; }
    public string? DefaultPriceId { get; set; }
    public string? CheckoutSuccessUrl { get; set; }
    public string? CheckoutCancelUrl { get; set; }
    public string? PortalReturnUrl { get; set; }
    public string? CloudPortalReturnUrl { get; set; }

    public bool IsProviderReady =>
        Enabled && !string.IsNullOrWhiteSpace(SecretKey);

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
