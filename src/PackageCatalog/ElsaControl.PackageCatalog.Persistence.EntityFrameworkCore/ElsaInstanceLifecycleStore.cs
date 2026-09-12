using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

/// <summary>
/// Relational implementation of the lifecycle acceptance boundary. The aggregate,
/// immutable intent revision, operation and outbox are committed in one transaction;
/// all ownership, version and reservation checks are repeated while that transaction
/// is open so the preflight reads in the application service are not trusted as a
/// concurrency boundary.
/// </summary>
public sealed class EfCoreElsaInstanceLifecycleStore(
    CatalogDbContext dbContext,
    IElsaInstanceLifecycleResolutionInputSource resolutionInputSource,
    TimeProvider? timeProvider = null,
    IElsaInstanceCommercialGate? commercialGate = null,
    IAzureProviderRecoveryObservationStore? recoveryObservationStore = null) :
    IElsaInstanceLifecycleStore,
    IElsaInstanceLifecycleWorkerStore,
    IElsaInstanceProviderSubmissionStore,
    IElsaInstanceProviderPendingOperationStore,
    IElsaInstanceProviderReconciliationStore,
    IElsaInstanceDeletionStore,
    IElsaInstanceEntitlementHoldStore
{
    private readonly IElsaInstanceLifecycleResolutionInputSource _resolutionInputSource =
        resolutionInputSource ?? throw new ArgumentNullException(nameof(resolutionInputSource));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IElsaInstanceCommercialGate _commercialGate = commercialGate ?? new EfCoreElsaInstanceCommercialGate(dbContext, timeProvider);
    private readonly IAzureProviderRecoveryObservationStore? _recoveryObservationStore = recoveryObservationStore;
    private static readonly TimeSpan WorkerLeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DeletionDeferralDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan[] IdempotencyReplayLookupDelays =
        [TimeSpan.Zero, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(75)];
    private static readonly JsonDocumentOptions SafeJsonOptions = new()
    {
        MaxDepth = 16,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    private IElsaInstanceLifecycleResolutionInputSource ResolutionInputSource => _resolutionInputSource;

    public async Task<ElsaInstanceProviderReconciliationTarget?> GetTargetAsync(
        Guid workspaceId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        var operation = await dbContext.ElsaInstanceOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == operationId, cancellationToken);
        if (operation is null || operation.InstanceId is null ||
            operation.State != ElsaInstanceOperationState.RecoveryRequired || operation.DeploymentRunId is null)
            return null;
        var instance = await dbContext.ElsaInstances
            .AsNoTracking()
            .Include(x => x.IdentityBinding)
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == operation.InstanceId, cancellationToken);
        var runIsUncertain = await dbContext.DeploymentRuns.AsNoTracking().AnyAsync(x =>
            x.Id == operation.DeploymentRunId && x.WorkspaceId == workspaceId &&
            x.ElsaInstanceId == operation.InstanceId && x.Status == WorkspaceDeploymentRunStatus.RecoveryRequired,
            cancellationToken);
        return instance is null || !runIsUncertain
            ? null
            : new ElsaInstanceProviderReconciliationTarget(MapInstance(instance), MapOperation(operation), operation.ReconciliationVersion);
    }

    public async Task<ElsaInstanceProviderReconciliationResult?> GetResultAsync(
        Guid workspaceId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        var operation = await dbContext.ElsaInstanceOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == operationId, cancellationToken);
        if (operation?.InstanceId is null || operation.State is not (ElsaInstanceOperationState.Succeeded or ElsaInstanceOperationState.Failed) ||
            operation.ReconciliationEvidenceFingerprint is null)
            return null;
        return ReconciliationResult(operation, replayed: false);
    }

    public async Task<ElsaInstanceProviderReconciliationResult> CommitAsync(
        ElsaInstanceProviderReconciliationCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        commit.Validate();
        dbContext.ChangeTracker.Clear();
        try
        {
            return await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
            {
                var current = await dbContext.ElsaInstanceOperations.AsNoTracking()
                    .Where(x => x.WorkspaceId == commit.WorkspaceId && x.Id == commit.OperationId)
                    .Select(x => new { x.State, x.ReconciliationEvidenceFingerprint })
                    .SingleOrDefaultAsync(cancellationToken);
                if (current?.State is ElsaInstanceOperationState.Succeeded or ElsaInstanceOperationState.Failed)
                {
                    if (!string.Equals(current.ReconciliationEvidenceFingerprint, commit.EvidenceFingerprint, StringComparison.Ordinal))
                        throw Conflict("Provider reconciliation evidence conflicts with the recorded result.");
                    return await GetResultAsync(commit.WorkspaceId, commit.OperationId, cancellationToken)
                        is { } result
                        ? result with { Replayed = true }
                        : throw Conflict("Provider reconciliation result is incomplete.");
                }
                if (current?.State == ElsaInstanceOperationState.RecoveryRequired &&
                    string.Equals(current.ReconciliationEvidenceFingerprint, commit.EvidenceFingerprint, StringComparison.Ordinal))
                {
                    var replayOperation = await dbContext.ElsaInstanceOperations.AsNoTracking()
                        .SingleAsync(x => x.WorkspaceId == commit.WorkspaceId && x.Id == commit.OperationId,
                            cancellationToken);
                    return ReconciliationResult(replayOperation, replayed: true);
                }

                var claimed = await dbContext.ElsaInstanceOperations
                    .Where(x => x.WorkspaceId == commit.WorkspaceId && x.Id == commit.OperationId &&
                                x.InstanceId == commit.InstanceId && x.State == ElsaInstanceOperationState.RecoveryRequired &&
                                x.AttemptNumber == commit.ExpectedAttemptNumber &&
                                x.ReconciliationVersion == commit.ExpectedReconciliationVersion)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.ReconciliationVersion, checked(commit.ExpectedReconciliationVersion + 1)), cancellationToken);
                if (claimed != 1)
                    throw Conflict("Provider reconciliation target changed concurrently.");

                dbContext.ChangeTracker.Clear();
                var operation = await dbContext.ElsaInstanceOperations.SingleAsync(x => x.Id == commit.OperationId, cancellationToken);
                var instance = await LoadTrackedInstanceAsync(commit.InstanceId, cancellationToken)
                    ?? throw Conflict("Provider reconciliation instance no longer exists.");
                var run = operation.DeploymentRunId is null ? null : await dbContext.DeploymentRuns
                    .Include(x => x.Environment)
                    .SingleOrDefaultAsync(x => x.Id == operation.DeploymentRunId && x.WorkspaceId == commit.WorkspaceId,
                        cancellationToken);
                if (instance.Version != commit.ExpectedInstanceVersion || run is null ||
                    run.ElsaInstanceId != commit.InstanceId || run.Status != WorkspaceDeploymentRunStatus.RecoveryRequired)
                    throw Conflict("Provider reconciliation target is inconsistent.");

                var priorObservedLifecycle = instance.ObservedLifecycle;
                ApplyAggregate(instance, commit.Instance);
                if (commit.Operation.State == ElsaInstanceOperationState.Succeeded &&
                    commit.Instance.ObservedLifecycle == ElsaObservedLifecycle.Ready &&
                    commit.Instance.Health == ElsaInstanceHealth.Healthy)
                    SynchronizeIdentityBinding(instance, commit.ReconciledAt);
                instance.UpdatedAt = commit.ReconciledAt.ToUniversalTime();
                operation.State = commit.Operation.State;
                var preserveUncertainSubmission = commit.Operation.State == ElsaInstanceOperationState.RecoveryRequired &&
                    !commit.RetrySafe &&
                    (string.Equals(operation.FailureCode, "provider.submission.uncertain", StringComparison.Ordinal) ||
                     string.Equals(run.RecoveryReason, "provider.submission.uncertain", StringComparison.Ordinal));
                operation.FailureCode = preserveUncertainSubmission
                    ? "provider.submission.uncertain"
                    : commit.Operation.State == ElsaInstanceOperationState.RecoveryRequired && commit.RetrySafe
                        ? ElsaInstanceProviderReconciliationService.RetrySafeCode
                        : commit.Operation.State == ElsaInstanceOperationState.Failed ? commit.DiagnosticCode : null;
                // Persistence derives the safe summary from FailureCode; do not assign
                // human-readable text here that the validation boundary will discard.
                operation.FailureSummary = null;
                operation.CompletedAt = commit.Operation.State == ElsaInstanceOperationState.RecoveryRequired
                    ? null
                    : commit.ReconciledAt.ToUniversalTime();
                operation.WorkerId = null;
                operation.LeaseTokenHash = null;
                operation.LeaseExpiresAt = null;
                operation.HeartbeatAt = null;
                operation.UpdatedAt = commit.ReconciledAt.ToUniversalTime();
                operation.ReconciliationEvidenceFingerprint = commit.EvidenceFingerprint;
                operation.ReconciliationDiagnosticCode = commit.DiagnosticCode;
                operation.ReconciliationRetryEvidenceReference = commit.RetryEvidenceReference ??
                    operation.ReconciliationRetryEvidenceReference;
                operation.ReconciliationRetryEvidenceDigest = commit.RetryEvidenceDigest ??
                    operation.ReconciliationRetryEvidenceDigest;
                operation.ReconciledObservedLifecycle = commit.Instance.ObservedLifecycle;
                operation.ReconciledHealth = commit.Instance.Health;
                operation.ReconciledInstanceVersion = checked(instance.Version + 1);
                operation.ReconciledAt = commit.ReconciledAt.ToUniversalTime();

                run.Status = commit.Operation.State switch
                {
                    ElsaInstanceOperationState.Succeeded => WorkspaceDeploymentRunStatus.Succeeded,
                    ElsaInstanceOperationState.Failed => WorkspaceDeploymentRunStatus.Failed,
                    _ => WorkspaceDeploymentRunStatus.RecoveryRequired
                };
                run.CompletedAt = run.Status == WorkspaceDeploymentRunStatus.RecoveryRequired
                    ? null
                    : commit.ReconciledAt.ToUniversalTime();
                run.RecoveryReason = run.Status == WorkspaceDeploymentRunStatus.RecoveryRequired
                    ? preserveUncertainSubmission ? "provider.submission.uncertain" : commit.DiagnosticCode
                    : null;
                run.FailureMessage = run.Status == WorkspaceDeploymentRunStatus.Failed
                    ? "Provider reconciliation established a terminal failure."
                    : null;
                run.WorkerId = null;
                run.WorkerHeartbeatAt = null;
                if (run.Environment is not null)
                {
                    run.Environment.UpdatedAt = commit.ReconciledAt.ToUniversalTime();
                    run.Environment.DeploymentStatus = run.Status == WorkspaceDeploymentRunStatus.Succeeded
                        ? DeploymentStatus.Succeeded
                        : DeploymentStatus.Blocked;
                    if (run.Status == WorkspaceDeploymentRunStatus.Succeeded)
                        run.Environment.DeployedRevisionId = run.SourceRevisionId;
                }

                await dbContext.DeploymentRunHistoryEvents.AddAsync(new()
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = commit.WorkspaceId,
                    RunId = run.Id,
                    Status = run.Status,
                    Message = "Provider state reconciliation recorded a deterministic lifecycle outcome.",
                    CreatedAt = commit.ReconciledAt.ToUniversalTime()
                }, cancellationToken);
                await dbContext.ElsaInstanceAuditEvents.AddAsync(await CreateAuditEventAsync(
                    instance, operation, priorObservedLifecycle, commit.ReconciledAt, cancellationToken,
                    eventType: "lifecycle.reconciled", deploymentRunId: run.Id,
                    diagnosticCode: commit.DiagnosticCode,
                    summary: "Provider state reconciliation recorded a deterministic lifecycle outcome."), cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                return ReconciliationResult(operation, replayed: false);
            }, cancellationToken);
        }
        catch (ElsaInstanceLifecycleConflictException)
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException or DbUpdateException or DbException)
        {
            dbContext.ChangeTracker.Clear();
            throw Conflict("Provider reconciliation conflicted with a newer observation.");
        }
    }

    public async Task<ElsaInstance?> GetInstanceAsync(
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty || instanceId == Guid.Empty)
            return null;

        var entity = await dbContext.ElsaInstances
            .AsNoTracking()
            .Include(x => x.IdentityBinding)
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == instanceId, cancellationToken);
        return entity is null ? null : MapInstance(entity);
    }

    public async Task<ElsaInstanceOperation?> GetActiveOperationAsync(
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty || instanceId == Guid.Empty)
            return null;

        var entity = await dbContext.ElsaInstanceOperations
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.InstanceId == instanceId)
            .Where(x => x.State == ElsaInstanceOperationState.Accepted ||
                        x.State == ElsaInstanceOperationState.WaitingForPriorOperation ||
                        x.State == ElsaInstanceOperationState.Queued ||
                        x.State == ElsaInstanceOperationState.EntitlementHeld ||
                        x.State == ElsaInstanceOperationState.Running ||
                        x.State == ElsaInstanceOperationState.RecoveryRequired)
            .OrderByDescending(x => x.State != ElsaInstanceOperationState.WaitingForPriorOperation)
            .ThenByDescending(x => x.AcceptedAt)
            .ThenByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : MapOperation(entity);
    }

    public async Task<ElsaInstanceOperation?> FindOperationByKeyAsync(
        Guid workspaceId,
        string idempotencyKey,
        Guid? instanceId = null,
        ElsaInstanceOperationAction? action = null,
        string? idempotencyScope = null,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty)
            return null;
        idempotencyKey = RequireIdempotencyKey(idempotencyKey);

        var isRecovery = action == ElsaInstanceOperationAction.Recover;
        if (isRecovery)
        {
            var recoveryQuery = dbContext.ElsaInstanceRecoveryRequests
                .AsNoTracking()
                .Where(x => x.WorkspaceId == workspaceId && x.IdempotencyKey == idempotencyKey);
            if (instanceId is not null)
                recoveryQuery = recoveryQuery.Where(x => x.InstanceId == instanceId);
            if (idempotencyScope is not null)
                recoveryQuery = recoveryQuery.Where(x => x.IdempotencyScope == idempotencyScope);
            var recovery = await recoveryQuery
                .OrderByDescending(x => x.AcceptedAt)
                .ThenByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (recovery is null)
                return null;
            var recoveryOperation = await dbContext.ElsaInstanceOperations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == recovery.OperationId, cancellationToken);
            return recoveryOperation is null ? null : MapOperation(recoveryOperation, recovery);
        }
        var query = dbContext.ElsaInstanceOperations
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.IdempotencyKey == idempotencyKey);
        if (instanceId is not null)
            query = query.Where(x => x.InstanceId == instanceId);
        if (action is not null)
            query = query.Where(x => x.Action == action);
        if (idempotencyScope is not null)
            query = query.Where(x => x.IdempotencyScope == idempotencyScope);
        var entity = await query
            .OrderByDescending(x => x.AcceptedAt)
            .ThenByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var normalLookupRecoveryQuery = dbContext.ElsaInstanceRecoveryRequests
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.IdempotencyKey == idempotencyKey);
        if (instanceId is not null)
            normalLookupRecoveryQuery = normalLookupRecoveryQuery.Where(x => x.InstanceId == instanceId);
        if (idempotencyScope is not null)
            normalLookupRecoveryQuery = normalLookupRecoveryQuery.Where(x => x.IdempotencyScope == idempotencyScope);
        var normalLookupRecovery = await normalLookupRecoveryQuery
            .OrderByDescending(x => x.AcceptedAt)
            .ThenByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (normalLookupRecovery is not null)
        {
            var recoveryOperation = await dbContext.ElsaInstanceOperations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == normalLookupRecovery.OperationId, cancellationToken);
            if (recoveryOperation is not null)
                return MapOperation(recoveryOperation, normalLookupRecovery);
        }
        return entity is null ? null : MapOperation(entity);
    }

    public Task<ElsaInstanceLifecycleAcceptance> CommitAcceptedAsync(
        ElsaInstance? expectedInstance,
        ElsaInstance instance,
        ElsaInstanceOperation operation,
        ElsaInstanceLifecycleOutboxMessage outbox,
        CancellationToken cancellationToken = default) =>
        CommitAcceptedCoreAsync(expectedInstance, instance, operation, outbox, null, cancellationToken);

    public Task<ElsaInstanceLifecycleAcceptance> CommitAcceptedWithContextAsync(
        ElsaInstance? expectedInstance,
        ElsaInstance instance,
        ElsaInstanceOperation operation,
        ElsaInstanceLifecycleOutboxMessage outbox,
        ElsaInstanceAcceptanceContext context,
        CancellationToken cancellationToken = default) =>
        CommitAcceptedCoreAsync(expectedInstance, instance, operation, outbox, context, cancellationToken);

    private async Task<ElsaInstanceLifecycleAcceptance> CommitAcceptedCoreAsync(
        ElsaInstance? expectedInstance,
        ElsaInstance instance,
        ElsaInstanceOperation operation,
        ElsaInstanceLifecycleOutboxMessage outbox,
        ElsaInstanceAcceptanceContext? context,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(outbox);
        ValidateEnvelope(instance, operation, outbox);
        try
        {
            return await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
            {
                // Check the operation ID first.     This handles an exact retry and also
                // prevents a caller from reusing an operation ID for another envelope.
                var existingOperation = await dbContext.ElsaInstanceOperations
                    .SingleOrDefaultAsync(x => x.Id == operation.Id, cancellationToken);
                if (existingOperation is not null)
                {
                    var existingInstance = await LoadTrackedInstanceAsync(existingOperation.InstanceId, cancellationToken);
                    var existingOutbox = await dbContext.ElsaInstanceLifecycleOutbox
                        .SingleOrDefaultAsync(x => x.OperationId == existingOperation.Id, cancellationToken);
                    ValidateExistingOperation(existingOperation, instance, operation, outbox);
                    if (operation.Action == ElsaInstanceOperationAction.Delete && context?.DeleteConfirmation is null &&
                        (operation.RecoveryIdempotencyKey is null || operation.RecoveryIdempotencyScope is null ||
                         operation.RecoveryRequestHash is null))
                        throw new ElsaInstanceDeleteConfirmationException();
                    if (operation.RecoveryIdempotencyKey is null && existingOperation.RecoveryIdempotencyKey is not null)
                        throw Conflict("Idempotency key was already used for a different request.",
                            ElsaInstanceLifecycleConflictReason.IdempotencyConflict);
                    return await CompleteExistingOperationAsync(
                        expectedInstance,
                        instance,
                        operation,
                        existingOperation,
                        existingInstance,
                        existingOutbox,
                        outbox.CreatedAt,
                        cancellationToken);
                }

                // A retained operation has already crossed the confirmation boundary;
                // recovery resumes that exact operation rather than approving a new Delete.
                if (operation.Action == ElsaInstanceOperationAction.Delete && context?.DeleteConfirmation is null)
                    throw new ElsaInstanceDeleteConfirmationException();

                // The service intentionally looks up by key before creating an ID, but
                // two first requests can race between that read and this transaction.
                // Treat the workspace/key row as the idempotency authority even though
                // the operation route scope is also persisted for downstream consumers.
                var existingRecoveryKey = await dbContext.ElsaInstanceRecoveryRequests
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        x => x.WorkspaceId == instance.WorkspaceId &&
                             x.IdempotencyScope == operation.IdempotencyScope &&
                             x.IdempotencyKey == operation.IdempotencyKey,
                        cancellationToken);
                if (existingRecoveryKey is not null)
                    throw Conflict("Idempotency key was already used for a different request.",
                        ElsaInstanceLifecycleConflictReason.IdempotencyConflict);

                var existingKeyOperation = await dbContext.ElsaInstanceOperations
                    .OrderByDescending(x => x.AcceptedAt)
                    .ThenByDescending(x => x.CreatedAt)
                    .FirstOrDefaultAsync(
                        x => x.WorkspaceId == instance.WorkspaceId &&
                             x.IdempotencyScope == operation.IdempotencyScope &&
                             x.IdempotencyKey == operation.IdempotencyKey,
                        cancellationToken);
                if (existingKeyOperation is not null)
                {
                    if (existingKeyOperation.RecoveryIdempotencyKey is not null &&
                        operation.RecoveryIdempotencyKey is null)
                        throw Conflict("Idempotency key was already used for a different request.",
                            ElsaInstanceLifecycleConflictReason.IdempotencyConflict);
                    if (existingKeyOperation.Action != operation.Action ||
                        !string.Equals(existingKeyOperation.RequestHash, operation.RequestHash, StringComparison.Ordinal))
                        throw Conflict("Idempotency key was already used for a different request.", ElsaInstanceLifecycleConflictReason.IdempotencyConflict);

                    var keyInstance = await LoadTrackedInstanceAsync(existingKeyOperation.InstanceId, cancellationToken);
                    var keyOutbox = await dbContext.ElsaInstanceLifecycleOutbox
                        .SingleOrDefaultAsync(x => x.OperationId == existingKeyOperation.Id, cancellationToken);
                    return Replay(keyInstance, existingKeyOperation, keyOutbox);
                }

                await ValidateAndStageDeleteConfirmationAsync(
                    context?.DeleteConfirmation, instance, operation, outbox.CreatedAt, cancellationToken);

                var storedInstance = await LoadTrackedInstanceAsync(instance.Id, cancellationToken);
                if (expectedInstance is null)
                {
                    if (storedInstance is not null)
                        throw Conflict("Elsa instance identity is already in use.");
                    if (operation.Action != ElsaInstanceOperationAction.Create)
                        throw Conflict("A lifecycle operation requires an existing instance.");
                }
                else
                {
                    ValidateExpectedInstance(expectedInstance, instance, storedInstance);
                }

                var activeOperation = storedInstance is null
                    ? null
                    : await dbContext.ElsaInstanceOperations
                        .AsNoTracking()
                        .Where(x => x.WorkspaceId == instance.WorkspaceId && x.InstanceId == instance.Id)
                        .Where(x => x.State == ElsaInstanceOperationState.Accepted ||
                                    x.State == ElsaInstanceOperationState.WaitingForPriorOperation ||
                                    x.State == ElsaInstanceOperationState.Queued ||
                                    x.State == ElsaInstanceOperationState.EntitlementHeld ||
                                    x.State == ElsaInstanceOperationState.Running ||
                                    x.State == ElsaInstanceOperationState.RecoveryRequired)
                        .OrderByDescending(x => x.AcceptedAt)
                        .ThenByDescending(x => x.CreatedAt)
                        .FirstOrDefaultAsync(cancellationToken);
                var supersedesEntitlementHeld = operation.Action is ElsaInstanceOperationAction.Stop or ElsaInstanceOperationAction.Delete &&
                    activeOperation is not null &&
                    activeOperation.State == ElsaInstanceOperationState.EntitlementHeld &&
                    activeOperation.Action != ElsaInstanceOperationAction.Delete;
                if (supersedesEntitlementHeld)
                {
                    var heldOperation = await dbContext.ElsaInstanceOperations
                        .SingleOrDefaultAsync(x => x.Id == activeOperation!.Id &&
                                                   x.WorkspaceId == instance.WorkspaceId &&
                                                   x.InstanceId == instance.Id,
                            cancellationToken);
                    if (heldOperation is null || heldOperation.State != ElsaInstanceOperationState.EntitlementHeld)
                        throw Conflict("The entitlement-held operation changed concurrently.", ElsaInstanceLifecycleConflictReason.OperationActive);
                    await SupersedeEntitlementHeldOperationAsync(heldOperation, storedInstance!, outbox.CreatedAt, cancellationToken);
                    activeOperation = null;
                }
                if (activeOperation is not null)
                {
                    var isDeleteSuccessor = operation.Action == ElsaInstanceOperationAction.Delete &&
                        operation.State == ElsaInstanceOperationState.WaitingForPriorOperation &&
                        activeOperation.Action != ElsaInstanceOperationAction.Delete;
                    if (!isDeleteSuccessor)
                        throw Conflict("An instance operation is already active.", ElsaInstanceLifecycleConflictReason.OperationActive);
                }

                var activeInstanceCount = operation.Action == ElsaInstanceOperationAction.Create
                    ? await dbContext.ElsaInstances.CountAsync(
                        x => x.OrganizationId == instance.OrganizationId && x.TargetMode == "managed" && x.DeletedAt == null,
                        cancellationToken)
                    : (int?)null;
                var commercialDecision = await _commercialGate.EvaluateAsync(
                    instance.OrganizationId,
                    operation.Action,
                    activeInstanceCount,
                    cancellationToken);
                if (!commercialDecision.Allowed)
                    throw new ElsaInstanceLifecycleConflictException(
                        commercialDecision.Summary,
                        ElsaInstanceLifecycleConflictReason.CommercialDenied,
                        commercialDecision.Code);

                var priorObservedLifecycle = storedInstance?.ObservedLifecycle;
                var instanceEntity = storedInstance ?? ToEntity(instance, outbox.CreatedAt);
                if (storedInstance is null)
                {
                    await dbContext.ElsaInstances.AddAsync(instanceEntity, cancellationToken);
                    // API-created managed instances carry the authenticated actor
                    // context and receive their control-owned target shell here.
                    // Keep the lower-level provider-neutral store usable for callers
                    // that provision an explicit deployment target in the same
                    // acceptance flow (including existing migration/test fixtures).
                    if (operation.Action == ElsaInstanceOperationAction.Create &&
                        context?.ActorAccountId is { } actorAccountId && actorAccountId != Guid.Empty &&
                        string.Equals(instance.PlacementIntent.TargetMode, "managed", StringComparison.OrdinalIgnoreCase))
                        await AddManagedDeploymentTargetAsync(instanceEntity, outbox.CreatedAt, cancellationToken);
                }
                else
                {
                    ApplyAggregate(instanceEntity, instance);
                    instanceEntity.UpdatedAt = outbox.CreatedAt.ToUniversalTime();
                }

                await AddIntentRevisionIfNeededAsync(
                    instanceEntity,
                    instance,
                    outbox.CreatedAt,
                    cancellationToken);

                var operationEntity = ToEntity(operation, instance, outbox.CreatedAt);
                // Every accepted operation points at the immutable intent revision that
                // was current for this transaction, including a mutation whose intent
                // hash happens to match the latest revision.
                operationEntity.DesiredStateRevisionId = instanceEntity.DesiredStateRevisionId;
                await dbContext.ElsaInstanceOperations.AddAsync(operationEntity, cancellationToken);

                var outboxEntity = ToEntity(outbox, instance);
                await dbContext.ElsaInstanceLifecycleOutbox.AddAsync(outboxEntity, cancellationToken);
                await dbContext.ElsaInstanceAuditEvents.AddAsync(
                    await CreateAuditEventAsync(
                        instanceEntity,
                        operationEntity,
                        priorObservedLifecycle,
                        occurredAt: outbox.CreatedAt,
                        cancellationToken: cancellationToken,
                        actorAccountId: context?.ActorAccountId,
                        summary: HashReason(context?.Reason)),
                    cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);

                return new ElsaInstanceLifecycleAcceptance(
                    MapInstance(instanceEntity),
                    MapOperation(operationEntity),
                    MapOutbox(outboxEntity),
                    Replayed: false);
            }, cancellationToken);
        }
        catch (ElsaInstanceLifecycleConflictException)
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            var recoveryReplay = await TryReplayCommittedRecoveryAsync(
                expectedInstance,
                instance,
                operation,
                cancellationToken);
            if (recoveryReplay is not null)
                return recoveryReplay;

            throw Conflict("Lifecycle acceptance conflicted with a newer instance version.", ElsaInstanceLifecycleConflictReason.VersionConflict);
        }
        catch (Exception exception) when (
            EfCoreDatabaseExceptionPolicy.IsSqlServerLifecycleReservationConflict(exception))
        {
            dbContext.ChangeTracker.Clear();
            var recoveryReplay = await TryReplayCommittedRecoveryAsync(
                expectedInstance,
                instance,
                operation,
                cancellationToken);
            if (recoveryReplay is not null)
                return recoveryReplay;

            var replay = await TryReplayCommittedAcceptanceAsync(
                expectedInstance,
                instance,
                operation,
                cancellationToken);
            if (replay is not null)
                return replay;

            if (exception is DbUpdateException updateException &&
                EfCoreDatabaseExceptionPolicy.IsElsaInstanceSlugUniqueViolation(updateException))
                throw Conflict("Instance slug is already in use in this workspace.", ElsaInstanceLifecycleConflictReason.SlugConflict);

            throw Conflict("Lifecycle acceptance conflicted with another request.");
        }
        catch (DbUpdateException exception) when (
            EfCoreDatabaseExceptionPolicy.IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            var recoveryReplay = await TryReplayCommittedRecoveryAsync(
                expectedInstance,
                instance,
                operation,
                cancellationToken);
            if (recoveryReplay is not null)
                return recoveryReplay;

            if (EfCoreDatabaseExceptionPolicy.IsElsaInstanceSlugUniqueViolation(exception))
                throw Conflict("Instance slug is already in use in this workspace.", ElsaInstanceLifecycleConflictReason.SlugConflict);

            throw Conflict("Lifecycle acceptance conflicted with another request.");
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            throw Conflict("Lifecycle acceptance conflicted with another request.");
        }
        catch (DbException)
        {
            dbContext.ChangeTracker.Clear();
            throw Conflict("Lifecycle acceptance could not obtain the persistence reservation.");
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// A managed instance owns one control-plane deployment target. Creating the
    /// small application/environment/engine shell with the lifecycle acceptance
    /// transaction makes an API-created instance immediately eligible for the
    /// existing run reservation and provider queues. It contains no provider
    /// resource identity or secret; the provider supplies those only as observed
    /// state after a successful apply.
    /// </summary>
    private async Task AddManagedDeploymentTargetAsync(
        ElsaInstanceEntity instance,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var application = new DeploymentApplicationEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = instance.WorkspaceId,
            // Application names are workspace-unique even when an old managed
            // instance with the same reusable slug remains as a tombstone.
            Name = $"Managed {instance.Slug} [{instance.Id:N}]",
            Description = "Control-owned managed Elsa instance target.",
            CreatedAt = createdAt.ToUniversalTime(),
            UpdatedAt = createdAt.ToUniversalTime()
        };
        var environment = new DeploymentEnvironmentEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = instance.WorkspaceId,
            ApplicationId = application.Id,
            ElsaInstanceId = instance.Id,
            Name = "managed",
            Tier = EnvironmentTier.Production,
            DeploymentStatus = DeploymentStatus.Blocked,
            DriftStatus = DriftStatus.Unknown,
            DesiredRevisionId = DeterministicGuid(instance.Id, "desired-revision"),
            CreatedAt = createdAt.ToUniversalTime(),
            UpdatedAt = createdAt.ToUniversalTime()
        };
        var engine = new WorkflowEngineEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = instance.WorkspaceId,
            EnvironmentId = environment.Id,
            Name = "managed",
            BaseUrl = "https://managed.invalid/",
            CertificateStatus = CertificateStatus.Untrusted,
            CredentialProvider = "provider-managed",
            CredentialReference = $"managed-instance:{instance.Id:D}",
            CredentialAssignmentStatus = EngineCredentialAssignmentStatus.Deferred,
            CredentialVerificationStatus = CredentialVerificationStatus.NotVerifiable,
            Health = DeploymentHealth.Unreachable,
            VerificationMessage = "The managed provider has not established a healthy endpoint.",
            HostingProvider = "managed",
            CreatedAt = createdAt.ToUniversalTime(),
            UpdatedAt = createdAt.ToUniversalTime()
        };
        environment.Engines.Add(engine);
        application.Environments.Add(environment);
        await dbContext.DeploymentApplications.AddAsync(application, cancellationToken);
    }

    private static Guid DeterministicGuid(Guid seed, string purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"elsa-control:{purpose}:{seed:D}"));
        return new Guid(bytes[..16]);
    }

    private async Task<ElsaInstanceLifecycleAcceptance?> TryReplayCommittedAcceptanceAsync(
        ElsaInstance? expectedInstance,
        ElsaInstance requestedInstance,
        ElsaInstanceOperation requestedOperation,
        CancellationToken cancellationToken)
    {
        foreach (var delay in IdempotencyReplayLookupDelays)
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);

            dbContext.ChangeTracker.Clear();
            try
            {
                var existingOperation = await dbContext.ElsaInstanceOperations
                    .AsNoTracking()
                    .OrderByDescending(x => x.AcceptedAt)
                    .ThenByDescending(x => x.CreatedAt)
                    .FirstOrDefaultAsync(
                        x => x.WorkspaceId == requestedInstance.WorkspaceId &&
                             x.IdempotencyScope == requestedOperation.IdempotencyScope &&
                             x.IdempotencyKey == requestedOperation.IdempotencyKey,
                        cancellationToken);
                if (existingOperation is null)
                    continue;

                if (!IsExactAuthoritativeReplay(
                        expectedInstance,
                        requestedInstance,
                        requestedOperation,
                        existingOperation))
                    throw Conflict("Idempotency key was already used for a different request.", ElsaInstanceLifecycleConflictReason.IdempotencyConflict);

                var existingInstance = await dbContext.ElsaInstances.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == existingOperation.InstanceId, cancellationToken);
                var existingOutbox = await dbContext.ElsaInstanceLifecycleOutbox.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.OperationId == existingOperation.Id, cancellationToken);
                if (existingInstance is null || existingOutbox is null)
                    continue;

                ValidateReplayEnvelope(existingInstance, existingOperation, existingOutbox);
                if (existingInstance.OrganizationId != requestedInstance.OrganizationId ||
                    existingInstance.WorkspaceId != requestedInstance.WorkspaceId ||
                    (expectedInstance is null
                        ? !string.Equals(existingInstance.Slug, requestedInstance.Slug, StringComparison.Ordinal)
                        : existingInstance.Id != requestedInstance.Id))
                    throw Conflict("Idempotency key was already used for a different request.", ElsaInstanceLifecycleConflictReason.IdempotencyConflict);

                return new ElsaInstanceLifecycleAcceptance(
                    MapInstance(existingInstance),
                    MapOperation(existingOperation),
                    MapOutbox(existingOutbox),
                    Replayed: true);
            }
            catch (ElsaInstanceLifecycleConflictException)
            {
                throw;
            }
            catch (Exception exception) when (
                EfCoreDatabaseExceptionPolicy.IsSqlServerLifecycleReservationConflict(exception))
            {
                // The winner can still be committing or this lookup can itself be
                // selected as a deadlock victim. Only known SQL Server reservation
                // conflicts are eligible for another bounded authoritative read.
                continue;
            }
            catch (Exception exception) when (exception is DbUpdateException or DbException)
            {
                throw Conflict("Lifecycle acceptance could not verify the persistence reservation.");
            }
        }

        return null;
    }

    private async Task<ElsaInstanceLifecycleAcceptance?> TryReplayCommittedRecoveryAsync(
        ElsaInstance? expectedInstance,
        ElsaInstance requestedInstance,
        ElsaInstanceOperation requestedOperation,
        CancellationToken cancellationToken)
    {
        if (requestedOperation.RecoveryIdempotencyScope is null ||
            requestedOperation.RecoveryIdempotencyKey is null ||
            requestedOperation.RecoveryRequestHash is null)
            return null;

        foreach (var delay in IdempotencyReplayLookupDelays)
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);

            dbContext.ChangeTracker.Clear();
            try
            {
                var recovery = await dbContext.ElsaInstanceRecoveryRequests
                    .AsNoTracking()
                    .OrderByDescending(x => x.AcceptedAt)
                    .ThenByDescending(x => x.CreatedAt)
                    .FirstOrDefaultAsync(
                        x => x.WorkspaceId == requestedInstance.WorkspaceId &&
                             x.IdempotencyScope == requestedOperation.RecoveryIdempotencyScope &&
                             x.IdempotencyKey == requestedOperation.RecoveryIdempotencyKey,
                        cancellationToken);
                if (recovery is null)
                    continue;
                var existingOperation = await dbContext.ElsaInstanceOperations.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == recovery.OperationId, cancellationToken);
                if (existingOperation is null)
                    continue;

                if (!IsExactAuthoritativeRecoveryReplay(
                        expectedInstance,
                        requestedInstance,
                        requestedOperation,
                        existingOperation,
                        recovery))
                    throw Conflict("Recovery request conflicts with the accepted recovery request.",
                        ElsaInstanceLifecycleConflictReason.IdempotencyConflict);

                var existingInstance = await dbContext.ElsaInstances.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == existingOperation.InstanceId, cancellationToken);
                var existingOutbox = await dbContext.ElsaInstanceLifecycleOutbox.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.OperationId == existingOperation.Id, cancellationToken);
                if (existingInstance is null || existingOutbox is null)
                    continue;

                ValidateReplayEnvelope(existingInstance, existingOperation, existingOutbox);
                if (existingInstance.Id != requestedInstance.Id ||
                    existingInstance.OrganizationId != requestedInstance.OrganizationId ||
                    existingInstance.WorkspaceId != requestedInstance.WorkspaceId)
                    throw Conflict("Recovery request conflicts with the accepted recovery request.",
                        ElsaInstanceLifecycleConflictReason.IdempotencyConflict);

                return new ElsaInstanceLifecycleAcceptance(
                    MapInstance(existingInstance),
                    MapOperation(existingOperation, recovery),
                    MapOutbox(existingOutbox),
                    Replayed: true);
            }
            catch (ElsaInstanceLifecycleConflictException)
            {
                throw;
            }
            catch (Exception exception) when (
                EfCoreDatabaseExceptionPolicy.IsSqlServerLifecycleReservationConflict(exception))
            {
                // The recovery winner may still be committing, or this bounded
                // authoritative read can itself be selected as a deadlock victim.
                continue;
            }
            catch (Exception exception) when (exception is DbUpdateException or DbException)
            {
                throw Conflict("Lifecycle recovery could not verify the persistence reservation.");
            }
        }

        return null;
    }

    internal static bool IsExactAuthoritativeReplay(
        ElsaInstance? expectedInstance,
        ElsaInstance requestedInstance,
        ElsaInstanceOperation requestedOperation,
        ElsaInstanceOperationEntity existingOperation) =>
        existingOperation.OrganizationId == requestedInstance.OrganizationId &&
        existingOperation.WorkspaceId == requestedInstance.WorkspaceId &&
        existingOperation.Action == requestedOperation.Action &&
        string.Equals(existingOperation.IdempotencyScope, requestedOperation.IdempotencyScope, StringComparison.Ordinal) &&
        string.Equals(existingOperation.IdempotencyKey, requestedOperation.IdempotencyKey, StringComparison.Ordinal) &&
        string.Equals(existingOperation.RequestHash, requestedOperation.RequestHash, StringComparison.Ordinal) &&
        (expectedInstance is null
            ? requestedOperation.Action == ElsaInstanceOperationAction.Create
            : existingOperation.InstanceId == requestedInstance.Id &&
              requestedOperation.InstanceId == requestedInstance.Id &&
              expectedInstance.Id == requestedInstance.Id &&
              expectedInstance.OrganizationId == requestedInstance.OrganizationId &&
              expectedInstance.WorkspaceId == requestedInstance.WorkspaceId);

    internal static bool IsExactAuthoritativeRecoveryReplay(
        ElsaInstance? expectedInstance,
        ElsaInstance requestedInstance,
        ElsaInstanceOperation requestedOperation,
        ElsaInstanceOperationEntity existingOperation,
        ElsaInstanceRecoveryRequestEntity recovery) =>
        expectedInstance is not null &&
        expectedInstance.Id == requestedInstance.Id &&
        expectedInstance.OrganizationId == requestedInstance.OrganizationId &&
        expectedInstance.WorkspaceId == requestedInstance.WorkspaceId &&
        requestedOperation.InstanceId == requestedInstance.Id &&
        existingOperation.Id == requestedOperation.Id &&
        existingOperation.InstanceId == requestedInstance.Id &&
        existingOperation.OrganizationId == requestedInstance.OrganizationId &&
        existingOperation.WorkspaceId == requestedInstance.WorkspaceId &&
        existingOperation.Action == requestedOperation.Action &&
        string.Equals(existingOperation.IdempotencyScope, requestedOperation.IdempotencyScope, StringComparison.Ordinal) &&
        string.Equals(existingOperation.IdempotencyKey, requestedOperation.IdempotencyKey, StringComparison.Ordinal) &&
        string.Equals(existingOperation.RequestHash, requestedOperation.RequestHash, StringComparison.Ordinal) &&
        recovery.OperationId == existingOperation.Id &&
        recovery.InstanceId == requestedInstance.Id &&
        recovery.OrganizationId == requestedInstance.OrganizationId &&
        recovery.WorkspaceId == requestedInstance.WorkspaceId &&
        string.Equals(recovery.IdempotencyScope, requestedOperation.RecoveryIdempotencyScope, StringComparison.Ordinal) &&
        string.Equals(recovery.IdempotencyKey, requestedOperation.RecoveryIdempotencyKey, StringComparison.Ordinal) &&
        string.Equals(recovery.RequestHash, requestedOperation.RecoveryRequestHash, StringComparison.Ordinal);

    public async Task<ElsaInstanceLifecycleWorkItem?> TryClaimNextAsync(
        string workerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("Lifecycle worker identity is required.", nameof(workerId));
        workerId = workerId.Trim();
        if (workerId.Length > 256 || workerId.Any(char.IsControl))
            throw new ArgumentException("Lifecycle worker identity is invalid.", nameof(workerId));

        const int MaxSkippedCandidates = 1024;
        var skippedCandidateIds = new HashSet<Guid>();
        ClaimedLifecycleWork claim;
        while (true)
        {
            (bool Quarantined, ClaimedLifecycleWork? Claim) attempt;
            try
            {
                attempt = await dbContext.ExecuteInTransactionAsync<(bool Quarantined, ClaimedLifecycleWork? Claim)>(
                    IsolationLevel.Serializable, async () =>
                {
                    // Accepted work is resolver-only until the atomic commit. A lease
                    // that expired before that commit may therefore be safely reclaimed
                    // by rotating both its token and version.
                    var nowUtc = now.ToUniversalTime();
                    var candidate = await dbContext.ElsaInstanceLifecycleOutbox
                        .AsNoTracking()
                        .Where(x => x.Operation != null &&
                                    x.Operation.Action != ElsaInstanceOperationAction.Delete &&
                                    x.QuarantinedAt == null &&
                                    !skippedCandidateIds.Contains(x.Id) &&
                                    (x.Operation.State == ElsaInstanceOperationState.Accepted ||
                                     (x.Operation.State == ElsaInstanceOperationState.WaitingForPriorOperation &&
                                      !dbContext.ElsaInstanceOperations.Any(operation =>
                                          operation.Id != x.OperationId &&
                                          operation.InstanceId == x.InstanceId &&
                                          (operation.State == ElsaInstanceOperationState.Accepted ||
                                           operation.State == ElsaInstanceOperationState.WaitingForPriorOperation ||
                                           operation.State == ElsaInstanceOperationState.Queued ||
                                           operation.State == ElsaInstanceOperationState.EntitlementHeld ||
                                           operation.State == ElsaInstanceOperationState.Running ||
                                           operation.State == ElsaInstanceOperationState.RecoveryRequired)))) &&
                                    (x.Operation.WorkerId == null ||
                                     x.Operation.LeaseExpiresAt == null ||
                                     x.Operation.LeaseExpiresAt <= nowUtc))
                        .OrderBy(x => x.CreatedAt)
                        .ThenBy(x => x.Id)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (candidate is null)
                        return (false, null);

                    var operationEntity = await dbContext.ElsaInstanceOperations
                        .SingleOrDefaultAsync(x => x.Id == candidate.OperationId, cancellationToken);
                    var instanceEntity = await LoadTrackedInstanceAsync(candidate.InstanceId, cancellationToken);
                    if (operationEntity is null || instanceEntity is null)
                    {
                        await QuarantineClaimCandidateAsync(
                            candidate.Id, null, null, false, nowUtc, cancellationToken);
                        return (true, null);
                    }
                    if (operationEntity.State == ElsaInstanceOperationState.WaitingForPriorOperation)
                        operationEntity.State = ElsaInstanceOperationState.Accepted;
                    if (operationEntity.State != ElsaInstanceOperationState.Accepted ||
                        (operationEntity.WorkerId is not null && operationEntity.LeaseExpiresAt > nowUtc))
                        return (false, null);

                    if (!TryMapPersistedWorkItem(candidate, operationEntity, instanceEntity,
                            out var outbox, out var operation, out var instance))
                    {
                        await QuarantineClaimCandidateAsync(
                            candidate.Id,
                            operationEntity,
                            instanceEntity,
                            CanQuarantine(candidate, operationEntity, instanceEntity),
                            nowUtc,
                            cancellationToken);
                        return (true, null);
                    }

                    if (operationEntity.LeaseVersion < 0 || operationEntity.LeaseVersion == int.MaxValue)
                    {
                        await QuarantineClaimCandidateAsync(
                            candidate.Id,
                            operationEntity,
                            instanceEntity,
                            CanQuarantine(candidate, operationEntity, instanceEntity),
                            nowUtc,
                            cancellationToken);
                        return (true, null);
                    }

                    var leaseToken = CreateLeaseToken();
                    var leaseVersion = checked(operationEntity.LeaseVersion + 1);
                    operationEntity.WorkerId = workerId;
                    operationEntity.LeaseTokenHash = HashLeaseToken(leaseToken);
                    operationEntity.LeaseVersion = leaseVersion;
                    operationEntity.LeaseExpiresAt = nowUtc.Add(WorkerLeaseDuration);
                    operationEntity.HeartbeatAt = nowUtc;
                    operationEntity.StartedAt ??= nowUtc;
                    operationEntity.UpdatedAt = nowUtc;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return (false, new ClaimedLifecycleWork(outbox, operation, instance, leaseToken, leaseVersion));
                }, cancellationToken);
            }
            catch (SkippedClaimCandidateException skipped)
            {
                // A malformed row may be identifiable but still fail the
                // context's durable-row validation. The transaction rolled
                // back and this candidate is excluded for this scan.
                dbContext.ChangeTracker.Clear();
                skippedCandidateIds.Add(skipped.CandidateId);
                if (skippedCandidateIds.Count >= MaxSkippedCandidates)
                    return null;
                continue;
            }
            catch (ElsaInstanceLifecycleConflictException)
            {
                dbContext.ChangeTracker.Clear();
                throw;
            }
            catch (Exception exception) when (exception is DbUpdateConcurrencyException or DbUpdateException or DbException)
            {
                dbContext.ChangeTracker.Clear();
                return null;
            }
            catch
            {
                dbContext.ChangeTracker.Clear();
                throw;
            }

            if (attempt.Quarantined)
            {
                dbContext.ChangeTracker.Clear();
                continue;
            }

            if (attempt.Claim is null)
            {
                dbContext.ChangeTracker.Clear();
                return null;
            }

            claim = attempt.Claim;
            break;
        }

        ElsaInstanceLifecycleResolutionInput? resolution = null;
        try
        {
            resolution = await ResolutionInputSource.GetAsync(claim.Instance, claim.Operation, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Leave the operation claimed and let the worker record the stable
            // resolution.invalid outcome using the lease it owns.
        }

        return new ElsaInstanceLifecycleWorkItem(
            claim.Outbox,
            claim.Operation,
            claim.Instance,
            resolution!,
            claim.LeaseToken,
            claim.LeaseVersion);
    }

    private sealed record ClaimedLifecycleWork(
        ElsaInstanceLifecycleOutboxMessage Outbox,
        ElsaInstanceOperation Operation,
        ElsaInstance Instance,
        string LeaseToken,
        int LeaseVersion);

    /// <summary>
    /// Quarantines a claim candidate inside the claim transaction. When that fails the attempt
    /// must roll back instead of committing a partial quarantine, so the failure surfaces as a
    /// skipped candidate.
    /// </summary>
    private async Task QuarantineClaimCandidateAsync(
        Guid candidateId,
        ElsaInstanceOperationEntity? operation,
        ElsaInstanceEntity? instance,
        bool failOperation,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await QuarantinePersistedWorkItemAsync(
                candidateId, operation, instance, failOperation, occurredAt, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbUpdateException or DbException)
        {
            throw new SkippedClaimCandidateException(candidateId, exception);
        }
    }

    private sealed class SkippedClaimCandidateException(Guid candidateId, Exception innerException)
        : Exception("The lifecycle claim candidate could not be quarantined.", innerException)
    {
        public Guid CandidateId { get; } = candidateId;
    }

    public async Task<ElsaInstanceDeletionWorkItem?> TryClaimNextDeletionAsync(
        string workerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("Deletion worker identity is required.", nameof(workerId));
        workerId = workerId.Trim();
        if (workerId.Length > 256 || workerId.Any(char.IsControl))
            throw new ArgumentException("Deletion worker identity is invalid.", nameof(workerId));

        try
        {
            return await dbContext.ExecuteInTransactionAsync<ElsaInstanceDeletionWorkItem?>(IsolationLevel.Serializable, async () =>
            {
                var nowUtc = now.ToUniversalTime();
                var candidate = await dbContext.ElsaInstanceLifecycleOutbox
                    .AsNoTracking()
                    .Where(x => x.Action == ElsaInstanceOperationAction.Delete     && x.Operation != null && x.QuarantinedAt == null &&
                                (x.Operation.State == ElsaInstanceOperationState.Accepted ||
                                 x.Operation.State == ElsaInstanceOperationState.Queued ||
                                 x.Operation.State == ElsaInstanceOperationState.Running ||
                                 (x.Operation.State == ElsaInstanceOperationState.WaitingForPriorOperation &&
                                  !dbContext.ElsaInstanceOperations.Any(prior => prior.Id != x.OperationId &&
                                      prior.WorkspaceId == x.WorkspaceId &&
                                      prior.InstanceId == x.InstanceId &&
                                      (prior.State == ElsaInstanceOperationState.Accepted ||
                                       prior.State == ElsaInstanceOperationState.WaitingForPriorOperation ||
                                       prior.State == ElsaInstanceOperationState.Queued ||
                                       prior.State == ElsaInstanceOperationState.EntitlementHeld ||
                                       prior.State == ElsaInstanceOperationState.Running ||
                                       prior.State == ElsaInstanceOperationState.RecoveryRequired)))) &&
                                !dbContext.DeploymentRuns.Any(run => run.WorkspaceId == x.WorkspaceId &&
                                    run.ElsaInstanceId == x.InstanceId &&
                                    (run.Status == WorkspaceDeploymentRunStatus.Queued ||
                                     run.Status == WorkspaceDeploymentRunStatus.Running ||
                                     run.Status == WorkspaceDeploymentRunStatus.RecoveryRequired)) &&
                                (x.Operation.WorkerId == null || x.Operation.LeaseExpiresAt == null || x.Operation.LeaseExpiresAt <= nowUtc))
                    .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                if (candidate is null)
                {
                    return null;
                }

                var operation = await dbContext.ElsaInstanceOperations.SingleAsync(x => x.Id == candidate.OperationId, cancellationToken);
                var instance = await LoadTrackedInstanceAsync(candidate.InstanceId, cancellationToken)
                    ?? throw Conflict("Deletion instance no longer exists.");
                if (operation.State == ElsaInstanceOperationState.WaitingForPriorOperation)
                    operation.State = ElsaInstanceOperationState.Accepted;
                if (operation.State is not (ElsaInstanceOperationState.Accepted or ElsaInstanceOperationState.Queued or ElsaInstanceOperationState.Running) ||
                    operation.Action != ElsaInstanceOperationAction.Delete)
                    throw Conflict("Deletion operation is not claimable.");

                var leaseToken = CreateLeaseToken();
                var leaseVersion = checked(operation.LeaseVersion + 1);
                operation.WorkerId = workerId;
                operation.LeaseTokenHash = HashLeaseToken(leaseToken);
                operation.LeaseVersion = leaseVersion;
                operation.LeaseExpiresAt = nowUtc.Add(WorkerLeaseDuration);
                operation.HeartbeatAt = nowUtc;
                operation.StartedAt ??= nowUtc;
                operation.UpdatedAt = nowUtc;
                if (operation.State == ElsaInstanceOperationState.Queued)
                    operation.State = ElsaInstanceOperationState.Running;

                var latestRunId = await dbContext.DeploymentRuns
                    .AsNoTracking()
                    .Where(x => x.WorkspaceId == candidate.WorkspaceId && x.ElsaInstanceId == candidate.InstanceId)
                    .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                    .Select(x => (Guid?)x.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                var currentRecoveryRows = await dbContext.ElsaInstanceRecoveryRequests
                    .AsNoTracking()
                    .Where(x => x.WorkspaceId == operation.WorkspaceId &&
                                x.InstanceId == instance.Id &&
                                x.OperationId == operation.Id &&
                                x.AttemptNumber == operation.AttemptNumber)
                    .ToListAsync(cancellationToken);
                if (currentRecoveryRows.Count > 1)
                    throw Conflict("Deletion recovery authority is ambiguous.");
                var recoveryRequestId = currentRecoveryRows.Count == 1 &&
                                        currentRecoveryRows[0].AzureDeleteRecoveryAuthority is not null
                    ? currentRecoveryRows[0].Id
                    : (Guid?)null;
                var mappedInstance = MapInstance(instance);
                var local = recoveryRequestId is null &&
                    mappedInstance.ObservedLifecycle != ElsaObservedLifecycle.Unknown &&
                    mappedInstance.CurrentDeploymentReference is null && mappedInstance.PlacementAssignmentReference is null &&
                    mappedInstance.ElsaTenantReference is null;

                await dbContext.SaveChangesAsync(cancellationToken);
                return new ElsaInstanceDeletionWorkItem(MapOutbox(candidate), MapOperation(operation), mappedInstance,
                    local, latestRunId, leaseToken, leaseVersion)
                {
                    RecoveryRequestId = recoveryRequestId
                };
            }, cancellationToken);
        }
        catch (ElsaInstanceLifecycleConflictException)
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException or DbUpdateException or DbException)
        {
            dbContext.ChangeTracker.Clear();
            return null;
        }
    }

    public async Task<bool> RenewDeletionLeaseAsync(ElsaInstanceDeletionWorkItem item, string workerId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        var nowUtc = now.ToUniversalTime();
        var tokenHash = HashLeaseToken(item.LeaseToken);
        var renewed = await dbContext.ElsaInstanceOperations
            .Where(operation => operation.Id == item.Operation.Id &&
                (operation.State == ElsaInstanceOperationState.Accepted ||
                 operation.State == ElsaInstanceOperationState.Running) &&
                operation.WorkerId == workerId && operation.LeaseTokenHash == tokenHash &&
                operation.LeaseVersion == item.LeaseVersion && operation.LeaseExpiresAt != null &&
                operation.LeaseExpiresAt > nowUtc)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(operation => operation.LeaseExpiresAt, nowUtc.Add(WorkerLeaseDuration))
                .SetProperty(operation => operation.HeartbeatAt, nowUtc)
                .SetProperty(operation => operation.UpdatedAt, nowUtc), cancellationToken);
        return renewed == 1;
    }

    public async Task<bool> DeferDeletionAsync(ElsaInstanceDeletionWorkItem item, string workerId, DateTimeOffset now,
        string diagnosticCode, CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        ArgumentNullException.ThrowIfNull(item);
        item.Validate();
        new ElsaInstanceCleanupObservation(
            ElsaInstanceCleanupObservationKind.InProgress, item.Operation.Id,
            item.Operation.AttemptNumber, diagnosticCode).Validate();
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("Deletion worker identity is required.", nameof(workerId));
        workerId = workerId.Trim();
        if (workerId.Length > 256 || workerId.Any(char.IsControl))
            throw new ArgumentException("Deletion worker identity is invalid.", nameof(workerId));
        if (item.Outbox.WorkspaceId != item.Instance.WorkspaceId || item.Outbox.InstanceId != item.Instance.Id ||
            item.Outbox.OperationId != item.Operation.Id || item.Outbox.Action != ElsaInstanceOperationAction.Delete ||
            !string.Equals(item.Outbox.RequestHash, item.Operation.RequestHash, StringComparison.Ordinal))
            return false;

        var nowUtc = now.ToUniversalTime();
        var deferred = await dbContext.ElsaInstanceOperations
            .Where(operation => operation.Id == item.Operation.Id &&
                operation.WorkspaceId == item.Instance.WorkspaceId &&
                operation.InstanceId == item.Instance.Id &&
                operation.Action == ElsaInstanceOperationAction.Delete &&
                (operation.State == ElsaInstanceOperationState.Accepted ||
                 operation.State == ElsaInstanceOperationState.Running) &&
                operation.AttemptNumber == item.Operation.AttemptNumber &&
                operation.RequestHash == item.Operation.RequestHash &&
                operation.WorkerId == workerId &&
                operation.LeaseTokenHash == HashLeaseToken(item.LeaseToken) &&
                operation.LeaseVersion == item.LeaseVersion &&
                operation.LeaseExpiresAt != null && operation.LeaseExpiresAt > nowUtc &&
                dbContext.ElsaInstances.Any(instance =>
                    instance.Id == operation.InstanceId && instance.WorkspaceId == operation.WorkspaceId &&
                    instance.Version == item.Instance.Version) &&
                dbContext.ElsaInstanceLifecycleOutbox.Any(outbox =>
                    outbox.Id == item.Outbox.Id && outbox.WorkspaceId == operation.WorkspaceId &&
                    outbox.InstanceId == operation.InstanceId && outbox.OperationId == operation.Id &&
                    outbox.Action == ElsaInstanceOperationAction.Delete &&
                    outbox.RequestHash == operation.RequestHash))
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(operation => operation.LeaseExpiresAt, nowUtc.Add(DeletionDeferralDelay))
                .SetProperty(operation => operation.HeartbeatAt, nowUtc)
                .SetProperty(operation => operation.DeletionDiagnosticCode, diagnosticCode)
                .SetProperty(operation => operation.UpdatedAt, nowUtc), cancellationToken);
        return deferred == 1;
    }

    public async Task<ElsaInstanceDeletionResult> CommitDeletionAsync(
        ElsaInstanceDeletionCommit commit,
        CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        ArgumentNullException.ThrowIfNull(commit);
        commit.Validate();
        try
        {
            return await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
            {
                var operation = await dbContext.ElsaInstanceOperations.SingleOrDefaultAsync(x => x.Id == commit.OperationId, cancellationToken);
                var instance = await LoadTrackedInstanceAsync(commit.InstanceId, cancellationToken);
                var outbox = await dbContext.ElsaInstanceLifecycleOutbox.SingleOrDefaultAsync(x => x.Id == commit.OutboxId, cancellationToken);
                if (operation is null || instance is null || outbox is null)
                    throw Conflict("Deletion work item no longer exists.");
                if (operation.State == ElsaInstanceOperationState.Succeeded && instance.ObservedLifecycle == ElsaObservedLifecycle.Deleted)
                {
                    if (!string.Equals(operation.DeletionEvidenceFingerprint, commit.EvidenceFingerprint, StringComparison.Ordinal))
                        throw Conflict("Deletion evidence conflicts with the terminal result.");
                    return DeletionResult(operation, instance, true);
                }

                EnsureDeletionLease(operation, instance, outbox, commit.WorkspaceId, commit.InstanceId, commit.OperationId,
                    commit.ExpectedInstanceVersion, commit.ExpectedAttemptNumber, commit.WorkerId, commit.LeaseToken, commit.LeaseVersion);
                var currentAggregate = MapInstance(instance);
                if (commit.ProofKind == ElsaInstanceDeletionProofKind.LocalNoOwnedResources &&
                    (currentAggregate.ObservedLifecycle == ElsaObservedLifecycle.Unknown ||
                     currentAggregate.CurrentDeploymentReference is not null ||
                     currentAggregate.PlacementAssignmentReference is not null ||
                     currentAggregate.ElsaTenantReference is not null))
                    throw Conflict("Local deletion proof is not valid for this instance.");
                var correlatedRun = await EnsureTerminalDeletionRunAsync(
                    commit.ExpectedRunId, commit.WorkspaceId, commit.InstanceId, cancellationToken);
                var environment = await dbContext.DeploymentEnvironments.SingleOrDefaultAsync(x =>
                    x.WorkspaceId == commit.WorkspaceId && x.ElsaInstanceId == commit.InstanceId, cancellationToken);
                if (correlatedRun is not null && (environment is null || environment.Id != correlatedRun.EnvironmentId))
                    throw Conflict("Deletion environment binding is inconsistent.");

                var priorState = instance.ObservedLifecycle;
                ApplyAggregate(instance, commit.Instance);
                instance.UpdatedAt = commit.DeletedAt.ToUniversalTime();
                operation.State = ElsaInstanceOperationState.Succeeded;
                operation.CompletedAt = commit.DeletedAt.ToUniversalTime();
                operation.WorkerId = null;
                operation.LeaseTokenHash = null;
                operation.LeaseExpiresAt = null;
                operation.HeartbeatAt = null;
                operation.DeletionEvidenceFingerprint = commit.EvidenceFingerprint;
                operation.DeletionEvidenceReference = commit.EvidenceReference;
                operation.DeletionEvidenceDigest = commit.EvidenceDigest;
                operation.DeletionDiagnosticCode = commit.DiagnosticCode;
                operation.UpdatedAt = commit.DeletedAt.ToUniversalTime();
                if (environment is not null)
                {
                    // Persist the tombstone inside this transaction before releasing the
                    // environment reservation so database guards can verify the release.
                    await dbContext.SaveChangesAsync(cancellationToken);
                    environment.ElsaInstanceId = null;
                    environment.DesiredRevisionId = null;
                    environment.DeployedRevisionId = null;
                    environment.DeploymentStatus = DeploymentStatus.Blocked;
                    environment.UpdatedAt = commit.DeletedAt.ToUniversalTime();
                }
                await dbContext.ElsaInstanceAuditEvents.AddAsync(await CreateAuditEventAsync(instance, operation, priorState,
                    commit.DeletedAt, cancellationToken, "lifecycle.deleted", commit.ExpectedRunId,
                    diagnosticCode: commit.DiagnosticCode, summary: "Instance deletion was positively confirmed."), cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                return DeletionResult(operation, instance, false);
            }, cancellationToken);
        }
        catch (ElsaInstanceLifecycleConflictException) { dbContext.ChangeTracker.Clear(); throw; }
        catch (DbUpdateConcurrencyException) { dbContext.ChangeTracker.Clear(); throw Conflict("Deletion conflicted with a newer instance version."); }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        { dbContext.ChangeTracker.Clear(); throw Conflict("Deletion finalization conflicted with another worker."); }
    }

    public async Task<ElsaInstanceDeletionResult> RequireDeletionRecoveryAsync(
        ElsaInstanceDeletionFailure failure,
        CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        ArgumentNullException.ThrowIfNull(failure);
        failure.Validate();
        return await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            var operation = await dbContext.ElsaInstanceOperations.SingleOrDefaultAsync(x => x.Id == failure.OperationId, cancellationToken);
            var instance = await LoadTrackedInstanceAsync(failure.InstanceId, cancellationToken);
            var outbox = await dbContext.ElsaInstanceLifecycleOutbox.SingleOrDefaultAsync(x => x.Id == failure.OutboxId, cancellationToken);
            if (operation is null || instance is null || outbox is null)
                throw Conflict("Deletion work item no longer exists.");
            EnsureDeletionLease(operation, instance, outbox, failure.WorkspaceId, failure.InstanceId, failure.OperationId,
                failure.ExpectedInstanceVersion, failure.ExpectedAttemptNumber, failure.WorkerId, failure.LeaseToken, failure.LeaseVersion);
            _ = await EnsureTerminalDeletionRunAsync(
                failure.ExpectedRunId, failure.WorkspaceId, failure.InstanceId, cancellationToken);
            if (operation.State == ElsaInstanceOperationState.Accepted)
            {
                operation.State = ElsaInstanceOperationState.Queued;
                operation.UpdatedAt = failure.FailedAt.ToUniversalTime();
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            operation.State = ElsaInstanceOperationState.RecoveryRequired;
            operation.FailureCode = failure.DiagnosticCode;
            operation.FailureSummary = failure.DiagnosticCode;
            operation.WorkerId = null;
            operation.LeaseTokenHash = null;
            operation.LeaseExpiresAt = null;
            operation.HeartbeatAt = null;
            operation.DeletionEvidenceFingerprint = failure.EvidenceFingerprint;
            operation.DeletionDiagnosticCode = failure.DiagnosticCode;
            operation.UpdatedAt = failure.FailedAt.ToUniversalTime();
            await dbContext.ElsaInstanceAuditEvents.AddAsync(await CreateAuditEventAsync(instance, operation,
                instance.ObservedLifecycle, failure.FailedAt, cancellationToken, "lifecycle.deletion-recovery-required",
                failure.ExpectedRunId, diagnosticCode: failure.DiagnosticCode), cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return DeletionResult(operation, instance, false);
        }, cancellationToken);
    }

    private void EnsureDeletionLease(
        ElsaInstanceOperationEntity operation,
        ElsaInstanceEntity instance,
        ElsaInstanceLifecycleOutboxEntity outbox,
        Guid workspaceId,
        Guid instanceId,
        Guid operationId,
        int expectedVersion,
        int expectedAttempt,
        string workerId,
        string leaseToken,
        int leaseVersion)
    {
        if (operation.WorkspaceId != workspaceId || operation.InstanceId != instanceId || operation.Id != operationId ||
            operation.Action != ElsaInstanceOperationAction.Delete ||
            operation.State is not (ElsaInstanceOperationState.Accepted or ElsaInstanceOperationState.Running) ||
            operation.AttemptNumber != expectedAttempt || instance.WorkspaceId != workspaceId || instance.Id != instanceId ||
            instance.Version != expectedVersion || outbox.OperationId != operationId || outbox.InstanceId != instanceId ||
            !string.Equals(operation.WorkerId, workerId, StringComparison.Ordinal) ||
            !string.Equals(operation.LeaseTokenHash, HashLeaseToken(leaseToken), StringComparison.Ordinal) ||
            operation.LeaseVersion != leaseVersion || operation.LeaseExpiresAt is null ||
            operation.LeaseExpiresAt <= _timeProvider.GetUtcNow())
            throw Conflict("Deletion work item is no longer owned by this worker.");
    }

    private async Task<DeploymentRunEntity?> EnsureTerminalDeletionRunAsync(
        Guid? runId,
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        if (runId is null)
            return null;
        var run = await dbContext.DeploymentRuns.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == runId && x.WorkspaceId == workspaceId && x.ElsaInstanceId == instanceId,
                cancellationToken);
        if (run is null || run.Status is WorkspaceDeploymentRunStatus.Queued or
            WorkspaceDeploymentRunStatus.Running or WorkspaceDeploymentRunStatus.RecoveryRequired)
            throw Conflict("Deletion run correlation is not terminal.");
        return run;
    }

    private static ElsaInstanceDeletionResult DeletionResult(
        ElsaInstanceOperationEntity operation,
        ElsaInstanceEntity instance,
        bool replayed)
    {
        if (operation.DeletionEvidenceFingerprint is null || operation.DeletionDiagnosticCode is null)
            throw Conflict("Deletion result is incomplete.");
        return new ElsaInstanceDeletionResult(
            operation.State == ElsaInstanceOperationState.Succeeded
                ? (replayed ? ElsaInstanceDeletionOutcome.AlreadyCompleted : ElsaInstanceDeletionOutcome.Deleted)
                : ElsaInstanceDeletionOutcome.RecoveryRequired,
            MapOperation(operation), MapInstance(instance), operation.DeletionDiagnosticCode,
            operation.DeletionEvidenceFingerprint, replayed);
    }

    public async Task<ElsaInstanceLifecycleWorkerResult> CommitResolvedAsync(
        ElsaInstanceLifecycleResolutionCommit commit,
        CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        ArgumentNullException.ThrowIfNull(commit);
        commit.Validate();

        try
        {
            return await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
            {
                var operation = await dbContext.ElsaInstanceOperations
                    .SingleOrDefaultAsync(x => x.Id == commit.OperationId, cancellationToken);
                var instance = await LoadTrackedInstanceAsync(commit.InstanceId, cancellationToken);
                var outbox = await dbContext.ElsaInstanceLifecycleOutbox
                    .SingleOrDefaultAsync(x => x.Id == commit.OutboxId, cancellationToken);
                if (operation is null || instance is null || outbox is null)
                    throw Conflict("Lifecycle work item no longer exists.");
                ValidateWorkerEnvelope(commit, operation, instance, outbox);

                if (operation.State == ElsaInstanceOperationState.Queued)
                {
                    if (operation.DeploymentRunId is null)
                        throw Conflict("Lifecycle operation is queued without a deployment run.");
                    var existingRun = await dbContext.DeploymentRuns
                        .SingleOrDefaultAsync(x => x.Id == operation.DeploymentRunId, cancellationToken);
                    if (existingRun is null ||
                        existingRun.WorkspaceId != commit.WorkspaceId ||
                        existingRun.EnvironmentId != commit.DeploymentTarget.EnvironmentId ||
                        existingRun.ApplicationId != commit.DeploymentTarget.ApplicationId ||
                        existingRun.ElsaInstanceId != commit.InstanceId ||
                        existingRun.Status is not (WorkspaceDeploymentRunStatus.Queued or
                            WorkspaceDeploymentRunStatus.Running or
                            WorkspaceDeploymentRunStatus.RecoveryRequired))
                        throw Conflict("Lifecycle operation deployment run is inconsistent.");
                    return new ElsaInstanceLifecycleWorkerResult(
                        ElsaInstanceLifecycleWorkerOutcome.AlreadyCompleted,
                        MapOperation(operation),
                        MapInstance(instance),
                        MapDeploymentRun(existingRun));
                }

                EnsureLease(commit, operation, _timeProvider.GetUtcNow());
                if (instance.Version != commit.Instance.Version)
                    throw Conflict("Lifecycle instance changed while it was being resolved.");
                var environment = await dbContext.DeploymentEnvironments
                    .SingleOrDefaultAsync(x => x.Id == commit.DeploymentTarget.EnvironmentId &&
                                               x.WorkspaceId == commit.WorkspaceId &&
                                               x.ApplicationId == commit.DeploymentTarget.ApplicationId,
                        cancellationToken);
                if (environment is null || environment.ElsaInstanceId != commit.InstanceId)
                    throw Conflict("Lifecycle deployment target is not bound to the instance.");

                var activeRun = await dbContext.DeploymentRuns
                    .Where(x => x.WorkspaceId == commit.WorkspaceId &&
                                x.EnvironmentId == commit.DeploymentTarget.EnvironmentId &&
                                (x.Status == WorkspaceDeploymentRunStatus.Queued ||
                                 x.Status == WorkspaceDeploymentRunStatus.Running ||
                                 x.Status == WorkspaceDeploymentRunStatus.RecoveryRequired))
                    .OrderBy(x => x.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                if (activeRun is not null)
                    return await CompleteReservationConflictAsync(
                        operation, instance, commit.CommittedAt, cancellationToken);

                ValidateCanonicalPlan(commit.Plan);
                var existingPlan = await dbContext.ElsaInstanceResolvedPlans
                    .SingleOrDefaultAsync(x => x.WorkspaceId == commit.WorkspaceId &&
                                               x.InstanceId == commit.InstanceId &&
                                               x.PlanId == commit.Plan.Reference.PlanId,
                        cancellationToken);
                if (existingPlan is not null)
                {
                    if (existingPlan.InstanceId != commit.InstanceId ||
                        existingPlan.OrganizationId != instance.OrganizationId ||
                        existingPlan.SchemaVersion != commit.Plan.Reference.SchemaVersion ||
                        !string.Equals(existingPlan.ContentHash, commit.Plan.Reference.ContentHash, StringComparison.Ordinal) ||
                        !string.Equals(existingPlan.PlanUri, commit.Plan.Reference.PlanUri, StringComparison.Ordinal) ||
                        !string.Equals(existingPlan.SerializedPlan, commit.Plan.SerializedPlan, StringComparison.Ordinal))
                        throw Conflict("Resolved plan identity is already bound to different content.");
                }
                else
                {
                    await dbContext.ElsaInstanceResolvedPlans.AddAsync(new ElsaInstanceResolvedPlanEntity
                    {
                        Id = Guid.NewGuid(),
                        OrganizationId = instance.OrganizationId,
                        WorkspaceId = commit.WorkspaceId,
                        InstanceId = commit.InstanceId,
                        PlanId = commit.Plan.Reference.PlanId,
                        SchemaVersion = commit.Plan.Reference.SchemaVersion,
                        ContentHash = commit.Plan.Reference.ContentHash,
                        PlanUri = commit.Plan.Reference.PlanUri,
                        SerializedPlan = commit.Plan.SerializedPlan,
                        CreatedAt = commit.CommittedAt.ToUniversalTime()
                    }, cancellationToken);
                }

                var runId = Guid.NewGuid();
                var priorObservedLifecycle = (ElsaObservedLifecycle)instance.ObservedLifecycle;
                ApplyAggregate(instance, commit.Instance);
                instance.UpdatedAt = commit.CommittedAt.ToUniversalTime();
                operation.State = ElsaInstanceOperationState.Queued;
                operation.AttemptNumber = commit.Operation.AttemptNumber;
                operation.ResolvedPlanId = commit.Plan.Reference.PlanId;
                operation.DeploymentRunId = runId;
                operation.WorkerId = null;
                operation.LeaseTokenHash = null;
                operation.LeaseExpiresAt = null;
                operation.HeartbeatAt = null;
                operation.UpdatedAt = commit.CommittedAt.ToUniversalTime();

                var runEntity = new DeploymentRunEntity
                {
                    Id = runId,
                    WorkspaceId = commit.WorkspaceId,
                    ElsaInstanceId = commit.InstanceId,
                    ApplicationId = commit.DeploymentTarget.ApplicationId,
                    EnvironmentId = commit.DeploymentTarget.EnvironmentId,
                    EngineId = commit.DeploymentTarget.EngineId,
                    SourceRevisionId = commit.DeploymentTarget.SourceRevisionId,
                    Status = WorkspaceDeploymentRunStatus.Queued,
                    ValidationOutcome = DeploymentValidationOutcome.Passed,
                    ConfirmationId = commit.DeploymentTarget.ConfirmationId,
                    ActorAccountId = commit.DeploymentTarget.ActorAccountId,
                    QueuedAt = commit.CommittedAt.ToUniversalTime(),
                    CreatedAt = commit.CommittedAt.ToUniversalTime(),
                    AttemptNumber = 1
                };
                await dbContext.DeploymentRuns.AddAsync(runEntity, cancellationToken);
                await dbContext.ElsaInstanceAuditEvents.AddAsync(
                    await CreateAuditEventAsync(
                        instance,
                        operation,
                        priorObservedLifecycle,
                        commit.CommittedAt,
                        cancellationToken,
                        eventType: "lifecycle.resolved",
                        deploymentRunId: runId,
                        planReference: commit.Plan.Reference.PlanUri),
                    cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);

                return new ElsaInstanceLifecycleWorkerResult(
                    ElsaInstanceLifecycleWorkerOutcome.Queued,
                    MapOperation(operation),
                    MapInstance(instance),
                    MapDeploymentRun(runEntity));
            }, cancellationToken);
        }
        catch (ElsaInstanceLifecycleConflictException)
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            throw Conflict("Lifecycle resolution conflicted with a newer operation.");
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            dbContext.ChangeTracker.Clear();
            return await ResolveReservationRaceAsync(commit, cancellationToken);
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<ElsaInstanceCommercialGateDecision> AuthorizeProviderSubmissionAsync(
        Guid workspaceId,
        Guid instanceId,
        Guid operationId,
        DateTimeOffset authorizedAt,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty || instanceId == Guid.Empty || operationId == Guid.Empty)
            throw new ArgumentException("A complete lifecycle identity is required.");

        dbContext.ChangeTracker.Clear();
        return await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            var operation = await dbContext.ElsaInstanceOperations.SingleOrDefaultAsync(
                x => x.Id == operationId && x.WorkspaceId == workspaceId && x.InstanceId == instanceId,
                cancellationToken);
            var instance = await LoadTrackedInstanceAsync(instanceId, cancellationToken);
            if (operation is null || instance is null || instance.OrganizationId == Guid.Empty)
                throw Conflict("Lifecycle operation no longer exists.");

            if (operation.Action == ElsaInstanceOperationAction.Delete ||
                operation.State is ElsaInstanceOperationState.Succeeded or ElsaInstanceOperationState.Failed or
                ElsaInstanceOperationState.Cancelled)
            {
                return ElsaInstanceCommercialGateDecision.Allow();
            }

            var decision = await _commercialGate.EvaluateAsync(
                instance.OrganizationId,
                operation.Action,
                cancellationToken: cancellationToken);
            if (!decision.Allowed)
            {
                if (operation.State == ElsaInstanceOperationState.Queued)
                {
                    operation.State = ElsaInstanceOperationState.EntitlementHeld;
                    operation.FailureCode = decision.Code;
                    operation.FailureSummary = decision.Summary;
                    operation.UpdatedAt = authorizedAt.ToUniversalTime();
                    var run = operation.DeploymentRunId is { } runId
                        ? await dbContext.DeploymentRuns.SingleOrDefaultAsync(x => x.Id == runId, cancellationToken)
                        : null;
                    await dbContext.ElsaInstanceAuditEvents.AddAsync(
                        await CreateAuditEventAsync(instance, operation, instance.ObservedLifecycle, authorizedAt,
                            cancellationToken, eventType: "lifecycle.entitlement-held", deploymentRunId: run?.Id,
                            diagnosticCode: decision.Code, summary: decision.Summary), cancellationToken);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                return decision;
            }

            if (operation.State == ElsaInstanceOperationState.EntitlementHeld)
            {
                operation.State = ElsaInstanceOperationState.Queued;
                operation.FailureCode = null;
                operation.FailureSummary = null;
                operation.UpdatedAt = authorizedAt.ToUniversalTime();
                var run = operation.DeploymentRunId is { } runId
                    ? await dbContext.DeploymentRuns.SingleOrDefaultAsync(x => x.Id == runId, cancellationToken)
                    : null;
                await dbContext.ElsaInstanceAuditEvents.AddAsync(
                    await CreateAuditEventAsync(instance, operation, instance.ObservedLifecycle, authorizedAt,
                        cancellationToken, eventType: "lifecycle.entitlement-resumed", deploymentRunId: run?.Id,
                        diagnosticCode: "instance.entitlement-restored", summary: "Provider submission entitlement was restored."), cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return decision;
        }, cancellationToken);
    }

    public async Task CommitProviderSubmissionAsync(
        ElsaInstanceProviderSubmissionCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        commit.Validate();
        dbContext.ChangeTracker.Clear();
        await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            var operation = await dbContext.ElsaInstanceOperations.SingleOrDefaultAsync(
                x => x.WorkspaceId == commit.WorkspaceId && x.Id == commit.OperationId,
                cancellationToken);
            var instance = operation?.InstanceId is { } instanceId
                ? await LoadTrackedInstanceAsync(instanceId, cancellationToken)
                : null;
            var run = operation?.DeploymentRunId is { } runId
                ? await dbContext.DeploymentRuns.SingleOrDefaultAsync(
                    x => x.WorkspaceId == commit.WorkspaceId && x.Id == runId,
                    cancellationToken)
                : null;
            if (operation is null || instance is null || run is null ||
                operation.InstanceId != commit.InstanceId || operation.AttemptNumber != commit.AttemptNumber ||
                instance.Id != commit.InstanceId || instance.WorkspaceId != commit.WorkspaceId ||
                run.ElsaInstanceId != commit.InstanceId)
                throw Conflict("Provider submission correlation is invalid.");

            if (operation.State is ElsaInstanceOperationState.Succeeded or ElsaInstanceOperationState.Failed)
            {
                return;
            }

            if (operation.State == ElsaInstanceOperationState.RecoveryRequired &&
                run.Status == WorkspaceDeploymentRunStatus.RecoveryRequired)
            {
                // A provider call can be accepted remotely while its response is lost. A
                // successful replay upgrades the uncertain marker to an accepted hand-off;
                // subsequent polls must reconcile only and never submit again.
                if ((operation.FailureCode == "provider.submission.uncertain" ||
                     run.RecoveryReason == "provider.submission.uncertain") &&
                    commit.CorrelationId != "provider-submission-uncertain")
                {
                    operation.FailureCode = null;
                    operation.FailureSummary = null;
                    operation.UpdatedAt = commit.SubmittedAt.ToUniversalTime();
                    run.RecoveryReason = "provider.submission.accepted";
                    run.WorkerId = null;
                    run.WorkerHeartbeatAt = null;
                    if (commit.PlacementAssignmentId is not null)
                        instance.PlacementAssignmentId = commit.PlacementAssignmentId;
                    await dbContext.ElsaInstanceAuditEvents.AddAsync(await CreateAuditEventAsync(
                        instance,
                        operation,
                        instance.ObservedLifecycle,
                        commit.SubmittedAt,
                        cancellationToken,
                        "lifecycle.provider-submitted",
                        run.Id,
                        diagnosticCode: run.RecoveryReason), cancellationToken);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                return;
            }

            if (operation.State != ElsaInstanceOperationState.Queued ||
                run.Status != WorkspaceDeploymentRunStatus.Queued)
                throw Conflict("Provider submission reservation is no longer queued.");

            operation.State = ElsaInstanceOperationState.RecoveryRequired;
            operation.FailureCode = commit.CorrelationId == "provider-submission-uncertain"
                ? "provider.submission.uncertain"
                : null;
            operation.FailureSummary = null;
            operation.WorkerId = null;
            operation.LeaseTokenHash = null;
            operation.LeaseExpiresAt = null;
            operation.HeartbeatAt = null;
            operation.UpdatedAt = commit.SubmittedAt.ToUniversalTime();
            run.Status = WorkspaceDeploymentRunStatus.RecoveryRequired;
            run.RecoveryReason = commit.CorrelationId == "provider-submission-uncertain"
                ? "provider.submission.uncertain"
                : "provider.submission.accepted";
            run.WorkerId = null;
            run.WorkerHeartbeatAt = null;
            if (commit.PlacementAssignmentId is not null)
                instance.PlacementAssignmentId = commit.PlacementAssignmentId;
            await dbContext.ElsaInstanceAuditEvents.AddAsync(await CreateAuditEventAsync(
                instance,
                operation,
                instance.ObservedLifecycle,
                commit.SubmittedAt,
                cancellationToken,
                "lifecycle.provider-submitted",
                run.Id,
                diagnosticCode: run.RecoveryReason), cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ElsaInstanceProviderPendingOperation>> ListPendingProviderOperationsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(limit));
        dbContext.ChangeTracker.Clear();
        var operationEntities = await dbContext.ElsaInstanceOperations
            .AsNoTracking()
            .Where(operation => operation.InstanceId != null &&
                                (operation.State == ElsaInstanceOperationState.Queued ||
                                 operation.State == ElsaInstanceOperationState.EntitlementHeld ||
                                 operation.State == ElsaInstanceOperationState.RecoveryRequired) &&
                                operation.DeploymentRunId != null &&
                                dbContext.DeploymentRuns.Any(run =>
                                    run.Id == operation.DeploymentRunId &&
                                    run.WorkspaceId == operation.WorkspaceId &&
                                    run.ElsaInstanceId == operation.InstanceId &&
                                    (run.Status == WorkspaceDeploymentRunStatus.Queued ||
                                     run.Status == WorkspaceDeploymentRunStatus.RecoveryRequired)))
            .OrderBy(operation => operation.UpdatedAt)
            .ThenBy(operation => operation.Id)
            .Take(limit)
            .ToArrayAsync(cancellationToken);

        var replayCandidates = operationEntities
            .Where(operation => operation.State == ElsaInstanceOperationState.Queued ||
                               operation.State == ElsaInstanceOperationState.EntitlementHeld ||
                               operation.State == ElsaInstanceOperationState.RecoveryRequired &&
                               string.Equals(operation.FailureCode, "provider.submission.uncertain", StringComparison.Ordinal))
            .Where(operation => operation.InstanceId is not null && operation.DeploymentRunId is not null &&
                                operation.ResolvedPlanId is not null)
            .ToArray();
        var instanceIds = replayCandidates.Select(operation => operation.InstanceId!.Value).Distinct().ToArray();
        var runIds = replayCandidates.Select(operation => operation.DeploymentRunId!.Value).Distinct().ToArray();
        var planIds = replayCandidates.Select(operation => operation.ResolvedPlanId!).Distinct(StringComparer.Ordinal).ToArray();
        var recoveryOperationIds = replayCandidates.Select(operation => operation.Id).Distinct().ToArray();
        var instances = instanceIds.Length == 0
            ? new Dictionary<Guid, ElsaInstanceEntity>()
            : await dbContext.ElsaInstances.AsNoTracking()
                .Where(instance => instanceIds.Contains(instance.Id))
                .ToDictionaryAsync(instance => instance.Id, cancellationToken);
        var runs = runIds.Length == 0
            ? new Dictionary<Guid, DeploymentRunEntity>()
            : await dbContext.DeploymentRuns.AsNoTracking()
                .Where(run => runIds.Contains(run.Id))
                .ToDictionaryAsync(run => run.Id, cancellationToken);
        var plans = instanceIds.Length == 0 || planIds.Length == 0
            ? new Dictionary<(Guid InstanceId, string PlanId), ElsaInstanceResolvedPlanEntity>()
            : (await dbContext.ElsaInstanceResolvedPlans.AsNoTracking()
                .Where(plan => instanceIds.Contains(plan.InstanceId) && planIds.Contains(plan.PlanId))
                .ToArrayAsync(cancellationToken))
                .ToDictionary(plan => (plan.InstanceId, plan.PlanId));
        var recoveries = recoveryOperationIds.Length == 0
            ? new Dictionary<Guid, ElsaInstanceRecoveryRequestEntity>()
            : (await dbContext.ElsaInstanceRecoveryRequests.AsNoTracking()
                .Where(recovery => recoveryOperationIds.Contains(recovery.OperationId))
                .OrderByDescending(recovery => recovery.AcceptedAt)
                .ThenByDescending(recovery => recovery.CreatedAt)
                .ToArrayAsync(cancellationToken))
                .GroupBy(recovery => recovery.OperationId)
                .ToDictionary(group => group.Key, group => group.First());

        var pending = new List<ElsaInstanceProviderPendingOperation>(operationEntities.Length);
        foreach (var operation in operationEntities)
        {
            var shouldReplaySubmission = operation.State == ElsaInstanceOperationState.Queued ||
                operation.State == ElsaInstanceOperationState.EntitlementHeld ||
                operation.State == ElsaInstanceOperationState.RecoveryRequired &&
                string.Equals(operation.FailureCode, "provider.submission.uncertain", StringComparison.Ordinal);
            if (!shouldReplaySubmission)
            {
                pending.Add(new(operation.WorkspaceId, operation.Id));
                continue;
            }

            if (operation.InstanceId is not { } instanceId || operation.DeploymentRunId is not { } runId ||
                operation.ResolvedPlanId is null)
            {
                pending.Add(new(operation.WorkspaceId, operation.Id) { HandoffInvalid = true });
                continue;
            }

            // The worker may have crashed after the provider operation was
            // submitted but before the hand-off marker was committed. Rebuild
            // the typed submission from the immutable plan/run records so the
            // next poll can safely replay the same provider idempotency key.
            instances.TryGetValue(instanceId, out var instance);
            runs.TryGetValue(runId, out var run);
            plans.TryGetValue((instanceId, operation.ResolvedPlanId), out var plan);

            if (instance is null || instance.WorkspaceId != operation.WorkspaceId ||
                run is null || run.WorkspaceId != operation.WorkspaceId || run.ElsaInstanceId != instanceId ||
                plan is null || plan.WorkspaceId != operation.WorkspaceId ||
                !TryRestoreResolvedPlan(plan, operation.ResolvedPlanId, out var resolvedPlan))
            {
                pending.Add(new(operation.WorkspaceId, operation.Id) { HandoffInvalid = true });
                continue;
            }

            var target = new ElsaInstanceLifecycleDeploymentTarget(
                run.ApplicationId,
                run.EnvironmentId,
                run.EngineId,
                run.SourceRevisionId,
                run.ConfirmationId,
                run.ActorAccountId);
            try
            {
                target.Validate();
                var candidate = new ElsaInstanceProviderSubmission(
                    operation.WorkspaceId,
                    instanceId,
                    operation.Id,
                    operation.AttemptNumber,
                    (ElsaDesiredLifecycle)instance.DesiredLifecycle,
                    resolvedPlan,
                    target,
                    instance.RegionCode,
                    instance.OrganizationId,
                    (ElsaInstanceOperationAction)operation.Action,
                    instance.PlacementAssignmentId ?? operation.Id.ToString("D"));
                candidate.Validate();
                ElsaInstanceProviderRecoveryEnvelope? recovery = null;
                if (operation.AttemptNumber > 1 &&
                    recoveries.TryGetValue(operation.Id, out var recoveryEntity) &&
                    (recoveryEntity.RecoveryObservationReference is not null ||
                     recoveryEntity.RecoveryObservationDigest is not null ||
                     recoveryEntity.ObservedLifecycleAttemptNumber is not null ||
                     recoveryEntity.ObservedInstanceVersion is not null))
                {
                    // Partial metadata is corrupt recovery authority, not permission
                    // to downgrade the accepted operation into ordinary submission.
                    recovery = new(
                        recoveryEntity.Id,
                        recoveryEntity.OrganizationId,
                        recoveryEntity.WorkspaceId,
                        recoveryEntity.InstanceId,
                        recoveryEntity.OperationId,
                        recoveryEntity.ObservedLifecycleAttemptNumber ?? 0,
                        recoveryEntity.ObservedInstanceVersion ?? 0,
                        recoveryEntity.AttemptNumber,
                        instance.Version,
                        recoveryEntity.IdempotencyScope,
                        recoveryEntity.IdempotencyKey,
                        recoveryEntity.RequestHash,
                        recoveryEntity.RecoveryObservationReference ?? "",
                        recoveryEntity.RecoveryObservationDigest ?? "");
                    recovery.Validate();
                }
                pending.Add(new(operation.WorkspaceId, operation.Id, candidate, recovery));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                pending.Add(new(operation.WorkspaceId, operation.Id) { HandoffInvalid = true });
            }
        }

        return pending;
    }

    private static bool TryRestoreResolvedPlan(
        ElsaInstanceResolvedPlanEntity entity,
        string expectedPlanId,
        out ResolvedElsaApplicationPlan plan)
    {
        plan = null!;
        try
        {
            var restored = ResolvedElsaApplicationPlanSerialization.Deserialize(entity.SerializedPlan);
            if (!string.Equals(restored.SchemaVersion, entity.SchemaVersion.ToString(), StringComparison.Ordinal) ||
                !string.Equals(ResolvedElsaApplicationPlanSerialization.ComputeContentHash(restored), entity.ContentHash,
                    StringComparison.Ordinal) ||
                ResolvedElsaApplicationPlanValidator.Validate(restored).Count != 0 ||
                !string.Equals(expectedPlanId, entity.PlanId, StringComparison.Ordinal))
                return false;
            plan = restored;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    public async Task<ElsaInstanceLifecycleWorkerResult> FailResolutionAsync(
        ElsaInstanceLifecycleResolutionFailure failure,
        CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        ArgumentNullException.ThrowIfNull(failure);
        failure.Validate();

        try
        {
            return await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
            {
                var operation = await dbContext.ElsaInstanceOperations
                    .SingleOrDefaultAsync(x => x.Id == failure.OperationId, cancellationToken);
                var instance = await LoadTrackedInstanceAsync(failure.InstanceId, cancellationToken);
                var outbox = await dbContext.ElsaInstanceLifecycleOutbox
                    .SingleOrDefaultAsync(x => x.Id == failure.OutboxId, cancellationToken);
                if (operation is null || instance is null || outbox is null)
                    throw Conflict("Lifecycle work item no longer exists.");
                ValidateWorkerEnvelope(failure, operation, instance, outbox);
                if (operation.State == ElsaInstanceOperationState.Failed &&
                    string.Equals(operation.FailureCode, failure.Code, StringComparison.Ordinal))
                {
                    return new ElsaInstanceLifecycleWorkerResult(
                        ElsaInstanceLifecycleWorkerOutcome.AlreadyCompleted,
                        MapOperation(operation),
                        MapInstance(instance),
                        FailureCode: operation.FailureCode,
                        FailureSummary: operation.FailureSummary);
                }

                EnsureLease(failure, operation, _timeProvider.GetUtcNow());
                var priorObservedLifecycle = instance.ObservedLifecycle;
                operation.State = ElsaInstanceOperationState.Failed;
                operation.FailureCode = failure.Code;
                operation.FailureSummary = failure.Summary;
                operation.CompletedAt = failure.FailedAt.ToUniversalTime();
                operation.WorkerId = null;
                operation.LeaseTokenHash = null;
                operation.LeaseExpiresAt = null;
                operation.HeartbeatAt = null;
                operation.UpdatedAt = failure.FailedAt.ToUniversalTime();
                await dbContext.ElsaInstanceAuditEvents.AddAsync(
                    await CreateAuditEventAsync(
                        instance,
                        operation,
                        priorObservedLifecycle,
                        failure.FailedAt,
                        cancellationToken,
                        eventType: "lifecycle.failed"),
                    cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                return new ElsaInstanceLifecycleWorkerResult(
                    ElsaInstanceLifecycleWorkerOutcome.Failed,
                    MapOperation(operation),
                    MapInstance(instance),
                    FailureCode: failure.Code,
                    FailureSummary: failure.Summary);
            }, cancellationToken);
        }
        catch (ElsaInstanceLifecycleConflictException)
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            throw Conflict("Lifecycle resolution conflicted with a newer operation.");
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            dbContext.ChangeTracker.Clear();
            throw Conflict("Lifecycle resolution could not be finalized safely.");
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<ElsaInstanceLifecycleAcceptance> CompleteExistingOperationAsync(
        ElsaInstance? expectedInstance,
        ElsaInstance requestedInstance,
        ElsaInstanceOperation requestedOperation,
        ElsaInstanceOperationEntity existingOperation,
        ElsaInstanceEntity? existingInstance,
        ElsaInstanceLifecycleOutboxEntity? existingOutbox,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        if (existingInstance is null || existingOutbox is null)
            throw Conflict("Lifecycle operation outbox record is missing.");

        ElsaInstanceRecoveryRequestEntity? recovery = null;
        if (requestedOperation.RecoveryIdempotencyKey is not null)
        {
            recovery = await dbContext.ElsaInstanceRecoveryRequests.SingleOrDefaultAsync(
                x => x.WorkspaceId == requestedInstance.WorkspaceId &&
                     x.IdempotencyScope == requestedOperation.RecoveryIdempotencyScope &&
                     x.IdempotencyKey == requestedOperation.RecoveryIdempotencyKey,
                cancellationToken);
            if (recovery is not null)
            {
                if (!IsExactAuthoritativeRecoveryReplay(
                        expectedInstance, requestedInstance, requestedOperation, existingOperation, recovery))
                    throw Conflict("Recovery request conflicts with the accepted recovery request.",
                        ElsaInstanceLifecycleConflictReason.IdempotencyConflict);
                return Replay(existingInstance, existingOperation, existingOutbox, recovery);
            }
        }

        if (existingOperation.State == requestedOperation.State &&
            existingOperation.AttemptNumber == requestedOperation.AttemptNumber)
        {
            if (requestedOperation.RecoveryIdempotencyKey is not null)
                throw Conflict("Recovery request conflicts with the accepted recovery request.",
                    ElsaInstanceLifecycleConflictReason.IdempotencyConflict);
            return Replay(existingInstance, existingOperation, existingOutbox);
        }

        if (expectedInstance is null)
            throw Conflict("Lifecycle operation state transition is not valid.");
        ValidateExpectedInstance(expectedInstance, requestedInstance, existingInstance);

        var canTransition = ElsaInstanceOperation.CanTransition(existingOperation.State, requestedOperation.State);
        var isRecoveryResume = existingOperation.State == ElsaInstanceOperationState.RecoveryRequired &&
            requestedOperation.State == ElsaInstanceOperationState.Queued &&
            requestedOperation.AttemptNumber == existingOperation.AttemptNumber + 1;
        if (isRecoveryResume && existingOperation.Action != ElsaInstanceOperationAction.Delete &&
            (!string.Equals(existingOperation.FailureCode,
                 ElsaInstanceProviderReconciliationService.RetrySafeCode, StringComparison.Ordinal) ||
             existingOperation.ReconciliationRetryEvidenceReference is null ||
             existingOperation.ReconciliationRetryEvidenceDigest is null))
            throw Conflict("Provider reconciliation has not established that retry is safe.");
        if ((!canTransition && !isRecoveryResume) || requestedOperation.AttemptNumber < existingOperation.AttemptNumber)
            throw Conflict("Lifecycle operation state transition is not valid.");

        // Keep the observation's pre-Recover tuple before mutating the operation.
        // The provider observation store revalidates this exact snapshot inside
        // the acceptance transaction; comparing it after the attempt increment
        // would either accept stale evidence or reject the legitimate resume.
        var observedLifecycleAttemptNumber = existingOperation.AttemptNumber;
        var observedInstanceVersion = existingInstance.Version;
        var hasAzureRecoveryObservation = isRecoveryResume &&
            existingOperation.Action != ElsaInstanceOperationAction.Delete &&
            await RequiresAzureRecoveryObservationAsync(existingInstance, existingOperation, cancellationToken);
        if (hasAzureRecoveryObservation)
        {
            if (_recoveryObservationStore is null)
                throw Conflict("Provider reconciliation retry observation storage is unavailable.");
            if (existingOperation.ReconciliationRetryEvidenceReference is null ||
                existingOperation.ReconciliationRetryEvidenceDigest is null ||
                !ElsaInstanceProviderRecoveryObservationReference.TryParse(
                    existingOperation.ReconciliationRetryEvidenceReference,
                    out _, out _))
                throw Conflict("Provider reconciliation has not established an opaque retry observation.");

            var observation = await _recoveryObservationStore.GetAndValidateRecordedAsync(
                existingOperation.OrganizationId,
                existingOperation.WorkspaceId,
                existingInstance.Id,
                existingOperation.Id,
                observedLifecycleAttemptNumber,
                existingOperation.ReconciliationRetryEvidenceReference,
                existingOperation.ReconciliationRetryEvidenceDigest,
                cancellationToken);
            var observationVersionIsThePriorReconciliationRead = observation is not null &&
                observation.ObservedInstanceVersion == observedInstanceVersion - 1 &&
                existingOperation.ReconciledInstanceVersion == observedInstanceVersion;
            if (!observationVersionIsThePriorReconciliationRead)
                throw Conflict("Provider reconciliation retry observation is stale.");
        }

        // Outbox rows are immutable and unique per operation. Recovery resumes the
        // existing durable work item instead of appending a second row for it.
        var priorObservedLifecycle = existingInstance.ObservedLifecycle;
        ApplyAggregate(existingInstance, requestedInstance);
        existingInstance.UpdatedAt = requestedAt.ToUniversalTime();
        existingOperation.State = requestedOperation.State;
        existingOperation.AttemptNumber = requestedOperation.AttemptNumber;
        existingOperation.RecoveryIdempotencyScope = requestedOperation.RecoveryIdempotencyScope;
        existingOperation.RecoveryIdempotencyKey = requestedOperation.RecoveryIdempotencyKey;
        existingOperation.RecoveryRequestHash = requestedOperation.RecoveryRequestHash;
        if (isRecoveryResume)
        {
            var deleteAuthority = existingOperation.Action == ElsaInstanceOperationAction.Delete
                ? await CaptureAzureDeleteRecoveryAuthorityAsync(
                    existingInstance, existingOperation, requestedOperation.AttemptNumber,
                    checked(observedInstanceVersion + 1), requestedAt, cancellationToken)
                : null;
            recovery = new ElsaInstanceRecoveryRequestEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = existingOperation.OrganizationId,
                WorkspaceId = existingOperation.WorkspaceId,
                InstanceId = existingInstance.Id,
                OperationId = existingOperation.Id,
                AttemptNumber = requestedOperation.AttemptNumber,
                IdempotencyScope = requestedOperation.RecoveryIdempotencyScope!,
                IdempotencyKey = requestedOperation.RecoveryIdempotencyKey!,
                RequestHash = requestedOperation.RecoveryRequestHash!,
                RecoveryObservationReference = hasAzureRecoveryObservation ? existingOperation.ReconciliationRetryEvidenceReference : null,
                RecoveryObservationDigest = hasAzureRecoveryObservation ? existingOperation.ReconciliationRetryEvidenceDigest : null,
                ObservedLifecycleAttemptNumber = hasAzureRecoveryObservation ? observedLifecycleAttemptNumber : null,
                ObservedInstanceVersion = hasAzureRecoveryObservation ? observedInstanceVersion : null,
                AzureDeleteRecoveryAuthority = deleteAuthority?.Serialize(),
                AcceptedAt = requestedAt.ToUniversalTime(),
                CreatedAt = requestedAt.ToUniversalTime()
            };
            await dbContext.ElsaInstanceRecoveryRequests.AddAsync(recovery, cancellationToken);
            existingOperation.FailureCode = null;
            existingOperation.FailureSummary = null;
            if (existingOperation.DeploymentRunId is { } deploymentRunId)
            {
                var run = await dbContext.DeploymentRuns
                    .Include(x => x.Environment)
                    .SingleOrDefaultAsync(x => x.Id == deploymentRunId &&
                        x.WorkspaceId == existingOperation.WorkspaceId &&
                        x.ElsaInstanceId == existingOperation.InstanceId, cancellationToken);
                if (run is null)
                    throw Conflict("Managed lifecycle recovery run is missing.");
                if (run.Status != WorkspaceDeploymentRunStatus.RecoveryRequired)
                    throw Conflict("Managed lifecycle recovery run is not awaiting recovery.");
                if (run.Environment is null)
                    throw Conflict("Managed lifecycle recovery environment is missing.");

                run.Status = WorkspaceDeploymentRunStatus.Queued;
                run.QueuedAt = requestedAt.ToUniversalTime();
                run.StartedAt = null;
                run.CompletedAt = null;
                run.WorkerId = null;
                run.WorkerHeartbeatAt = null;
                run.RecoveryReason = null;
                run.FailureMessage = null;
                run.Environment.UpdatedAt = requestedAt.ToUniversalTime();
                run.Environment.DeploymentStatus = DeploymentStatus.Running;
                await dbContext.DeploymentRunHistoryEvents.AddAsync(new()
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = run.WorkspaceId,
                    RunId = run.Id,
                    Status = WorkspaceDeploymentRunStatus.Queued,
                    Message = "Deployment run requeued after provider reconciliation.",
                    CreatedAt = requestedAt.ToUniversalTime()
                }, cancellationToken);
            }
        }
        existingOperation.UpdatedAt = requestedAt.ToUniversalTime();
        await dbContext.ElsaInstanceAuditEvents.AddAsync(
            await CreateAuditEventAsync(
                existingInstance,
                existingOperation,
                priorObservedLifecycle,
                requestedAt,
                cancellationToken,
                eventType: "lifecycle.operation-updated"),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ElsaInstanceLifecycleAcceptance(
            MapInstance(existingInstance),
            recovery is null ? MapOperation(existingOperation) : MapOperation(existingOperation, recovery),
            MapOutbox(existingOutbox),
            Replayed: false);
    }

    private async Task<AzureProviderDeleteRecoveryAuthority?> CaptureAzureDeleteRecoveryAuthorityAsync(
        ElsaInstanceEntity instance,
        ElsaInstanceOperationEntity lifecycleOperation,
        int acceptedLifecycleAttemptNumber,
        int acceptedInstanceVersion,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken)
    {
        // Opaque non-Azure placements remain provider-owned. Retained Azure provenance must
        // never be downgraded to that path when its assignment reference is malformed.
        if (string.IsNullOrWhiteSpace(instance.PlacementAssignmentId))
            return null;
        if (lifecycleOperation.Action != ElsaInstanceOperationAction.Delete ||
            lifecycleOperation.State != ElsaInstanceOperationState.Queued ||
            instance.DesiredLifecycle != ElsaDesiredLifecycle.Deleting)
            throw Conflict("Azure delete recovery lifecycle authority is unavailable.");
        if (!Guid.TryParseExact(instance.PlacementAssignmentId, "D", out var assignmentId))
        {
            var hasAzureProvenance = await dbContext.AzureProviderResourceAssignments.AsNoTracking()
                .AnyAsync(x => x.InstanceId == instance.Id, cancellationToken) ||
                await dbContext.AzureProviderOperations.AsNoTracking()
                    .AnyAsync(x => x.InstanceId == instance.Id, cancellationToken);
            if (hasAzureProvenance)
                throw Conflict("Azure delete recovery assignment authority is invalid.");
            return null;
        }

        var assignment = await dbContext.AzureProviderResourceAssignments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == assignmentId &&
                                       x.OrganizationId == lifecycleOperation.OrganizationId &&
                                       x.WorkspaceId == lifecycleOperation.WorkspaceId &&
                                       x.InstanceId == instance.Id,
                cancellationToken);
        if (assignment is null || assignment.LastOperationId is not { } providerOperationId)
            throw Conflict("Azure delete recovery assignment authority is unavailable.");

        var providerOperation = await dbContext.AzureProviderOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == providerOperationId &&
                                       x.WorkspaceId == lifecycleOperation.WorkspaceId,
                cancellationToken);
        if (providerOperation is null ||
            providerOperation.Action != AzureProviderOperationAction.Delete ||
            providerOperation.OrganizationId != lifecycleOperation.OrganizationId ||
            providerOperation.InstanceId != instance.Id ||
            providerOperation.LifecycleAction != ElsaInstanceOperationAction.Delete ||
            providerOperation.ProviderAssignmentId != assignment.Id ||
            !string.Equals(providerOperation.TargetKey, assignment.WorkloadName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(NormalizeProviderScope(providerOperation.ProviderScopeFingerprint),
                NormalizeProviderScope(assignment.ProviderScopeFingerprint), StringComparison.Ordinal) ||
            !AzureProviderOperationValidation.IsLifecycleDeleteIdempotencyKey(
                providerOperation.IdempotencyKey, lifecycleOperation.Id))
            throw Conflict("Azure delete recovery provider authority is unavailable.");

        // A terminal provider delete has no uncertain remote action to replay. Preserve the
        // existing cleanup/finalization paths for that known outcome, while never downgrading a
        // malformed or still-active Azure binding to ordinary cleanup.
        if (providerOperation.Status == AzureProviderOperationStatus.Succeeded)
        {
            if (assignment.State != AzureProviderAssignmentState.Deleted)
                throw Conflict("Azure delete recovery assignment authority is unavailable.");
            return null;
        }
        if (providerOperation.Status is AzureProviderOperationStatus.Failed or AzureProviderOperationStatus.Cancelled)
        {
            if (assignment.State == AzureProviderAssignmentState.Deleted)
                throw Conflict("Azure delete recovery assignment authority is unavailable.");
            return null;
        }
        var verifiedCleanupFinalization =
            AzureProviderOperationStore.IsVerifiedCleanupEligible(providerOperation, assignment);
        if (providerOperation.Status != AzureProviderOperationStatus.RecoveryRequired ||
            !verifiedCleanupFinalization &&
            (assignment.State == AzureProviderAssignmentState.Deleted ||
             providerOperation.Phase != AzureProviderOperationPhase.CleanupSubmitted ||
             providerOperation.AttemptedStep != AzureProviderRunnerStep.Cleanup) ||
            providerOperation.AttemptNumber < 1 || providerOperation.CheckpointSequence < 1 ||
            providerOperation.LeaseExpiresAt is { } providerLeaseExpires &&
            providerLeaseExpires > acceptedAt.ToUniversalTime())
            throw Conflict("Azure delete recovery provider authority is unavailable.");

        try
        {
            var authority = new AzureProviderDeleteRecoveryAuthority(
                providerOperation.Id,
                assignment.Id,
                acceptedLifecycleAttemptNumber,
                // CatalogDbContext advances the aggregate version on SaveChanges. Bind the
                // ledger to that committed version, not the pre-save tracked snapshot.
                acceptedInstanceVersion,
                providerOperation.AttemptNumber,
                providerOperation.Version,
                providerOperation.CheckpointSequence,
                providerOperation.OperationIdentity,
                providerOperation.RequestHash,
                providerOperation.TargetKey,
                NormalizeProviderScope(providerOperation.ProviderScopeFingerprint)!,
                providerOperation.PlanFingerprint,
                providerOperation.TemplateFingerprint);
            authority.Validate();
            return authority;
        }
        catch (ArgumentException)
        {
            throw Conflict("Azure delete recovery provider authority is invalid.");
        }
    }

    private static string? NormalizeProviderScope(string? value) => value?.Trim().ToLowerInvariant();

    private static ElsaInstanceLifecycleAcceptance Replay(
        ElsaInstanceEntity? instance,
        ElsaInstanceOperationEntity operation,
        ElsaInstanceLifecycleOutboxEntity? outbox,
        ElsaInstanceRecoveryRequestEntity? recovery = null)
    {
        if (instance is null || outbox is null)
            throw Conflict("Lifecycle operation outbox record is missing.");
        ValidateReplayEnvelope(instance, operation, outbox);
        return new ElsaInstanceLifecycleAcceptance(
            MapInstance(instance),
            recovery is null ? MapOperation(operation) : MapOperation(operation, recovery),
            MapOutbox(outbox),
            Replayed: true);
    }

    private static void ValidateReplayEnvelope(
        ElsaInstanceEntity instance,
        ElsaInstanceOperationEntity operation,
        ElsaInstanceLifecycleOutboxEntity outbox)
    {
        if (operation.InstanceId is null ||
            operation.InstanceId != instance.Id ||
            outbox.OperationId != operation.Id ||
            outbox.InstanceId != instance.Id ||
            outbox.WorkspaceId != operation.WorkspaceId ||
            outbox.Action != operation.Action ||
            !string.Equals(outbox.RequestHash, operation.RequestHash, StringComparison.Ordinal) ||
            operation.OrganizationId != instance.OrganizationId ||
            operation.WorkspaceId != instance.WorkspaceId)
            throw Conflict("Lifecycle operation outbox record is inconsistent.");
    }

    private async Task<ElsaInstanceEntity?> LoadTrackedInstanceAsync(
        Guid? instanceId,
        CancellationToken cancellationToken)
    {
        if (instanceId is null || instanceId == Guid.Empty)
            return null;
        return await dbContext.ElsaInstances
            .Include(x => x.IdentityBinding)
            .SingleOrDefaultAsync(x => x.Id == instanceId, cancellationToken);
    }

    private static void SynchronizeIdentityBinding(ElsaInstanceEntity instance, DateTimeOffset changedAt)
    {
        if (instance.CurrentDeploymentEndpointUri is null)
            return;
        if (!ElsaManagedEndpointOrigin.TryCreate(instance.CurrentDeploymentEndpointUri, out var endpointOrigin))
            throw new InvalidOperationException("Managed deployment endpoint origin is invalid.");

        var verifiedOrigin = endpointOrigin.Value;
        if (instance.IdentityBinding is null)
        {
            var created = ElsaInstanceIdentityBinding.Create(instance.Id, verifiedOrigin, changedAt);
            instance.IdentityBinding = new ElsaInstanceIdentityBindingEntity
            {
                InstanceId = instance.Id,
                Audience = created.Audience,
                CanonicalCallbackUri = created.CanonicalCallbackUri,
                VerifiedEndpointOrigin = created.VerifiedEndpointOrigin,
                BindingVersion = created.BindingVersion,
                ChangedAt = created.ChangedAt
            };
            return;
        }

        var persisted = instance.IdentityBinding;
        var current = ElsaInstanceIdentityBinding.Hydrate(
            instance.Id, persisted.VerifiedEndpointOrigin, persisted.BindingVersion, persisted.ChangedAt);
        if (string.Equals(current.VerifiedEndpointOrigin, verifiedOrigin, StringComparison.Ordinal))
            return;

        var rotationTime = changedAt <= current.ChangedAt ? current.ChangedAt.AddTicks(1) : changedAt;
        var rotated = current.Rotate(verifiedOrigin, rotationTime);
        persisted.Audience = rotated.Audience;
        persisted.CanonicalCallbackUri = rotated.CanonicalCallbackUri;
        persisted.VerifiedEndpointOrigin = rotated.VerifiedEndpointOrigin;
        persisted.BindingVersion = rotated.BindingVersion;
        persisted.ChangedAt = rotated.ChangedAt;
    }

    private async Task AddIntentRevisionIfNeededAsync(
        ElsaInstanceEntity entity,
        ElsaInstance instance,
        DateTimeOffset authoredAt,
        CancellationToken cancellationToken)
    {
        var contentHash = instance.ComputeCanonicalIntentHash();
        var latest = await dbContext.ElsaInstanceIntentRevisions
            .Where(x => x.InstanceId == instance.Id)
            .OrderByDescending(x => x.RevisionNumber)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is not null && string.Equals(latest.ContentHash, contentHash, StringComparison.Ordinal))
        {
            entity.DesiredStateRevisionId = latest.Id.ToString("D");
            return;
        }

        var revision = ToEntity(instance.Intent, instance, authoredAt, latest?.RevisionNumber + 1 ?? 1);
        await dbContext.ElsaInstanceIntentRevisions.AddAsync(revision, cancellationToken);
        entity.DesiredStateRevisionId = revision.Id.ToString("D");
    }

    private async Task<bool> RequiresAzureRecoveryObservationAsync(
        ElsaInstanceEntity instance,
        ElsaInstanceOperationEntity operation,
        CancellationToken cancellationToken)
    {
        if (ElsaInstanceProviderRecoveryObservationReference.TryParse(
                operation.ReconciliationRetryEvidenceReference, out _, out _))
            return true;

        // Select from retained authority, not caller-controlled evidence syntax:
        // changing an Azure observation to an HTTPS reference cannot downgrade validation.
        var providerKey = $"elsa-instance-operation:{operation.Id:D}";
        if (await dbContext.AzureProviderOperations.AsNoTracking().AnyAsync(
                x => x.WorkspaceId == operation.WorkspaceId && x.IdempotencyKey == providerKey,
                cancellationToken))
            return true;

        return Guid.TryParse(instance.PlacementAssignmentId, out var assignmentId) &&
            await dbContext.AzureProviderResourceAssignments.AsNoTracking().AnyAsync(
                x => x.Id == assignmentId && x.WorkspaceId == instance.WorkspaceId,
                cancellationToken);
    }

    private async Task ValidateAndStageDeleteConfirmationAsync(
        ElsaInstanceDeleteConfirmationRequirement? requirement,
        ElsaInstance instance,
        ElsaInstanceOperation operation,
        DateTimeOffset consumedAt,
        CancellationToken cancellationToken)
    {
        if (requirement is null)
            return;
        if (operation.Action != ElsaInstanceOperationAction.Delete ||
            requirement.ConfirmationId == Guid.Empty || requirement.AccountId == Guid.Empty)
            throw new ElsaInstanceDeleteConfirmationException();

        var consumedAtUtc = consumedAt.ToUniversalTime();
        var affected = await dbContext.ActionConfirmations
            .Where(x => x.WorkspaceId == instance.WorkspaceId &&
                        x.Id == requirement.ConfirmationId &&
                        x.ActionType == ConfirmationActionType.DeleteManagedInstance &&
                        x.ConfirmedByAccountId == requirement.AccountId &&
                        x.UsedAt == null &&
                        x.ExpiresAt > consumedAt &&
                        x.TargetId == instance.Id.ToString("D"))
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.UsedAt, consumedAtUtc),
                cancellationToken);
        if (affected == 0)
            throw new ElsaInstanceDeleteConfirmationException();
    }

    private async Task<ElsaInstanceAuditEventEntity> CreateAuditEventAsync(
        ElsaInstanceEntity instance,
        ElsaInstanceOperationEntity operation,
        ElsaObservedLifecycle? priorObservedLifecycle,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken,
        string eventType = "lifecycle.accepted",
        Guid? deploymentRunId = null,
        string? planReference = null,
        string? diagnosticCode = null,
        string? summary = null,
        Guid? actorAccountId = null)
    {
        var lastSequence = await dbContext.ElsaInstanceAuditEvents
            .Where(x => x.InstanceId == instance.Id)
            .Select(x => (long?)x.Sequence)
            .MaxAsync(cancellationToken) ?? 0;
        return new ElsaInstanceAuditEventEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = instance.OrganizationId,
            WorkspaceId = instance.WorkspaceId,
            InstanceId = instance.Id,
            Sequence = checked(lastSequence + 1),
            EventType = eventType,
            ActorAccountId = actorAccountId,
            OperationId = operation.Id,
            DeploymentRunId = deploymentRunId,
            PriorState = priorObservedLifecycle?.ToString(),
            NewState = instance.ObservedLifecycle.ToString(),
            DesiredStateRevisionId = instance.DesiredStateRevisionId,
            PlanReference = planReference,
            DiagnosticCode = diagnosticCode,
            Summary = summary,
            RequestKeyHash = HashIdempotencyKey(operation.IdempotencyKey),
            OccurredAt = occurredAt.ToUniversalTime()
        };
    }

    private static ElsaInstanceProviderReconciliationResult ReconciliationResult(
        ElsaInstanceOperationEntity operation,
        bool replayed)
    {
        if (operation.InstanceId is null || operation.ReconciliationDiagnosticCode is null ||
            operation.ReconciledObservedLifecycle is null || operation.ReconciledHealth is null ||
            operation.ReconciledInstanceVersion is null || operation.ReconciledAt is null)
            throw Conflict("Provider reconciliation result is incomplete.");
        var diagnosticCode = operation.ReconciliationDiagnosticCode;
        var outcome = operation.State switch
        {
            ElsaInstanceOperationState.Succeeded => ElsaInstanceProviderReconciliationOutcome.Converged,
            ElsaInstanceOperationState.Failed when diagnosticCode is
                ElsaInstanceProviderReconciliationService.HealthFailedCode or
                ElsaInstanceProviderReconciliationService.HealthUnknownCode =>
                ElsaInstanceProviderReconciliationOutcome.HealthGateFailed,
            ElsaInstanceOperationState.Failed => ElsaInstanceProviderReconciliationOutcome.Failed,
            _ => ElsaInstanceProviderReconciliationOutcome.RecoveryRequired
        };
        var projection = new ElsaInstanceProviderReconciliationProjection(
            operation.WorkspaceId, operation.InstanceId.Value, operation.Id, operation.AttemptNumber,
            operation.ReconciledObservedLifecycle.Value, operation.ReconciledHealth.Value,
            operation.ReconciledInstanceVersion.Value, operation.State);
        return new(outcome, projection, diagnosticCode,
            operation.State == ElsaInstanceOperationState.RecoveryRequired &&
            string.Equals(operation.FailureCode, ElsaInstanceProviderReconciliationService.RetrySafeCode,
                StringComparison.Ordinal) &&
            operation.ReconciliationRetryEvidenceReference is not null &&
            operation.ReconciliationRetryEvidenceDigest is not null,
            replayed, operation.ReconciledAt.Value.ToUniversalTime());
    }

    private static string? HashReason(string? reason) =>
        reason is null
            ? null
            : "reason.sha256." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reason)));

    private static void ValidateEnvelope(
        ElsaInstance instance,
        ElsaInstanceOperation operation,
        ElsaInstanceLifecycleOutboxMessage outbox)
    {
        if (instance.Id == Guid.Empty || instance.OrganizationId == Guid.Empty || instance.WorkspaceId == Guid.Empty)
            throw Conflict("Lifecycle instance ownership is invalid.");
        if (operation.InstanceId != instance.Id || outbox.InstanceId != instance.Id ||
            outbox.OperationId != operation.Id || outbox.WorkspaceId != instance.WorkspaceId ||
            outbox.Action != operation.Action ||
            !string.Equals(outbox.RequestHash, operation.RequestHash, StringComparison.Ordinal))
            throw Conflict("Lifecycle operation identity is inconsistent.");
        if (string.IsNullOrWhiteSpace(operation.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(operation.IdempotencyScope))
            throw Conflict("Lifecycle operation scope is invalid.");
    }

    private static void ValidateWorkerEnvelope(
        ElsaInstanceLifecycleResolutionCommit commit,
        ElsaInstanceOperationEntity operation,
        ElsaInstanceEntity instance,
        ElsaInstanceLifecycleOutboxEntity outbox)
    {
        if (operation.InstanceId != commit.InstanceId || operation.OrganizationId != instance.OrganizationId ||
            operation.WorkspaceId != commit.WorkspaceId || outbox.OperationId != commit.OperationId ||
            outbox.InstanceId != commit.InstanceId || outbox.WorkspaceId != commit.WorkspaceId ||
            outbox.Action != operation.Action ||
            !string.Equals(operation.RequestHash, commit.RequestHash, StringComparison.Ordinal) ||
            !string.Equals(outbox.RequestHash, commit.RequestHash, StringComparison.Ordinal))
            throw Conflict("Lifecycle work item envelope is inconsistent.");
    }

    private static void ValidateWorkerEnvelope(
        ElsaInstanceLifecycleResolutionFailure failure,
        ElsaInstanceOperationEntity operation,
        ElsaInstanceEntity instance,
        ElsaInstanceLifecycleOutboxEntity outbox)
    {
        if (operation.InstanceId != failure.InstanceId || operation.OrganizationId != instance.OrganizationId ||
            operation.WorkspaceId != failure.WorkspaceId || outbox.OperationId != failure.OperationId ||
            outbox.InstanceId != failure.InstanceId || outbox.WorkspaceId != failure.WorkspaceId ||
            outbox.Action != operation.Action ||
            !string.Equals(operation.RequestHash, failure.RequestHash, StringComparison.Ordinal) ||
            !string.Equals(outbox.RequestHash, failure.RequestHash, StringComparison.Ordinal))
            throw Conflict("Lifecycle work item envelope is inconsistent.");
    }

    private static void EnsureLease(
        ElsaInstanceLifecycleResolutionCommit commit,
        ElsaInstanceOperationEntity operation,
        DateTimeOffset now)
    {
        if (operation.State != ElsaInstanceOperationState.Accepted ||
            !string.Equals(operation.WorkerId, commit.WorkerId, StringComparison.Ordinal) ||
            operation.LeaseVersion != commit.LeaseVersion ||
            !string.Equals(operation.LeaseTokenHash, HashLeaseToken(commit.LeaseToken!), StringComparison.Ordinal) ||
            operation.LeaseExpiresAt is null || operation.LeaseExpiresAt <= now)
            throw Conflict("Lifecycle work item is no longer owned by this worker.");
    }

    private static void EnsureLease(
        ElsaInstanceLifecycleResolutionFailure failure,
        ElsaInstanceOperationEntity operation,
        DateTimeOffset now)
    {
        if (operation.State != ElsaInstanceOperationState.Accepted ||
            !string.Equals(operation.WorkerId, failure.WorkerId, StringComparison.Ordinal) ||
            operation.LeaseVersion != failure.LeaseVersion ||
            !string.Equals(operation.LeaseTokenHash, HashLeaseToken(failure.LeaseToken!), StringComparison.Ordinal) ||
            operation.LeaseExpiresAt is null || operation.LeaseExpiresAt <= now)
            throw Conflict("Lifecycle work item is no longer owned by this worker.");
    }

    private static void ValidateCanonicalPlan(ElsaInstanceLifecycleResolvedPlan plan)
    {
        try
        {
            var typed = ResolvedElsaApplicationPlanSerialization.Deserialize(plan.SerializedPlan);
            var canonical = ResolvedElsaApplicationPlanSerialization.Serialize(typed);
            if (!string.Equals(canonical, plan.SerializedPlan, StringComparison.Ordinal) ||
                !string.Equals(ResolvedElsaApplicationPlanSerialization.ComputeContentHash(typed), plan.Reference.ContentHash, StringComparison.Ordinal) ||
                !int.TryParse(typed.SchemaVersion, out var schemaVersion) || schemaVersion != plan.Reference.SchemaVersion ||
                ResolvedElsaApplicationPlanValidator.Validate(typed).Count > 0)
                throw new InvalidOperationException();
            if (string.IsNullOrWhiteSpace(plan.Reference.PlanId) || string.IsNullOrWhiteSpace(plan.Reference.PlanUri))
                throw new InvalidOperationException();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw Conflict("Resolved plan content is invalid.");
        }
    }

    private async Task<ElsaInstanceLifecycleWorkerResult> CompleteReservationConflictAsync(
        ElsaInstanceOperationEntity operation,
        ElsaInstanceEntity instance,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var priorObservedLifecycle = instance.ObservedLifecycle;
        operation.State = ElsaInstanceOperationState.Failed;
        operation.FailureCode = "run.reservation.conflict";
        operation.FailureSummary = "Lifecycle target already has active work.";
        operation.CompletedAt = occurredAt.ToUniversalTime();
        operation.WorkerId = null;
        operation.LeaseTokenHash = null;
        operation.LeaseExpiresAt = null;
        operation.HeartbeatAt = null;
        operation.UpdatedAt = occurredAt.ToUniversalTime();
        await dbContext.ElsaInstanceAuditEvents.AddAsync(
            await CreateAuditEventAsync(
                instance,
                operation,
                priorObservedLifecycle,
                occurredAt,
                cancellationToken,
                eventType: "lifecycle.failed"),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new ElsaInstanceLifecycleWorkerResult(
            ElsaInstanceLifecycleWorkerOutcome.Conflict,
            MapOperation(operation),
            MapInstance(instance),
            FailureCode: "run.reservation.conflict",
            FailureSummary: "Lifecycle target already has active work.");
    }

    private async Task<ElsaInstanceLifecycleWorkerResult> ResolveReservationRaceAsync(
        ElsaInstanceLifecycleResolutionCommit commit,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        try
        {
            return await ResolveReservationRaceCoreAsync(commit, cancellationToken);
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    private Task<ElsaInstanceLifecycleWorkerResult> ResolveReservationRaceCoreAsync(
        ElsaInstanceLifecycleResolutionCommit commit,
        CancellationToken cancellationToken) =>
        dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            var operation = await dbContext.ElsaInstanceOperations
                .SingleOrDefaultAsync(x => x.Id == commit.OperationId, cancellationToken);
            var instance = await LoadTrackedInstanceAsync(commit.InstanceId, cancellationToken);
            if (operation is null || instance is null)
                throw Conflict("Lifecycle work item no longer exists.");

            var activeRun = await dbContext.DeploymentRuns
                .Where(x => x.WorkspaceId == commit.WorkspaceId &&
                            x.EnvironmentId == commit.DeploymentTarget.EnvironmentId &&
                            (x.Status == WorkspaceDeploymentRunStatus.Queued ||
                             x.Status == WorkspaceDeploymentRunStatus.Running ||
                             x.Status == WorkspaceDeploymentRunStatus.RecoveryRequired))
                .OrderBy(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (activeRun is null)
                throw Conflict("Lifecycle resolution conflicted with another request.");
            if (activeRun.ElsaInstanceId == commit.InstanceId &&
                activeRun.ApplicationId == commit.DeploymentTarget.ApplicationId &&
                operation.DeploymentRunId == activeRun.Id &&
                operation.State == ElsaInstanceOperationState.Queued)
            {
                return new ElsaInstanceLifecycleWorkerResult(
                    ElsaInstanceLifecycleWorkerOutcome.AlreadyCompleted,
                    MapOperation(operation),
                    MapInstance(instance),
                    MapDeploymentRun(activeRun));
            }

            if (operation.State != ElsaInstanceOperationState.Accepted || operation.InstanceId != commit.InstanceId ||
                !string.Equals(operation.RequestHash, commit.RequestHash, StringComparison.Ordinal))
                throw Conflict("Lifecycle work item is no longer available.");

            return await CompleteReservationConflictAsync(
                operation, instance, commit.CommittedAt, cancellationToken);
        }, cancellationToken);

    private async Task QuarantinePersistedWorkItemAsync(
        Guid outboxId,
        ElsaInstanceOperationEntity? operation,
        ElsaInstanceEntity? instance,
        bool failOperation,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var outbox = await dbContext.ElsaInstanceLifecycleOutbox
            .SingleOrDefaultAsync(x => x.Id == outboxId, cancellationToken)
            ?? throw new InvalidOperationException("Lifecycle outbox record no longer exists.");
        outbox.QuarantinedAt = occurredAt;
        outbox.QuarantineCode = "outbox.invalid";

        if (failOperation && operation is not null && instance is not null)
        {
            var priorObservedLifecycle = instance.ObservedLifecycle;
            operation.State = ElsaInstanceOperationState.Failed;
            operation.FailureCode = "outbox.invalid";
            operation.FailureSummary = "Lifecycle work item could not be read safely.";
            operation.CompletedAt = occurredAt;
            operation.WorkerId = null;
            operation.LeaseTokenHash = null;
            operation.LeaseExpiresAt = null;
            operation.HeartbeatAt = null;
            operation.UpdatedAt = occurredAt;
            await dbContext.ElsaInstanceAuditEvents.AddAsync(
                await CreateAuditEventAsync(
                    instance,
                    operation,
                    priorObservedLifecycle,
                    occurredAt,
                    cancellationToken,
                    eventType: "lifecycle.failed"),
                cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool TryMapPersistedWorkItem(
        ElsaInstanceLifecycleOutboxEntity candidate,
        ElsaInstanceOperationEntity? operationEntity,
        ElsaInstanceEntity? instanceEntity,
        out ElsaInstanceLifecycleOutboxMessage outbox,
        out ElsaInstanceOperation operation,
        out ElsaInstance instance)
    {
        outbox = null!;
        operation = null!;
        instance = null!;
        try
        {
            if (operationEntity is null || instanceEntity is null)
                return false;

            outbox = MapOutbox(candidate);
            operation = MapOperation(operationEntity);
            instance = MapInstance(instanceEntity);
            if (outbox.OperationId != operation.Id || outbox.InstanceId != instance.Id ||
                outbox.WorkspaceId != instance.WorkspaceId || operation.InstanceId != instance.Id ||
                operationEntity.WorkspaceId != instance.WorkspaceId || operationEntity.OrganizationId != instance.OrganizationId ||
                candidate.OrganizationId != instance.OrganizationId ||
                outbox.Action != operation.Action ||
                !string.Equals(outbox.RequestHash, operation.RequestHash, StringComparison.Ordinal))
                return false;
            return true;
        }
        catch (Exception)
        {
            outbox = null!;
            operation = null!;
            instance = null!;
            return false;
        }
    }

    private static bool CanQuarantine(
        ElsaInstanceLifecycleOutboxEntity candidate,
        ElsaInstanceOperationEntity? operation,
        ElsaInstanceEntity? instance)
    {
        return operation is not null && instance is not null &&
               candidate.Id != Guid.Empty &&
               candidate.OperationId != Guid.Empty && candidate.InstanceId != Guid.Empty &&
               operation.Id == candidate.OperationId &&
               operation.InstanceId == candidate.InstanceId &&
               operation.State == ElsaInstanceOperationState.Accepted &&
               Enum.IsDefined(operation.Action) &&
               operation.OrganizationId != Guid.Empty &&
               operation.WorkspaceId != Guid.Empty &&
               operation.OrganizationId == instance.OrganizationId &&
               operation.WorkspaceId == instance.WorkspaceId &&
               instance.Id == candidate.InstanceId &&
               instance.WorkspaceId == candidate.WorkspaceId &&
               IsCanonicalHash(operation.RequestHash) &&
               operation.LeaseVersion >= 0 && operation.LeaseVersion < int.MaxValue &&
               IsSafePersistedReference(operation.IdempotencyScope, 256) &&
               IsSafePersistedToken(operation.IdempotencyKey, 128) &&
               operation.ExpectedVersion >= 1 && operation.AttemptNumber >= 1 &&
               IsOptionalSafePersistedToken(operation.WorkerId, 256) &&
               IsOptionalCanonicalHash(operation.LeaseTokenHash) &&
               IsOptionalSafePersistedReference(operation.DesiredStateRevisionId, 128) &&
               IsOptionalSafePersistedReference(operation.ResolvedPlanId, 128) &&
               IsOptionalSafePersistedCode(operation.FailureCode) &&
               IsOptionalSafePersistedReference(instance.DesiredStateRevisionId, 128);
    }

    private static bool IsSafePersistedReference(string? value, int maxLength) =>
        value is not null && value.Length > 0 && value.Length <= maxLength && value == value.Trim() &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':' or '/' or '+');

    private static bool IsSafePersistedToken(string? value, int maxLength) =>
        IsSafePersistedReference(value, maxLength) &&
        !value!.Contains('/', StringComparison.Ordinal) &&
        !value!.Contains(':', StringComparison.Ordinal) &&
        !value!.Contains('+', StringComparison.Ordinal);

    private static bool IsOptionalSafePersistedReference(string? value, int maxLength) =>
        value is null || IsSafePersistedReference(value, maxLength);

    private static bool IsOptionalSafePersistedToken(string? value, int maxLength) =>
        value is null || IsSafePersistedToken(value, maxLength);

    private static bool IsOptionalCanonicalHash(string? value) =>
        value is null || IsCanonicalHash(value);

    private static bool IsOptionalSafePersistedCode(string? value) =>
        value is null || (value.Length is > 0 and <= 128 && value == value.Trim() &&
                          value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':'));

    private static WorkspaceDeploymentRun MapDeploymentRun(DeploymentRunEntity entity) =>
        new(
            entity.Id,
            entity.WorkspaceId,
            entity.ApplicationId,
            entity.EnvironmentId,
            entity.EngineId,
            entity.SourceRevisionId,
            entity.PreviousDeployedRevisionId,
            entity.RollbackSourceRunId,
            entity.Status,
            entity.ValidationOutcome,
            entity.ConfirmationId,
            entity.ActorAccountId,
            entity.QueuedAt,
            entity.StartedAt,
            entity.CompletedAt,
            entity.CreatedAt,
            entity.WorkerId,
            entity.WorkerHeartbeatAt,
            entity.AttemptNumber,
            entity.RecoveryReason,
            entity.FailureMessage);

    private static string CreateLeaseToken() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static string HashLeaseToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static void ValidateExistingOperation(
        ElsaInstanceOperationEntity existing,
        ElsaInstance instance,
        ElsaInstanceOperation operation,
        ElsaInstanceLifecycleOutboxMessage outbox)
    {
        if (existing.InstanceId != operation.InstanceId || existing.InstanceId != instance.Id ||
            existing.OrganizationId != instance.OrganizationId || existing.WorkspaceId != instance.WorkspaceId ||
            existing.Action != operation.Action || existing.ExpectedVersion != operation.ExpectedVersion ||
            !string.Equals(existing.IdempotencyScope, operation.IdempotencyScope, StringComparison.Ordinal) ||
            !string.Equals(existing.IdempotencyKey, operation.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(existing.RequestHash, operation.RequestHash, StringComparison.Ordinal) ||
            outbox.OperationId != existing.Id ||
            outbox.WorkspaceId != existing.WorkspaceId ||
            outbox.InstanceId != existing.InstanceId ||
            outbox.Action != existing.Action ||
            !string.Equals(outbox.RequestHash, existing.RequestHash, StringComparison.Ordinal))
            throw Conflict("Lifecycle operation identity is already in use.");
    }

    private static void ValidateExpectedInstance(
        ElsaInstance expected,
        ElsaInstance requested,
        ElsaInstanceEntity? stored)
    {
        if (stored is null || expected.Id != requested.Id || expected.OrganizationId != requested.OrganizationId ||
            expected.WorkspaceId != requested.WorkspaceId || stored.OrganizationId != expected.OrganizationId ||
            stored.WorkspaceId != expected.WorkspaceId || stored.Version != expected.Version ||
            !string.Equals(stored.Slug, expected.Slug, StringComparison.Ordinal))
            throw stored is null || stored.Version != expected.Version
                ? Conflict("Instance version conflict.", ElsaInstanceLifecycleConflictReason.VersionConflict)
                : Conflict("Elsa instance does not exist in the workspace.", ElsaInstanceLifecycleConflictReason.InvalidState);
    }

    private static ElsaInstanceEntity ToEntity(ElsaInstance instance, DateTimeOffset now)
    {
        var entity = new ElsaInstanceEntity
        {
            Id = instance.Id,
            OrganizationId = instance.OrganizationId,
            WorkspaceId = instance.WorkspaceId,
            Name = instance.Name,
            Slug = instance.Slug,
            Version = instance.Version,
            CreatedAt = now.ToUniversalTime(),
            UpdatedAt = now.ToUniversalTime()
        };
        ApplyAggregate(entity, instance);
        return entity;
    }

    private static void ApplyAggregate(ElsaInstanceEntity entity, ElsaInstance instance)
    {
        if (!string.Equals(entity.Slug, instance.Slug, StringComparison.Ordinal) && entity.Id != Guid.Empty)
            throw Conflict("An Elsa instance slug is immutable.");

        var release = instance.Intent.Release;
        var application = instance.Intent.Application;
        var placement = instance.Intent.Placement;
        entity.Name = instance.Name;
        entity.Slug = instance.Slug;
        entity.DistributionId = release.DistributionId;
        entity.ReleaseLine = release.ReleaseLine;
        entity.RequestedVersion = release.RequestedVersion;
        entity.Channel = release.Channel;
        entity.PatchUpdates = release.PatchUpdates;
        entity.MinorUpdates = release.MinorUpdates;
        entity.MajorMigrations = release.MajorMigrations;
        entity.PreviewManifestDigest = release.PreviewManifestDigest;
        entity.TopologyId = application.TopologyId;
        entity.FeaturePresetId = application.FeaturePresetId;
        entity.FeatureOverridesJson = SerializeFeatureOverrides(application.FeatureOverrides);
        entity.PackagePolicy = application.PackagePolicy;
        entity.ConfigurationShapeRevisionId = application.ConfigurationShapeRevisionId;
        entity.TargetMode = placement.TargetMode;
        entity.RegionCode = placement.RegionCode;
        entity.IsolationProfile = placement.IsolationProfile;
        entity.CapacityProfile = placement.CapacityProfile;
        entity.NetworkOutcome = placement.NetworkOutcome;
        entity.DomainOutcome = placement.DomainOutcome;
        entity.DesiredLifecycle = instance.DesiredLifecycle;
        entity.ObservedLifecycle = instance.ObservedLifecycle;
        entity.Health = instance.Health;
        entity.DesiredStateRevisionId = instance.DesiredStateRevisionId?.Value;
        entity.ResolvedPlanId = instance.ResolvedPlanReference?.PlanId;
        entity.ResolvedPlanSchemaVersion = instance.ResolvedPlanReference?.SchemaVersion;
        entity.ResolvedPlanContentHash = instance.ResolvedPlanReference?.ContentHash;
        entity.ResolvedPlanUri = instance.ResolvedPlanReference?.PlanUri;
        entity.CurrentReleaseDistributionId = instance.CurrentResolvedRelease?.DistributionId;
        entity.CurrentReleaseLine = instance.CurrentResolvedRelease?.ReleaseLine;
        entity.CurrentReleaseVersion = instance.CurrentResolvedRelease?.Version;
        entity.CurrentReleaseManifestDigest = instance.CurrentResolvedRelease?.ManifestDigest;
        entity.CurrentReleaseComponentDigestsJson = instance.CurrentResolvedRelease is null
            ? null
            : SerializeComponentDigests(instance.CurrentResolvedRelease.ComponentDigests);
        entity.CurrentDeploymentId = instance.CurrentDeploymentReference?.DeploymentId;
        entity.CurrentDeploymentRevisionId = instance.CurrentDeploymentReference?.RevisionId;
        entity.CurrentDeploymentEndpointUri = instance.CurrentDeploymentReference?.EndpointUri;
        entity.PlacementAssignmentId = instance.PlacementAssignmentReference?.AssignmentId;
        entity.ElsaTenantId = instance.ElsaTenantReference?.TenantId;
        entity.ElsaTenantAudience = instance.ElsaTenantReference?.Audience;
        entity.LastOperationId = instance.LastOperationId?.Value;
        entity.DeletedAt = instance.DeletedAt;
        // Version is part of the aggregate snapshot even when a valid intent
        // mutation leaves all normalized fields equal (for example, an explicit
        // no-op intent update). CatalogDbContext still validates the increment
        // against the tracked original version before saving.
        entity.Version = instance.Version;
    }

    private static ElsaInstanceOperationEntity ToEntity(
        ElsaInstanceOperation operation,
        ElsaInstance instance,
        DateTimeOffset now) => new()
        {
            Id = operation.Id,
            InstanceId = operation.InstanceId,
            OrganizationId = instance.OrganizationId,
            WorkspaceId = instance.WorkspaceId,
            Action = operation.Action,
            IdempotencyScope = operation.IdempotencyScope,
            IdempotencyKey = operation.IdempotencyKey,
            RequestHash = operation.RequestHash,
            ExpectedVersion = operation.ExpectedVersion,
            State = operation.State,
            AttemptNumber = operation.AttemptNumber,
            AcceptedAt = operation.AcceptedAt,
            CreatedAt = now.ToUniversalTime(),
            UpdatedAt = now.ToUniversalTime()
        };

    private static ElsaInstanceLifecycleOutboxEntity ToEntity(
        ElsaInstanceLifecycleOutboxMessage outbox,
        ElsaInstance instance) => new()
        {
            Id = outbox.Id,
            OrganizationId = instance.OrganizationId,
            WorkspaceId = outbox.WorkspaceId,
            InstanceId = outbox.InstanceId,
            OperationId = outbox.OperationId,
            Action = outbox.Action,
            RequestHash = outbox.RequestHash,
            CreatedAt = outbox.CreatedAt.ToUniversalTime()
        };

    private static ElsaInstanceIntentRevisionEntity ToEntity(
        ElsaInstanceIntent intent,
        ElsaInstance instance,
        DateTimeOffset authoredAt,
        int revisionNumber)
    {
        var release = intent.Release;
        var application = intent.Application;
        var placement = intent.Placement;
        return new ElsaInstanceIntentRevisionEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = instance.OrganizationId,
            WorkspaceId = instance.WorkspaceId,
            InstanceId = instance.Id,
            RevisionNumber = revisionNumber,
            ContentHash = intent.ComputeCanonicalHash(),
            DistributionId = release.DistributionId,
            ReleaseLine = release.ReleaseLine,
            RequestedVersion = release.RequestedVersion,
            Channel = release.Channel,
            PatchUpdates = release.PatchUpdates,
            MinorUpdates = release.MinorUpdates,
            MajorMigrations = release.MajorMigrations,
            PreviewManifestDigest = release.PreviewManifestDigest,
            TopologyId = application.TopologyId,
            FeaturePresetId = application.FeaturePresetId,
            FeatureOverridesJson = SerializeFeatureOverrides(application.FeatureOverrides),
            PackagePolicy = application.PackagePolicy,
            ConfigurationShapeRevisionId = application.ConfigurationShapeRevisionId,
            TargetMode = placement.TargetMode,
            RegionCode = placement.RegionCode,
            IsolationProfile = placement.IsolationProfile,
            CapacityProfile = placement.CapacityProfile,
            NetworkOutcome = placement.NetworkOutcome,
            DomainOutcome = placement.DomainOutcome,
            DesiredLifecycle = intent.DesiredLifecycle,
            AuthoredAt = authoredAt.ToUniversalTime(),
            CreatedAt = authoredAt.ToUniversalTime()
        };
    }

    private static ElsaInstanceLifecycleOutboxMessage MapOutbox(ElsaInstanceLifecycleOutboxEntity entity)
    {
        if (entity.Id == Guid.Empty || entity.WorkspaceId == Guid.Empty || entity.InstanceId == Guid.Empty ||
            entity.OperationId == Guid.Empty || !Enum.IsDefined(entity.Action) || !IsCanonicalHash(entity.RequestHash))
            throw new InvalidOperationException("Persisted lifecycle outbox record is invalid.");
        return new ElsaInstanceLifecycleOutboxMessage(
            entity.Id, entity.WorkspaceId, entity.InstanceId, entity.OperationId, entity.Action,
            entity.RequestHash, entity.CreatedAt);
    }

    internal static ElsaInstanceOperation MapOperation(ElsaInstanceOperationEntity entity)
        => MapOperation(entity, null);

    private async Task SupersedeEntitlementHeldOperationAsync(
        ElsaInstanceOperationEntity operation,
        ElsaInstanceEntity instance,
        DateTimeOffset supersededAt,
        CancellationToken cancellationToken)
    {
        var timestamp = supersededAt.ToUniversalTime();
        operation.State = ElsaInstanceOperationState.Cancelled;
        operation.FailureCode = ElsaInstanceCommercialOperation.EntitlementSafeExitSuperseded;
        operation.FailureSummary = null;
        operation.CompletedAt = timestamp;
        operation.WorkerId = null;
        operation.LeaseTokenHash = null;
        operation.LeaseExpiresAt = null;
        operation.HeartbeatAt = null;
        operation.UpdatedAt = timestamp;

        if (operation.DeploymentRunId is not { } runId)
            return;

        var run = await dbContext.DeploymentRuns
            .Include(x => x.Environment)
            .SingleOrDefaultAsync(x => x.Id == runId &&
                                       x.WorkspaceId == operation.WorkspaceId &&
                                       x.ElsaInstanceId == instance.Id,
                cancellationToken);
        if (run is null)
            return;
        if (run.Status is WorkspaceDeploymentRunStatus.Queued or
            WorkspaceDeploymentRunStatus.Running or
            WorkspaceDeploymentRunStatus.RecoveryRequired)
        {
            run.Status = WorkspaceDeploymentRunStatus.Cancelled;
            run.CompletedAt = timestamp;
            run.RecoveryReason = ElsaInstanceCommercialOperation.EntitlementSafeExitSuperseded;
            run.FailureMessage = null;
            run.WorkerId = null;
            run.WorkerHeartbeatAt = null;
            if (run.Environment is not null)
            {
                run.Environment.UpdatedAt = timestamp;
                run.Environment.DeploymentStatus = DeploymentStatus.Blocked;
            }
            await dbContext.DeploymentRunHistoryEvents.AddAsync(new()
            {
                Id = Guid.NewGuid(),
                WorkspaceId = run.WorkspaceId,
                RunId = run.Id,
                Status = WorkspaceDeploymentRunStatus.Cancelled,
                Message = "Deployment run was cancelled by a safe lifecycle exit.",
                CreatedAt = timestamp
            }, cancellationToken);
        }
    }

    private static ElsaInstanceOperation MapOperation(
        ElsaInstanceOperationEntity entity,
        ElsaInstanceRecoveryRequestEntity? recovery)
    {
        try
        {
            if (entity.InstanceId is null)
                throw new InvalidOperationException();
            return ElsaInstanceOperation.Hydrate(
                entity.Id,
                entity.InstanceId.Value,
                entity.Action,
                entity.IdempotencyScope,
                entity.IdempotencyKey,
                entity.RequestHash,
                entity.ExpectedVersion,
                entity.State,
                entity.AttemptNumber,
                entity.AcceptedAt,
                recovery?.IdempotencyScope ?? entity.RecoveryIdempotencyScope,
                recovery?.IdempotencyKey ?? entity.RecoveryIdempotencyKey,
                recovery?.RequestHash ?? entity.RecoveryRequestHash);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            throw new InvalidOperationException("Persisted lifecycle operation is invalid.");
        }
    }

    internal static ElsaInstance MapInstance(ElsaInstanceEntity entity)
    {
        try
        {
            var intent = MapIntent(
                entity.DistributionId,
                entity.ReleaseLine,
                entity.RequestedVersion,
                entity.Channel,
                entity.PatchUpdates,
                entity.MinorUpdates,
                entity.MajorMigrations,
                entity.PreviewManifestDigest,
                entity.TopologyId,
                entity.FeaturePresetId,
                entity.FeatureOverridesJson,
                entity.PackagePolicy,
                entity.ConfigurationShapeRevisionId,
                entity.TargetMode,
                entity.RegionCode,
                entity.IsolationProfile,
                entity.CapacityProfile,
                entity.NetworkOutcome,
                entity.DomainOutcome,
                entity.DesiredLifecycle);

            var plan = MapPlan(entity);
            var currentRelease = MapCurrentRelease(entity, plan);
            var identityBinding = MapIdentityBinding(entity);
            return ElsaInstance.Hydrate(
                entity.Id,
                entity.OrganizationId,
                entity.WorkspaceId,
                entity.Name,
                entity.Slug,
                intent,
                entity.ObservedLifecycle,
                entity.Health,
                entity.Version,
                identityBinding,
                OptionalRevision(entity.DesiredStateRevisionId),
                plan,
                currentRelease,
                MapDeployment(entity),
                entity.PlacementAssignmentId is null ? null : new ElsaPlacementAssignmentReference(entity.PlacementAssignmentId),
                MapTenant(entity),
                entity.LastOperationId is null ? null : new ElsaLastOperationId(entity.LastOperationId),
                entity.DeletedAt);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException or InvalidOperationException or JsonException)
        {
            throw new InvalidOperationException("Persisted Elsa instance is invalid.");
        }
    }

    private static ElsaInstanceIntent MapIntent(
        string distributionId,
        string releaseLine,
        string? requestedVersion,
        string channel,
        string patchUpdates,
        string minorUpdates,
        string majorMigrations,
        string? previewManifestDigest,
        string topologyId,
        string? featurePresetId,
        string featureOverridesJson,
        string? packagePolicy,
        string? configurationShapeRevisionId,
        string targetMode,
        string regionCode,
        string isolationProfile,
        string capacityProfile,
        string networkOutcome,
        string domainOutcome,
        ElsaDesiredLifecycle desiredLifecycle) => new(
        new ElsaReleaseIntent(distributionId, releaseLine, requestedVersion, channel, patchUpdates, minorUpdates, majorMigrations, previewManifestDigest),
        new ElsaApplicationIntent(topologyId, featurePresetId, ParseFeatureOverrides(featureOverridesJson), packagePolicy, configurationShapeRevisionId),
        new ElsaPlacementIntent(targetMode, regionCode, isolationProfile, capacityProfile, networkOutcome, domainOutcome),
        desiredLifecycle);

    private static ElsaResolvedPlanReference? MapPlan(ElsaInstanceEntity entity)
    {
        var values = new object?[] { entity.ResolvedPlanId, entity.ResolvedPlanSchemaVersion, entity.ResolvedPlanContentHash, entity.ResolvedPlanUri };
        if (values.All(x => x is null))
            return null;
        if (values.Any(x => x is null) || entity.ResolvedPlanId is null || entity.ResolvedPlanContentHash is null || entity.ResolvedPlanUri is null || entity.ResolvedPlanSchemaVersion is null)
            throw new InvalidOperationException();
        return new ElsaResolvedPlanReference(entity.ResolvedPlanId, entity.ResolvedPlanSchemaVersion.Value,
            entity.ResolvedPlanContentHash, entity.ResolvedPlanUri);
    }

    private static ElsaCurrentResolvedRelease? MapCurrentRelease(ElsaInstanceEntity entity, ElsaResolvedPlanReference? plan)
    {
        var values = new string?[]
        {
            entity.CurrentReleaseDistributionId, entity.CurrentReleaseLine, entity.CurrentReleaseVersion,
            entity.CurrentReleaseManifestDigest, entity.CurrentReleaseComponentDigestsJson
        };
        if (values.All(string.IsNullOrWhiteSpace))
            return null;
        if (plan is null || values.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException();
        return new ElsaCurrentResolvedRelease(
            plan,
            entity.CurrentReleaseDistributionId!,
            entity.CurrentReleaseLine!,
            entity.CurrentReleaseVersion!,
            entity.CurrentReleaseManifestDigest!,
            ParseComponentDigests(entity.CurrentReleaseComponentDigestsJson!));
    }

    private static ElsaCurrentDeploymentReference? MapDeployment(ElsaInstanceEntity entity)
    {
        if (entity.CurrentDeploymentId is null)
        {
            if (entity.CurrentDeploymentRevisionId is not null || entity.CurrentDeploymentEndpointUri is not null)
                throw new InvalidOperationException();
            return null;
        }
        var endpoint = ElsaManagedEndpointOrigin.TryCreate(entity.CurrentDeploymentEndpointUri, out var origin)
            ? origin.Value
            : null;
        return new ElsaCurrentDeploymentReference(entity.CurrentDeploymentId, entity.CurrentDeploymentRevisionId, endpoint);
    }

    private static ElsaTenantReference? MapTenant(ElsaInstanceEntity entity)
    {
        if (entity.ElsaTenantId is null)
        {
            if (entity.ElsaTenantAudience is not null)
                throw new InvalidOperationException();
            return null;
        }
        return new ElsaTenantReference(entity.ElsaTenantId, entity.ElsaTenantAudience);
    }

    private static ElsaInstanceIdentityBinding? MapIdentityBinding(ElsaInstanceEntity entity)
        => ManagedElsaIdentityBindingMapper.TryMapCurrent(entity, out var binding, out _)
            ? binding
            : null;

    private static ElsaDesiredStateRevisionId? OptionalRevision(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new ElsaDesiredStateRevisionId(value);

    private static IReadOnlyDictionary<string, ElsaFeatureOverride> ParseFeatureOverrides(string json)
    {
        using var document = JsonDocument.Parse(json, SafeJsonOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException();
        if (document.RootElement.EnumerateObject().Count() > 256)
            throw new InvalidOperationException();
        var values = new Dictionary<string, ElsaFeatureOverride>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!IsSafeJsonName(property.Name) || property.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException();
            string? kind = null;
            string? value = null;
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in property.Value.EnumerateObject())
            {
                if (!fields.Add(field.Name) || field.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException();
                if (field.Name == "kind") kind = field.Value.GetString();
                else if (field.Name == "value") value = field.Value.GetString();
                else throw new InvalidOperationException();
            }
            if (kind is null || value is null || !IsSafeFeatureValue(value) ||
                !Enum.TryParse<ElsaFeatureOverrideKind>(kind, true, out var parsedKind) || !Enum.IsDefined(parsedKind))
                throw new InvalidOperationException();
            values.Add(property.Name, ParseFeatureOverride(parsedKind, value));
        }
        return values;
    }

    private static ElsaFeatureOverride ParseFeatureOverride(ElsaFeatureOverrideKind kind, string value) => kind switch
    {
        ElsaFeatureOverrideKind.Boolean when bool.TryParse(value, out var parsed) => ElsaFeatureOverride.FromBoolean(parsed),
        ElsaFeatureOverrideKind.Number => ElsaFeatureOverride.FromNumber(value),
        ElsaFeatureOverrideKind.Catalog => ElsaFeatureOverride.FromCatalog(value),
        _ => throw new InvalidOperationException()
    };

    private static IReadOnlyList<ElsaComponentDigest> ParseComponentDigests(string json)
    {
        using var document = JsonDocument.Parse(json, SafeJsonOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException();
        var values = new List<ElsaComponentDigest>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException();
            string? componentId = null;
            string? digest = null;
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in item.EnumerateObject())
            {
                if (!fields.Add(field.Name))
                    throw new InvalidOperationException();
                if (field.Name == "componentId" && field.Value.ValueKind == JsonValueKind.String) componentId = field.Value.GetString();
                else if (field.Name == "digest" && field.Value.ValueKind == JsonValueKind.String) digest = field.Value.GetString();
                else throw new InvalidOperationException();
            }
            if (componentId is null || digest is null)
                throw new InvalidOperationException();
            values.Add(new ElsaComponentDigest(componentId, digest));
        }
        return values;
    }

    private static string SerializeFeatureOverrides(IReadOnlyDictionary<string, ElsaFeatureOverride> values) =>
        JsonSerializer.Serialize(values.ToDictionary(
            x => x.Key,
            x => (object?)new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["kind"] = x.Value.Kind.ToString(),
                ["value"] = x.Value.Value
            },
            StringComparer.Ordinal));

    private static string SerializeComponentDigests(IReadOnlyList<ElsaComponentDigest> values) =>
        JsonSerializer.Serialize(values.Select(x => new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["componentId"] = x.ComponentId,
            ["digest"] = x.Digest
        }));

    private static string RequireIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Idempotency key is required.", nameof(value));
        return value.Trim();
    }

    private static string HashIdempotencyKey(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsCanonicalHash(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit) && value == value.ToLowerInvariant();

    private static bool IsSafeJsonName(string value) =>
        value.Length is > 0 and <= 128 && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_');

    private static bool IsSafeFeatureValue(string value) =>
        value.Length is > 0 and <= 512 && !value.Any(char.IsControl) && !ContainsSensitiveMarker(value);

    private static bool ContainsSensitiveMarker(string value) =>
        value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("connectionstring", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("workflow", StringComparison.OrdinalIgnoreCase);

    private static ElsaInstanceLifecycleConflictException Conflict(
        string message, ElsaInstanceLifecycleConflictReason reason = ElsaInstanceLifecycleConflictReason.InvalidState) =>
        new(message, reason);
}
