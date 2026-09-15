using System.Text.Json.Serialization;
using ElsaControl.Api.Authentication;
using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.Cloud;

public static class CloudBootstrapEndpoints
{
    public static IEndpointRouteBuilder MapCloudBootstrapEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/cloud/bootstrap", async (
            CloudBootstrapRequest request,
            HttpContext context,
            ControlIdentityReader bearerIdentityReader,
            CustomerSessionIdentityReader cookieIdentityReader,
            AccountWorkspaceService accounts,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.Pragma = "no-cache";

            var identity = await ReadCustomerIdentityAsync(context, bearerIdentityReader, cookieIdentityReader);
            if (identity is null)
            {
                return Results.Problem(
                    title: "Control customer authentication is required.",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var accountContext = await accounts.GetOrCreateAsync(identity, cancellationToken);
            var workspace = accountContext.Workspaces
                .OrderByDescending(x => x.OrganizationRole is OrganizationRole.Owner)
                .ThenByDescending(x => x.Role is WorkspaceRole.Owner)
                .ThenBy(x => x.OrganizationId)
                .ThenBy(x => x.Id)
                .FirstOrDefault();
            if (workspace is null)
            {
                return Results.Problem(
                    title: "The customer organization has no accessible workspace.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "cloud.bootstrap.workspace-unavailable"
                    });
            }

            return Results.Ok(new CloudBootstrapResponse(workspace.OrganizationId, workspace.Id));
        }).WithTags("Cloud Bootstrap")
            .AllowCloudBff();

        return endpoints;
    }

    private static async ValueTask<TrustedWorkspaceIdentity?> ReadCustomerIdentityAsync(
        HttpContext context,
        ControlIdentityReader bearerIdentityReader,
        CustomerSessionIdentityReader cookieIdentityReader)
    {
        var identity = await bearerIdentityReader.ReadAsync(context);
        if (identity is not null || ControlIdentityReader.HasBearerToken(context))
            return identity;

        return await cookieIdentityReader.ReadAsync(context);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloudBootstrapRequest;

public sealed record CloudBootstrapResponse(Guid OrganizationId, Guid WorkspaceId);
