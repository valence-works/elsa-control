using System.Security.Claims;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.OrganizationBilling;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Admin.Organizations;

/// <summary>
/// Staging-only operator lever that moves a billing grace or constraint deadline
/// into the past and then runs the normal lifecycle advancer. It is never Cloud
/// BFF allowlisted and cannot write commercial or engine state itself.
/// </summary>
public static class AdminOrganizationBillingLifecycleLeverEndpoints
{
    public static IEndpointRouteBuilder MapAdminOrganizationBillingLifecycleLeverEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/admin/organizations/{organizationId:guid}/billing/lifecycle-deadline/advance",
                async (
                    Guid organizationId,
                    AdminBillingLifecycleDeadlineAdvanceRequest request,
                    HttpContext context,
                    IOptions<ControlIdentityOptions> identityOptions,
                    StagingBillingLifecycleLever lever,
                    CancellationToken cancellationToken) =>
                {
                    if (request.Deadline is null || !Enum.IsDefined(request.Deadline.Value))
                    {
                        return Results.ValidationProblem(
                            new Dictionary<string, string[]>
                            {
                                ["deadline"] = ["Deadline must be graceEndsAt or constrainedAt."]
                            },
                            extensions: new Dictionary<string, object?>
                            {
                                ["code"] = "billing.staging-lifecycle-lever.invalid"
                            });
                    }

                    var result = await lever.AdvanceAsync(
                        organizationId,
                        request.Deadline.Value,
                        OperatorSubject(context.User, identityOptions.Value),
                        cancellationToken);
                    return ToHttpResult(result);
                })
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Admin Organizations");

        return endpoints;
    }

    private static string? OperatorSubject(ClaimsPrincipal user, ControlIdentityOptions identityOptions) =>
        user.FindFirstValue(identityOptions.Claims.Subject) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    private static IResult ToHttpResult(StagingBillingLifecycleLeverResult result) => result.Outcome switch
    {
        StagingBillingLifecycleLeverOutcome.Advanced => Results.Ok(ToResponse(result)),
        StagingBillingLifecycleLeverOutcome.Disabled => Problem(
            StagingBillingLifecycleLeverDefaults.DisabledCode,
            "The staging billing lifecycle lever is disabled.",
            StatusCodes.Status403Forbidden),
        StagingBillingLifecycleLeverOutcome.OrganizationNotAllowed => Problem(
            StagingBillingLifecycleLeverDefaults.OrganizationNotAllowedCode,
            "The organization is not harness-created or staging-allowlisted.",
            StatusCodes.Status403Forbidden),
        StagingBillingLifecycleLeverOutcome.OrganizationNotFound => Problem(
            "organization.not-found",
            "Organization was not found.",
            StatusCodes.Status404NotFound),
        StagingBillingLifecycleLeverOutcome.SubscriptionNotFound => Problem(
            "billing.subscription-not-found",
            "The organization has no billing subscription.",
            StatusCodes.Status404NotFound),
        StagingBillingLifecycleLeverOutcome.DeadlineNotApplicable => Problem(
            StagingBillingLifecycleLeverDefaults.DeadlineNotApplicableCode,
            "The requested deadline cannot be moved on the current subscription state.",
            StatusCodes.Status409Conflict),
        _ => throw new InvalidOperationException("Unsupported staging lifecycle lever outcome.")
    };

    private static AdminBillingLifecycleDeadlineAdvanceResponse ToResponse(StagingBillingLifecycleLeverResult result)
    {
        var move = result.Move!;
        var advance = result.Advances?
            .LastOrDefault(x => x.OrganizationId == move.OrganizationId && x.SubscriptionId == move.SubscriptionId);
        return new AdminBillingLifecycleDeadlineAdvanceResponse(
            move.OrganizationId,
            move.SubscriptionId!.Value,
            move.Deadline!.Value,
            move.PreviousDeadlineAt,
            move.DeadlineAt,
            advance?.PreviousState ?? move.State,
            advance?.CurrentState ?? move.State,
            advance is not null,
            advance?.NoticeCreated ?? false);
    }

    private static IResult Problem(string code, string title, int statusCode) =>
        Results.Problem(
            title: title,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}

public sealed record AdminBillingLifecycleDeadlineAdvanceRequest(
    OrganizationBillingLifecycleDeadline? Deadline);

public sealed record AdminBillingLifecycleDeadlineAdvanceResponse(
    Guid OrganizationId,
    Guid SubscriptionId,
    OrganizationBillingLifecycleDeadline Deadline,
    DateTimeOffset? PreviousDeadlineAt,
    DateTimeOffset? DeadlineAt,
    OrganizationSubscriptionState? PreviousState,
    OrganizationSubscriptionState? CurrentState,
    bool Advanced,
    bool NoticeCreated);
