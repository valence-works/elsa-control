using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.PackageCatalog.Core.Tests;

public sealed class OrganizationAzureBoundEntitlementPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Provider_name_is_distinct_and_stable()
    {
        Assert.Equal("azure-bound", BillingProviderNames.AzureBound);
        Assert.NotEqual(BillingProviderNames.Stripe, BillingProviderNames.AzureBound);
        Assert.NotEqual(BillingProviderNames.Internal, BillingProviderNames.AzureBound);
    }

    [Fact]
    public void Omitted_cap_and_expiry_use_the_guided_default_without_inventing_an_expiry()
    {
        var errors = OrganizationAzureBoundEntitlementPolicy.Validate(
            "Guided design partner",
            null,
            null,
            Now,
            out var terms);

        Assert.Empty(errors);
        Assert.Equal(new OrganizationAzureBoundEntitlementTerms("Guided design partner", 1, null), terms);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Instance_cap_stays_within_the_existing_preview_bounds(int cap)
    {
        var errors = OrganizationAzureBoundEntitlementPolicy.Validate(
            "Guided design partner",
            cap,
            null,
            Now,
            out var terms);

        Assert.Null(terms);
        Assert.Equal(["MaxInstances must be between 1 and 3."], errors[OrganizationAzureBoundEntitlementPolicy.MaxInstancesField]);
    }

    [Fact]
    public void Optional_expiry_is_normalized_and_bounded_when_present()
    {
        var errors = OrganizationAzureBoundEntitlementPolicy.Validate(
            "Guided design partner",
            3,
            "2026-10-14T10:00:00Z",
            Now,
            out var terms);

        Assert.Empty(errors);
        Assert.Equal(Now.AddDays(30), terms!.ExpiresAt);

        errors = OrganizationAzureBoundEntitlementPolicy.Validate(
            "Guided design partner",
            1,
            "2026-12-14T10:00:00Z",
            Now,
            out terms);
        Assert.Null(terms);
        Assert.Equal(["ExpiresAt must be at most 90 days ahead."], errors[OrganizationAzureBoundEntitlementPolicy.ExpiresAtField]);
    }
}
