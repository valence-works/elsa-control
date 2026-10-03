using ElsaControl.Billing.Stripe;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.OrganizationBilling;

public sealed class StripeBillingConfigurationValidator(
    IOptions<StripeBillingOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = options.Value.ValidateExpectedMode().ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException(
                $"Stripe billing configuration is invalid: {string.Join(" ", errors)}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
