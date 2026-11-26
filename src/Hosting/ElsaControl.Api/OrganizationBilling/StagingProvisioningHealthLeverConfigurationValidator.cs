using ElsaControl.Billing.Stripe;
using ElsaControl.Deployment.Azure;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.OrganizationBilling;

/// <summary>
/// Binds the staging provisioning-health lever from host configuration and
/// refuses an enabled lever whose allowlist is out of range. Sibling billing,
/// recovery, and late-success flags never arm this lever.
/// </summary>
public sealed class StagingProvisioningHealthLeverConfigurationValidator(
    IOptions<StagingProvisioningHealthLeverOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return Task.CompletedTask;

        var errors = options.Value.Validate().ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException(
                $"Staging provisioning health lever configuration is invalid: {string.Join(" ", errors)}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class StagingProvisioningHealthLeverFactory
{
    public static StagingProvisioningHealthLever Create(
        StagingProvisioningHealthLeverOptions options,
        StripeBillingOptions stripe)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stripe);
        return new StagingProvisioningHealthLever(
            options,
            StagingLifecycleLeverGate.IsStripeTestMode(stripe));
    }
}
