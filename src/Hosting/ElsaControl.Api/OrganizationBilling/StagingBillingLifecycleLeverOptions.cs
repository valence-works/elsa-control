using Microsoft.Extensions.Options;

namespace ElsaControl.Api.OrganizationBilling;

public static class StagingBillingLifecycleLeverDefaults
{
    public const string ConfigurationSection = "Billing:StagingLifecycleLever";
    public const string DisabledCode = "billing.staging-lifecycle-lever.disabled";
    public const string OrganizationNotAllowedCode = "billing.staging-lifecycle-lever.organization-not-allowed";
    public const string DeadlineNotApplicableCode = "billing.staging-lifecycle-lever.deadline-not-applicable";
    public const string SyntheticCustomerReference = "synthetic";
}

public sealed class StagingBillingLifecycleLeverOptions
{
    public const string ConfigurationSection = StagingBillingLifecycleLeverDefaults.ConfigurationSection;

    /// <summary>
    /// Master switch. Off by default. Staging sets this true; production ships false
    /// and the lever also refuses the Production environment even if misconfigured.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Organization ids the lever may target when they are not harness-marked
    /// with <see cref="SyntheticCustomerReference"/>. Empty by default.
    /// </summary>
    public string[] AllowedOrganizationIds { get; init; } = [];

    /// <summary>
    /// Exact <c>Organization.CustomerReference</c> that marks a harness-created
    /// organization. Defaults to <c>synthetic</c>.
    /// </summary>
    public string SyntheticCustomerReference { get; init; } =
        StagingBillingLifecycleLeverDefaults.SyntheticCustomerReference;

    public bool AllowsOrganization(Guid organizationId, string? customerReference)
    {
        if (!Enabled || organizationId == Guid.Empty)
            return false;

        if (IsSynthetic(customerReference))
            return true;

        foreach (var value in AllowedOrganizationIds)
        {
            if (TryParseOrganizationId(value, out var allowed) && allowed == organizationId)
                return true;
        }

        return false;
    }

    public bool IsSynthetic(string? customerReference) =>
        !string.IsNullOrWhiteSpace(SyntheticCustomerReference) &&
        string.Equals(customerReference, SyntheticCustomerReference, StringComparison.Ordinal);

    internal IEnumerable<string> Validate()
    {
        if (!IsSafeMarker(SyntheticCustomerReference))
        {
            yield return
                $"{ConfigurationSection}:SyntheticCustomerReference must be a non-empty token without whitespace.";
        }

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

    private static bool IsSafeMarker(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsWhiteSpace) &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') &&
        value.Length <= 64;
}

public sealed class StagingBillingLifecycleLeverConfigurationValidator(
    IOptions<StagingBillingLifecycleLeverOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = options.Value.Validate().ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException(
                $"Staging billing lifecycle lever configuration is invalid: {string.Join(" ", errors)}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
