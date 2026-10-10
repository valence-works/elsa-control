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
            Guid? retainedAssignmentId = null;
            AzureProviderResourceAssignment? retainedAssignment = null;
            var assignmentScopeCurrent = true;
            var assignmentPlacementMatchesCurrent = true;
            var expectedOperationScope = options.ProviderScopeFingerprint;
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
                // Retained operations can have a previous runner fingerprint after a
                // template/tool rotation. Reading their status does not rebind the
                // assignment or authorize recovery; report the drift explicitly.
                assignmentScopeCurrent = string.Equals(assignment.ProviderScopeFingerprint,
                    options.ProviderScopeFingerprint, StringComparison.Ordinal);
                assignmentPlacementMatchesCurrent = assignment.NamingVersion == options.ResourceGroupNamingVersion &&
                    string.Equals(assignment.SubscriptionId, options.SubscriptionId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(assignment.ResourceGroupName,
                        AzureProviderResourceAssignmentNaming.ResourceGroupName(
                            options.ResourceGroupNamePrefix, instanceId, options.ResourceGroupNamingVersion),
                        StringComparison.Ordinal);
                expectedOperationScope = assignment.ProviderScopeFingerprint;
                if (assignment.LastOperationId is not { } providerOperationId)
                    return ProviderReadoutUnavailable("provider-readout.operation-reference-missing");
                retainedAssignmentId = assignment.Id;
                retainedAssignment = assignment;

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
                providerOperation.LifecycleAction != lifecycleOperation.Action)
                return ProviderReadoutUnavailable("provider-readout.operation-correlation-mismatch");

            var operationScopeCorrelated = string.Equals(providerOperation.ProviderScopeFingerprint,
                expectedOperationScope, StringComparison.Ordinal);
            if (!operationScopeCorrelated && retainedAssignmentId is { } boundAssignmentId &&
                !string.IsNullOrWhiteSpace(providerOperation.ProviderScopeFingerprint) &&
                !string.IsNullOrWhiteSpace(expectedOperationScope))
            {
                // A governed rebind updates the assignment but retains the original
                // operation scope. Only its append-only lineage can bridge them.
                try
                {
                    operationScopeCorrelated = await assignments.HasRebindLineageAsync(
                        workspaceId, boundAssignmentId, providerOperation.ProviderScopeFingerprint,
                        expectedOperationScope, cancellationToken);
                }
                catch (NotSupportedException)
                {
                    operationScopeCorrelated = false;
                }
            }
            if (!operationScopeCorrelated)
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
                    : [],
                assignmentScopeCurrent,
                string.Equals(providerOperation.ProviderScopeFingerprint,
                    options.ProviderScopeFingerprint, StringComparison.Ordinal),
                assignmentPlacementMatchesCurrent,
                retainedAssignment?.State,
                retainedAssignment is not null && AzureProviderDeleteRecoverySupport.IsBoundGroupOnly(
                    providerOperation, retainedAssignment),
                providerOperation.Health,
                ElsaManagedEndpointOrigin.TryCreate(providerOperation.Endpoint, out _))
            {
                ReasonCode = AzureProviderOperationValidation.IsSafeCode(providerOperation.LastObservationReasonCode)
                    ? providerOperation.LastObservationReasonCode
                    : null
            });
        });

        // Historical provider cleanup is distinct from a fresh ARM observation. This
        // route is intentionally private and only attests what the trusted cleanup
        // runner recorded when the Delete completed.
        group.MapGet("/{operationId:guid}/cleanup-receipt/{organizationId:guid}", async (
            Guid workspaceId,
            Guid instanceId,
            Guid operationId,
            Guid organizationId,
            IElsaInstanceLifecycleStore lifecycle,
            IManagedElsaInstanceApiStore queries,
            IAzureProviderOperationStore providerOperations,
            IAzureProviderResourceAssignmentStore assignments,
            CancellationToken cancellationToken) =>
        {
            var evidence = await ReadHistoricalCleanupEvidenceAsync(
                workspaceId, organizationId, instanceId, operationId,
                lifecycle, queries, providerOperations, assignments, cancellationToken);
            if (evidence is null)
                return Results.NotFound();

            return Results.Ok(new AdminManagedElsaCleanupReceiptResponse(
                InstanceTombstonePresent: true,
                LifecycleDeleteSucceeded: true,
                ProviderDeleteSucceeded: true,
                ProviderAbsenceVerifiedAtCompletion: true,
                ProviderCompletedAt: evidence.ProviderOperation.CompletedAt!.Value,
                LifecycleCompletedAt: evidence.LifecycleOperation.CompletedAt!.Value,
                TombstoneDeletedAt: evidence.Instance.DeletedAt!.Value,
                ReceiptDigest: evidence.Digest));
        });

        group.MapGet("/{operationId:guid}/cleanup-observation/{organizationId:guid}", async (
            Guid workspaceId,
            Guid instanceId,
            Guid operationId,
            Guid organizationId,
            HttpContext context,
            IServiceProvider services,
            IElsaInstanceLifecycleStore lifecycle,
            IManagedElsaInstanceApiStore queries,
            IAzureProviderOperationStore providerOperations,
            IAzureProviderResourceAssignmentStore assignments,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var observer = services.GetService<IAzureProviderFreshResourceGroupObserver>();
            if (observer is null)
                return CleanupObservationUnavailable("instance.cleanup-observation.unavailable");

            var evidence = await ReadHistoricalCleanupEvidenceAsync(
                workspaceId, organizationId, instanceId, operationId,
                lifecycle, queries, providerOperations, assignments, cancellationToken);
            if (evidence is null)
                return Results.NotFound();

            AzureProviderFreshResourceGroupObservation observation;
            try
            {
                observation = await observer.ObserveAsync(
                    evidence.ProviderOperation, operationId, evidence.Assignment, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return CleanupObservationUnavailable("instance.cleanup-observation.unavailable");
            }

            // The network read may outlive a concurrent retained-evidence change.
            // Never pair an observation with a different historical ownership tuple.
            var current = await ReadHistoricalCleanupEvidenceAsync(
                workspaceId, organizationId, instanceId, operationId,
                lifecycle, queries, providerOperations, assignments, cancellationToken);
            if (current is null || !string.Equals(current.Digest, evidence.Digest, StringComparison.Ordinal))
                return CleanupObservationUnavailable("instance.cleanup-observation.evidence-changed");

            return Results.Ok(new AdminManagedElsaFreshCleanupObservationResponse(
                observation.State,
                observation.ObservedAt,
                observation.ReasonCode,
                observation.EvidenceDigest,
                evidence.Digest));
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
                        ExpectedOperationId: operationId,
                        OperatorInitiated: true),
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

    private sealed record HistoricalCleanupEvidence(
        ElsaInstance Instance,
        ElsaInstanceOperationSummary LifecycleOperation,
        AzureProviderResourceAssignment Assignment,
        AzureProviderOperation ProviderOperation,
        string Digest);

    private static async Task<HistoricalCleanupEvidence?> ReadHistoricalCleanupEvidenceAsync(
        Guid workspaceId,
        Guid organizationId,
        Guid instanceId,
        Guid operationId,
        IElsaInstanceLifecycleStore lifecycle,
        IManagedElsaInstanceApiStore queries,
        IAzureProviderOperationStore providerOperations,
        IAzureProviderResourceAssignmentStore assignments,
        CancellationToken cancellationToken)
    {
        if (workspaceId == Guid.Empty || instanceId == Guid.Empty ||
            operationId == Guid.Empty || organizationId == Guid.Empty)
            return null;

        var instance = await lifecycle.GetInstanceAsync(workspaceId, instanceId, cancellationToken);
        var operation = await queries.GetOperationForOrganizationAsync(
            workspaceId, organizationId, instanceId, operationId, cancellationToken);
        if (instance is null || operation is null ||
            instance.OrganizationId != organizationId ||
            instance.WorkspaceId != workspaceId || instance.Id != instanceId ||
            !IsHistoricalDeleteTombstone(instance, operation, operationId))
            return null;

        // Deletion clears the placement reference: require one exact retained owner.
        var ownerAssignments = await assignments.ListForInstanceAsync(
            workspaceId, organizationId, instanceId, cancellationToken);
        if (ownerAssignments.Count != 1)
            return null;
        var assignment = ownerAssignments[0];
        if (assignment.LastOperationId is not { } providerOperationId)
            return null;
        var providerOperation = await providerOperations.GetAsync(
            workspaceId, providerOperationId, cancellationToken);
        if (!IsHistoricalCleanupReceiptEligible(
                instance, operation, operationId, assignment, providerOperation))
            return null;

        return new(instance, operation, assignment, providerOperation!,
            ComputeHistoricalCleanupReceiptDigest(
                workspaceId, organizationId, instanceId, operationId,
                operation.CompletedAt!.Value, assignment, providerOperation!));
    }

    private static bool IsHistoricalDeleteTombstone(
        ElsaInstance instance,
        ElsaInstanceOperationSummary operation,
        Guid operationId)
    {
        return operation.Id == operationId &&
               operation.InstanceId == instance.Id &&
               operation.Action == ElsaInstanceOperationAction.Delete &&
               operation.State == ElsaInstanceOperationState.Succeeded &&
               operation.CompletedAt is not null &&
               instance.Intent.DesiredLifecycle == ElsaDesiredLifecycle.Deleting &&
               instance.ObservedLifecycle == ElsaObservedLifecycle.Deleted &&
               instance.DeletedAt is not null &&
               instance.DeletedAt.Value == operation.CompletedAt.Value &&
               Guid.TryParseExact(instance.LastOperationId?.Value, "D", out var lastOperationId) &&
               lastOperationId == operationId;
    }

    private static bool IsHistoricalCleanupReceiptEligible(
        ElsaInstance instance,
        ElsaInstanceOperationSummary lifecycleOperation,
        Guid lifecycleOperationId,
        AzureProviderResourceAssignment assignment,
        AzureProviderOperation? providerOperation)
    {
        var expectedWorkloadName = AzureElsaInstanceProvider.WorkloadName(instance.Id);
        return assignment.WorkspaceId == instance.WorkspaceId &&
               assignment.OrganizationId == instance.OrganizationId &&
               assignment.InstanceId == instance.Id &&
               string.Equals(assignment.WorkloadName, expectedWorkloadName, StringComparison.OrdinalIgnoreCase) &&
               assignment.LastOperationId is { } providerOperationId &&
               providerOperation is not null &&
               providerOperation.Id == providerOperationId &&
               providerOperation.CompletedAt is not null &&
               lifecycleOperation.CompletedAt is { } lifecycleCompletedAt &&
               providerOperation.CompletedAt.Value <= lifecycleCompletedAt &&
               assignment.DeletedAt is { } assignmentDeletedAt &&
               assignmentDeletedAt <= providerOperation.CompletedAt.Value &&
               AzureProviderOperationValidation.IsLifecycleDeleteIdempotencyKey(
                   providerOperation.IdempotencyKey, lifecycleOperationId) &&
               AzureProviderDeleteRecoverySupport.IsTerminalVerifiedCleanupEligible(
                   providerOperation, assignment);
    }

    private static string ComputeHistoricalCleanupReceiptDigest(
        Guid workspaceId,
        Guid organizationId,
        Guid instanceId,
        Guid lifecycleOperationId,
        DateTimeOffset lifecycleCompletedAt,
        AzureProviderResourceAssignment assignment,
        AzureProviderOperation providerOperation)
    {
        var canonical = string.Join('\n',
        [
            "elsa-managed-delete-cleanup-receipt-v1",
            workspaceId.ToString("D"),
            organizationId.ToString("D"),
            instanceId.ToString("D"),
            lifecycleOperationId.ToString("D"),
            lifecycleCompletedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            assignment.Id.ToString("D"),
            assignment.SubscriptionId,
            assignment.ResourceGroupName,
            assignment.ProviderScopeFingerprint,
            providerOperation.Id.ToString("D"),
            providerOperation.CheckpointSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            providerOperation.CompletedAt!.Value.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            providerOperation.Phase.ToString(),
            providerOperation.Status.ToString()
        ]);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    private static IResult CleanupObservationUnavailable(string code) =>
        ManagedElsaInstanceEndpoints.Problem(
            code,
            "Fresh provider cleanup evidence is unavailable. No absence is confirmed.",
            StatusCodes.Status503ServiceUnavailable);

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
    IReadOnlyList<string> DiagnosticCodes,
    bool AssignmentScopeCurrent = true,
    bool OperationScopeCurrent = true,
    bool AssignmentPlacementMatchesCurrent = true,
    AzureProviderAssignmentState? AssignmentState = null,
    bool AssignmentGroupOnly = false,
    AzureProviderHealth Health = AzureProviderHealth.Unknown,
    bool EndpointValid = false)
{
    public string? ReasonCode { get; init; }
}

/// <summary>
/// Safe historical receipt for a completed managed-instance Delete. The provider
/// absence flag describes the runner's verified result at completion; it is not a
/// current Azure Resource Manager observation.
/// </summary>
public sealed record AdminManagedElsaCleanupReceiptResponse(
    bool InstanceTombstonePresent,
    bool LifecycleDeleteSucceeded,
    bool ProviderDeleteSucceeded,
    bool ProviderAbsenceVerifiedAtCompletion,
    DateTimeOffset ProviderCompletedAt,
    DateTimeOffset LifecycleCompletedAt,
    DateTimeOffset TombstoneDeletedAt,
    string ReceiptDigest);

/// <summary>Private fresh ARM observation bound to the exact retained cleanup receipt.</summary>
public sealed record AdminManagedElsaFreshCleanupObservationResponse(
    AzureProviderFreshResourceGroupState State,
    DateTimeOffset? ObservedAt,
    string ReasonCode,
    string? EvidenceDigest,
    string HistoricalReceiptDigest);
