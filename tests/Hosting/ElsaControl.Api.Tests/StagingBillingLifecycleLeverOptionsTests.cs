using ElsaControl.Api.OrganizationBilling;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class StagingBillingLifecycleLeverOptionsTests
{
    private static readonly Guid OrganizationId = Guid.Parse("20000000-0000-0000-0000-000000000022");

    [Fact]
    public void Disabled_options_refuse_synthetic_and_allowlisted_orgs()
    {
        var options = new StagingBillingLifecycleLeverOptions
        {
            Enabled = false,
            AllowedOrganizationIds = [OrganizationId.ToString("D")]
        };

        Assert.False(options.AllowsOrganization(OrganizationId, "synthetic"));
        Assert.False(options.AllowsOrganization(OrganizationId, null));
    }

    [Fact]
    public void Enabled_options_accept_synthetic_or_allowlisted_orgs_only()
    {
        var options = new StagingBillingLifecycleLeverOptions
        {
            Enabled = true,
            AllowedOrganizationIds = [OrganizationId.ToString("D")]
        };

        Assert.True(options.AllowsOrganization(Guid.NewGuid(), "synthetic"));
        Assert.True(options.AllowsOrganization(OrganizationId, null));
        Assert.False(options.AllowsOrganization(Guid.NewGuid(), "customer"));
        Assert.False(options.IsSynthetic("Synthetic"));
    }

    [Fact]
    public async Task Validator_fails_closed_on_malformed_allowlist_ids()
    {
        var validator = new StagingBillingLifecycleLeverConfigurationValidator(Options.Create(
            new StagingBillingLifecycleLeverOptions
            {
                AllowedOrganizationIds = ["not-a-guid"]
            }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));

        Assert.Contains("AllowedOrganizationIds[0]", error.Message, StringComparison.Ordinal);
    }
}
