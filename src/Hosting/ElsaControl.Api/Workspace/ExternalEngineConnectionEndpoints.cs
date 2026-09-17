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

        group.MapPost("/heartbeat", async (
            Guid connectionId,
            ExternalEngineHeartbeatRequest request,
            HttpContext context,
            ExternalEngineHeartbeatService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (request.Proof is null || connectionId != request.Proof.ConnectionId)
                    return request.Proof is null
                        ? Problem("external-engine.heartbeat.invalid", "A connector proof is required.", StatusCodes.Status400BadRequest)
                        : RuntimeDenied();
                var result = await service.SubmitAsync(request, cancellationToken);
                return result switch
                {
                    null => Results.NotFound(),
                    { Accepted: true, Connection: not null } => Results.Ok(ToResponse(result.Connection)),
                    { Status: ExternalEngineHeartbeatStatus.ProofDenied } => RuntimeDenied(),
                    { Status: ExternalEngineHeartbeatStatus.Revoked } =>
                        Problem("external-engine.heartbeat.revoked", "The connector identity or connection is revoked.", StatusCodes.Status410Gone),
                    { Status: ExternalEngineHeartbeatStatus.OutOfOrder } =>
                        Problem("external-engine.heartbeat.out-of-order", "The heartbeat sequence is not newer than the accepted observation.", StatusCodes.Status409Conflict),
                    { Status: ExternalEngineHeartbeatStatus.RateLimited } =>
                        HeartbeatRateLimited(context, result.RetryAfter),
                    { Status: ExternalEngineHeartbeatStatus.Conflict } =>
                        Problem("external-engine.heartbeat.conflict", "The heartbeat raced another connection update; retry with a new proof.", StatusCodes.Status409Conflict),
                    _ => Problem("external-engine.heartbeat.invalid", "The heartbeat report is invalid or unsupported.", StatusCodes.Status400BadRequest)
                };
            }
            catch (ArgumentException exception)
            {
                return Problem("external-engine.heartbeat.invalid", exception.Message, StatusCodes.Status400BadRequest);
            }
        })
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024))
        .RequireRateLimiting("external-engine-heartbeat");

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
            value.ObservedRuntimeKind,
            value.ReleaseEvidenceLevel,
            value.ReleaseEvidenceReference,
            value.StudioDestination,
            value.Capabilities,
            value.CapabilitiesObservedAt,
            value.LastHeartbeatSequence,
            value.LastHeartbeatObservedAt,
            ExternalEngineConnectionFreshness.Classify(value, DateTimeOffset.UtcNow),
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

    private static IResult HeartbeatRateLimited(HttpContext context, TimeSpan? retryAfter)
    {
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((retryAfter ?? ExternalEngineHeartbeatService.MinimumInterval).TotalSeconds));
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Problem(
            title: "The heartbeat cadence is too frequent. Sign a fresh proof before retrying.",
            statusCode: StatusCodes.Status429TooManyRequests,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "external-engine.heartbeat.rate-limited",
                ["retryAfterSeconds"] = retryAfterSeconds
            });
    }

    private static IResult Problem(string code, string title, int status) =>
        Results.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
