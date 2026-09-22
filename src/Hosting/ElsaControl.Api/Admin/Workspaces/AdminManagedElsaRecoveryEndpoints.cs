using ElsaControl.Api.Authentication;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Api.Admin.Workspaces;

/// <summary>
/// Narrow operator escape hatch for a single durable managed-instance operation.
/// The lifecycle service remains the only mutation boundary; this adapter only
/// supplies operator authentication and binds recovery to an explicit operation.
/// </summary>
public static class AdminManagedElsaRecoveryEndpoints
{
    public static IEndpointRouteBuilder MapAdminManagedElsaRecoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/workspaces/{workspaceId:guid}/instances/{instanceId:guid}/operations")
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Admin Managed Elsa");

        group.MapGet("/current", async (
            Guid workspaceId,
            Guid instanceId,
            HttpContext context,
            IElsaInstanceLifecycleStore lifecycle,
            IManagedElsaInstanceApiStore queries,
            CancellationToken cancellationToken) =>
        {
            var instance = await lifecycle.GetInstanceAsync(workspaceId, instanceId, cancellationToken);
            if (instance is null)
                return Results.NotFound();

            var operation = await lifecycle.GetActiveOperationAsync(workspaceId, instanceId, cancellationToken);
            if (operation is null)
                return Results.NoContent();

            var summary = await queries.GetOperationAsync(workspaceId, instanceId, operation.Id, cancellationToken);
            if (summary is null)
                return Results.NotFound();

            // These stores intentionally expose separate read models. Revalidate
            // the mutation precondition and operation identity before returning a
            // pair that an operator can safely use for recovery.
            var currentInstance = await lifecycle.GetInstanceAsync(workspaceId, instanceId, cancellationToken);
            var currentOperation = await lifecycle.GetActiveOperationAsync(workspaceId, instanceId, cancellationToken);
            if (currentInstance is null || currentOperation is null ||
                currentInstance.Version != instance.Version ||
                currentOperation.Id != operation.Id ||
                currentOperation.State != summary.State ||
                currentOperation.AttemptNumber != summary.AttemptNumber)
                return ManagedElsaInstanceEndpoints.Problem(
                    "instance.operation-changed",
                    "The active operation changed while it was being read. Retry discovery.",
                    StatusCodes.Status409Conflict);

            context.Response.Headers.ETag = $"\"{currentInstance.Version}\"";
            return Results.Ok(ManagedElsaInstanceEndpoints.ToOperationResponse(workspaceId, instanceId, summary));
        });

        group.MapGet("/topology", async (
            Guid workspaceId,
            Guid instanceId,
            HttpContext context,
            IManagedElsaInstanceApiStore queries,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var topology = await queries.GetLifecycleTopologyAsync(workspaceId, instanceId, cancellationToken);
                if (topology is null)
                    return Results.NotFound();

                context.Response.Headers.ETag = $"\"{topology.InstanceVersion}\"";
                return Results.Ok(topology);
            }
            catch (ElsaInstanceLifecycleTopologyChangedException)
            {
                return ManagedElsaInstanceEndpoints.Problem(
                    "instance.topology-changed",
                    "The lifecycle topology changed while it was being read. Retry discovery.",
                    StatusCodes.Status409Conflict);
            }
        });

        group.MapGet("/{operationId:guid}", async (
            Guid workspaceId,
            Guid instanceId,
            Guid operationId,
            HttpContext context,
            IElsaInstanceLifecycleStore lifecycle,
            IManagedElsaInstanceApiStore queries,
            CancellationToken cancellationToken) =>
        {
            var operation = await queries.GetOperationAsync(workspaceId, instanceId, operationId, cancellationToken);
            var instance = await lifecycle.GetInstanceAsync(workspaceId, instanceId, cancellationToken);
            if (operation is null || instance is null)
                return Results.NotFound();

            context.Response.Headers.ETag = $"\"{instance.Version}\"";
            return Results.Ok(ManagedElsaInstanceEndpoints.ToOperationResponse(workspaceId, instanceId, operation));
        });

        group.MapPost("/{operationId:guid}/recover", async (
            Guid workspaceId,
            Guid instanceId,
            Guid operationId,
            AdminManagedElsaRecoveryRequest request,
            HttpContext context,
            ElsaInstanceLifecycleService lifecycle,
            CancellationToken cancellationToken) =>
        {
            var expectedVersion = ManagedElsaInstanceEndpoints.ReadIfMatch(context.Request);
            if (expectedVersion is null)
                return ManagedElsaInstanceEndpoints.Problem("instance.if-match-required", "A strong If-Match header is required for recovery.", StatusCodes.Status428PreconditionRequired);

            var keyResult = ManagedElsaInstanceEndpoints.ReadIdempotencyKey(context);
            if (keyResult.State == IdempotencyKeyState.Missing)
                return ManagedElsaInstanceEndpoints.Problem("instance.idempotency-key-required", "Idempotency-Key is required for recovery.", StatusCodes.Status400BadRequest);
            if (keyResult.State == IdempotencyKeyState.Invalid)
                return ManagedElsaInstanceEndpoints.Problem("instance.idempotency-key-invalid", "Idempotency-Key must be a safe token of at most 128 characters.", StatusCodes.Status400BadRequest);

            try
            {
                var accepted = await lifecycle.RecoverAsync(
                    new ElsaInstanceLifecycleRequest(
                        workspaceId,
                        instanceId,
                        expectedVersion.Value,
                        keyResult.Value!,
                        request.Reason,
                        ActorAccountId: null,
                        ExpectedOperationId: operationId),
                    cancellationToken);
                var operationUrl = $"/api/admin/workspaces/{workspaceId:D}/instances/{instanceId:D}/operations/{accepted.Operation.Id:D}";
                context.Response.Headers.ETag = $"\"{accepted.Instance.Version}\"";
                return Results.Accepted(operationUrl, new AdminManagedElsaRecoveryResponse(
                    accepted.Operation.Id,
                    accepted.Operation.State,
                    accepted.Operation.AttemptNumber,
                    accepted.Instance.Version,
                    accepted.Replayed,
                    operationUrl));
            }
            catch (ElsaInstanceLifecycleConflictException exception)
            {
                return ManagedElsaInstanceEndpoints.Problem(
                    ManagedElsaInstanceEndpoints.ConflictCode(exception),
                    exception.Reason == ElsaInstanceLifecycleConflictReason.RecoveryAuthorityUnavailable
                        ? "Azure delete recovery authority is unavailable because provider correlation is missing and assignment inventory does not prove the workload is absent."
                        : "The recovery request conflicts with the current instance state.",
                    ManagedElsaInstanceEndpoints.ConflictStatusCode(exception));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ArgumentException)
            {
                return ManagedElsaInstanceEndpoints.Problem("instance.recovery-invalid", "The recovery request is invalid.", StatusCodes.Status422UnprocessableEntity);
            }
        });

        return endpoints;
    }

}

public sealed record AdminManagedElsaRecoveryRequest(string? Reason = null);

public sealed record AdminManagedElsaRecoveryResponse(
    Guid OperationId,
    ElsaInstanceOperationState State,
    int AttemptNumber,
    int InstanceVersion,
    bool Replayed,
    string OperationUrl);
