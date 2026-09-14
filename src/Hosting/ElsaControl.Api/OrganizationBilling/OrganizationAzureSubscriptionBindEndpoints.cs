using ElsaControl.Api.Authentication;
using ElsaControl.Deployment.Abstractions.Azure;
using ElsaControl.Deployment.Azure;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.OrganizationBilling;

public static class OrganizationAzureSubscriptionBindEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationAzureSubscriptionBindEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/azure-subscription-bind")
            .WithTags("Organization Azure Subscription Bind");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });

        group.MapGet("", async (
            Guid organizationId,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            AccountWorkspaceService accounts,
            OrganizationAzureSubscriptionBindService binds,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();
            var access = await accounts.GetOrganizationAccessAsync(identity, organizationId, OrganizationOperation.ViewOrganization, cancellationToken);
            if (!access.Succeeded)
                return ToAccessResult(access.Failure!.Value);
            var bind = await binds.GetLatestAsync(organizationId, cancellationToken);
            return Results.Ok(new AzureSubscriptionBindingView(
                bind is null ? null : OrganizationAzureSubscriptionBindResponse.From(bind),
                AzureLighthouseOfferResponse.Default,
                AzureBindingReadinessResponse.NotEvaluated));
        });

        group.MapPost("", async (
            Guid organizationId,
            CreateAzureSubscriptionBindRequest request,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            AccountWorkspaceService accounts,
            OrganizationAzureSubscriptionBindService binds,
            IOptions<AzureProviderRunnerOptions> runnerOptions,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();
            var access = await accounts.GetOrganizationAccessAsync(identity, organizationId, OrganizationOperation.ManageBilling, cancellationToken);
            if (!access.Succeeded)
                return ToAccessResult(access.Failure!.Value);
            if (!request.ConsentConfirmed)
                return Results.BadRequest(new { code = "azure-bind.consent-required" });
            if (!IsNonEmptyGuid(request.SubscriptionId) || !IsNonEmptyGuid(request.CustomerTenantId))
                return Results.BadRequest(new { code = "azure-bind.invalid-request" });
            var bindRequest = BuildServerBindRequest(request, runnerOptions.Value);
            if (bindRequest is null)
                return Results.Json(new { code = "azure-bind.authority-unconfigured" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            var result = await binds.CreateAsync(organizationId, bindRequest, access.AccountId, cancellationToken);
            return ToMutationResult(result, organizationId);
        });

        group.MapPost("/relink", async (
            Guid organizationId,
            CreateAzureSubscriptionBindRequest request,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            AccountWorkspaceService accounts,
            OrganizationAzureSubscriptionBindService binds,
            IOptions<AzureProviderRunnerOptions> runnerOptions,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();
            var access = await accounts.GetOrganizationAccessAsync(identity, organizationId, OrganizationOperation.ManageBilling, cancellationToken);
            if (!access.Succeeded)
                return ToAccessResult(access.Failure!.Value);
            if (!request.ConsentConfirmed)
                return Results.BadRequest(new { code = "azure-bind.consent-required" });
            if (!IsNonEmptyGuid(request.SubscriptionId) || !IsNonEmptyGuid(request.CustomerTenantId))
                return Results.BadRequest(new { code = "azure-bind.invalid-request" });
            var bindRequest = BuildServerBindRequest(request, runnerOptions.Value);
            if (bindRequest is null)
                return Results.Json(new { code = "azure-bind.authority-unconfigured" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            var result = await binds.RelinkAsync(organizationId, bindRequest, access.AccountId, cancellationToken);
            return ToMutationResult(result, organizationId);
        });

        group.MapPost("/verify", async (
            Guid organizationId,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            AccountWorkspaceService accounts,
            OrganizationAzureSubscriptionBindService binds,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();
            var access = await accounts.GetOrganizationAccessAsync(identity, organizationId, OrganizationOperation.ManageBilling, cancellationToken);
            if (!access.Succeeded)
                return ToAccessResult(access.Failure!.Value);
            var current = await binds.GetLatestAsync(organizationId, cancellationToken);
            if (current is null)
                return Results.NotFound(new { code = "azure-bind.not-found" });
            var result = await binds.VerifyAsync(organizationId, current.Id, cancellationToken);
            return ToMutationResult(result, organizationId);
        });

        group.MapPost("/{bindId:guid}/verify", async (
            Guid organizationId,
            Guid bindId,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            AccountWorkspaceService accounts,
            OrganizationAzureSubscriptionBindService binds,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();
            var access = await accounts.GetOrganizationAccessAsync(identity, organizationId, OrganizationOperation.ManageBilling, cancellationToken);
            if (!access.Succeeded)
                return ToAccessResult(access.Failure!.Value);
            var result = await binds.VerifyAsync(organizationId, bindId, cancellationToken);
            return ToMutationResult(result, organizationId);
        });

        group.MapPost("/{bindId:guid}/unbind", async (
            Guid organizationId,
            Guid bindId,
            OrganizationAzureSubscriptionBindUnbindRequest? request,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            AccountWorkspaceService accounts,
            OrganizationAzureSubscriptionBindService binds,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();
            var access = await accounts.GetOrganizationAccessAsync(identity, organizationId, OrganizationOperation.ManageBilling, cancellationToken);
            if (!access.Succeeded)
                return ToAccessResult(access.Failure!.Value);
            var result = await binds.UnbindAsync(organizationId, bindId, request?.Reason, cancellationToken);
            return ToMutationResult(result, organizationId);
        });

        return endpoints;
    }

    private static OrganizationAzureSubscriptionBindRequest? BuildServerBindRequest(
        CreateAzureSubscriptionBindRequest request,
        AzureProviderRunnerOptions options)
    {
        if (!Guid.TryParseExact(request.SubscriptionId, "D", out var subscriptionId) ||
            !Guid.TryParseExact(request.CustomerTenantId, "D", out var customerTenantId) ||
            string.IsNullOrWhiteSpace(options.LighthouseManagingTenantId) ||
            !Guid.TryParseExact(options.LighthouseManagingTenantId, "D", out var managingTenantId) ||
            !Guid.TryParseExact(options.SqlBootstrapObjectId, "D", out var managingPrincipalObjectId) ||
            !Guid.TryParseExact(options.AzureCliClientId, "D", out var managingPrincipalClientId))
            return null;

        return new(
            customerTenantId.ToString("D"),
            subscriptionId.ToString("D"),
            managingTenantId.ToString("D"),
            managingPrincipalObjectId.ToString("D"),
            managingPrincipalClientId.ToString("D"),
            AzureLighthouseOfferIdentity.RegistrationDefinitionId(subscriptionId.ToString("D")));
    }

    private static bool IsNonEmptyGuid(string? value) =>
        Guid.TryParseExact(value, "D", out var guid) && guid != Guid.Empty;

    private static IResult ToMutationResult(OrganizationAzureSubscriptionBindResult result, Guid organizationId)
    {
        if (result.Succeeded)
        {
            var response = new AzureSubscriptionBindingView(
                OrganizationAzureSubscriptionBindResponse.From(result.Bind!),
                AzureLighthouseOfferResponse.Default,
                AzureBindingReadinessResponse.NotEvaluated);
            return result.Bind!.State == OrganizationAzureSubscriptionBindState.PendingConsent
                ? Results.Created($"/api/organizations/{organizationId}/azure-subscription-bind", response)
                : Results.Ok(response);
        }

        return result.Failure switch
        {
            OrganizationAzureSubscriptionBindFailure.OrganizationNotFound or OrganizationAzureSubscriptionBindFailure.BindNotFound => Results.NotFound(new { code = "azure-bind.not-found" }),
            OrganizationAzureSubscriptionBindFailure.AlreadyBound => Results.Conflict(new { code = "azure-bind.already-bound" }),
            OrganizationAzureSubscriptionBindFailure.BindInFlight => Results.Conflict(new { code = "azure-bind.in-flight" }),
            OrganizationAzureSubscriptionBindFailure.DegradedBindRequiresUnbind => Results.Conflict(new { code = "azure-bind.unbind-required" }),
            OrganizationAzureSubscriptionBindFailure.SubscriptionMustChange => Results.Conflict(new { code = "azure-bind.subscription-must-change" }),
            OrganizationAzureSubscriptionBindFailure.InvalidState => Results.Conflict(new { code = "azure-bind.invalid-state" }),
            OrganizationAzureSubscriptionBindFailure.InvalidRequest => Results.BadRequest(new { code = "azure-bind.invalid-request" }),
            _ => Results.BadRequest(new { code = "azure-bind.invalid" })
        };
    }

    private static IResult ToAccessResult(OrganizationWorkspaceFailure failure) =>
        failure switch
        {
            OrganizationWorkspaceFailure.OrganizationNotAllowed => Results.NotFound(new { code = "organization.not-found" }),
            OrganizationWorkspaceFailure.OrganizationRoleNotAllowed => Results.Forbid(),
            _ => Results.Forbid()
        };

    public sealed record OrganizationAzureSubscriptionBindResponse(
        Guid Id,
        Guid OrganizationId,
        string CustomerTenantId,
        string SubscriptionId,
        string ManagingTenantId,
        string ManagingPrincipalObjectId,
        string ManagingPrincipalClientId,
        string RegistrationDefinitionId,
        string? RegistrationDefinitionFingerprint,
        OrganizationAzureSubscriptionBindState State,
        DateTimeOffset? VerifiedAt,
        string? LastPreflightCode,
        Guid? CreatedByAccountId,
        string? UnbindReason,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt)
    {
        public IReadOnlyList<string> ManagingPrincipalIds => [ManagingPrincipalObjectId];

        public static OrganizationAzureSubscriptionBindResponse From(OrganizationAzureSubscriptionBind bind) => new(
            bind.Id,
            bind.OrganizationId,
            bind.CustomerTenantId,
            bind.SubscriptionId,
            bind.ManagingTenantId,
            bind.ManagingPrincipalObjectId,
            bind.ManagingPrincipalClientId,
            bind.RegistrationDefinitionId,
            bind.RegistrationDefinitionFingerprint,
            bind.State,
            bind.VerifiedAt,
            bind.LastPreflightCode,
            bind.CreatedByAccountId,
            bind.UnbindReason,
            bind.CreatedAt,
            bind.UpdatedAt);
    }

    public sealed record CreateAzureSubscriptionBindRequest(
        string SubscriptionId,
        string CustomerTenantId,
        bool ConsentConfirmed);

    public sealed record AzureSubscriptionBindingView(
        OrganizationAzureSubscriptionBindResponse? Bind,
        AzureLighthouseOfferResponse Offer,
        AzureBindingReadinessResponse Readiness);

    public sealed record AzureLighthouseOfferResponse(string Version, string ArtifactUrl, string ArtifactLabel)
    {
        public static AzureLighthouseOfferResponse Default { get; } = new(
            AzureLighthouseOfferIdentity.Version,
            AzureLighthouseOfferIdentity.ArtifactUrl,
            AzureLighthouseOfferIdentity.ArtifactLabel);
    }

    public sealed record AzureBindingReadinessResponse(string Entitlement, string Targeting)
    {
        public static AzureBindingReadinessResponse NotEvaluated { get; } = new(
            "Not evaluated",
            "Not available in this onboarding step");
    }
}
