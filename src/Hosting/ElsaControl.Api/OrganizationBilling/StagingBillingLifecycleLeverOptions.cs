using Microsoft.Extensions.Options;

namespace ElsaControl.Api.OrganizationBilling;

public static class StagingBillingLifecycleLeverDefaults
{
    public const string ConfigurationSection = "Billing:StagingLifecycleLever";
    public const string DisabledCode = "billing.staging-lifecycle-lever.disabled";
    public const string OrganizationNotAllowedCode = "billing.staging-lifecycle-lever.organization-not-allowed";
    public const string DeadlineNotApplicableCode = "billing.staging-lifecycle-lever.deadline-not-applicable";
    public const string StripeTestSecretKeyPrefix = "sk_test_";
}

public sealed class StagingBillingLifecycleLeverOptions
{
    public const string ConfigurationSection = StagingBillingLifecycleLeverDefaults.ConfigurationSection;

    /// <summary>
    /// Master switch. Off by default. Staging may set this true through the
    /// deploy pipeline; committed appsettings files must keep it false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Organization ids the lever may target. Empty by default. The lever is
    /// allowlist-only; there is no synthetic-marker fallback.
    /// </summary>
    public string[] AllowedOrganizationIds { get; init; } = [];

    public bool AllowsOrganization(Guid organizationId)
    {
        if (!Enabled || organizationId == Guid.Empty)
            return false;

        foreach (var value in AllowedOrganizationIds)
        {
            if (TryParseOrganizationId(value, out var allowed) && allowed == organizationId)
                return true;
        }

        return false;
    }

    internal IEnumerable<string> Validate()
    {
        var values = AllowedOrganizationIds ?? [];
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryParseOrganizationId(values[index], out _))
            {
                yield return
                    $"{ConfigurationSection}:AllowedOrganizationIds[{index}] must be a GUID organization id.";
            }
        }
    }

    internal static bool TryParseOrganizationId(string? value, out Guid organizationId)
    {
        organizationId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsWhiteSpace))
            return false;

        return Guid.TryParse(value, out organizationId) && organizationId != Guid.Empty;
    }
}

public sealed class StagingBillingLifecycleLeverConfigurationValidator(
    IOptions<StagingBillingLifecycleLeverOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return Task.CompletedTask;

        var errors = options.Value.Validate().ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException(
                $"Staging billing lifecycle lever configuration is invalid: {string.Join(" ", errors)}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
