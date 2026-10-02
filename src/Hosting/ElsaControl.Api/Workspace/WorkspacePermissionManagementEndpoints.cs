using ElsaControl.Api.Authentication;
using ElsaControl.Deployment.Core.Workspace;

namespace ElsaControl.Api.Workspace;

public static class WorkspacePermissionManagementEndpoints
{
    public static IEndpointRouteBuilder MapWorkspacePermissionManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/workspaces/{workspaceId:guid}/permissions")
            .WithTags("Workspace Permissions")
            .MapCommonApiExceptions();

        group.MapGet("/catalog", (WorkspacePermissionService permissions) =>
        {
            var catalog = permissions.GetCatalog();
            return Results.Ok(new WorkspacePermissionCatalogResponse(
                catalog.All.Order(StringComparer.Ordinal).ToList(),
                catalog.OwnerDefaults.Order(StringComparer.Ordinal).ToList()));
        }).RequireWorkspaceOwner();

        group.MapGet("/grants", async (
            Guid workspaceId,
            Guid? accountId,
            WorkspacePermissionService permissions,
            CancellationToken cancellationToken) =>
            Results.Ok(new WorkspacePermissionGrantsResponse(
                await permissions.ListGrantsAsync(workspaceId, accountId, cancellationToken))))
            .RequireWorkspaceOwner();

        group.MapGet("/audit", async (
            Guid workspaceId,
            Guid? accountId,
            WorkspacePermissionService permissions,
            CancellationToken cancellationToken) =>
            Results.Ok(new WorkspacePermissionAuditResponse(
                await permissions.ListAuditRecordsAsync(workspaceId, accountId, cancellationToken))))
            .RequireWorkspaceOwner();

        group.MapPost("/grants", async (
            Guid workspaceId,
            WorkspacePermissionGrantRequest request,
            HttpContext context,
            WorkspacePermissionService permissions,
            CancellationToken cancellationToken) =>
        {
            var grant = await permissions.GrantAsync(
                workspaceId,
                new GrantWorkspacePermissionRequest(request.AccountId, request.Permission, context.GetWorkspaceAccess().AccountId),
                cancellationToken);
            return Results.Ok(grant);
        }).RequireWorkspaceOwner().AllowCloudBff();

        group.MapPost("/revocations", async (
            Guid workspaceId,
            WorkspacePermissionRevokeRequest request,
            HttpContext context,
            WorkspacePermissionService permissions,
            CancellationToken cancellationToken) =>
        {
            var result = await permissions.RevokeAsync(
                workspaceId,
                new RevokeWorkspacePermissionRequest(request.AccountId, request.Permission, context.GetWorkspaceAccess().AccountId),
                cancellationToken);
            return Results.Ok(result);
        }).RequireWorkspaceOwner().AllowCloudBff();

        return endpoints;
    }
}

public sealed record WorkspacePermissionGrantRequest(Guid AccountId, string Permission);
public sealed record WorkspacePermissionRevokeRequest(Guid AccountId, string Permission);
public sealed record WorkspacePermissionCatalogResponse(IReadOnlyList<string> All, IReadOnlyList<string> OwnerDefaults);
public sealed record WorkspacePermissionGrantsResponse(IReadOnlyList<WorkspacePermissionGrant> Items);
public sealed record WorkspacePermissionAuditResponse(IReadOnlyList<WorkspacePermissionAuditRecord> Items);
