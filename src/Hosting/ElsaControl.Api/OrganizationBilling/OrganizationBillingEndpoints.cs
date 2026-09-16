using ElsaControl.Api.Authentication;
using ElsaControl.Billing.Stripe;
using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.OrganizationBilling;

public static class OrganizationBillingEndpoints
{
    private const int MaxWebhookBodyBytes = 512 * 1024;

    public static IEndpointRouteBuilder MapOrganizationBillingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/billing")
            .WithTags("Organization Billing");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });

        group.MapGet("/", async (
            Guid organizationId,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            OrganizationBillingApiService billing,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();

            var result = await billing.GetStatusAsync(identity, organizationId, cancellationToken);
            if (result.Succeeded)
                return Results.Ok(result.Status);

            return result.Failure is OrganizationWorkspaceFailure.OrganizationNotAllowed
                ? Results.NotFound(new { code = "organization.not-found" })
                : Results.Forbid();
        });

        group.MapPost("/checkout", async (
            Guid organizationId,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            OrganizationBillingApiService billing,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();

            var result = await billing.CreateCheckoutAsync(identity, organizationId, cancellationToken);
            return ToHttpResult(result);
        });

        group.MapPost("/prepare-hosted-trial", async (
            Guid organizationId,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            OrganizationBillingApiService billing,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();

            var result = await billing.PrepareHostedTrialAsync(identity, organizationId, cancellationToken);
            return ToHostedTrialPreparationHttpResult(result);
        }).AllowCloudBff();

        group.MapPost("/portal", async (
            Guid organizationId,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            OrganizationBillingApiService billing,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();

            var result = await billing.CreatePortalAsync(identity, organizationId, cancellationToken);
            return ToHttpResult(result);
        });

        group.MapPost("/delete", async (
            Guid organizationId,
            HttpContext context,
            IWorkspaceIdentityReader identityReader,
            OrganizationBillingApiService billing,
            CancellationToken cancellationToken) =>
        {
            var identity = await identityReader.ReadAsync(context);
            if (identity is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();

            var result = await billing.RequestDeletionAsync(identity, organizationId, cancellationToken);
            return ToDeletionHttpResult(result);
        });

        endpoints.MapPost("/api/billing/webhooks/stripe", HandleStripeWebhookAsync)
            .WithTags("Billing Webhooks");

        return endpoints;
    }

    private static async Task<IResult> HandleStripeWebhookAsync(
        HttpContext context,
        IBillingProvider provider,
        OrganizationBillingService billing,
        CancellationToken cancellationToken)
    {
        // The route is Stripe-specific. Fail closed if dependency injection is
        // ever changed to resolve another provider implementation here.
        if (!string.Equals(provider.Provider, BillingProviderNames.Stripe, StringComparison.Ordinal))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        if (context.Request.ContentLength is > MaxWebhookBodyBytes)
            return Results.BadRequest(new { code = "webhook.invalid" });

        var rawBody = await ReadBodyAsync(context.Request.Body, cancellationToken);
        if (rawBody is null)
            return Results.BadRequest(new { code = "webhook.invalid" });

        var signature = context.Request.Headers["Stripe-Signature"].FirstOrDefault();
        BillingWebhookNormalizationResult normalized;
        try
        {
            normalized = provider.VerifyAndNormalizeWebhook(rawBody.Value, signature ?? "", DateTimeOffset.UtcNow);
        }
        catch (BillingProviderUnavailableException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception)
        {
            return Results.BadRequest(new { code = "webhook.invalid" });
        }
        if (normalized.Status == BillingWebhookNormalizationStatus.Invalid || normalized.Event is null)
            return Results.BadRequest(new { code = normalized.FailureCode ?? "webhook.invalid" });

        BillingEventConsumptionResult result;
        try
        {
            result = normalized.Status == BillingWebhookNormalizationStatus.Unknown
                ? await billing.RecordUnknownAsync(normalized.Event, cancellationToken)
                : await billing.ConsumeAsync(normalized.Event, cancellationToken);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { code = "webhook.invalid" });
        }
        catch (BillingProviderEventConflictException)
        {
            return Results.Conflict(new { code = "webhook.conflict" });
        }
        return Results.Ok(new { status = ToWireStatus(result.Outcome) });
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            total += read;
            if (total > MaxWebhookBodyBytes)
                return null;
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return buffer.ToArray();
    }

    private static IResult ToHttpResult(OrganizationBillingApiResult result)
    {
        if (result.Succeeded)
            return Results.Ok(new OrganizationBillingSessionResponse(result.Session!.Url));
        if (result.Failure is OrganizationWorkspaceFailure.OrganizationNotAllowed)
            return Results.NotFound(new { code = "organization.not-found" });
        if (result.Failure is OrganizationWorkspaceFailure.OrganizationRoleNotAllowed)
            return Results.Forbid();
        if (result.CustomerNotReady)
            return Results.Conflict(new { code = "billing.customer-not-ready" });
        if (result.SubscriptionTerminal)
            return Results.Conflict(new { code = "billing.subscription-terminal" });
        if (result.ProviderUnavailable)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        return Results.BadRequest(new { code = "billing.invalid" });
    }

    private static IResult ToHostedTrialPreparationHttpResult(HostedTrialPreparationApiResult result)
    {
        if (result.Succeeded)
            return Results.Ok(new HostedTrialPreparationResponse(result.TrialEndsAt!.Value));
        if (result.Failure is OrganizationWorkspaceFailure.OrganizationNotAllowed)
            return Results.NotFound(new { code = "organization.not-found" });
        if (result.Failure is OrganizationWorkspaceFailure.OrganizationRoleNotAllowed)
            return Results.Forbid();
        if (result.SubscriptionTerminal)
            return Results.Conflict(new { code = "billing.subscription-terminal" });
        if (result.ProviderUnavailable)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        return Results.BadRequest(new { code = "billing.invalid" });
    }

    private static string ToWireStatus(BillingEventConsumptionOutcome outcome) => outcome switch
    {
        BillingEventConsumptionOutcome.Applied => "applied",
        BillingEventConsumptionOutcome.Replayed => "replayed",
        BillingEventConsumptionOutcome.IgnoredOutOfOrder => "ignored-out-of-order",
        BillingEventConsumptionOutcome.Rejected => "rejected",
        BillingEventConsumptionOutcome.RecordedUnknown => "recorded-unknown",
        _ => "unknown"
    };

    private static IResult ToDeletionHttpResult(OrganizationBillingDeletionApiResult result)
    {
        if (result.Succeeded)
            return Results.Accepted(value: new OrganizationBillingDeletionResponse(
                result.Advance!.CurrentState.ToString(),
                result.Advance.CleanupQueued));
        if (result.Failure is OrganizationWorkspaceFailure.OrganizationNotAllowed || result.OrganizationUnavailable)
            return Results.NotFound(new { code = "organization.not-found" });
        if (result.Failure is OrganizationWorkspaceFailure.OrganizationRoleNotAllowed)
            return Results.Forbid();
        return Results.BadRequest(new { code = "billing.deletion.invalid" });
    }
}

