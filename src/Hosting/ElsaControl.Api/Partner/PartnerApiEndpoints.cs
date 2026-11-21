using ElsaControl.Api.Authentication;
using ElsaControl.Deployment.Core.Workspace;
using Microsoft.AspNetCore.RateLimiting;

namespace ElsaControl.Api.Partner;

public static class PartnerApiEndpoints
{
    public static IEndpointRouteBuilder MapPartnerApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/partner/v1")
            .RequireAuthorization(PartnerApiKeyDefaults.Policy)
            .RequireRateLimiting(PartnerApiRateLimitDefaults.PolicyName)
            .AllowPartnerApi()
            .WithGroupName(PartnerApiKeyDefaults.OpenApiDocumentName)
            .WithTags("Partner API");

        group.MapGet("/account", (HttpContext context, WorkspacePermissionService permissions) =>
        {
            if (!PartnerApiPrincipal.TryRead(context.User, out var organizationId, out var workspaceId, out var accountId, out var keyId))
                return PartnerUnauthenticated();

            var scopes = context.User.FindAll(PartnerApiKeyDefaults.ScopeClaim).Select(claim => claim.Value).ToArray();
            var effective = permissions.GetPartnerProvisionerPermissions(workspaceId, accountId, scopes);
            return Results.Json(
                new PartnerAccountResponse(
                    organizationId,
                    workspaceId,
                    accountId,
                    keyId,
                    PartnerApiKeyDefaults.Role,
                    scopes,
                    effective.Permissions.Order(StringComparer.Ordinal).ToArray()),
                statusCode: StatusCodes.Status200OK);
        })
        .AddEndpointFilter(NoStoreCacheFilter.Instance)
        .Produces<PartnerAccountResponse>();

        group.MapGet("/account/workspace", (HttpContext context) =>
        {
            if (!PartnerApiPrincipal.TryRead(context.User, out _, out var workspaceId, out var accountId, out var keyId))
                return PartnerUnauthenticated();

            return Results.Json(
                new PartnerWorkspaceResponse(workspaceId, accountId, keyId, PartnerApiKeyDefaults.Role),
                statusCode: StatusCodes.Status200OK);
        })
        .RequireAuthorization(PartnerApiAuthorization.PolicyName(ElsaControl.PackageCatalog.Core.Accounts.PartnerApiKeyScopes.EnginesWrite))
        .AddEndpointFilter(NoStoreCacheFilter.Instance)
        .Produces<PartnerWorkspaceResponse>();

        return endpoints;
    }

    private static IResult PartnerUnauthenticated() =>
        Results.Problem(
            title: PartnerApiKeyDefaults.UnauthorizedTitle,
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = PartnerApiKeyDefaults.UnauthorizedCode
            });
}

public sealed record PartnerAccountResponse(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid AccountId,
    string KeyId,
    string Role,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Permissions);

public sealed record PartnerWorkspaceResponse(
    Guid WorkspaceId,
    Guid AccountId,
    string KeyId,
    string Role);

file sealed class NoStoreCacheFilter : IEndpointFilter
{
    public static NoStoreCacheFilter Instance { get; } = new();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return result;
    }
}
