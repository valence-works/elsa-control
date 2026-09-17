using ElsaControl.Api.Authentication;

namespace ElsaControl.Api.Cloud;

public static class CloudCompatibilityEndpoints
{
    private static readonly string[] Capabilities =
    [
        "cloud.bootstrap.v1",
        "hosted.instances.list.v1",
        "hosted.instances.create.v1",
        "hosted.instances.status.v1",
        "hosted.studio.handoff.issue.v1",
        "hosted.instances.quota-problem.v1",
        "hosted.instances.confirmed-delete.v1"
    ];

    public static IEndpointRouteBuilder MapCloudCompatibilityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/cloud/compatibility", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";

            return Results.Ok(new CloudCompatibilityResponse(1, Capabilities));
        })
            .WithTags("Cloud Compatibility")
            .RequireAuthorization()
            .AllowCloudBff();

        return endpoints;
    }
}

public sealed record CloudCompatibilityResponse(int ContractVersion, IReadOnlyList<string> Capabilities);
