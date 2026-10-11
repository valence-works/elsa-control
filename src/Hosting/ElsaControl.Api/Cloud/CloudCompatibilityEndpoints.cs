using ElsaControl.Api.Authentication;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Cloud;

public static class CloudCompatibilityEndpoints
{
    public const string ProvisioningProgressCapability = "hosted.instances.provisioning-progress.v1";
    public const int CurrentContractVersion = 1;

    private static readonly string[] Capabilities =
    [
        "cloud.bootstrap.v1",
        "hosted.instances.list.v1",
        "hosted.instances.create.v1",
        "hosted.instances.status.v1",
        ProvisioningProgressCapability,
        "hosted.instances.overview.v1",
        "hosted.studio.handoff.issue.v1",
        "hosted.instances.quota-problem.v1",
        "hosted.instances.confirmed-delete.v1",
        "hosted.instances.reconciliation-cleanup.v1",
        "hosted.subscription.manage.v1",
        "hosted.deployments.audit.v1"
    ];

    public static IEndpointRouteBuilder MapCloudCompatibilityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/cloud/compatibility", (
            HttpContext context,
            IOptions<CloudCompatibilityOptions> options) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";

            return Results.Ok(CreateResponse(options.Value));
        })
            .WithTags("Cloud Compatibility")
            .RequireAuthorization()
            .AllowCloudBff();

        return endpoints;
    }

    internal static CloudCompatibilityResponse CreateResponse(CloudCompatibilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.NormalizedStagingFixture switch
        {
            CloudCompatibilityStagingFixture.MissingCapability =>
                new CloudCompatibilityResponse(
                    CurrentContractVersion,
                    Capabilities.Where(capability => capability != ProvisioningProgressCapability).ToArray()),
            CloudCompatibilityStagingFixture.OlderContract =>
                new CloudCompatibilityResponse(0, Capabilities),
            _ => new CloudCompatibilityResponse(CurrentContractVersion, Capabilities)
        };
    }
}

public sealed record CloudCompatibilityResponse(int ContractVersion, IReadOnlyList<string> Capabilities);
