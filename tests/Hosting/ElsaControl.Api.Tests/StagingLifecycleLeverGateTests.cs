using ElsaControl.Api.OrganizationBilling;
using ElsaControl.Billing.Stripe;

namespace ElsaControl.Api.Tests;

public sealed class StagingLifecycleLeverGateTests
{
    [Fact]
    public void Own_flag_and_stripe_test_key_arm_the_shared_gate()
    {
        Assert.True(StagingLifecycleLeverGate.IsArmed(
            true,
            new StripeBillingOptions { Enabled = true, SecretKey = "sk_test_harness" }));
    }

    [Theory]
    [InlineData(false, true, "sk_test_harness")]
    [InlineData(true, false, "sk_test_harness")]
    [InlineData(true, true, "sk_live_harness")]
    [InlineData(true, true, "rk_test_x")]
    [InlineData(true, true, "")]
    [InlineData(true, true, "  ")]
    [InlineData(true, true, null)]
    public void Missing_own_flag_or_non_test_key_leaves_the_gate_closed(
        bool enabled,
        bool stripeEnabled,
        string? secretKey)
    {
        Assert.False(StagingLifecycleLeverGate.IsArmed(
            enabled,
            new StripeBillingOptions { Enabled = stripeEnabled, SecretKey = secretKey }));
    }

    [Fact]
    public void Billing_and_recovery_flags_never_arm_each_other()
    {
        var stripe = new StripeBillingOptions { Enabled = true, SecretKey = "sk_test_harness" };
        var billing = new StagingBillingLifecycleLeverOptions { Enabled = true };
        var recovery = new StagingRecoveryLifecycleLeverOptions { Enabled = false };

        Assert.True(StagingLifecycleLeverGate.IsArmed(billing.Enabled, stripe));
        Assert.False(StagingLifecycleLeverGate.IsArmed(recovery.Enabled, stripe));

        billing.Enabled = false;
        recovery.Enabled = true;
        Assert.False(StagingLifecycleLeverGate.IsArmed(billing.Enabled, stripe));
        Assert.True(StagingLifecycleLeverGate.IsArmed(recovery.Enabled, stripe));
    }

    [Fact]
    public void Stripe_test_prefix_is_shared_and_ordinal()
    {
        Assert.Equal("sk_test_", StagingLifecycleLeverGate.StripeTestSecretKeyPrefix);
        Assert.Equal(
            StagingLifecycleLeverGate.StripeTestSecretKeyPrefix,
            StagingBillingLifecycleLeverDefaults.StripeTestSecretKeyPrefix);
        Assert.False(StagingLifecycleLeverGate.IsStripeTestMode(
            new StripeBillingOptions { Enabled = true, SecretKey = "SK_TEST_harness" }));
    }
}
