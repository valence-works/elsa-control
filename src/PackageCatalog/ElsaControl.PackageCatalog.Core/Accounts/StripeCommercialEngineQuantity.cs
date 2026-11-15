namespace ElsaControl.PackageCatalog.Core.Accounts;

/// <summary>
/// Maps a Stripe Cloud-price subscription item quantity onto the Dedicated
/// engine allowance. Trial stays at one engine. Active uses the current
/// quantity, which is the only source of <c>MaxInstances</c> on a Stripe-owned
/// organization. A quantity outside the self-serve range is rejected rather
/// than clamped.
/// </summary>
public static class StripeCommercialEngineQuantity
{
    public const int TrialAllowance = 1;
    public const int MissingOrZeroAllowance = 1;
    public const int MinSelfServe = OrganizationInternalEntitlementPolicy.MinInstanceCap;
    public const int MaxSelfServe = OrganizationInternalEntitlementPolicy.MaxInstanceCap;

    public static bool TryResolveActiveAllowance(int? quantity, out int maxInstances)
    {
        if (quantity is null or 0)
        {
            maxInstances = MissingOrZeroAllowance;
            return true;
        }

        if (quantity >= MinSelfServe && quantity <= MaxSelfServe)
        {
            maxInstances = quantity.Value;
            return true;
        }

        maxInstances = default;
        return false;
    }

    public static bool IsUninitializedCap(int maxInstances) =>
        maxInstances is 0 or int.MaxValue;
}
