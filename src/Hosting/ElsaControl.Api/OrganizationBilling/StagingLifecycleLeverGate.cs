using ElsaControl.Billing.Stripe;

namespace ElsaControl.Api.OrganizationBilling;

/// <summary>
/// Shared two-signal arming gate for staging-only operator levers. A lever is
/// armed only when its own flag is on and Control's Stripe secret key is in
/// test mode. Sibling levers never share a flag, so turning one on cannot arm
/// the other.
/// </summary>
public static class StagingLifecycleLeverGate
{
    public const string StripeTestSecretKeyPrefix = "sk_test_";

    public static bool IsArmed(bool leverEnabled, StripeBillingOptions stripe) =>
        leverEnabled && IsStripeTestMode(stripe);

    public static bool IsStripeTestMode(StripeBillingOptions stripe)
    {
        ArgumentNullException.ThrowIfNull(stripe);
        if (!stripe.Enabled || string.IsNullOrWhiteSpace(stripe.SecretKey))
            return false;

        return stripe.SecretKey.StartsWith(StripeTestSecretKeyPrefix, StringComparison.Ordinal);
    }
}
