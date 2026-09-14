using System.Security.Claims;
using ElsaControl.Api.Authentication;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Admin.Organizations;

/// <summary>
/// Operator-only lifecycle for a guided design-partner entitlement. Minting is
/// admitted only when a verified active Azure subscription bind already exists.
/// </summary>
public static class AdminOrganizationAzureBoundEntitlementEndpoints
{
    public static IEndpointRouteBuilder MapAdminOrganizationAzureBoundEntitlementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/organizations/{organizationId:guid}/azure-bound-entitlement")
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Admin Organizations");

        group.MapGet("", async (
            Guid organizationId,
            OrganizationAzureBoundEntitlementService entitlements,
            CancellationToken cancellationToken) =>
            ToHttpResult(await entitlements.GetAsync(organizationId, cancellationToken)));

        group.MapPut("", async (
            Guid organizationId,
            AdminAzureBoundEntitlementRequest request,
            HttpContext context,
            IOptions<ControlIdentityOptions> identityOptions,
            OrganizationAzureBoundEntitlementService entitlements,
            CancellationToken cancellationToken) =>
            ToHttpResult(await entitlements.MintAsync(
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
            OrganizationAzureBoundEntitlementService entitlements,
            CancellationToken cancellationToken) =>
            ToHttpResult(await entitlements.RevokeAsync(
                organizationId,
                OperatorSubject(context.User, identityOptions.Value),
                cancellationToken)));

        return endpoints;
    }

    private static string? OperatorSubject(ClaimsPrincipal user, ControlIdentityOptions identityOptions) =>
        user.FindFirstValue(identityOptions.Claims.Subject) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    private static IResult ToHttpResult(OrganizationAzureBoundEntitlementResult result) => result.Outcome switch
    {
        OrganizationAzureBoundEntitlementOutcome.Current or
            OrganizationAzureBoundEntitlementOutcome.Minted => Results.Ok(ToResponse(result.Status!)),
        OrganizationAzureBoundEntitlementOutcome.Revoked or
            OrganizationAzureBoundEntitlementOutcome.Unchanged => Results.NoContent(),
        OrganizationAzureBoundEntitlementOutcome.Invalid => Results.ValidationProblem(
            result.Errors!,
            extensions: new Dictionary<string, object?> { ["code"] = "azure-bound-entitlement.invalid" }),
        OrganizationAzureBoundEntitlementOutcome.OrganizationNotFound => Problem(
            "organization.not-found", "Organization was not found.", StatusCodes.Status404NotFound),
        OrganizationAzureBoundEntitlementOutcome.NotMinted => Problem(
            "azure-bound-entitlement.not-minted",
            "The organization has no Azure-bound entitlement to revoke.",
            StatusCodes.Status404NotFound),
        OrganizationAzureBoundEntitlementOutcome.BindingRequired => Problem(
            ElsaControl.Deployment.Core.Instances.ElsaInstanceCommercialOperation.BindingRequired,
            "An active Azure subscription bind is required before minting this entitlement.",
            StatusCodes.Status409Conflict),
        OrganizationAzureBoundEntitlementOutcome.CommercialSubscriptionExists => Problem(
            "azure-bound-entitlement.commercial-subscription",
            "Another commercial provider owns this organization's subscription.",
            StatusCodes.Status409Conflict),
        OrganizationAzureBoundEntitlementOutcome.AlreadyMinted => Problem(
            "azure-bound-entitlement.already-minted",
            "The organization already has an active Azure-bound subscription.",
            StatusCodes.Status409Conflict),
        _ => throw new InvalidOperationException("Unsupported Azure-bound entitlement outcome.")
    };

    private static AdminAzureBoundEntitlementResponse ToResponse(OrganizationAzureBoundEntitlementStatus status) =>
        new(status.OrganizationId, status.State, status.MaxInstances, status.ExpiresAt, status.UpdatedAt);

    private static IResult Problem(string code, string title, int statusCode) =>
        Results.Problem(title: title, statusCode: statusCode, extensions: new Dictionary<string, object?> { ["code"] = code });
}
