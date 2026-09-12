using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.Admin.Organizations;

/// <param name="ExpiresAt">ISO-8601 timestamp that explicitly denotes UTC, for example <c>2026-10-01T00:00:00Z</c>.</param>
public sealed record AdminInternalEntitlementRequest(string? Reason, int? MaxInstances, string? ExpiresAt);

/// <summary>Value-free grant state: never the operator reason, operator identity or provider references.</summary>
public sealed record AdminInternalEntitlementResponse(
    Guid OrganizationId,
    OrganizationInternalEntitlementState State,
    int? MaxInstances,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? UpdatedAt);
