using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.Admin.Organizations;

/// <param name="ExpiresAt">Optional ISO-8601 timestamp that explicitly denotes UTC.</param>
public sealed record AdminAzureBoundEntitlementRequest(string? Reason, int? MaxInstances, string? ExpiresAt);

/// <summary>
/// Value-free guided design-partner state. Azure infrastructure charges remain
/// separate from Elsa fees, and this does not claim customer-subscription create readiness.
/// </summary>
public sealed record AdminAzureBoundEntitlementResponse(
    Guid OrganizationId,
    OrganizationAzureBoundEntitlementState State,
    int? MaxInstances,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? UpdatedAt);
