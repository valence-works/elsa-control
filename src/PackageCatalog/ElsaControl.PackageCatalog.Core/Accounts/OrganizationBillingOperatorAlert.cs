using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ElsaControl.PackageCatalog.Core.Accounts;

/// <summary>
/// Durable operator alert for commercial engine-allowance transitions.
/// Reuses the #657 shape: SHA-256 dedupe, opaque organization id, no
/// customer names, emails, Stripe references, or price text.
/// </summary>
public static class OrganizationBillingOperatorAlert
{
    public const string ActivitySourceName = "ElsaControl.Billing";
    public const string ActivityName = "billing.engine_allowance.alert";
    public const string OrganizationIdTag = "organization.id";
    public const string KindTag = "kind";
    public const string DedupeIdentityTag = "dedupe_identity";
    public const string MaxInstancesTag = "max_instances";
    public const string ActiveDedicatedCountTag = "active_dedicated_count";
    public const string QuantityTag = "quantity";

    public const string OverAllowanceKind = "over-allowance";
    public const string QuantityOutOfRangeKind = "quantity-out-of-range";

    public const string OverAllowanceSummary = "Dedicated engine count exceeds the paid allowance.";
    public const string QuantityOutOfRangeSummary = "Cloud engine quantity is outside the self-serve range.";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static string OverAllowanceDedupeIdentity(
        Guid organizationId,
        int fromMaxInstances,
        int toMaxInstances,
        int activeDedicatedCount,
        string? providerEventId) =>
        Hash(
            organizationId,
            OverAllowanceKind,
            fromMaxInstances.ToString(CultureInfo.InvariantCulture),
            toMaxInstances.ToString(CultureInfo.InvariantCulture),
            activeDedicatedCount.ToString(CultureInfo.InvariantCulture),
            providerEventId ?? "");

    public static string QuantityOutOfRangeDedupeIdentity(Guid organizationId, int quantity) =>
        Hash(organizationId, QuantityOutOfRangeKind, quantity.ToString(CultureInfo.InvariantCulture));

    public static void Record(
        Guid organizationId,
        string kind,
        string dedupeIdentity,
        int? maxInstances = null,
        int? activeDedicatedCount = null,
        int? quantity = null)
    {
        if (organizationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(kind) ||
            string.IsNullOrWhiteSpace(dedupeIdentity) ||
            dedupeIdentity.Length != 64)
            return;

        using var activity = ActivitySource.StartActivity(ActivityName, ActivityKind.Internal);
        if (activity is null)
            return;

        activity.SetTag(OrganizationIdTag, organizationId.ToString("D"));
        activity.SetTag(KindTag, kind);
        activity.SetTag(DedupeIdentityTag, dedupeIdentity);
        if (maxInstances is { } max)
            activity.SetTag(MaxInstancesTag, max);
        if (activeDedicatedCount is { } count)
            activity.SetTag(ActiveDedicatedCountTag, count);
        if (quantity is { } value)
            activity.SetTag(QuantityTag, value);
        activity.SetStatus(ActivityStatusCode.Ok);
    }

    private static string Hash(Guid organizationId, string kind, params string[] parts)
    {
        var builder = new StringBuilder();
        builder.Append(organizationId.ToString("D"));
        builder.Append('\n');
        builder.Append(kind);
        foreach (var part in parts)
        {
            builder.Append('\n');
            builder.Append(part);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

/// <summary>
/// Append-only operator-alert outbox. One row per organization per
/// allowance transition. Delivery is the activity export; the unique
/// dedupe identity makes retries idempotent.
/// </summary>
public sealed class OrganizationBillingOperatorAlertOutbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public string Kind { get; set; } = "";
    public string DedupeIdentity { get; set; } = "";
    public int? ActiveDedicatedCount { get; set; }
    public int? MaxInstances { get; set; }
    public int? Quantity { get; set; }
    public string Summary { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
