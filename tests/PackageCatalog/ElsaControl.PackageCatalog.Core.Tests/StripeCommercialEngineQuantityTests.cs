using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.PackageCatalog.Core.Tests;

public sealed class StripeCommercialEngineQuantityTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void Active_allowance_uses_quantity_or_the_missing_zero_default(int? quantity, int expected)
    {
        Assert.True(StripeCommercialEngineQuantity.TryResolveActiveAllowance(quantity, out var maxInstances));
        Assert.Equal(expected, maxInstances);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(99)]
    [InlineData(-1)]
    public void Quantity_outside_the_self_serve_range_is_rejected_not_clamped(int quantity)
    {
        Assert.False(StripeCommercialEngineQuantity.TryResolveActiveAllowance(quantity, out var maxInstances));
        Assert.Equal(0, maxInstances);
        Assert.NotEqual(StripeCommercialEngineQuantity.MaxSelfServe, quantity);
    }

    [Fact]
    public void Self_serve_bounds_match_the_internal_grant_cap()
    {
        Assert.Equal(OrganizationInternalEntitlementPolicy.MinInstanceCap, StripeCommercialEngineQuantity.MinSelfServe);
        Assert.Equal(OrganizationInternalEntitlementPolicy.MaxInstanceCap, StripeCommercialEngineQuantity.MaxSelfServe);
        Assert.Equal(3, StripeCommercialEngineQuantity.MaxSelfServe);
    }
}
