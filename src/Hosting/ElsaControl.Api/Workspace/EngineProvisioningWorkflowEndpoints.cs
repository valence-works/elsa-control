using ElsaControl.Api.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Provisioning;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.RuntimeBuilder.Core.RuntimeConfigurations;
using Microsoft.AspNetCore.Mvc;

namespace ElsaControl.Api.Workspace;

public static class EngineProvisioningWorkflowEndpoints
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapEngineProvisioningWorkflowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/workspaces/{workspaceId:guid}/engine-provisioning")
            .WithTags("Workspace Engine Provisioning");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });

        group.MapGet("/targets", async (
            Guid workspaceId, IEngineProvisioningTargetStore targets, CancellationToken cancellationToken) =>
            Results.Ok(new EngineProvisioningTargetsResponse(
                await targets.GetAvailableTargetsAsync(workspaceId, cancellationToken))))
            .RequireDeploymentPermission(WorkspaceDeploymentPermissions.ManageSetup)
            .AddEndpointFilter<EngineProvisioningRequiredFilter>();

        group.MapPost("/preview", async (
            Guid workspaceId, EngineProvisioningRequest request, HttpContext context,
            IElsaInstanceCommercialGate commercialGate, EngineProvisioningPreviewService previews,
            CancellationToken cancellationToken) =>
        {
            var commercial = await commercialGate.EvaluateAsync(context.GetWorkspaceAccess().OrganizationId,
                ElsaInstanceOperationAction.Create, cancellationToken: cancellationToken);
            if (!commercial.Allowed)
                return Problem(commercial.Code, commercial.Summary, StatusCodes.Status422UnprocessableEntity);
            return Results.Ok(await previews.PreviewAsync(workspaceId, request, cancellationToken: cancellationToken));
        }).RequireDeploymentPermission(WorkspaceDeploymentPermissions.ManageSetup)
            .AddEndpointFilter<EngineProvisioningRequiredFilter>();

        group.MapPost("", async (
            Guid workspaceId, EngineProvisioningRequest request, HttpContext context,
            IElsaInstanceCommercialGate commercialGate, EngineProvisioningPreviewService previews,
            ElsaInstanceLifecycleService lifecycle, IElsaInstanceLifecycleStore lifecycleStore,
            IManagedElsaInstanceApiStore queries, WorkspacePermissionService permissions,
            [FromServices] IManagedElsaInstanceIdentityStore identities, DeploymentCockpitService cockpit,
            CancellationToken cancellationToken) =>
        {
            var access = context.GetWorkspaceAccess();
            var keys = context.Request.Headers["Idempotency-Key"];
            string key;
            try
            {
                if (keys.Count != 1) throw new ArgumentException();
                key = ElsaInstanceIdempotencyKey.Normalize(keys[0]);
            }
            catch (ArgumentException)
            { return Problem("instance.idempotency-key-invalid", "A valid Idempotency-Key is required.", StatusCodes.Status400BadRequest); }
            if (string.IsNullOrWhiteSpace(request.PreviewDigest))
                return Problem("provisioning.preview-required", "Review the deployment before provisioning.", StatusCodes.Status422UnprocessableEntity);

            var existing = await lifecycleStore.FindOperationByKeyAsync(workspaceId, key,
                action: ElsaInstanceOperationAction.Create, idempotencyScope: ElsaInstanceLifecycleService.CreateIdempotencyScope,
                cancellationToken: cancellationToken);
            var requestDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(request with { PreviewDigest = null }, RequestJsonOptions))));
            ElsaInstanceProvisioningContext provisioningContext;
            if (existing is not null)
            {
                // A retry acknowledges already accepted work. Do not resolve a mutable saved
                // configuration or re-apply a quota that the accepted instance now consumes.
                var stored = await lifecycleStore.GetProvisioningContextAsync(workspaceId, existing.InstanceId, cancellationToken);
                if (stored is null || stored.RequestDigest != requestDigest || stored.PreviewDigest != request.PreviewDigest)
                    return Problem("instance.idempotency-conflict", "This request key was already used for a different deployment.", StatusCodes.Status409Conflict);
                provisioningContext = stored;
            }
            else
            {
                var commercial = await commercialGate.EvaluateAsync(access.OrganizationId,
                    ElsaInstanceOperationAction.Create, cancellationToken: cancellationToken);
                if (!commercial.Allowed)
                    return Problem(commercial.Code, commercial.Summary, StatusCodes.Status422UnprocessableEntity);
                var preview = await previews.PreviewAsync(workspaceId, request, cancellationToken: cancellationToken);
                if (!preview.CanProvision)
                    return Results.UnprocessableEntity(preview);
                if (!string.Equals(preview.PreviewDigest, request.PreviewDigest, StringComparison.Ordinal))
                    return Problem("provisioning.preview-stale", "The configuration has changed. Review the deployment again.", StatusCodes.Status409Conflict);
                provisioningContext = new ElsaInstanceProvisioningContext(request.ApplicationId, request.EnvironmentId,
                    RuntimeConfigurationService.SerializeIntent(preview.BuilderIntent!), preview.ConfigurationDigest!,
                    request.RuntimeConfigurationId, preview.ConfigurationName, preview.PreviewDigest, requestDigest,
                    preview.ResolvedPlanDigest);
            }
            try
            {
                var accepted = await lifecycle.CreateAsync(new ElsaInstanceCreateRequest(
                    access.OrganizationId, workspaceId, request.Name!, request.Slug!, request.Intent!, key,
                    ActorAccountId: access.AccountId,
                    ProvisioningContext: provisioningContext), cancellationToken);
                var deployment = await cockpit.GetCockpitAsync(workspaceId, cancellationToken);
                var engineId = deployment.Applications.SingleOrDefault(x => x.Id == request.ApplicationId.ToString())
                    ?.Environments.SingleOrDefault(x => x.Id == request.EnvironmentId.ToString())?.EngineIds.SingleOrDefault();
                var links = new Dictionary<string, string> { ["workspace"] = "/admin/overview" };
                if (engineId is not null)
                    links["engine"] = $"/admin/deployments/applications/{request.ApplicationId:D}/environments/{request.EnvironmentId:D}/engines/{engineId}";
                return await ManagedElsaInstanceEndpoints.AcceptedAsync(workspaceId, accepted, queries, permissions,
                    identities, access.AccountId, cancellationToken, links);
            }
            catch (ElsaInstanceLifecycleConflictException exception)
            { return Problem(ManagedElsaInstanceEndpoints.ConflictCode(exception), "The engine could not be provisioned because the target or request changed.", StatusCodes.Status409Conflict); }
            catch (ArgumentException)
            { return Problem("provisioning.request.invalid", "The provisioning request is invalid.", StatusCodes.Status422UnprocessableEntity); }
        }).RequireDeploymentPermission(WorkspaceDeploymentPermissions.ManageSetup)
            .AddEndpointFilter<EngineProvisioningRequiredFilter>();

        return endpoints;
    }

    private static IResult Problem(string code, string message, int status) =>
        Results.Problem(title: message, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}

public sealed record EngineProvisioningTargetsResponse(IReadOnlyList<EngineProvisioningTarget> Targets);
