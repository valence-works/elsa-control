using ElsaControl.Deployment.Abstractions.Instances;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Provider-neutral acceptance service for managed Elsa lifecycle intent. This
/// service only commits customer intent, operation identity and durable work; a
/// worker resolves the plan and invokes a provider after that transaction commits.
/// </summary>
public sealed class ElsaInstanceLifecycleService(
    IElsaInstanceLifecycleStore store,
    TimeProvider? timeProvider = null)
{
    public const string CreateIdempotencyScope = "instances";
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ElsaInstanceLifecycleAcceptance> CreateAsync(
        ElsaInstanceCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateWorkspace(request.WorkspaceId);
        ValidateOrganization(request.OrganizationId);
        ArgumentNullException.ThrowIfNull(request.Intent);
        ValidateRequired(request.Name, nameof(request.Name), "Instance name is required.");
        ValidateRequired(request.Slug, nameof(request.Slug), "Instance slug is required.");
        ValidateActor(request.ActorAccountId);
        var provisioningContext = request.ProvisioningContext?.Normalize();
        var key = RequireKey(request.IdempotencyKey);
        var name = ElsaInstanceValue.DisplayName(request.Name, nameof(request.Name));
        var slug = ElsaInstanceSlug.Normalize(request.Slug);
        var requestHash = ComputeCreateRequestHash(
            ElsaInstanceOperationAction.Create,
            expectedVersion: 1,
            [
                request.Intent.ComputeCanonicalHash(),
                request.OrganizationId.ToString("D"),
                request.WorkspaceId.ToString("D"),
                name,
                slug,
                request.InstanceId?.ToString("D") ?? "generated"
            ],
            provisioningContext?.ComputeCanonicalHash());

        var existingOperation = await store.FindOperationByKeyAsync(
            request.WorkspaceId, key, action: ElsaInstanceOperationAction.Create,
            idempotencyScope: CreateIdempotencyScope, cancellationToken: cancellationToken);
        if (existingOperation is not null)
        {
            if (existingOperation.Action != ElsaInstanceOperationAction.Create)
                throw new ElsaInstanceLifecycleConflictException("Idempotency key was already used for a different request.", ElsaInstanceLifecycleConflictReason.IdempotencyConflict);
            if (!string.Equals(existingOperation.RequestHash, requestHash, StringComparison.Ordinal))
                throw new ElsaInstanceLifecycleConflictException("Idempotency key was already used for a different request.", ElsaInstanceLifecycleConflictReason.IdempotencyConflict);
            var existing = await store.GetInstanceAsync(request.WorkspaceId, existingOperation.InstanceId, cancellationToken)
                ?? throw new ElsaInstanceLifecycleConflictException("Lifecycle operation outbox record is orphaned.", ElsaInstanceLifecycleConflictReason.InvalidState);
            if (existing.OrganizationId != request.OrganizationId)
                throw new ElsaInstanceLifecycleConflictException("Idempotency key was already used for a different request.", ElsaInstanceLifecycleConflictReason.IdempotencyConflict);
            var replayTransition = RequestTransition(
                existing,
                ElsaInstanceOperationAction.Create,
                existingOperation,
                existing.Version,
                key,
                requestHash,
                idempotencyScope: CreateIdempotencyScope);
            return await CommitAsync(existing, replayTransition,
                new ElsaInstanceAcceptanceContext(request.ActorAccountId, null,
                    ProvisioningContext: provisioningContext), cancellationToken);
        }

        var instanceId = request.InstanceId ?? Guid.NewGuid();
        if (instanceId == Guid.Empty)
            throw new ArgumentException("Instance ID cannot be empty.", nameof(request.InstanceId));
        var instance = new ElsaInstance(
            instanceId,
            request.OrganizationId,
            request.WorkspaceId,
            name,
            slug,
            request.Intent);
        var transition = RequestTransition(
            instance,
            ElsaInstanceOperationAction.Create,
            idempotencyKey: key,
            requestHash: requestHash,
            expectedVersion: instance.Version,
            idempotencyScope: CreateIdempotencyScope);
        return await CommitAsync(null, transition,
            new ElsaInstanceAcceptanceContext(request.ActorAccountId, null,
                ProvisioningContext: provisioningContext), cancellationToken);
    }

    public Task<ElsaInstanceLifecycleAcceptance> UpdateIntentAsync(
        ElsaInstanceIntentUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Intent is null && request.Name is null)
            throw new ArgumentException("An intent or name update is required.", nameof(request));
        return AcceptAsync(request.WorkspaceId, request.InstanceId, ElsaInstanceOperationAction.UpdateIntent,
            request.ExpectedVersion, request.IdempotencyKey, request.Intent, request.Name, request.Reason,
            cancellationToken, actorAccountId: request.ActorAccountId);
    }

    public Task<ElsaInstanceLifecycleAcceptance> UpdateAsync(
        ElsaInstanceIntentUpdateRequest request,
        CancellationToken cancellationToken = default) => UpdateIntentAsync(request, cancellationToken);

    public Task<ElsaInstanceLifecycleAcceptance> StartAsync(
        ElsaInstanceLifecycleRequest request,
        CancellationToken cancellationToken = default) =>
        AcceptAsync(request.WorkspaceId, request.InstanceId, ElsaInstanceOperationAction.Start,
            request.ExpectedVersion, request.IdempotencyKey, null, null, request.Reason, cancellationToken,
            actorAccountId: request.ActorAccountId);

    public Task<ElsaInstanceLifecycleAcceptance> StopAsync(
        ElsaInstanceLifecycleRequest request,
        CancellationToken cancellationToken = default) =>
        AcceptAsync(request.WorkspaceId, request.InstanceId, ElsaInstanceOperationAction.Stop,
            request.ExpectedVersion, request.IdempotencyKey, null, null, request.Reason, cancellationToken,
            actorAccountId: request.ActorAccountId);

    public Task<ElsaInstanceLifecycleAcceptance> RestartAsync(
        ElsaInstanceLifecycleRequest request,
        CancellationToken cancellationToken = default) =>
        AcceptAsync(request.WorkspaceId, request.InstanceId, ElsaInstanceOperationAction.Restart,
            request.ExpectedVersion, request.IdempotencyKey, null, null, request.Reason, cancellationToken,
            actorAccountId: request.ActorAccountId);

    public Task<ElsaInstanceLifecycleAcceptance> ReconcileAsync(
        ElsaInstanceLifecycleRequest request,
        CancellationToken cancellationToken = default) =>
        AcceptAsync(request.WorkspaceId, request.InstanceId, ElsaInstanceOperationAction.Reconcile,
            request.ExpectedVersion, request.IdempotencyKey, null, null, request.Reason, cancellationToken,
            actorAccountId: request.ActorAccountId);

    public Task<ElsaInstanceLifecycleAcceptance> RecoverAsync(
        ElsaInstanceLifecycleRequest request,
        CancellationToken cancellationToken = default) =>
        AcceptAsync(request.WorkspaceId, request.InstanceId, ElsaInstanceOperationAction.Recover,
            request.ExpectedVersion, request.IdempotencyKey, null, null, request.Reason, cancellationToken,
            actorAccountId: request.ActorAccountId);

    public Task<ElsaInstanceLifecycleAcceptance> DeleteAsync(
        ElsaInstanceLifecycleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DeleteConfirmationId is null || request.DeleteConfirmationId == Guid.Empty)
            throw new ArgumentException("Delete confirmation ID is required.", nameof(request.DeleteConfirmationId));
        if (request.ActorAccountId is null || request.ActorAccountId == Guid.Empty)
            throw new ArgumentException("Actor account ID is required for deletion.", nameof(request.ActorAccountId));
        return AcceptAsync(request.WorkspaceId, request.InstanceId, ElsaInstanceOperationAction.Delete,
            request.ExpectedVersion, request.IdempotencyKey, null, null, request.Reason, cancellationToken,
            confirmationId: request.DeleteConfirmationId, actorAccountId: request.ActorAccountId);
    }

    public Task<ElsaInstanceLifecycleAcceptance> ApproveMinorUpgradeAsync(
        ElsaInstanceIntentUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Intent);
        return AcceptAsync(request.WorkspaceId, request.InstanceId, ElsaInstanceOperationAction.ApproveMinorUpgrade,
            request.ExpectedVersion, request.IdempotencyKey, request.Intent, request.Name, request.Reason,
            cancellationToken, minorApproved: true, actorAccountId: request.ActorAccountId);
    }

    public Task<ElsaInstanceLifecycleAcceptance> MajorMigrationAsync(
        ElsaInstanceIntentUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Intent);
        return AcceptAsync(request.WorkspaceId, request.InstanceId, ElsaInstanceOperationAction.MajorMigration,
            request.ExpectedVersion, request.IdempotencyKey, request.Intent, request.Name, request.Reason,
            cancellationToken, minorApproved: true, migrationAuthorized: true, actorAccountId: request.ActorAccountId);
    }

    public Task<ElsaInstanceLifecycleAcceptance> RetryAsync(
        ElsaInstanceLifecycleRequest request,
        CancellationToken cancellationToken = default) =>
        AcceptAsync(request.WorkspaceId, request.InstanceId, ElsaInstanceOperationAction.Retry,
            request.ExpectedVersion, request.IdempotencyKey, null, null, request.Reason, cancellationToken,
            actorAccountId: request.ActorAccountId);

    private async Task<ElsaInstanceLifecycleAcceptance> AcceptAsync(
        Guid workspaceId,
        Guid instanceId,
        ElsaInstanceOperationAction action,
        int expectedVersion,
        string idempotencyKey,
        ElsaInstanceIntent? requestedIntent,
        string? requestedName,
        string? reason,
        CancellationToken cancellationToken,
        bool minorApproved = false,
        bool migrationAuthorized = false,
        Guid? confirmationId = null,
        Guid? actorAccountId = null)
    {
        ValidateWorkspace(workspaceId);
        if (instanceId == Guid.Empty)
            throw new ArgumentException("Instance ID is required.", nameof(instanceId));
        if (expectedVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion), "Expected version must be positive.");
        ValidateActor(actorAccountId);
        reason = NormalizeReason(reason);
        if (requestedName is not null)
            requestedName = ElsaInstanceValue.DisplayName(requestedName, nameof(requestedName));
        var key = RequireKey(idempotencyKey);
        var instance = await store.GetInstanceAsync(workspaceId, instanceId, cancellationToken)
            ?? throw new KeyNotFoundException("Elsa instance does not exist in the workspace.");
        var operationScope = OperationScope(instanceId, action);
        if (action == ElsaInstanceOperationAction.Recover)
        {
            var normalOperation = await store.FindOperationByKeyAsync(
                workspaceId, key, instanceId,
                action: null,
                idempotencyScope: operationScope, cancellationToken: cancellationToken);
            if (normalOperation is not null &&
                !(string.Equals(normalOperation.RecoveryIdempotencyScope, operationScope, StringComparison.Ordinal) &&
                  string.Equals(normalOperation.RecoveryIdempotencyKey, key, StringComparison.Ordinal)))
                throw new ElsaInstanceLifecycleConflictException("Idempotency key was already used for a different request.", ElsaInstanceLifecycleConflictReason.IdempotencyConflict);
        }
        var existingOperation = await store.FindOperationByKeyAsync(
            workspaceId, key, instanceId,
            action: action == ElsaInstanceOperationAction.Recover ? ElsaInstanceOperationAction.Recover : null,
            idempotencyScope: operationScope, cancellationToken: cancellationToken);
        if (existingOperation is not null)
        {
            if (existingOperation.InstanceId != instanceId ||
                (existingOperation.Action != action && action != ElsaInstanceOperationAction.Recover))
                throw new ElsaInstanceLifecycleConflictException("Idempotency key was already used for a different request.", ElsaInstanceLifecycleConflictReason.IdempotencyConflict);

            var existingRequestHash = action == ElsaInstanceOperationAction.Recover
                ? existingOperation.RecoveryRequestHash ?? existingOperation.RequestHash
                : existingOperation.RequestHash;
            var replayRequestHash = ComputeRequestHash(
                action,
                expectedVersion,
                requestedIntent?.ComputeCanonicalHash(),
                requestedName,
                reason,
                confirmationId);
            if (!string.Equals(existingRequestHash, replayRequestHash, StringComparison.Ordinal))
                throw new ElsaInstanceLifecycleConflictException("Idempotency key was already used for a different request.", ElsaInstanceLifecycleConflictReason.IdempotencyConflict);

            // Supplying the existing operation to the state machine makes an exact
            // replay independent of the caller's current If-Match value, including
            // replays after the operation has reached a terminal state.
            var replayTransition = RequestTransition(
                instance,
                action,
                existingOperation,
                existingOperation.ExpectedVersion,
                key,
                existingRequestHash,
                requestedIntent,
                minorApproved,
                migrationAuthorized,
                operationScope);
            if (requestedName is not null && !string.Equals(replayTransition.Instance.Name, requestedName, StringComparison.Ordinal))
                replayTransition = new ElsaInstanceTransitionResult(replayTransition.Instance.Rename(requestedName), replayTransition.Operation);
            return await CommitAsync(instance, replayTransition,
                AcceptanceContext(action, confirmationId, actorAccountId, reason), cancellationToken);
        }

        var requestHash = ComputeRequestHash(
            action,
            expectedVersion,
            requestedIntent?.ComputeCanonicalHash(),
            requestedName,
            reason,
            confirmationId);
        var activeOperation = await store.GetActiveOperationAsync(workspaceId, instanceId, cancellationToken);
        var effectiveIntent = EffectiveRequestedIntent(action, instance, requestedIntent);
        var transition = RequestTransition(
            instance,
            action,
            activeOperation,
            expectedVersion,
            key,
            requestHash,
            effectiveIntent,
            minorApproved,
            migrationAuthorized,
            operationScope);
        if (requestedName is not null && !string.Equals(transition.Instance.Name, requestedName, StringComparison.Ordinal))
            transition = new ElsaInstanceTransitionResult(transition.Instance.Rename(requestedName), transition.Operation);
        return await CommitAsync(instance, transition,
            AcceptanceContext(action, confirmationId, actorAccountId, reason), cancellationToken);
    }

    private static ElsaInstanceTransitionResult RequestTransition(
        ElsaInstance instance,
        ElsaInstanceOperationAction action,
        ElsaInstanceOperation? activeOperation = null,
        int? expectedVersion = null,
        string? idempotencyKey = null,
        string? requestHash = null,
        ElsaInstanceIntent? requestedIntent = null,
        bool minorApproved = false,
        bool migrationAuthorized = false,
        string? idempotencyScope = null)
    {
        try
        {
            return ElsaInstanceStateMachine.Request(instance, action, activeOperation, expectedVersion,
                idempotencyKey, requestHash, requestedIntent, minorApproved, migrationAuthorized, idempotencyScope);
        }
        catch (ElsaInstanceStateConflictException exception)
        {
            throw new ElsaInstanceLifecycleConflictException(
                "The request conflicts with the current instance state.",
                exception.Reason switch
                {
                    ElsaInstanceStateConflictReason.VersionConflict => ElsaInstanceLifecycleConflictReason.VersionConflict,
                    ElsaInstanceStateConflictReason.OperationActive => ElsaInstanceLifecycleConflictReason.OperationActive,
                    ElsaInstanceStateConflictReason.ActiveOperationOwnershipMismatch => ElsaInstanceLifecycleConflictReason.InvalidState,
                    ElsaInstanceStateConflictReason.InvalidState => ElsaInstanceLifecycleConflictReason.InvalidState,
                    _ => ElsaInstanceLifecycleConflictReason.InvalidState
                });
        }
    }

    private static ElsaInstanceIntent? EffectiveRequestedIntent(
        ElsaInstanceOperationAction action,
        ElsaInstance instance,
        ElsaInstanceIntent? requestedIntent) =>
        action is ElsaInstanceOperationAction.UpdateIntent or
            ElsaInstanceOperationAction.ApproveMinorUpgrade or
            ElsaInstanceOperationAction.MajorMigration
            ? requestedIntent ?? instance.Intent
            : requestedIntent;

    private Task<ElsaInstanceLifecycleAcceptance> CommitAsync(
        ElsaInstance? expectedInstance,
        ElsaInstanceTransitionResult transition,
        ElsaInstanceAcceptanceContext context,
        CancellationToken cancellationToken)
    {
        var outbox = new ElsaInstanceLifecycleOutboxMessage(
            Guid.NewGuid(),
            transition.Instance.WorkspaceId,
            transition.Instance.Id,
            transition.Operation.Id,
            transition.Operation.Action,
            transition.Operation.RequestHash,
            _timeProvider.GetUtcNow());
        return store.CommitAcceptedWithContextAsync(
            expectedInstance, transition.Instance, transition.Operation, outbox, context, cancellationToken);
    }

    private static ElsaInstanceAcceptanceContext AcceptanceContext(
        ElsaInstanceOperationAction action,
        Guid? confirmationId,
        Guid? actorAccountId,
        string? reason) =>
        new(actorAccountId, reason,
            action == ElsaInstanceOperationAction.Delete && confirmationId is not null && actorAccountId is not null
                ? new ElsaInstanceDeleteConfirmationRequirement(confirmationId.Value, actorAccountId.Value)
                : null);

    private static void ValidateActor(Guid? actorAccountId)
    {
        if (actorAccountId == Guid.Empty)
            throw new ArgumentException("Actor account ID cannot be empty.", nameof(actorAccountId));
    }

    private static string? NormalizeReason(string? reason)
    {
        if (reason is null)
            return null;
        var normalized = reason.Trim();
        if (normalized.Length == 0)
            return null;
        if (normalized.Length > 128 || normalized.Any(char.IsControl))
            throw new ArgumentException("Reason must be a safe summary of at most 128 characters.", nameof(reason));
        return normalized;
    }

    private static string RequireKey(string value) => ElsaInstanceIdempotencyKey.Normalize(value);

    private static string OperationScope(Guid instanceId, ElsaInstanceOperationAction action) =>
        action == ElsaInstanceOperationAction.UpdateIntent
            ? $"instance/{instanceId:D}/{action}"
            : $"instance/{instanceId:D}/operations";

    private static void ValidateRequired(string? value, string parameterName, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(message, parameterName);
    }

    private static string ComputeRequestHash(
        ElsaInstanceOperationAction action,
        int expectedVersion,
        string? intentHash,
        string? requestedName = null,
        string? reason = null,
        Guid? confirmationId = null)
    {
        var canonical = new StringBuilder()
            .Append(action).Append('\n')
            .Append(expectedVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
        AppendOptional(canonical, intentHash);
        AppendOptional(canonical, requestedName);
        AppendOptional(canonical, reason);
        if (confirmationId is not null)
        {
            if (confirmationId == Guid.Empty)
                throw new ArgumentException("Confirmation ID cannot be empty.", nameof(confirmationId));
            AppendOptional(canonical, confirmationId.Value.ToString("D"));
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendOptional(StringBuilder canonical, string? value)
    {
        if (value is null)
        {
            canonical.Append("null\n");
            return;
        }
        canonical.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');
    }

    private static string ComputeCreateRequestHash(
        ElsaInstanceOperationAction action,
        int expectedVersion,
        IReadOnlyList<string> values,
        string? provisioningContextHash)
    {
        var canonical = new StringBuilder()
            .Append(action).Append('\n')
            .Append(expectedVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var value in values)
            canonical.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');
        if (provisioningContextHash is not null)
            AppendOptional(canonical, provisioningContextHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void ValidateWorkspace(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
    }

    private static void ValidateOrganization(Guid organizationId)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("Organization ID is required.", nameof(organizationId));
    }
}
