using ElsaControl.Api.Authentication;
using ElsaControl.Deployment.Azure;

namespace ElsaControl.Api.Admin.Staging;

/// <summary>
/// Staging-only operator lever that arms a Health-step crash-loop or timeout
/// inject on an allowlisted instance. It is never Cloud BFF allowlisted and
/// never mutates Azure or catalog state itself.
/// </summary>
public static class StagingProvisioningHealthLeverEndpoints
{
    public static IEndpointRouteBuilder MapStagingProvisioningHealthLeverEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/staging/provisioning-health-lever/{instanceId:guid}/crash-loop",
                (Guid instanceId, StagingProvisioningHealthLever lever) =>
                    ToHttpResult(
                        lever.Arm(instanceId, StagingProvisioningHealthFailureMode.CrashLoop),
                        instanceId,
                        StagingProvisioningHealthFailureMode.CrashLoop))
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Staging Provisioning Health Lever");

        endpoints.MapPost(
                "/api/staging/provisioning-health-lever/{instanceId:guid}/health-timeout",
                (Guid instanceId, StagingProvisioningHealthLever lever) =>
                    ToHttpResult(
                        lever.Arm(instanceId, StagingProvisioningHealthFailureMode.Timeout),
                        instanceId,
                        StagingProvisioningHealthFailureMode.Timeout))
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Staging Provisioning Health Lever");

        endpoints.MapPost(
                "/api/staging/provisioning-health-lever/{instanceId:guid}/reset",
                (Guid instanceId, StagingProvisioningHealthLever lever) =>
                    ToHttpResult(
                        lever.Reset(instanceId),
                        instanceId,
                        StagingProvisioningHealthFailureMode.None))
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Staging Provisioning Health Lever");

        return endpoints;
    }

    private static IResult ToHttpResult(
        StagingProvisioningHealthLeverOutcome outcome,
        Guid instanceId,
        StagingProvisioningHealthFailureMode mode) => outcome switch
    {
        StagingProvisioningHealthLeverOutcome.Armed or StagingProvisioningHealthLeverOutcome.Reset =>
            Results.Ok(new StagingProvisioningHealthLeverResponse(
                instanceId,
                StagingProvisioningHealthLever.ModeName(mode),
                StagingProvisioningHealthLever.OutcomeCode(outcome, mode))),
        StagingProvisioningHealthLeverOutcome.Disabled => Problem(
            StagingProvisioningHealthLeverDefaults.DisabledCode,
            "The staging provisioning health lever is disabled.",
            StatusCodes.Status403Forbidden),
        StagingProvisioningHealthLeverOutcome.InstanceNotAllowed => Problem(
            StagingProvisioningHealthLeverDefaults.InstanceNotAllowedCode,
            "The instance is not on the staging provisioning health lever allowlist.",
            StatusCodes.Status403Forbidden),
        _ => throw new InvalidOperationException("Unsupported staging provisioning health lever outcome.")
    };

    private static IResult Problem(string code, string title, int statusCode) =>
        Results.Problem(
            title: title,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}

public sealed record StagingProvisioningHealthLeverResponse(
    Guid InstanceId,
    string Mode,
    string Code);
