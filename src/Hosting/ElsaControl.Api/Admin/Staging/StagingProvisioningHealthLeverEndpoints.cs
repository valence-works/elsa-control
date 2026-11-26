using System.Security.Claims;
using ElsaControl.Api.Authentication;
using ElsaControl.Deployment.Azure;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
                (
                    Guid instanceId,
                    StagingProvisioningHealthLever lever,
                    HttpContext context,
                    IOptions<ControlIdentityOptions> identityOptions,
                    ILogger<StagingProvisioningHealthLever> logger) =>
                    ToHttpResult(
                        Arm(
                            lever,
                            logger,
                            instanceId,
                            StagingProvisioningHealthFailureMode.CrashLoop,
                            OperatorSubject(context.User, identityOptions.Value)),
                        instanceId,
                        StagingProvisioningHealthFailureMode.CrashLoop))
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Staging Provisioning Health Lever");

        endpoints.MapPost(
                "/api/staging/provisioning-health-lever/{instanceId:guid}/health-timeout",
                (
                    Guid instanceId,
                    StagingProvisioningHealthLever lever,
                    HttpContext context,
                    IOptions<ControlIdentityOptions> identityOptions,
                    ILogger<StagingProvisioningHealthLever> logger) =>
                    ToHttpResult(
                        Arm(
                            lever,
                            logger,
                            instanceId,
                            StagingProvisioningHealthFailureMode.Timeout,
                            OperatorSubject(context.User, identityOptions.Value)),
                        instanceId,
                        StagingProvisioningHealthFailureMode.Timeout))
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Staging Provisioning Health Lever");

        endpoints.MapPost(
                "/api/staging/provisioning-health-lever/{instanceId:guid}/reset",
                (
                    Guid instanceId,
                    StagingProvisioningHealthLever lever,
                    HttpContext context,
                    IOptions<ControlIdentityOptions> identityOptions,
                    ILogger<StagingProvisioningHealthLever> logger) =>
                    ToHttpResult(
                        Reset(
                            lever,
                            logger,
                            instanceId,
                            OperatorSubject(context.User, identityOptions.Value)),
                        instanceId,
                        StagingProvisioningHealthFailureMode.None))
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Staging Provisioning Health Lever");

        return endpoints;
    }

    internal static StagingProvisioningHealthLeverOutcome Arm(
        StagingProvisioningHealthLever lever,
        ILogger logger,
        Guid instanceId,
        StagingProvisioningHealthFailureMode mode,
        string? operatorSubject)
    {
        var outcome = lever.Arm(instanceId, mode);
        StagingProvisioningHealthLeverAudit.Write(logger, outcome, mode, operatorSubject);
        return outcome;
    }

    internal static StagingProvisioningHealthLeverOutcome Reset(
        StagingProvisioningHealthLever lever,
        ILogger logger,
        Guid instanceId,
        string? operatorSubject)
    {
        var outcome = lever.Reset(instanceId);
        StagingProvisioningHealthLeverAudit.Write(
            logger,
            outcome,
            StagingProvisioningHealthFailureMode.None,
            operatorSubject);
        return outcome;
    }

    private static string? OperatorSubject(ClaimsPrincipal user, ControlIdentityOptions identityOptions) =>
        user.FindFirstValue(identityOptions.Claims.Subject) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

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

internal static class StagingProvisioningHealthLeverAudit
{
    public static void Write(
        ILogger logger,
        StagingProvisioningHealthLeverOutcome outcome,
        StagingProvisioningHealthFailureMode mode,
        string? operatorSubject)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogInformation(
            "The staging provisioning health lever completed {Outcome} for {Mode}. Operator {OperatorSubject}.",
            StagingProvisioningHealthLever.OutcomeCode(outcome, mode),
            StagingProvisioningHealthLever.ModeName(mode),
            OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject(operatorSubject) ?? "anonymous");
    }
}

public sealed record StagingProvisioningHealthLeverResponse(
    Guid InstanceId,
    string Mode,
    string Code);
