using ElsaControl.Deployment.Core.Provisioning;

namespace ElsaControl.Api.Workspace;

/// <summary>Prevents new provisioning requests when no provider module is composed.</summary>
public sealed class EngineProvisioningRequiredFilter(IEnumerable<IEngineProvisioningModule> modules) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        modules.Any()
            ? next(context)
            : ValueTask.FromResult<object?>(Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Engine provisioning unavailable",
                detail: "No engine provisioning module is enabled.",
                extensions: new Dictionary<string, object?> { ["code"] = "engine-provisioning.disabled" }));
}
