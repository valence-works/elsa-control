using ElsaControl.Api.Authentication;
using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.Workspace;

public static class WorkspaceMeEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceMeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/me/workspaces", async (
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            AccountWorkspaceService accounts,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();

            var accountContext = await accounts.GetOrCreateAsync(identity, cancellationToken);
            return Results.Ok(ToResponse(accountContext));
        }).WithTags("Workspace Identity").AllowCloudBff().AllowHubBff().PrivateNoStore();

        endpoints.MapGet("/api/me/organizations", async (
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            AccountWorkspaceService accounts,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();

            var accountContext = await accounts.GetOrCreateAsync(identity, cancellationToken);
            return Results.Ok(ToResponse(accountContext));
        }).WithTags("Workspace Identity").AllowCloudBff().AllowHubBff().PrivateNoStore();

        // Non-provisioning read: an unknown identity gets an empty context and no rows are written.
        endpoints.MapGet("/api/me/workspace-memberships", async (
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            AccountWorkspaceService accounts,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();

            var accountContext = await accounts.FindAsync(identity, cancellationToken);
            var response = accountContext is null ? null : ToResponse(accountContext);
            return Results.Ok(new MeWorkspaceMembershipsResponse(response?.Account, response?.Workspaces ?? [], response?.Organizations ?? []));
        }).WithTags("Workspace Identity").AllowCloudBff().AllowHubBff();

        return endpoints;
    }

    public static MeWorkspacesResponse ToResponse(AccountWorkspaceContext context) =>
        new(
            new AccountContextResponse(context.Account.Id, context.Account.DisplayName, context.Account.Email),
            context.Workspaces.Select(x => new WorkspaceContextResponse(x.Id, x.Name, x.Kind, x.Role, x.OrganizationId, x.OrganizationName, x.OrganizationRole)).ToList())
        {
            Organizations = context.Organizations.Select(x => new OrganizationContextResponse(x.Id, x.Name, x.Role)).ToList()
        };
}
