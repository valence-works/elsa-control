using ElsaControl.Api.Authentication;
using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.Workspace;

public static class OrganizationWorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationWorkspaceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/workspaces")
            .WithTags("Organization Workspaces");

        group.MapGet("/", async (
            Guid organizationId,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            AccountWorkspaceService accounts,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();

            var result = await accounts.ListOrganizationWorkspacesAsync(identity, organizationId, cancellationToken);
            return result.Succeeded
                ? Results.Ok(new OrganizationWorkspacesResponse(result.Workspaces.Select(ToResponse).ToList()))
                : ToHttpResult(result.Failure!.Value);
        }).AllowCloudBff();

        group.MapPost("/", async (
            Guid organizationId,
            OrganizationWorkspaceCreateRequest request,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            AccountWorkspaceService accounts,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new WorkspaceValidationErrorResponse(["Workspace name is required."]));

            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();

            var createRequest = new CreateOrganizationWorkspaceRequest(
                request.Name.Trim(),
                (request.InitialMembers ?? [])
                .Select(x => new InitialWorkspaceMember(x.AccountId, x.Role))
                .ToList());
            var result = await accounts.CreateOrganizationWorkspaceAsync(identity, organizationId, createRequest, cancellationToken);
            if (!result.Succeeded)
                return ToHttpResult(result.Failure!.Value);

            return Results.Created($"/api/organizations/{organizationId}/workspaces/{result.Workspace!.Id}", ToResponse(result.Workspace));
        });

        group.MapPatch("/{workspaceId:guid}", async (
            Guid organizationId,
            Guid workspaceId,
            OrganizationWorkspaceUpdateRequest request,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            AccountWorkspaceService accounts,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new WorkspaceValidationErrorResponse(["Workspace name is required."]));

            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();

            var result = await accounts.UpdateOrganizationWorkspaceAsync(
                identity,
                organizationId,
                workspaceId,
                new UpdateOrganizationWorkspaceRequest(request.Name.Trim(), request.Status),
                cancellationToken);
            return result.Succeeded
                ? Results.Ok(ToResponse(result.Workspace!))
                : ToHttpResult(result.Failure!.Value);
        });

        group.MapPut("/{workspaceId:guid}/members/{accountId:guid}", async (
            Guid organizationId,
            Guid workspaceId,
            Guid accountId,
            OrganizationWorkspaceMembershipRequest request,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            AccountWorkspaceService accounts,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();

            var result = await accounts.SetWorkspaceMembershipAsync(identity, organizationId, workspaceId, accountId, request.Role, cancellationToken);
            return result.Succeeded
                ? Results.Ok(ToResponse(result.Workspace!))
                : ToHttpResult(result.Failure!.Value);
        }).AllowCloudBff();

        group.MapDelete("/{workspaceId:guid}/members/{accountId:guid}", async (
            Guid organizationId,
            Guid workspaceId,
            Guid accountId,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            AccountWorkspaceService accounts,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();

            var result = await accounts.RemoveWorkspaceMembershipAsync(identity, organizationId, workspaceId, accountId, cancellationToken);
            return result.Succeeded
                ? Results.NoContent()
                : ToHttpResult(result.Failure!.Value);
        }).AllowCloudBff();

        return endpoints;
    }

    private static WorkspaceContextResponse ToResponse(WorkspaceSummary workspace) =>
        new(workspace.Id, workspace.Name, workspace.Kind, workspace.Role, workspace.OrganizationId, workspace.OrganizationName, workspace.OrganizationRole);

    private static IResult ToHttpResult(OrganizationWorkspaceFailure failure) =>
        failure switch
        {
            OrganizationWorkspaceFailure.OrganizationNotAllowed => Results.Problem(
                title: "Organization access is not allowed.",
                statusCode: StatusCodes.Status404NotFound),
            OrganizationWorkspaceFailure.OrganizationRoleNotAllowed => Results.Problem(
                title: "Organization role does not allow this operation.",
                statusCode: StatusCodes.Status403Forbidden),
            OrganizationWorkspaceFailure.EntitlementRequired => Results.Problem(
                title: "Organization entitlement is required.",
                statusCode: StatusCodes.Status403Forbidden),
            OrganizationWorkspaceFailure.WorkspaceLimitReached => Results.Problem(
                title: "Organization workspace limit has been reached.",
                statusCode: StatusCodes.Status403Forbidden),
            OrganizationWorkspaceFailure.DuplicateWorkspaceName => Results.Problem(
                title: "Workspace name already exists in this organization.",
                statusCode: StatusCodes.Status409Conflict),
            OrganizationWorkspaceFailure.WorkspaceNotFound => Results.Problem(
                title: "Workspace was not found.",
                statusCode: StatusCodes.Status404NotFound),
            OrganizationWorkspaceFailure.TargetAccountNotOrganizationMember => Results.Problem(
                title: "Target account is not an active organization member.",
                statusCode: StatusCodes.Status400BadRequest),
            OrganizationWorkspaceFailure.LastWorkspaceOwner => Results.Problem(
                title: "The last workspace owner cannot be removed.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Results.Problem(
                title: "Organization workspace request failed.",
                statusCode: StatusCodes.Status400BadRequest)
        };
}
