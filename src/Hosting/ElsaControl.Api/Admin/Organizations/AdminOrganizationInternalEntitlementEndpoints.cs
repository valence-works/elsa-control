using System.Security.Claims;
using ElsaControl.Api.Authentication;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Admin.Organizations;

/// <summary>
/// Operator-only grant of a narrow, audited, capped and time-limited managed-hosting
/// entitlement for internal dogfood organizations. It never writes over a billing
/// provider's subscription and never replaces billing for customers.
/// </summary>
public static class AdminOrganizationInternalEntitlementEndpoints
{
    public static IEndpointRouteBuilder MapAdminOrganizationInternalEntitlementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/organizations/{organizationId:guid}/internal-entitlement")
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Admin Organizations");

        group.MapGet("", async (
            Guid organizationId,
            OrganizationInternalEntitlementService entitlements,
            CancellationToken cancellationToken) =>
            ToHttpResult(await entitlements.GetAsync(organizationId, cancellationToken)));

        group.MapPut("", async (
            Guid organizationId,
            AdminInternalEntitlementRequest request,
            HttpContext context,
            IOptions<ControlIdentityOptions> identityOptions,
            OrganizationInternalEntitlementService entitlements,
            CancellationToken cancellationToken) =>
            ToHttpResult(await entitlements.GrantAsync(
                organizationId,
                request.Reason,
                request.MaxInstances,
                request.ExpiresAt,
                OperatorSubject(context.User, identityOptions.Value),
                cancellationToken)));

        group.MapDelete("", async (
            Guid organizationId,
            HttpContext context,
            IOptions<ControlIdentityOptions> identityOptions,
            OrganizationInternalEntitlementService entitlements,
            CancellationToken cancellationToken) =>
            ToHttpResult(await entitlements.RevokeAsync(
                organizationId,
                OperatorSubject(context.User, identityOptions.Value),
                cancellationToken)));

        return endpoints;
    }

    // API-key principals carry the fixed "api-key" name identifier; control
    // sessions carry the configured subject claim.
    private static string? OperatorSubject(ClaimsPrincipal user, ControlIdentityOptions identityOptions) =>
        user.FindFirstValue(identityOptions.Claims.Subject) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    private static IResult ToHttpResult(OrganizationInternalEntitlementResult result) => result.Outcome switch
    {
        OrganizationInternalEntitlementOutcome.Current or
            OrganizationInternalEntitlementOutcome.Granted or
            OrganizationInternalEntitlementOutcome.Regranted => Results.Ok(ToResponse(result.Status!)),
        OrganizationInternalEntitlementOutcome.Revoked or
            OrganizationInternalEntitlementOutcome.Unchanged => Results.NoContent(),
        OrganizationInternalEntitlementOutcome.Invalid => Results.ValidationProblem(
            result.Errors!,
            extensions: new Dictionary<string, object?> { ["code"] = "internal-entitlement.invalid" }),
        OrganizationInternalEntitlementOutcome.OrganizationNotFound => Problem(
            "organization.not-found", "Organization was not found.", StatusCodes.Status404NotFound),
        OrganizationInternalEntitlementOutcome.NotGranted => Problem(
            "internal-entitlement.not-granted", "The organization has no internal entitlement to revoke.", StatusCodes.Status404NotFound),
        OrganizationInternalEntitlementOutcome.CommercialSubscriptionExists => Problem(
            "internal-entitlement.commercial-subscription",
            "A billing provider owns this organization's subscription; an internal entitlement cannot change it.",
            StatusCodes.Status409Conflict),
        OrganizationInternalEntitlementOutcome.SubscriptionClosed => Problem(
            "internal-entitlement.subscription-closed",
            "The organization's internal subscription is closed and cannot be re-granted.",
            StatusCodes.Status409Conflict),
        _ => throw new InvalidOperationException("Unsupported internal entitlement outcome.")
    };

    private static AdminInternalEntitlementResponse ToResponse(OrganizationInternalEntitlementStatus status) =>
        new(status.OrganizationId, status.State, status.MaxInstances, status.ExpiresAt, status.UpdatedAt);

    private static IResult Problem(string code, string title, int statusCode) =>
        Results.Problem(title: title, statusCode: statusCode, extensions: new Dictionary<string, object?> { ["code"] = code });
}
