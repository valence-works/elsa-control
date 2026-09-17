using ElsaControl.Api.Authentication;
using ElsaControl.Deployment.Core.ExternalConnections;
using ElsaControl.Deployment.Core.Workspace;

namespace ElsaControl.Api.Workspace;

public static class ExternalEngineConnectionEndpoints
{
    public static IEndpointRouteBuilder MapExternalEngineConnectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapCustomerEndpoints(endpoints);
        MapRuntimeEndpoints(endpoints);
        return endpoints;
    }

    private static void MapCustomerEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/workspaces/{workspaceId:guid}/external-engine-connections")
            .WithTags("External Engine Connections")
            .MapCommonApiExceptions();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });

        group.MapGet("", async (
            Guid workspaceId,
            HttpContext context,
            ExternalEngineConnectionService service,
            CancellationToken cancellationToken) =>
        {
            var access = context.GetWorkspaceAccess();
            var items = await service.ListAsync(access.OrganizationId, workspaceId, cancellationToken);
            return Results.Ok(new ExternalEngineConnectionListResponse(items.Select(ToResponse).ToList()));
        }).RequireDeploymentPermission(WorkspaceDeploymentPermissions.Read).AllowCloudBff();

        group.MapGet("/{connectionId:guid}", async (
            Guid workspaceId,
            Guid connectionId,
            HttpContext context,
            ExternalEngineConnectionService service,
            CancellationToken cancellationToken) =>
        {
            var access = context.GetWorkspaceAccess();
            var connection = await service.FindAsync(access.OrganizationId, workspaceId, connectionId, cancellationToken);
            return connection is null ? Results.NotFound() : Results.Ok(ToResponse(connection));
        }).RequireDeploymentPermission(WorkspaceDeploymentPermissions.Read).AllowCloudBff();

        group.MapPost("", async (
            Guid workspaceId,
            CreateExternalEngineConnectionRequest request,
            HttpContext context,
            ExternalEngineConnectionService service,
            CancellationToken cancellationToken) =>
        {
            var keys = context.Request.Headers["Idempotency-Key"];
            if (keys.Count != 1)
                return Problem("external-engine.idempotency-key-required", "A single Idempotency-Key is required.", StatusCodes.Status400BadRequest);
            try
            {
                var access = context.GetWorkspaceAccess();
                var attempt = await service.CreatePairingAsync(
                    new ExternalEngineConnectionCreateRequest(access.OrganizationId, workspaceId, request.DisplayName, keys[0]!),
                    cancellationToken);
                var response = ToPairingResponse(attempt);
                return attempt.ReplayedConnection
                    ? Results.Ok(response)
                    : Results.Created($"/api/workspaces/{workspaceId:D}/external-engine-connections/{attempt.Connection.Id:D}", response);
            }
            catch (ArgumentException exception)
            {
                return Problem("external-engine.request.invalid", exception.Message, StatusCodes.Status400BadRequest);
            }
            catch (ExternalEngineConnectionConflictException)
            {
                return Problem("external-engine.idempotency-conflict", "The request key was already used for another connection request.", StatusCodes.Status409Conflict);
            }
        }).RequireDeploymentPermission(WorkspaceDeploymentPermissions.ManageSetup).AllowCloudBff();

        group.MapGet("/{connectionId:guid}/pairing", async (
            Guid workspaceId,
            Guid connectionId,
            HttpContext context,
            ExternalEngineConnectionService service,
            CancellationToken cancellationToken) =>
        {
            var access = context.GetWorkspaceAccess();
            var progress = await service.GetPairingProgressAsync(
                access.OrganizationId, workspaceId, connectionId, cancellationToken);
            return progress is null ? Results.NotFound() : Results.Ok(ToResponse(progress));
        }).RequireDeploymentPermission(WorkspaceDeploymentPermissions.Read).AllowCloudBff();

        group.MapPost("/{connectionId:guid}/repair", async (
            Guid workspaceId,
            Guid connectionId,
            HttpContext context,
            ExternalEngineConnectionService service,
            CancellationToken cancellationToken) =>
        {
            var access = context.GetWorkspaceAccess();
            var attempt = await service.RepairAsync(access.OrganizationId, workspaceId, connectionId, cancellationToken);
            return attempt is null ? Results.NotFound() : Results.Ok(ToPairingResponse(attempt));
        }).RequireDeploymentPermission(WorkspaceDeploymentPermissions.ManageSetup).AllowCloudBff();

        group.MapPost("/{connectionId:guid}/disconnect", async (
            Guid workspaceId,
            Guid connectionId,
            HttpContext context,
            ExternalEngineConnectionService service,
            CancellationToken cancellationToken) =>
        {
            var access = context.GetWorkspaceAccess();
            var connection = await service.DisconnectAsync(access.OrganizationId, workspaceId, connectionId, cancellationToken);
            return connection is null ? Results.NotFound() : Results.Ok(ToResponse(connection));
        }).RequireDeploymentPermission(WorkspaceDeploymentPermissions.ManageSetup).AllowCloudBff();
    }

    private static void MapRuntimeEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/runtime/external-engine-connections/{connectionId:guid}")
            .WithTags("External Engine Runtime")
            .MapCommonApiExceptions();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });

        group.MapPost("/enrollment/redeem", async (
            Guid connectionId,
            ExternalEngineEnrollmentRedeemRequest request,
            ExternalEngineConnectionService service,
            CancellationToken cancellationToken) =>
        {
            if (connectionId != request.ConnectionId)
                return RuntimeDenied();
            var result = await service.RedeemAsync(request, cancellationToken);
            return result switch
            {
                null => Results.NotFound(),
                { Succeeded: true } => Results.Ok(ToResponse(result.Identity!)),
                _ => RuntimeDenied()
            };
        });

        group.MapPost("/authenticate", async (
            Guid connectionId,
            ExternalEngineConnectorProof proof,
            ExternalEngineConnectionService service,
            CancellationToken cancellationToken) =>
        {
            if (connectionId != proof.ConnectionId)
                return RuntimeDenied();
            var result = await service.AuthenticateAsync(proof, cancellationToken);
            return result switch
            {
                null => Results.NotFound(),
                { Succeeded: true } => Results.Ok(ToResponse(result.Identity!)),
                _ => RuntimeDenied()
            };
        });

        group.MapPost("/identity/rotate", async (
            Guid connectionId,
            ExternalEngineConnectorKeyRotationRequest request,
            ExternalEngineConnectionService service,
            CancellationToken cancellationToken) =>
        {
            if (connectionId != request.Proof.ConnectionId)
                return RuntimeDenied();
            var result = await service.RotateAsync(request, cancellationToken);
            return result switch
            {
                null => Results.NotFound(),
                { Succeeded: true } => Results.Ok(ToResponse(result.Identity!)),
                _ => RuntimeDenied()
            };
        });

        group.MapPost("/identity/revoke", async (
            Guid connectionId,
            ExternalEngineConnectorProof proof,
            ExternalEngineConnectionService service,
            CancellationToken cancellationToken) =>
        {
            if (connectionId != proof.ConnectionId)
                return RuntimeDenied();
            var result = await service.RevokeAsync(proof, cancellationToken);
            return result switch
            {
                null => Results.NotFound(),
                { Succeeded: true } => Results.Ok(ToResponse(result.Identity!)),
                _ => RuntimeDenied()
            };
        });
    }

    private static ExternalEngineConnectionResponse ToResponse(ExternalEngineConnection value) =>
        new(
            value.Id,
            value.WorkspaceId,
            value.DisplayName,
            ExternalEngineConnection.OwnershipMode,
            value.Status,
            value.RuntimeHealth,
            value.ConnectorReachability,
            value.LastAuthenticatedAt,
            value.ConnectorProtocol,
            value.ConnectorVersion,
            value.ObservedDistribution,
            value.ObservedVersion,
            value.ReleaseEvidenceLevel,
            value.StudioDestination,
            value.Capabilities,
            value.CapabilitiesObservedAt,
            value.CreatedAt,
            value.UpdatedAt,
            value.RevokedAt);

    private static ExternalEnginePairingAttemptResponse ToPairingResponse(ExternalEnginePairingAttempt value) =>
        new(
            ToResponse(value.Connection),
            new ExternalEngineEnrollmentResponse(
                value.Enrollment.ChallengeId,
                value.Enrollment.ConnectionId,
                value.Enrollment.Purpose,
                value.Enrollment.Audience,
                value.Enrollment.Challenge,
                value.Enrollment.IssuedAt,
                value.Enrollment.ExpiresAt),
            value.ReplayedConnection);

    private static ExternalEnginePairingProgressResponse ToResponse(ExternalEnginePairingProgress value) =>
        new(value.ConnectionId, value.ChallengeId, value.State, value.IssuedAt, value.ExpiresAt, value.RedeemedAt);

    private static ExternalEngineConnectorIdentityResponse ToResponse(ExternalEngineConnectorIdentity value) =>
        new(value.ConnectionId, value.Id, value.KeyVersion, value.EnrolledAt, value.RotatedAt, value.RevokedAt);

    private static IResult RuntimeDenied() =>
        Problem("external-engine.runtime.denied", "The connector proof was not accepted.", StatusCodes.Status401Unauthorized);

    private static IResult Problem(string code, string title, int status) =>
        Results.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
