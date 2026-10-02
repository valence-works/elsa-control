using System.Security.Claims;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.OrganizationBilling;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Admin.Staging;

/// <summary>
/// Staging-only operator lever that accepts a Reconcile and parks it in
/// RecoveryRequired through the real transition. It is never Cloud BFF
/// allowlisted and never writes operation state itself.
/// </summary>
public static class StagingRecoveryLifecycleLeverEndpoints
{
    public static IEndpointRouteBuilder MapStagingRecoveryLifecycleLeverEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/staging/lifecycle-lever/instances/{instanceId:guid}/recovery-required",
                async (
                    Guid instanceId,
                    HttpContext context,
                    IOptions<ControlIdentityOptions> identityOptions,
                    StagingRecoveryLifecycleLever lever,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var result = await lever.FireAsync(
                            instanceId,
                            OperatorSubject(context.User, identityOptions.Value),
                            await ReadReasonAsync(context, cancellationToken),
                            cancellationToken);
                        return ToHttpResult(result);
                    }
                    catch (ElsaInstanceLifecycleConflictException exception)
                    {
                        return Conflict(exception, "The state machine refused the recovery-required transition.");
                    }
                    catch (ElsaInstanceStateConflictException)
                    {
                        return InvalidState("The state machine refused the recovery-required transition.");
                    }
                    catch (InvalidOperationException)
                    {
                        return InvalidState("The state machine refused the recovery-required transition.");
                    }
                })
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Staging Lifecycle Lever");

        endpoints.MapPost(
                "/api/staging/lifecycle-lever/instances/{instanceId:guid}/reset",
                async (
                    Guid instanceId,
                    HttpContext context,
                    IOptions<ControlIdentityOptions> identityOptions,
                    StagingRecoveryLifecycleLever lever,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var result = await lever.ResetAsync(
                            instanceId,
                            OperatorSubject(context.User, identityOptions.Value),
                            cancellationToken);
                        return ToHttpResult(result);
                    }
                    catch (ElsaInstanceLifecycleConflictException exception)
                    {
                        return Conflict(exception, "The state machine refused the staging lever reset.");
                    }
                    catch (ElsaInstanceStateConflictException)
                    {
                        return InvalidState("The state machine refused the staging lever reset.");
                    }
                    catch (InvalidOperationException)
                    {
                        return InvalidState("The state machine refused the staging lever reset.");
                    }
                })
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Staging Lifecycle Lever");

        return endpoints;
    }

    private static IResult Conflict(ElsaInstanceLifecycleConflictException exception, string invalidStateTitle) =>
        ManagedElsaInstanceEndpoints.Problem(
            ManagedElsaInstanceEndpoints.ConflictCode(exception),
            exception.Reason == ElsaInstanceLifecycleConflictReason.OperationActive
                ? "An instance operation is already active."
                : invalidStateTitle,
            ManagedElsaInstanceEndpoints.ConflictStatusCode(exception));

    private static IResult InvalidState(string title) =>
        ManagedElsaInstanceEndpoints.Problem(
            "instance.invalid-state",
            title,
            StatusCodes.Status409Conflict);

    private static string? OperatorSubject(ClaimsPrincipal user, ControlIdentityOptions identityOptions) =>
        user.FindFirstValue(identityOptions.Claims.Subject) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    private static async Task<string?> ReadReasonAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is null or 0)
            return null;

        var request = await context.Request.ReadFromJsonAsync<StagingRecoveryLifecycleLeverFireRequest>(cancellationToken);
        return request?.Reason;
    }

    private static IResult ToHttpResult(StagingRecoveryLifecycleLeverResult result) => result.Outcome switch
    {
        StagingRecoveryLifecycleLeverOutcome.Fired => Results.Ok(ToResponse(result.Commit!, result.Commit!.Reason)),
        StagingRecoveryLifecycleLeverOutcome.Reset => Results.Ok(ToResponse(result.Commit!, StagingRecoveryLifecycleLeverDefaults.ResetCode)),
        StagingRecoveryLifecycleLeverOutcome.Disabled => Problem(
            StagingRecoveryLifecycleLeverDefaults.DisabledCode,
            "The staging recovery lifecycle lever is disabled.",
            StatusCodes.Status403Forbidden),
        StagingRecoveryLifecycleLeverOutcome.InstanceNotAllowed => Problem(
            StagingRecoveryLifecycleLeverDefaults.InstanceNotAllowedCode,
            "The instance is not on the staging recovery lifecycle lever allowlist.",
            StatusCodes.Status403Forbidden),
        StagingRecoveryLifecycleLeverOutcome.InstanceNotFound => Results.NotFound(),
        StagingRecoveryLifecycleLeverOutcome.ReasonNotAllowed => Problem(
            StagingRecoveryLifecycleLeverDefaults.ReasonNotAllowedCode,
            "The staging recovery lifecycle lever reason is not allowed.",
            StatusCodes.Status400BadRequest),
        _ => throw new InvalidOperationException("Unsupported staging recovery lifecycle lever outcome.")
    };

    private static StagingRecoveryLifecycleLeverResponse ToResponse(
        StagingRecoveryLifecycleLeverCommit commit,
        string code) =>
        new(
            commit.Instance.Id,
            commit.Instance.WorkspaceId,
            commit.Operation.Id,
            commit.Operation.Action,
            commit.Operation.State,
            commit.Operation.AttemptNumber,
            commit.Instance.Version,
            code);

    private static IResult Problem(string code, string title, int statusCode) =>
        Results.Problem(
            title: title,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}

public sealed record StagingRecoveryLifecycleLeverFireRequest(string? Reason = null);

public sealed record StagingRecoveryLifecycleLeverResponse(
    Guid InstanceId,
    Guid WorkspaceId,
    Guid OperationId,
    ElsaInstanceOperationAction Action,
    ElsaInstanceOperationState State,
    int AttemptNumber,
    int InstanceVersion,
    string Code);
