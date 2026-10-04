using ElsaControl.Billing.Stripe;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Cloud;

public static class CloudCompatibilityStagingFixture
{
    public const string MissingCapability = "missing-capability";
    public const string OlderContract = "older-contract";
}

public sealed class CloudCompatibilityOptions
{
    public const string ConfigurationSection = "CloudCompatibility";
    public const string StagingFixtureKey = ConfigurationSection + ":StagingFixture";

    /// <summary>
    /// Staging-only incompatibility switch. Unset is the normal contract.
    /// Allowed values: <see cref="CloudCompatibilityStagingFixture.MissingCapability"/>
    /// and <see cref="CloudCompatibilityStagingFixture.OlderContract"/>.
    /// </summary>
    public string? StagingFixture { get; set; }

    internal bool HasUnknownStagingFixture
    {
        get
        {
            if (string.IsNullOrWhiteSpace(StagingFixture))
                return false;

            var value = StagingFixture.Trim();
            return !string.Equals(value, CloudCompatibilityStagingFixture.MissingCapability, StringComparison.Ordinal)
                   && !string.Equals(value, CloudCompatibilityStagingFixture.OlderContract, StringComparison.Ordinal);
        }
    }

    internal string? NormalizedStagingFixture
    {
        get
        {
            if (string.IsNullOrWhiteSpace(StagingFixture) || HasUnknownStagingFixture)
                return null;

            return StagingFixture.Trim();
        }
    }
}

public sealed class CloudCompatibilityStagingFixtureValidator(
    IOptions<CloudCompatibilityOptions> options,
    IOptions<StripeBillingOptions> stripe,
    ILogger<CloudCompatibilityStagingFixtureValidator> logger) : IHostedService
{
    public const string UnrecognizedValueReason =
        "Cloud compatibility staging fixture refused to start because the configured value is not recognized.";

    public const string NonTestEnvironmentReason =
        "Cloud compatibility staging fixture refused to start because it is only allowed in the test environment.";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var fixture = options.Value;
        if (string.IsNullOrWhiteSpace(fixture.StagingFixture))
            return Task.CompletedTask;

        if (fixture.HasUnknownStagingFixture)
            throw Refuse(UnrecognizedValueReason);

        if (!IsTestEnvironment(stripe.Value))
            throw Refuse(NonTestEnvironmentReason);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static bool IsTestEnvironment(StripeBillingOptions stripe)
    {
        // GitHub environment `test` (staging) is identified at runtime by the
        // deploy-pipeline Stripe expected mode. ASPNETCORE_ENVIRONMENT is
        // Production on both staging and production hosting.
        ArgumentNullException.ThrowIfNull(stripe);
        return string.Equals(
            StripeBillingOptions.NormalizeMode(stripe.ExpectedMode),
            StripeBillingOptions.TestMode,
            StringComparison.Ordinal);
    }

    private InvalidOperationException Refuse(string reason)
    {
        logger.LogError("{Reason}", reason);
        return new InvalidOperationException(reason);
    }
}
