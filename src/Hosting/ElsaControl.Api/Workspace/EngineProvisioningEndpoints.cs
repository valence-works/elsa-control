using ElsaControl.Api.Authentication;
using ElsaControl.Deployment.Core.Provisioning;

namespace ElsaControl.Api.Workspace;

public static class EngineProvisioningEndpoints
{
    public static IEndpointRouteBuilder MapEngineProvisioningEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/workspaces/{workspaceId:guid}/engine-provisioning/providers")
            .WithTags("Workspace Engine Provisioning");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });

        group.MapGet("", (
            IEnumerable<IEngineProvisioningModule> modules) =>
        {
            return Results.Ok(new EngineProvisioningProvidersResponse(
                modules
                    .Select(module => new EngineProvisioningProviderResponse(module.Id, module.DisplayName))
                    .OrderBy(provider => provider.Id, StringComparer.Ordinal)
                    .ToList()));
        })
            .RequireWorkspaceAccess();

        return endpoints;
    }
}

public sealed record EngineProvisioningProvidersResponse(
    IReadOnlyList<EngineProvisioningProviderResponse> Providers);

public sealed record EngineProvisioningProviderResponse(string Id, string DisplayName);
