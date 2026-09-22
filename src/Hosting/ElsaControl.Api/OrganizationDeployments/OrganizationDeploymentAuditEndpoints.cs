using ElsaControl.Api.Authentication;
using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.OrganizationDeployments;

public static class OrganizationDeploymentAuditEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationDeploymentAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/organizations/{organizationId:guid}/deployments/audit", async (
            Guid organizationId,
            int? page,
            int? pageSize,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            OrganizationDeploymentAuditService audit,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";

            if (!OrganizationDeploymentAuditRules.TryNormalizePage(page, pageSize, out var currentPage, out var currentPageSize))
                return Problem("deployments.audit-invalid", "The deployment audit page request is invalid.", StatusCodes.Status400BadRequest);

            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();

            var result = await audit.ListAsync(identity, organizationId, currentPage, currentPageSize, cancellationToken);
            if (result.Succeeded)
                return Results.Ok(result.Page);

            if (result.Unavailable)
                return Problem(
                    "deployments.audit-unavailable",
                    "The deployment audit feed is temporarily unavailable.",
                    StatusCodes.Status503ServiceUnavailable);

            return result.Failure is OrganizationWorkspaceFailure.OrganizationNotAllowed
                ? Results.NotFound(new { code = "organization.not-found" })
                : Results.Forbid();
        })
            .WithTags("Organization Deployments")
            .AllowCloudBff();

        return endpoints;
    }

    private static IResult Problem(string code, string title, int statusCode) =>
        Results.Problem(
            title: title,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
