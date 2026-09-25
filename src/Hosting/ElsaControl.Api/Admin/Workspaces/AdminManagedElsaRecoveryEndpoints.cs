using ElsaControl.Api.Authentication;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Azure;

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

        // A provider command may time out after Azure has accepted it. Expose only
        // value-free durable state to operators before they consider an explicit
        // recovery; never return the provider row's resources, endpoint or plan.
        group.MapGet("/provider-current", async (
            Guid workspaceId,
            Guid instanceId,
            IElsaInstanceLifecycleStore lifecycle,
            IAzureProviderOperationStore providerOperations,
            IAzureProviderResourceAssignmentStore assignments,
            IServiceProvider services,
            CancellationToken cancellationToken) =>
        {
            var instance = await lifecycle.GetInstanceAsync(workspaceId, instanceId, cancellationToken);
            if (instance is null)
                return Results.NotFound();

            var lifecycleOperation = await lifecycle.GetActiveOperationAsync(workspaceId, instanceId, cancellationToken);
            if (lifecycleOperation is null)
                return Results.NoContent();

            var options = services.GetService<AzureElsaInstanceProviderOptions>();
            if (options is null || !options.Enabled)
                return ProviderReadoutUnavailable("provider-readout.disabled");

            AzureProviderOperation? providerOperation;
            if (lifecycleOperation.Action == ElsaInstanceOperationAction.Delete)
            {
                // Delete is a distinct provider action. Follow the durable placement's
                // last operation, the same authority used by lifecycle recovery, rather
                // than querying the latest Reconcile (which cannot return a Delete).
                if (!Guid.TryParseExact(instance.PlacementAssignmentReference?.AssignmentId, "D", out var assignmentId))
                    return ProviderReadoutUnavailable("provider-readout.assignment-reference-missing");
                var assignment = await assignments.GetAsync(workspaceId, assignmentId, cancellationToken);
                if (assignment is null)
                    return ProviderReadoutUnavailable("provider-readout.assignment-missing");
                if (assignment.WorkspaceId != workspaceId)
                    return ProviderReadoutUnavailable("provider-readout.assignment-workspace-mismatch");
                if (assignment.OrganizationId != instance.OrganizationId)
                    return ProviderReadoutUnavailable("provider-readout.assignment-organization-mismatch");
                if (assignment.InstanceId != instanceId)
                    return ProviderReadoutUnavailable("provider-readout.assignment-instance-mismatch");
                if (!string.Equals(assignment.WorkloadName,
                        AzureElsaInstanceProvider.WorkloadName(instanceId), StringComparison.OrdinalIgnoreCase))
                    return ProviderReadoutUnavailable("provider-readout.assignment-workload-mismatch");
                if (!string.Equals(assignment.ProviderScopeFingerprint,
                        options.ProviderScopeFingerprint, StringComparison.Ordinal))
                    return ProviderReadoutUnavailable("provider-readout.assignment-scope-mismatch");
                if (assignment.LastOperationId is not { } providerOperationId)
                    return ProviderReadoutUnavailable("provider-readout.operation-reference-missing");

                providerOperation = await providerOperations.GetAsync(workspaceId, providerOperationId, cancellationToken);
                if (providerOperation is null)
                    return ProviderReadoutUnavailable("provider-readout.operation-missing");
                if (providerOperation.Action != AzureProviderOperationAction.Delete ||
                    providerOperation.ProviderAssignmentId != assignment.Id ||
                    !AzureProviderOperationValidation.IsLifecycleDeleteIdempotencyKey(
                        providerOperation.IdempotencyKey, lifecycleOperation.Id))
                    return ProviderReadoutUnavailable("provider-readout.delete-correlation-mismatch");
            }
            else
            {
                providerOperation = await providerOperations.GetLatestReconcileAsync(
                    workspaceId,
                    AzureElsaInstanceProvider.WorkloadName(instanceId),
                    options.ProviderScopeFingerprint,
                    cancellationToken);
                if (providerOperation is null ||
                    providerOperation.Action != AzureProviderOperationAction.Reconcile ||
                    !string.Equals(providerOperation.IdempotencyKey,
                        AzureProviderOperationValidation.LifecycleIdempotencyKey(lifecycleOperation.Id),
                        StringComparison.Ordinal))
                    return Results.NotFound();
            }
            if (providerOperation is null || providerOperation.WorkspaceId != workspaceId ||
                (lifecycleOperation.Action == ElsaInstanceOperationAction.Delete &&
                 providerOperation.OrganizationId != instance.OrganizationId) ||
                providerOperation.InstanceId != instanceId ||
                !string.Equals(providerOperation.TargetKey,
                    AzureElsaInstanceProvider.WorkloadName(instanceId), StringComparison.OrdinalIgnoreCase) ||
                providerOperation.LifecycleAction != lifecycleOperation.Action ||
                !string.Equals(providerOperation.ProviderScopeFingerprint,
                    options.ProviderScopeFingerprint, StringComparison.Ordinal))
                return ProviderReadoutUnavailable("provider-readout.operation-correlation-mismatch");

            var transitions = await providerOperations.ListTransitionsAsync(
                workspaceId, providerOperation.Id, cancellationToken);
            var latestTransition = transitions.MaxBy(transition => transition.Sequence);
            return Results.Ok(new AdminManagedElsaProviderOperationResponse(
                providerOperation.Status,
                providerOperation.Phase,
                providerOperation.AttemptedStep,
                providerOperation.AttemptNumber,
                providerOperation.CheckpointSequence,
                providerOperation.UpdatedAt,
                AzureProviderOperationValidation.IsSafeCode(latestTransition?.Code)
                    ? latestTransition!.Code
                    : null,
                AzureProviderOperationValidation.IsSafeDiagnostics(providerOperation.Diagnostics)
                    ? providerOperation.Diagnostics.Select(diagnostic => diagnostic.Code).ToArray()
                    : []));
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

    private static IResult ProviderReadoutUnavailable(string code) =>
        ManagedElsaInstanceEndpoints.Problem(
            code,
            "The provider operation cannot be correlated with the current managed instance.",
            StatusCodes.Status404NotFound);
}

public sealed record AdminManagedElsaRecoveryRequest(string? Reason = null);

public sealed record AdminManagedElsaRecoveryResponse(
    Guid OperationId,
    ElsaInstanceOperationState State,
    int AttemptNumber,
    int InstanceVersion,
    bool Replayed,
    string OperationUrl);

public sealed record AdminManagedElsaProviderOperationResponse(
    AzureProviderOperationStatus Status,
    AzureProviderOperationPhase Phase,
    AzureProviderRunnerStep? AttemptedStep,
    int AttemptNumber,
    long CheckpointSequence,
    DateTimeOffset UpdatedAt,
    string? LastTransitionCode,
    IReadOnlyList<string> DiagnosticCodes);
