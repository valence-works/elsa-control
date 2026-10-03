using ElsaControl.Api.OrganizationBilling;
using ElsaControl.Billing.Stripe;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class StripeBillingConfigurationValidatorTests
{
    [Theory]
    [InlineData("test", "sk_live_test", "does not match")]
    [InlineData("live", "sk_test_test", "does not match")]
    [InlineData("live", "rk_test_test", "supported Stripe test or live")]
    [InlineData("test", "unknown_test", "supported Stripe test or live")]
    [InlineData("live", "sk_live_", "supported Stripe test or live")]
    public async Task Enabled_billing_fails_closed_when_key_mode_is_wrong(
        string expectedMode,
        string secretKey,
        string expectedMessage)
    {
        var validator = CreateValidator(new StripeBillingOptions
        {
            Enabled = true,
            ExpectedMode = expectedMode,
            SecretKey = secretKey
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretKey, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sk_live_test")]
    [InlineData("rk_live_test")]
    public async Task Live_mode_accepts_standard_and_restricted_live_keys(string secretKey)
    {
        var validator = CreateValidator(new StripeBillingOptions
        {
            Enabled = true,
            ExpectedMode = StripeBillingOptions.LiveMode,
            SecretKey = secretKey
        });

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Enabled_billing_requires_an_explicit_expected_mode()
    {
        var validator = CreateValidator(new StripeBillingOptions
        {
            Enabled = true,
            SecretKey = "sk_test_test"
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("ExpectedMode", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_test", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_billing_does_not_require_a_key_or_mode()
    {
        var validator = CreateValidator(new StripeBillingOptions());

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Production_alias_normalizes_to_live_mode()
    {
        var validator = CreateValidator(new StripeBillingOptions
        {
            Enabled = true,
            ExpectedMode = "production",
            SecretKey = "rk_live_test"
        });

        await validator.StartAsync(CancellationToken.None);
    }

    private static StripeBillingConfigurationValidator CreateValidator(StripeBillingOptions options) =>
        new(Options.Create(options));
}
