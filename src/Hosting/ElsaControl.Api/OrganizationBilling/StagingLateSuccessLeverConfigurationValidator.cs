using ElsaControl.Billing.Stripe;
using ElsaControl.Deployment.Azure;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.OrganizationBilling;

/// <summary>
/// Binds the staging late-success lever from host configuration and refuses an
/// enabled lever whose wait or allowlist is out of range. Sibling billing and
/// recovery flags never arm this lever.
/// </summary>
public sealed class StagingLateSuccessLeverConfigurationValidator(
    IOptions<StagingLateSuccessLeverOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return Task.CompletedTask;

        var errors = options.Value.Validate().ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException(
                $"Staging late-success lever configuration is invalid: {string.Join(" ", errors)}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class StagingLateSuccessLeverFactory
{
    public static StagingLateSuccessLever Create(
        StagingLateSuccessLeverOptions options,
        StripeBillingOptions stripe)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stripe);
        var isPipelineTargetTest = string.Equals(
            StripeBillingOptions.NormalizeMode(stripe.ExpectedMode),
            StripeBillingOptions.TestMode,
            StringComparison.Ordinal);
        return new StagingLateSuccessLever(
            options,
            isPipelineTargetTest,
            StagingLifecycleLeverGate.IsStripeTestMode(stripe));
    }
}