public sealed record OrganizationBillingSessionResponse(string Url);

public sealed record OrganizationBillingStatusResponse(
    Guid OrganizationId,
    OrganizationBillingSubscriptionResponse? Subscription,
    OrganizationBillingEntitlementResponse? Entitlements,
    OrganizationBillingCapacityResponse Capacity,
    IReadOnlyList<string> Capabilities)
{
    public static OrganizationBillingStatusResponse From(
        Guid organizationId,
        OrganizationSubscription? subscription,
        OrganizationEntitlementSnapshot? entitlement,
        int activeWorkspaces,
        int activeManagedInstances) =>
        new(
            organizationId,
            subscription is null ? null : new OrganizationBillingSubscriptionResponse(
                subscription.State.ToString(),
                subscription.TrialStartedAt,
                subscription.TrialEndsAt,
                subscription.ActivatedAt,
                subscription.PastDueAt,
                subscription.ConstrainedAt,
                subscription.SuspendedAt,
                subscription.RetainedAt,
                subscription.DeletedAt,
                subscription.UpdatedAt),
            entitlement is null ? null : new OrganizationBillingEntitlementResponse(
                entitlement.CanCreateCustomSources,
                entitlement.MaxSources,
                entitlement.MaxWorkspaces,
                entitlement.MaxInstances,
                entitlement.MaxPackagesIndexed,
                entitlement.MaxVersionsPerPackage,
                entitlement.MaxSyncsPerDay,
                entitlement.PrivateFeedsEnabled,
                entitlement.ManagedHostingEnabled,
                entitlement.DeploymentTargetsEnabled,
                entitlement.SyncedAt),
            new OrganizationBillingCapacityResponse(
                activeManagedInstances,
                entitlement?.MaxInstances,
                activeWorkspaces,
                entitlement?.MaxWorkspaces),
            CapabilitiesFor(entitlement));

    private static IReadOnlyList<string> CapabilitiesFor(OrganizationEntitlementSnapshot? entitlement)
    {
        if (entitlement is null)
            return [];

        var capabilities = new List<string>(4);
        if (entitlement.ManagedHostingEnabled)
            capabilities.Add("managed-hosting");
        if (entitlement.DeploymentTargetsEnabled)
            capabilities.Add("deployment-targets");
        if (entitlement.CanCreateCustomSources)
            capabilities.Add("custom-sources");
        if (entitlement.PrivateFeedsEnabled)
            capabilities.Add("private-feeds");
        return capabilities;
    }
}

public sealed record OrganizationBillingSubscriptionResponse(
    string State,
    DateTimeOffset TrialStartedAt,
    DateTimeOffset TrialEndsAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? PastDueAt,
    DateTimeOffset? ConstrainedAt,
    DateTimeOffset? SuspendedAt,
    DateTimeOffset? RetainedAt,
    DateTimeOffset? DeletedAt,
    DateTimeOffset UpdatedAt);

public sealed record OrganizationBillingEntitlementResponse(
    bool CanCreateCustomSources,
    int MaxSources,
    int MaxWorkspaces,
    int MaxInstances,
    int? MaxPackagesIndexed,
    int? MaxVersionsPerPackage,
    int? MaxSyncsPerDay,
    bool PrivateFeedsEnabled,
    bool ManagedHostingEnabled,
    bool DeploymentTargetsEnabled,
    DateTimeOffset SyncedAt);

public sealed record OrganizationBillingCapacityResponse(
    int ManagedInstancesUsed,
    int? ManagedInstancesLimit,
    int WorkspacesUsed,
    int? WorkspacesLimit);

public sealed record OrganizationBillingDeletionResponse(string State, bool CleanupQueued);
