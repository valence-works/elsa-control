using System.Security.Claims;
using ElsaControl.Api.Authentication;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Admin.Organizations;

public static class AdminOrganizationEndpoints
{
    public static IEndpointRouteBuilder MapAdminOrganizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/organizations")
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Admin Organizations");

        group.MapPost("", async (
            AdminOrganizationCreateRequest request,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            IOptions<ControlIdentityOptions> identityOptions,
            AccountWorkspaceService organizations,
            CancellationToken cancellationToken) =>
        {
            // API-key principals are service operators rather than accounts. Never reinterpret
            // the synthetic api-key name as a customer identity when ownerAccountId is omitted.
            var isApiKey = context.User.Identities.Any(identity =>
                identity.IsAuthenticated &&
                string.Equals(identity.AuthenticationType, ApiKeyAuthenticationDefaults.Scheme, StringComparison.Ordinal));
            var currentIdentity = isApiKey ? null : await identityReader.ReadAsync(context);
            var operatorSubject = currentIdentity?.Subject
                ?? context.User.FindFirstValue(identityOptions.Value.Claims.Subject)
                ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var result = await organizations.CreateOrganizationAsync(
                request.Name,
                request.OwnerAccountId,
                currentIdentity,
                operatorSubject,
                cancellationToken);

            return result.Failure switch
            {
                OrganizationCreateFailure.NameRequired => Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["name"] = ["Name is required."] }),
                OrganizationCreateFailure.NameTooLong => Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["name"] = ["Name must be 256 characters or fewer."] }),
                OrganizationCreateFailure.OwnerAccountRequired => Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["ownerAccountId"] = ["OwnerAccountId is required when the operator account cannot be resolved."] }),
                OrganizationCreateFailure.OwnerAccountNotFound => Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["ownerAccountId"] = ["The owner account was not found."] }),
                null when result.Succeeded => Results.Ok(new AdminOrganizationCreateResponse(
                    result.OrganizationId,
                    result.WorkspaceId,
                    result.OwnerAccountId)),
                _ => Results.Problem(
                    title: "The organization could not be created.",
                    statusCode: StatusCodes.Status500InternalServerError)
            };
        });

        return endpoints;
    }
}
