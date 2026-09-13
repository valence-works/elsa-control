using System.Collections.ObjectModel;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.EntityFrameworkCore;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class AzureProviderOperationStore(CatalogDbContext db) :
    IAzureProviderOperationStore,
    IAzureProviderOperationAuthorizationStore,
    IAzureProviderResourceAssignmentStore,
    IAzureProviderRecoveryObservationStore,
    IAzureProviderDeleteRecoveryStore
{
    private static readonly IReadOnlyDictionary<string, string> EmptySecretReferences =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    async Task<AzureProviderDeleteRecoveryAuthority?> IAzureProviderDeleteRecoveryStore.GetDeleteRecoveryAuthorityAsync(
        Guid workspaceId,
        Guid recoveryRequestId,
        Guid instanceId,
        Guid lifecycleOperationId,
        CancellationToken cancellationToken)
    {
        if (workspaceId == Guid.Empty || recoveryRequestId == Guid.Empty || instanceId == Guid.Empty || lifecycleOperationId == Guid.Empty)
            return null;

        var recovery = await db.ElsaInstanceRecoveryRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == recoveryRequestId && x.WorkspaceId == workspaceId &&
                                       x.InstanceId == instanceId && x.OperationId == lifecycleOperationId,
                cancellationToken);
        if (recovery is null ||
            !AzureProviderDeleteRecoveryAuthority.TryParse(recovery.AzureDeleteRecoveryAuthority, out var authority) ||
            authority is null || authority.LifecycleAttemptNumber != recovery.AttemptNumber)
            return null;

        var operation = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == lifecycleOperationId && x.WorkspaceId == workspaceId &&
                                       x.InstanceId == instanceId,
                cancellationToken);
        return operation?.Action == ElsaInstanceOperationAction.Delete &&
               operation.AttemptNumber == recovery.AttemptNumber &&
               operation.RecoveryIdempotencyScope == recovery.IdempotencyScope &&
               operation.RecoveryIdempotencyKey == recovery.IdempotencyKey &&
               operation.RecoveryRequestHash == recovery.RequestHash
            ? authority
            : null;
    }

    async Task<AzureProviderOperation?> IAzureProviderDeleteRecoveryStore.ClaimDeleteRecoveryAsync(
        AzureProviderDeleteRecoveryClaimRequest request,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1))
            throw new ArgumentException("Lease duration must be positive and bounded.", nameof(leaseDuration));
        DateTimeOffset leaseExpires;
        try { leaseExpires = now.ToUniversalTime().Add(leaseDuration); }
        catch (ArgumentOutOfRangeException) { throw new ArgumentException("Lease duration overflowed.", nameof(leaseDuration)); }

        db.ChangeTracker.Clear();
        try
        {
            return await db.ExecuteInTransactionAsync<AzureProviderOperation?>(IsolationLevel.Serializable, async () =>
            {
                var recovery = await db.ElsaInstanceRecoveryRequests.SingleOrDefaultAsync(
                    x => x.Id == request.RecoveryRequestId && x.WorkspaceId == request.WorkspaceId &&
                         x.InstanceId == request.InstanceId && x.OperationId == request.LifecycleOperationId,
                    cancellationToken);
                if (recovery is null ||
                    !AzureProviderDeleteRecoveryAuthority.TryParse(recovery.AzureDeleteRecoveryAuthority, out var authority) ||
                    authority is null || authority.LifecycleAttemptNumber != request.LifecycleAttemptNumber ||
                    recovery.AttemptNumber != request.LifecycleAttemptNumber)
                    return null;

                var lifecycleOperation = await db.ElsaInstanceOperations.SingleOrDefaultAsync(
                    x => x.Id == request.LifecycleOperationId && x.WorkspaceId == request.WorkspaceId &&
                         x.InstanceId == request.InstanceId,
                    cancellationToken);
                var instance = await db.ElsaInstances.AsNoTracking().SingleOrDefaultAsync(
                    x => x.Id == request.InstanceId && x.WorkspaceId == request.WorkspaceId,
                    cancellationToken);
                var providerOperation = await db.AzureProviderOperations.SingleOrDefaultAsync(
                    x => x.Id == authority.ProviderOperationId && x.WorkspaceId == request.WorkspaceId,
                    cancellationToken);
                var assignment = await db.AzureProviderResourceAssignments.AsNoTracking().SingleOrDefaultAsync(
                    x => x.Id == authority.ProviderAssignmentId && x.WorkspaceId == request.WorkspaceId,
                    cancellationToken);
                var verifiedCleanupFinalization = providerOperation is not null && assignment is not null &&
                    IsVerifiedCleanupEligible(providerOperation, assignment);

                var lifecycleLeaseHash = Hash(request.LeaseToken);
                var nowUtc = now.ToUniversalTime();
                var lifecycleIsCurrent = lifecycleOperation is not null && instance is not null &&
                    lifecycleOperation.OrganizationId == recovery.OrganizationId &&
                    lifecycleOperation.Action == ElsaInstanceOperationAction.Delete &&
                    lifecycleOperation.State == ElsaInstanceOperationState.Running &&
                    lifecycleOperation.AttemptNumber == request.LifecycleAttemptNumber &&
                    lifecycleOperation.RecoveryIdempotencyScope == recovery.IdempotencyScope &&
                    lifecycleOperation.RecoveryIdempotencyKey == recovery.IdempotencyKey &&
                    lifecycleOperation.RecoveryRequestHash == recovery.RequestHash &&
                    instance.OrganizationId == recovery.OrganizationId && instance.Version == request.InstanceVersion &&
                    request.InstanceVersion == authority.InstanceVersion &&
                    instance.DesiredLifecycle == ElsaDesiredLifecycle.Deleting &&
                    HasCapturedPlacement(instance, authority.ProviderAssignmentId) &&
                    lifecycleOperation.WorkerId == request.WorkerId &&
                    lifecycleOperation.LeaseTokenHash == lifecycleLeaseHash &&
                    lifecycleOperation.LeaseVersion == request.LeaseVersion &&
                    lifecycleOperation.LeaseExpiresAt is { } lifecycleLeaseExpires && lifecycleLeaseExpires > nowUtc;
                var providerIdentityIsCurrent = providerOperation is not null &&
                    providerOperation.OrganizationId == recovery.OrganizationId &&
                    providerOperation.InstanceId == request.InstanceId &&
                    providerOperation.ProviderAssignmentId == authority.ProviderAssignmentId &&
                    providerOperation.LifecycleAction == ElsaInstanceOperationAction.Delete &&
                    providerOperation.Action == AzureProviderOperationAction.Delete &&
                    (providerOperation.LeaseExpiresAt is null || providerOperation.LeaseExpiresAt <= nowUtc) &&
                    providerOperation.AttemptNumber == authority.ProviderAttemptNumber &&
                    providerOperation.Version == authority.ProviderVersion &&
                    providerOperation.CheckpointSequence == authority.ProviderCheckpointSequence &&
                    providerOperation.OperationIdentity == authority.ProviderOperationIdentity &&
                    providerOperation.RequestHash == authority.ProviderRequestHash &&
                    providerOperation.TargetKey == authority.TargetKey &&
                    await ScopeMatchesOrReboundAsync(
                        request.WorkspaceId,
                        authority.ProviderAssignmentId,
                        authority.ProviderScopeFingerprint,
                        providerOperation.ProviderScopeFingerprint,
                        cancellationToken) &&
                    providerOperation.PlanFingerprint == authority.ProviderPlanFingerprint &&
                    providerOperation.TemplateFingerprint == authority.ProviderTemplateFingerprint &&
                    AzureProviderOperationValidation.IsLifecycleDeleteIdempotencyKey(
                        providerOperation.IdempotencyKey, request.LifecycleOperationId);
                var providerIsCurrent = providerIdentityIsCurrent &&
                    providerOperation!.Status == AzureProviderOperationStatus.RecoveryRequired &&
                    (providerOperation.Phase == AzureProviderOperationPhase.CleanupSubmitted &&
                     providerOperation.AttemptedStep == AzureProviderRunnerStep.Cleanup &&
                     assignment is not null && assignment.State != AzureProviderAssignmentState.Deleted ||
                     verifiedCleanupFinalization);
                var assignmentIsCurrent = assignment is not null &&
                    assignment.OrganizationId == recovery.OrganizationId && assignment.InstanceId == request.InstanceId &&
                    assignment.LastOperationId == authority.ProviderOperationId &&
                    string.Equals(assignment.WorkloadName, authority.TargetKey, StringComparison.OrdinalIgnoreCase) &&
                    await ScopeMatchesOrReboundAsync(
                        request.WorkspaceId,
                        authority.ProviderAssignmentId,
                        authority.ProviderScopeFingerprint,
                        assignment.ProviderScopeFingerprint,
                        cancellationToken) &&
                    (assignment.State != AzureProviderAssignmentState.Deleted || verifiedCleanupFinalization);
                var competingOperation = providerOperation is not null && await db.AzureProviderOperations.AnyAsync(
                    x => x.Id != authority.ProviderOperationId && x.WorkspaceId == request.WorkspaceId &&
                         x.TargetKey == authority.TargetKey &&
                         (x.Status == AzureProviderOperationStatus.Accepted ||
                          x.Status == AzureProviderOperationStatus.Queued ||
                          x.Status == AzureProviderOperationStatus.EntitlementHeld ||
                          x.Status == AzureProviderOperationStatus.Running ||
                          x.Status == AzureProviderOperationStatus.RecoveryRequired),
                    cancellationToken);
                if (providerOperation is null || !lifecycleIsCurrent || !providerIsCurrent || !assignmentIsCurrent || competingOperation)
                    return null;

                providerOperation.Status = AzureProviderOperationStatus.Running;
                providerOperation.WorkerId = request.WorkerId;
                providerOperation.LeaseTokenHash = Hash(request.LeaseToken);
                providerOperation.CompletionLeaseTokenHash = null;
                providerOperation.CompletionFingerprint = null;
                providerOperation.LeaseExpiresAt = leaseExpires;
                providerOperation.HeartbeatAt = nowUtc;
                providerOperation.AttemptNumber = checked(providerOperation.AttemptNumber + 1);
                providerOperation.UpdatedAt = nowUtc;
                providerOperation.Version = checked(providerOperation.Version + 1);
                AddTransition(providerOperation,
                    "operation.delete-recovery.claimed",
                    "Explicit Azure delete recovery claimed.",
                    nowUtc);
                await db.SaveChangesAsync(cancellationToken);
                return ToModel(providerOperation);
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return null;
        }
    }

    private static bool HasCapturedPlacement(ElsaInstanceEntity instance, Guid assignmentId) =>
        Guid.TryParseExact(instance.PlacementAssignmentId, "D", out var placementAssignmentId) &&
        placementAssignmentId == assignmentId;

    private static string? NormalizeProviderScope(string? value) => value?.Trim().ToLowerInvariant();

    internal static bool IsVerifiedCleanupEligible(
        AzureProviderOperationEntity operation,
        AzureProviderResourceAssignmentEntity assignment) =>
        AzureProviderDeleteRecoverySupport.IsVerifiedCleanupEligible(
            ToModel(operation),
            ToModel(assignment));

    async Task<AzureProviderRecoveryObservationReceipt> IAzureProviderRecoveryObservationStore.CreateOrGetAsync(
        AzureProviderRecoveryObservationRecord observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        observation.Validate();
        await ValidateRecoveryObservationRecordAuthorityAsync(observation, cancellationToken);

        var existing = await FindRecoveryObservationAsync(observation, cancellationToken);
        if (existing is not null)
            return ToRecoveryObservationReceipt(existing);

        var now = observation.ObservedAt.ToUniversalTime();
        var entity = ToRecoveryObservationEntity(observation, now);
        db.AzureProviderRecoveryObservations.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return ToRecoveryObservationReceipt(entity);
        }
        catch (DbUpdateException)
        {
            // The unique natural key is the concurrency boundary. A losing
            // writer rereads the winner and returns the original immutable row;
            // it must never create a second audit record for an unchanged poll.
            db.ChangeTracker.Clear();
            existing = await FindRecoveryObservationAsync(observation, cancellationToken);
            if (existing is not null)
                return ToRecoveryObservationReceipt(existing);
            throw;
        }
    }

    async Task<AzureProviderRecoveryObservationRecord?> IAzureProviderRecoveryObservationStore.GetAndValidateRecordedAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid instanceId,
        Guid lifecycleOperationId,
        int observedLifecycleAttemptNumber,
        string reference,
        string digest,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty || workspaceId == Guid.Empty || instanceId == Guid.Empty ||
            lifecycleOperationId == Guid.Empty || observedLifecycleAttemptNumber < 1 ||
            !ElsaInstanceProviderRecoveryObservationReference.TryParse(reference, out var recordId, out var referenceDigest) ||
            !string.Equals(referenceDigest, digest, StringComparison.Ordinal))
            return null;

        var entity = await db.AzureProviderRecoveryObservations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == recordId &&
                                       x.OrganizationId == organizationId &&
                                       x.WorkspaceId == workspaceId &&
                                       x.InstanceId == instanceId &&
                                       x.LifecycleOperationId == lifecycleOperationId &&
                                       x.ObservedLifecycleAttemptNumber == observedLifecycleAttemptNumber,
                cancellationToken);
        if (entity is null)
            return null;

        var model = ToRecoveryObservation(entity);
        model.Validate();
        if (!string.Equals(model.ComputeRecordDigest(recordId), digest, StringComparison.Ordinal))
            return null;
        await ValidateRecoveryObservationStoredIntegrityAsync(model, cancellationToken);
        return model;
    }

    async Task<AzureProviderRecoveryObservationRecord?> IAzureProviderRecoveryObservationStore.GetAndValidateForAcceptedRecoveryAsync(
        AzureProviderRecoveryObservationBinding binding,
        CancellationToken cancellationToken)
    {
        var model = await LoadAcceptedRecoveryObservationAsync(binding, cancellationToken);
        if (model is null)
            return null;

        await ValidateRecoveryObservationStoredIntegrityAsync(model, cancellationToken);
        return model;
    }

    async Task<AzureProviderRecoveryObservationRecord?> IAzureProviderRecoveryObservationStore.GetAndValidateForAcceptedRecoveryReplayAsync(
        AzureProviderRecoveryObservationBinding binding,
        CancellationToken cancellationToken)
    {
        var model = await LoadAcceptedRecoveryObservationAsync(
            binding, cancellationToken, allowRecoveryRequiredLifecycleState: true);
        if (model is null)
            return null;

        var providerOperation = await LoadAndValidateRecoveryProviderOperationAsync(model, cancellationToken);
        if (providerOperation.Status is not (AzureProviderOperationStatus.Running or AzureProviderOperationStatus.Succeeded) ||
            model.ProviderAttemptNumber == int.MaxValue ||
            providerOperation.AttemptNumber != model.ProviderAttemptNumber + 1 ||
            providerOperation.Version <= model.ProviderVersion ||
            providerOperation.CheckpointSequence < model.ProviderCheckpointSequence)
            return null;

        ValidateRecoveryObservationProviderRequest(providerOperation);
        await ValidateRecoveryObservationAssignmentAsync(model, cancellationToken);
        await ValidateRecoveryObservationPlanAuthorityAsync(model, cancellationToken);
        return model;
    }

    private async Task<AzureProviderRecoveryObservationRecord?> LoadAcceptedRecoveryObservationAsync(
        AzureProviderRecoveryObservationBinding binding,
        CancellationToken cancellationToken,
        bool allowRecoveryRequiredLifecycleState = false)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.Validate();
        if (!ElsaInstanceProviderRecoveryObservationReference.TryParse(
                binding.Reference, out var recordId, out var referenceDigest))
            return null;

        var recovery = await db.ElsaInstanceRecoveryRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == binding.RecoveryRequestId, cancellationToken);
        if (recovery is null ||
            recovery.OrganizationId != binding.OrganizationId ||
            recovery.WorkspaceId != binding.WorkspaceId ||
            recovery.InstanceId != binding.InstanceId ||
            recovery.OperationId != binding.LifecycleOperationId ||
            recovery.AttemptNumber != binding.AcceptedLifecycleAttemptNumber ||
            !string.Equals(recovery.IdempotencyScope, binding.IdempotencyScope, StringComparison.Ordinal) ||
            !string.Equals(recovery.IdempotencyKey, binding.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(recovery.RequestHash, binding.RequestHash, StringComparison.Ordinal) ||
            !string.Equals(recovery.RecoveryObservationReference, binding.Reference, StringComparison.Ordinal) ||
            !string.Equals(recovery.RecoveryObservationDigest, binding.Digest, StringComparison.Ordinal) ||
            recovery.ObservedLifecycleAttemptNumber != binding.ObservedLifecycleAttemptNumber ||
            recovery.ObservedInstanceVersion != binding.ObservedInstanceVersion)
            return null;

        var entity = await db.AzureProviderRecoveryObservations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == recordId &&
                                       x.OrganizationId == binding.OrganizationId &&
                                       x.WorkspaceId == binding.WorkspaceId &&
                                       x.InstanceId == binding.InstanceId &&
                                       x.LifecycleOperationId == binding.LifecycleOperationId &&
                                       x.ObservedLifecycleAttemptNumber == binding.ObservedLifecycleAttemptNumber,
                cancellationToken);
        if (entity is null)
            return null;

        var model = ToRecoveryObservation(entity);
        model.Validate();
        if (!string.Equals(referenceDigest, binding.Digest, StringComparison.Ordinal) ||
            !string.Equals(model.ComputeRecordDigest(recordId), binding.Digest, StringComparison.Ordinal) ||
            model.ObservedInstanceVersion == int.MaxValue ||
            binding.ObservedInstanceVersion != model.ObservedInstanceVersion + 1)
            return null;

        var lifecycleOperation = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == binding.LifecycleOperationId &&
                                       x.OrganizationId == binding.OrganizationId &&
                                       x.WorkspaceId == binding.WorkspaceId &&
                                       x.InstanceId == binding.InstanceId,
                cancellationToken);
        var instance = await db.ElsaInstances.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == binding.InstanceId &&
                                       x.OrganizationId == binding.OrganizationId &&
                                       x.WorkspaceId == binding.WorkspaceId,
                cancellationToken);
        var lifecycleStateIsAccepted = lifecycleOperation?.State is ElsaInstanceOperationState.Queued or ElsaInstanceOperationState.Running ||
            allowRecoveryRequiredLifecycleState && lifecycleOperation?.State == ElsaInstanceOperationState.RecoveryRequired;
        if (lifecycleOperation is null || instance is null ||
            lifecycleOperation.Action != model.LifecycleAction ||
            lifecycleOperation.AttemptNumber != binding.AcceptedLifecycleAttemptNumber ||
            !lifecycleStateIsAccepted ||
            !string.Equals(lifecycleOperation.RecoveryIdempotencyScope, binding.IdempotencyScope, StringComparison.Ordinal) ||
            !string.Equals(lifecycleOperation.RecoveryIdempotencyKey, binding.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(lifecycleOperation.RecoveryRequestHash, binding.RequestHash, StringComparison.Ordinal) ||
            instance.Version != binding.AcceptedInstanceVersion)
            return null;

        return model;
    }

    public async Task<AzureProviderOperation> CreateOrGetAsync(
        AzureProviderOperationRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        (await CreateOrGetWithResultAsync(request, now, cancellationToken)).Operation;

    public async Task<AzureProviderOperationCreateResult> CreateOrGetWithResultAsync(
        AzureProviderOperationRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var normalized = AzureProviderOperationValidation.Normalize(request);
        var hash = AzureProviderOperationValidation.ComputeRequestHash(normalized);
        var identity = AzureProviderOperationValidation.ComputeOperationIdentity(normalized);
        // Safe-exit supersession and successor reservation are one serializable
        // decision. Two Stop/Delete requests must not both observe the same
        // held predecessor and race to insert successors.
        Guid? supersededHeldOperationId = null;
        try
        {
            return await db.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
            {
                var existing = await FindByKeyAsync(normalized, cancellationToken);
                var identityEntity = await db.AzureProviderOperations.AsNoTracking()
                    .Where(x => x.WorkspaceId == normalized.WorkspaceId && x.TargetKey == normalized.TargetKey &&
                                x.OperationIdentity == identity &&
                                (x.Status == AzureProviderOperationStatus.Accepted || x.Status == AzureProviderOperationStatus.Queued || x.Status == AzureProviderOperationStatus.EntitlementHeld ||
                                 x.Status == AzureProviderOperationStatus.Running || x.Status == AzureProviderOperationStatus.RecoveryRequired))
                    .SingleOrDefaultAsync(cancellationToken);
                existing ??= identityEntity is null ? null : ToModel(identityEntity);
                if (existing is not null)
                    return new(EnsureSameRequest(existing, hash), Replayed: true);

                // Reset per attempt; the recovery read below uses the value from the failed one.
                supersededHeldOperationId = null;
                if (normalized.LifecycleAction is ElsaInstanceOperationAction.Stop or ElsaInstanceOperationAction.Delete)
                {
                    var heldSafeExit = await FindBoundEntitlementHeldAsync(normalized, cancellationToken);
                    if (heldSafeExit is not null)
                    {
                        supersededHeldOperationId = heldSafeExit.Id;
                        heldSafeExit.Status = AzureProviderOperationStatus.Cancelled;
                        heldSafeExit.CompletedAt = now;
                        heldSafeExit.UpdatedAt = now;
                        heldSafeExit.Version++;
                        heldSafeExit.WorkerId = null;
                        heldSafeExit.LeaseTokenHash = null;
                        heldSafeExit.CompletionLeaseTokenHash = null;
                        heldSafeExit.CompletionFingerprint = null;
                        heldSafeExit.LeaseExpiresAt = null;
                        heldSafeExit.HeartbeatAt = null;
                        AddTransition(
                            heldSafeExit,
                            ElsaInstanceCommercialOperation.EntitlementSafeExitSuperseded,
                            "The Azure provider operation was superseded by a safe lifecycle exit.",
                            now);
                    }
                }

                var activeTargetEntity = await FindActiveTargetAsync(normalized, supersededHeldOperationId, cancellationToken);
                if (activeTargetEntity is not null)
                    throw new AzureProviderOperationConflictException(ToModel(activeTargetEntity));
                var previousResources = await GetLatestReconcileAsync(
                    normalized.WorkspaceId,
                    normalized.TargetKey,
                    normalized.ProviderScopeFingerprint,
                    cancellationToken);

                var entity = new AzureProviderOperationEntity
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = normalized.WorkspaceId,
                    OrganizationId = normalized.OrganizationId,
                    InstanceId = normalized.InstanceId,
                    ProviderAssignmentId = normalized.ProviderAssignmentId,
                    LifecycleAction = normalized.LifecycleAction,
                    TargetKey = normalized.TargetKey,
                    Action = normalized.Action,
                    IdempotencyKey = normalized.IdempotencyKey,
                    RequestHash = hash,
                    OperationIdentity = identity,
                    PlanFingerprint = normalized.PlanFingerprint,
                    TemplateFingerprint = normalized.TemplateFingerprint,
                    ProviderScopeFingerprint = normalized.ProviderScopeFingerprint,
                    SqlWorkflowPackageVersion = normalized.SqlWorkflowPackageVersion,
                    SqlQuartzPackageVersion = normalized.SqlQuartzPackageVersion,
                    CapacityMinReplicas = normalized.Capacity?.MinReplicas,
                    CapacityMaxReplicas = normalized.Capacity?.MaxReplicas,
                    CapacityCpuMillicores = normalized.Capacity?.CpuMillicores,
                    CapacityMemoryMiB = normalized.Capacity?.MemoryMiB,
                    ManagedHandoff = normalized.ManagedHandoff,
                    ElsaVersion = normalized.ElsaVersion,
                    ReleaseLine = normalized.ReleaseLine,
                    Topology = normalized.Topology,
                    Isolation = normalized.Isolation,
                    Location = normalized.Location,
                    ImageRepository = normalized.ImageRepository,
                    ImageDigest = normalized.ImageDigest,
                    ReleaseManifestDigest = normalized.ReleaseManifestDigest,
                    ReleaseManifestSignatureDigest = normalized.ReleaseManifestSignatureDigest,
                    ReleaseManifestReference = normalized.ReleaseManifestReference,
                    ReleaseManifestSignatureReference = normalized.ReleaseManifestSignatureReference,
                    SecretReferencesJson = JsonSerializer.Serialize(normalized.SecretReferences),
                    Status = AzureProviderOperationStatus.Accepted,
                    Phase = AzureProviderOperationPhase.Planned,
                    CheckpointSequence = 0,
                    AttemptNumber = 0,
                    Version = 1,
                    Health = AzureProviderHealth.Unknown,
                    CreatedAt = now,
                    UpdatedAt = now,
                    ResourceGroupName = previousResources?.Resources.ResourceGroupName,
                    FoundationDeploymentId = previousResources?.Resources.FoundationDeploymentId,
                    WorkloadDeploymentId = previousResources?.Resources.WorkloadDeploymentId,
                    WorkloadResourceId = previousResources?.Resources.WorkloadResourceId,
                    WorkloadRevisionName = previousResources?.Resources.WorkloadRevisionName,
                    StableTrafficRevisionName = previousResources?.Resources.StableTrafficRevisionName,
                    WorkloadIdentityResourceId = previousResources?.Resources.WorkloadIdentityResourceId,
                    WorkloadIdentityClientId = previousResources?.Resources.WorkloadIdentityClientId,
                    WorkloadIdentityPrincipalId = previousResources?.Resources.WorkloadIdentityPrincipalId,
                    KeyVaultResourceId = previousResources?.Resources.KeyVaultResourceId,
                    KeyVaultUri = previousResources?.Resources.KeyVaultUri,
                    SqlServerResourceId = previousResources?.Resources.SqlServerResourceId,
                    SqlServerFqdn = previousResources?.Resources.SqlServerFqdn,
                    ContainerAppsEnvironmentResourceId = previousResources?.Resources.ContainerAppsEnvironmentResourceId,
                    RegistryResourceId = previousResources?.Resources.RegistryResourceId,
                    AcrPullDeploymentId = previousResources?.Resources.AcrPullDeploymentId,
                    AcrPullRoleAssignmentId = previousResources?.Resources.AcrPullRoleAssignmentId
                };
                entity.Transitions.Add(new AzureProviderOperationTransitionEntity
                {
                    Id = Guid.NewGuid(),
                    OperationId = entity.Id,
                    Sequence = 1,
                    Status = entity.Status,
                    Phase = entity.Phase,
                    Code = "operation.accepted",
                    Message = "Azure provider operation accepted.",
                    OccurredAt = now
                });
                db.AzureProviderOperations.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return new AzureProviderOperationCreateResult(ToModel(entity), Replayed: false);
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            // The failed transaction may have staged the safe-exit transition,
            // so reread the winner in a fresh serializable transaction.
            var winner = await db.ExecuteInTransactionAsync<AzureProviderOperationCreateResult?>(IsolationLevel.Serializable, async () =>
            {
                var existing = await FindByKeyAsync(normalized, cancellationToken);
                var identityEntity = await db.AzureProviderOperations.AsNoTracking()
                    .Where(x => x.WorkspaceId == normalized.WorkspaceId && x.TargetKey == normalized.TargetKey && x.OperationIdentity == identity &&
                                (x.Status == AzureProviderOperationStatus.Accepted || x.Status == AzureProviderOperationStatus.Queued || x.Status == AzureProviderOperationStatus.EntitlementHeld ||
                                 x.Status == AzureProviderOperationStatus.Running || x.Status == AzureProviderOperationStatus.RecoveryRequired))
                    .SingleOrDefaultAsync(cancellationToken);
                existing ??= identityEntity is null ? null : ToModel(identityEntity);
                if (existing is not null)
                    return new AzureProviderOperationCreateResult(EnsureSameRequest(existing, hash), Replayed: true);
                var activeTargetEntity = await FindActiveTargetAsync(normalized, supersededHeldOperationId, cancellationToken);
                if (activeTargetEntity is not null)
                    throw new AzureProviderOperationConflictException(ToModel(activeTargetEntity));
                return null;
            }, cancellationToken);
            if (winner is null)
                throw;
            return winner;
        }
    }

    public async Task<AzureProviderOperation?> GetAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken = default) =>
        await db.AzureProviderOperations.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == operationId, cancellationToken) is { } entity ? ToModel(entity) : null;

    async Task<AzureProviderResourceAssignment> IAzureProviderResourceAssignmentStore.CreateOrGetAsync(
        AzureProviderResourceAssignmentRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAssignmentRequest(request);
        request.Rebind?.Validate();
        try
        {
            return await db.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
            {
                var existing = await TryGetOrRebindAssignmentAsync(
                    request.WorkspaceId,
                    request.InstanceId,
                    NormalizeProviderScope(request.ProviderScopeFingerprint)!,
                    request.SubscriptionId,
                    request.ResourceGroupNamePrefix,
                    request.NamingVersion,
                    request.Rebind,
                    now,
                    createRequest: request,
                    cancellationToken);
                if (existing is not null)
                    return ToModel(existing);

                var assignmentId = Guid.NewGuid();
                var resourceGroupName = AzureProviderResourceAssignmentNaming.ResourceGroupName(
                    request.ResourceGroupNamePrefix, request.InstanceId, request.NamingVersion);
                var entity = new AzureProviderResourceAssignmentEntity
                {
                    Id = assignmentId,
                    WorkspaceId = request.WorkspaceId,
                    OrganizationId = request.OrganizationId,
                    InstanceId = request.InstanceId,
                    ProviderScopeFingerprint = request.ProviderScopeFingerprint.ToLowerInvariant(),
                    NamingVersion = request.NamingVersion,
                    SubscriptionId = request.SubscriptionId.ToLowerInvariant(),
                    ResourceGroupName = resourceGroupName,
                    WorkloadName = request.WorkloadName,
                    OwnershipKey = AzureProviderResourceAssignmentNaming.OwnershipKey(
                        assignmentId, request.InstanceId, request.ProviderScopeFingerprint),
                    Location = request.Location.ToLowerInvariant(),
                    State = AzureProviderAssignmentState.Reserved,
                    Version = 1,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.AzureProviderResourceAssignments.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return ToModel(entity);
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var existing = await db.AzureProviderResourceAssignments.AsNoTracking()
                .SingleOrDefaultAsync(x => x.WorkspaceId == request.WorkspaceId &&
                                           x.InstanceId == request.InstanceId &&
                                           x.ProviderScopeFingerprint == request.ProviderScopeFingerprint,
                    cancellationToken);
            if (existing is null)
                throw;
            EnsureSameAssignment(existing, request);
            return ToModel(existing);
        }
    }

    async Task<AzureProviderResourceAssignment?> IAzureProviderResourceAssignmentStore.GetAsync(
        Guid workspaceId,
        Guid assignmentId,
        CancellationToken cancellationToken)
    {
        if (workspaceId == Guid.Empty || assignmentId == Guid.Empty)
            throw new ArgumentException("The Azure assignment identity is invalid.");
        var entity = await db.AzureProviderResourceAssignments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == assignmentId, cancellationToken);
        return entity is null ? null : ToModel(entity);
    }

    async Task<AzureProviderResourceAssignment?> IAzureProviderResourceAssignmentStore.RebindToCurrentScopeAsync(
        AzureProviderAssignmentScopeAuthority authority,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ValidateScopeAuthority(authority);
        authority.Rebind?.Validate();
        return await db.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            var entity = await TryGetOrRebindAssignmentAsync(
                authority.WorkspaceId,
                authority.InstanceId,
                NormalizeProviderScope(authority.ProviderScopeFingerprint)!,
                authority.SubscriptionId,
                authority.ResourceGroupNamePrefix,
                authority.NamingVersion,
                authority.Rebind,
                now,
                createRequest: null,
                cancellationToken);
            return entity is null ? null : ToModel(entity);
        }, cancellationToken);
    }

    async Task<bool> IAzureProviderResourceAssignmentStore.HasRebindLineageAsync(
        Guid workspaceId,
        Guid assignmentId,
        string fromProviderScopeFingerprint,
        string toProviderScopeFingerprint,
        CancellationToken cancellationToken)
    {
        if (workspaceId == Guid.Empty || assignmentId == Guid.Empty)
            throw new ArgumentException("The Azure assignment identity is invalid.");
        var from = NormalizeProviderScope(fromProviderScopeFingerprint);
        var to = NormalizeProviderScope(toProviderScopeFingerprint);
        if (from is null || to is null)
            return false;
        if (string.Equals(from, to, StringComparison.Ordinal))
            return true;

        var edges = await db.AzureProviderAssignmentRebinds.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.AssignmentId == assignmentId)
            .Select(x => new { x.FromProviderScopeFingerprint, x.ToProviderScopeFingerprint })
            .ToListAsync(cancellationToken);
        return HasRebindPath(edges.Select(x => (x.FromProviderScopeFingerprint, x.ToProviderScopeFingerprint)), from, to);
    }

    async Task<IReadOnlyList<AzureProviderAssignmentRebindRecord>> IAzureProviderResourceAssignmentStore.ListRebindsAsync(
        Guid workspaceId,
        Guid assignmentId,
        CancellationToken cancellationToken)
    {
        if (workspaceId == Guid.Empty || assignmentId == Guid.Empty)
            throw new ArgumentException("The Azure assignment identity is invalid.");
        var records = await db.AzureProviderAssignmentRebinds.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.AssignmentId == assignmentId)
            .OrderBy(x => x.OccurredAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return records.Select(x => x.ToRecord()).ToArray();
    }

    public async Task<IReadOnlyList<AzureProviderOperation>> ListRunnableAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var operations = await db.AzureProviderOperations.AsNoTracking()
            .Where(x => (x.Status == AzureProviderOperationStatus.Accepted ||
                         x.Status == AzureProviderOperationStatus.Queued ||
                         x.Status == AzureProviderOperationStatus.EntitlementHeld) &&
                        // Keep legacy rows marked by an unrestorable-plan
                        // transition out of automatic polling. RecoveryRequired
                        // is already excluded by the status predicate and uses
                        // explicit provider recovery instead.
                        !db.AzureProviderOperationTransitions.Any(transition =>
                            transition.OperationId == x.Id &&
                            transition.Code == "azure.plan.unrestorable") &&
                        x.CompletedAt == null &&
                        (x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now))
            .OrderBy(x => x.UpdatedAt)
            .ThenBy(x => x.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return operations.Select(ToModel).ToList();
    }

    public async Task<AzureProviderOperation?> GetLatestReconcileAsync(
        Guid workspaceId,
        string targetKey,
        string? providerScopeFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetKey))
            throw new ArgumentException("Target key is required.", nameof(targetKey));

        var normalizedTargetKey = targetKey.Trim().ToLowerInvariant();
        var normalizedProviderScopeFingerprint = providerScopeFingerprint?.Trim().ToLowerInvariant();
        var entity = await FindLatestReconcileEntityAsync(
            workspaceId,
            normalizedTargetKey,
            normalizedProviderScopeFingerprint,
            activeOnly: false,
            cancellationToken);
        return entity is null ? null : ToModel(entity);
    }

    /// <summary>
    /// Finds the active reconcile reservation even when interruption happened before the first
    /// resource checkpoint. This is used only by the explicit disposable proof cleanup recovery.
    /// </summary>
    public async Task<AzureProviderOperation?> GetLatestActiveReconcileAsync(
        Guid workspaceId,
        string targetKey,
        string? providerScopeFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetKey))
            throw new ArgumentException("Target key is required.", nameof(targetKey));

        var normalizedTargetKey = targetKey.Trim().ToLowerInvariant();
        var normalizedProviderScopeFingerprint = providerScopeFingerprint?.Trim().ToLowerInvariant();
        var entity = await FindLatestReconcileEntityAsync(
            workspaceId,
            normalizedTargetKey,
            normalizedProviderScopeFingerprint,
            activeOnly: true,
            cancellationToken);
        return entity is null ? null : ToModel(entity);
    }

    public async Task<AzureProviderOperation?> MarkUnrestorableAsync(
        Guid workspaceId,
        Guid operationId,
        DateTimeOffset now,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        return await db.ExecuteInTransactionAsync<AzureProviderOperation?>(IsolationLevel.Unspecified, async () =>
        {
            var changed = await db.AzureProviderOperations
                .Where(x => x.WorkspaceId == workspaceId && x.Id == operationId &&
                             (x.Status == AzureProviderOperationStatus.Accepted ||
                              x.Status == AzureProviderOperationStatus.Queued || x.Status == AzureProviderOperationStatus.EntitlementHeld ||
                             x.Status == AzureProviderOperationStatus.RecoveryRequired) &&
                            (!expectedVersion.HasValue || x.Version == expectedVersion.Value))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, x => x.Status == AzureProviderOperationStatus.RecoveryRequired
                        ? AzureProviderOperationStatus.RecoveryRequired
                        : AzureProviderOperationStatus.Failed)
                    .SetProperty(x => x.CompletedAt, x => x.Status == AzureProviderOperationStatus.RecoveryRequired ? null : now)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.Version, x => x.Version + 1)
                    .SetProperty(x => x.LeaseTokenHash, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.WorkerId, (string?)null)
                    .SetProperty(x => x.CompletionLeaseTokenHash, (string?)null)
                    .SetProperty(x => x.CompletionFingerprint, (string?)null), cancellationToken);
            if (changed == 0)
                return null;

            db.ChangeTracker.Clear();
            var entity = await db.AzureProviderOperations
                .SingleAsync(x => x.WorkspaceId == workspaceId && x.Id == operationId, cancellationToken);
            AddTransition(
                entity,
                "azure.plan.unrestorable",
                "The persisted provider plan cannot be restored.",
                now);
            await db.SaveChangesAsync(cancellationToken);
            return ToModel(entity);
        }, cancellationToken);
    }

    public Task<AzureProviderOperation?> ClaimAsync(Guid workspaceId, Guid operationId, string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
        ClaimCoreAsync(workspaceId, operationId, workerId, leaseToken, leaseDuration, now, expectedVersion, allowRecovery: false, cancellationToken);

    public Task<AzureProviderOperation?> ClaimRecoveryAsync(Guid workspaceId, Guid operationId, string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
        ClaimCoreAsync(workspaceId, operationId, workerId, leaseToken, leaseDuration, now, expectedVersion, allowRecovery: true, cancellationToken);

    public async Task<AzureProviderOperationAuthorizationResult?> AuthorizeAsync(
        Guid workspaceId,
        Guid operationId,
        string leaseToken,
        IElsaInstanceCommercialGate commercialGate,
        DateTimeOffset now,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty || operationId == Guid.Empty)
            throw new ArgumentException("A complete provider operation identity is required.");
        AzureProviderOperationValidation.ValidateLeaseToken(leaseToken);
        ArgumentNullException.ThrowIfNull(commercialGate);

        db.ChangeTracker.Clear();
        return await db.ExecuteInTransactionAsync<AzureProviderOperationAuthorizationResult?>(IsolationLevel.Serializable, async () =>
        {
            var entity = await db.AzureProviderOperations.SingleOrDefaultAsync(
                x => x.WorkspaceId == workspaceId && x.Id == operationId,
                cancellationToken);
            if (entity is null || entity.Status != AzureProviderOperationStatus.Running ||
                !LeaseMatches(entity, leaseToken, now) ||
                expectedVersion.HasValue && entity.Version != expectedVersion.Value)
                return null;

            var decision = entity.OrganizationId is not { } organizationId || organizationId == Guid.Empty ||
                           entity.InstanceId is not { } instanceId || instanceId == Guid.Empty ||
                           entity.LifecycleAction is not { } lifecycleAction
                ? new ElsaInstanceCommercialGateDecision(
                    false,
                    ElsaInstanceCommercialOperation.BindingRequired,
                    "The managed-instance provider operation is missing its durable identity binding.")
                : await commercialGate.EvaluateAsync(
                    organizationId,
                    lifecycleAction,
                    cancellationToken: cancellationToken);

            if (decision.Allowed)
                return new AzureProviderOperationAuthorizationResult(ToModel(entity), decision);

            entity.Status = AzureProviderOperationStatus.EntitlementHeld;
            entity.CompletedAt = null;
            entity.UpdatedAt = now;
            entity.Version++;
            entity.CompletionLeaseTokenHash = entity.LeaseTokenHash;
            entity.CompletionFingerprint = Hash($"{AzureProviderOperationStatus.EntitlementHeld}|{decision.Code}");
            entity.LeaseTokenHash = null;
            entity.LeaseExpiresAt = null;
            entity.WorkerId = null;
            AddTransition(entity, decision.Code, decision.Summary, now);
            await db.SaveChangesAsync(cancellationToken);
            return new AzureProviderOperationAuthorizationResult(ToModel(entity), decision);
        }, cancellationToken);
    }

    private async Task<AzureProviderOperation?> ClaimCoreAsync(Guid workspaceId, Guid operationId, string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion, bool allowRecovery, CancellationToken cancellationToken)
    {
        AzureProviderOperationValidation.ValidateWorkerId(workerId);
        AzureProviderOperationValidation.ValidateLeaseToken(leaseToken);
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1)) throw new ArgumentException("Lease duration is required and bounded.");
        var hash = Hash(leaseToken);
        DateTimeOffset leaseExpires;
        try { leaseExpires = now.Add(leaseDuration); } catch (ArgumentOutOfRangeException) { throw new ArgumentException("Lease duration overflowed.", nameof(leaseDuration)); }
        db.ChangeTracker.Clear();
        return await db.ExecuteInTransactionAsync<AzureProviderOperation?>(IsolationLevel.Unspecified, async () =>
        {
            var changed = await db.AzureProviderOperations.Where(x => x.WorkspaceId == workspaceId && x.Id == operationId &&
                     (allowRecovery
                         ? x.Status == AzureProviderOperationStatus.RecoveryRequired
                         : x.Status == AzureProviderOperationStatus.Accepted || x.Status == AzureProviderOperationStatus.Queued || x.Status == AzureProviderOperationStatus.EntitlementHeld) &&
                     (x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now) && (!expectedVersion.HasValue || x.Version == expectedVersion.Value) &&
                     !db.AzureProviderOperations.Any(other => other.Id != operationId && other.WorkspaceId == workspaceId &&
                         other.TargetKey == x.TargetKey &&
                         (other.Status == AzureProviderOperationStatus.Accepted || other.Status == AzureProviderOperationStatus.Queued || other.Status == AzureProviderOperationStatus.EntitlementHeld ||
                          other.Status == AzureProviderOperationStatus.Running || other.Status == AzureProviderOperationStatus.RecoveryRequired)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, AzureProviderOperationStatus.Running)
                    .SetProperty(x => x.WorkerId, workerId)
                    .SetProperty(x => x.LeaseTokenHash, hash)
                    .SetProperty(x => x.CompletionLeaseTokenHash, (string?)null)
                    .SetProperty(x => x.CompletionFingerprint, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, leaseExpires)
                    .SetProperty(x => x.HeartbeatAt, now)
                    .SetProperty(x => x.AttemptNumber, x => x.AttemptNumber + 1)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
            if (changed == 0)
            {
                var replay = await db.AzureProviderOperations.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.WorkspaceId == workspaceId && x.Id == operationId &&
                    x.Status == AzureProviderOperationStatus.Running && x.WorkerId == workerId &&
                    x.LeaseTokenHash == hash && x.LeaseExpiresAt > now, cancellationToken);
                return replay is null ? null : ToModel(replay);
            }
            db.ChangeTracker.Clear();
            var entity = await db.AzureProviderOperations.SingleAsync(x => x.Id == operationId, cancellationToken);
            AddTransition(entity, allowRecovery ? "operation.recovery.claimed" : "operation.claimed", allowRecovery ? "Recovery reconciliation claimed." : "Azure provider operation claimed.", now);
            await db.SaveChangesAsync(cancellationToken);
            return ToModel(entity);
        }, cancellationToken);
    }

    public async Task<AzureProviderOperation?> HeartbeatAsync(Guid workspaceId, Guid operationId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        AzureProviderOperationValidation.ValidateLeaseToken(leaseToken);
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1)) throw new ArgumentException("Lease duration must be positive and bounded.");
        DateTimeOffset leaseExpires;
        try { leaseExpires = now.Add(leaseDuration); } catch (ArgumentOutOfRangeException) { throw new ArgumentException("Lease duration overflowed.", nameof(leaseDuration)); }
        var entity = await db.AzureProviderOperations.SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == operationId, cancellationToken);
        if (entity is null || entity.Status != AzureProviderOperationStatus.Running || !LeaseMatches(entity, leaseToken, now) || expectedVersion.HasValue && entity.Version != expectedVersion.Value) return null;
        entity.HeartbeatAt = now; entity.LeaseExpiresAt = leaseExpires; entity.UpdatedAt = now; entity.Version++;
        try { await db.SaveChangesAsync(cancellationToken); } catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return null; }
        return ToModel(entity);
    }

    public async Task<AzureProviderOperation?> CheckpointAsync(Guid workspaceId, Guid operationId, string leaseToken, AzureProviderCheckpoint checkpoint, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        AzureProviderOperationValidation.ValidateLeaseToken(leaseToken);
        AzureProviderOperationValidation.ValidateCheckpoint(checkpoint);
        var entity = await db.AzureProviderOperations.SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == operationId, cancellationToken);
        if (entity is null || entity.Status != AzureProviderOperationStatus.Running || !LeaseMatches(entity, leaseToken, now) || expectedVersion.HasValue && entity.Version != expectedVersion.Value) return null;
        if (AzureProviderOperationPhaseOrdering.Compare(checkpoint.Phase, entity.Phase) < 0)
            throw new InvalidOperationException("Checkpoint phase cannot move backwards.");
        AzureProviderResourceAssignmentEntity? assignment = null;
        if (entity.ProviderAssignmentId is { } assignmentId)
        {
            assignment = await db.AzureProviderResourceAssignments.SingleOrDefaultAsync(
                x => x.Id == assignmentId && x.WorkspaceId == workspaceId,
                cancellationToken);
            if (assignment is null || assignment.OrganizationId != entity.OrganizationId || assignment.InstanceId != entity.InstanceId)
                throw new InvalidOperationException("The Azure provider assignment binding is invalid.");
        }
        var safeDiagnostics = checkpoint.Diagnostics
            .Select(x => new AzureProviderDiagnostic(x.Code, x.Code))
            .ToArray();
        var diagnosticsJson = JsonSerializer.Serialize(safeDiagnostics);
        var resources = checkpoint.ReplaceResources
            ? checkpoint.Resources
            : MergeResources(entity, checkpoint.Resources);
        if (assignment is not null)
        {
            if (resources.ResourceGroupName is not null &&
                !string.Equals(resources.ResourceGroupName, assignment.ResourceGroupName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Azure provider assignment binding is invalid.");

            // The assignment is the immutable resource-group authority. Preserve its original
            // spelling in operation snapshots, including when cleanup replaces the inventory.
            resources = resources with { ResourceGroupName = assignment.ResourceGroupName };
        }
        var lastTransitionCode = await db.AzureProviderOperationTransitions.AsNoTracking()
            .Where(x => x.OperationId == entity.Id)
            .OrderByDescending(x => x.Sequence)
            .Select(x => x.Code)
            .FirstOrDefaultAsync(cancellationToken);
        var endpoint = checkpoint.Endpoint is null
            ? entity.Endpoint
            : AzureProviderOperationValidation.NormalizeEndpoint(checkpoint.Endpoint);
        var health = checkpoint.Health == AzureProviderHealth.Unknown ? entity.Health : checkpoint.Health;
        if (entity.Phase == checkpoint.Phase && entity.Endpoint == endpoint && entity.Health == health &&
            entity.DiagnosticsJson == diagnosticsJson && ResourcesEqual(entity, resources) &&
            entity.AttemptedStep == checkpoint.AttemptedStep &&
            lastTransitionCode == checkpoint.Code)
            return ToModel(entity);
        entity.Phase = checkpoint.Phase; entity.AttemptedStep = checkpoint.AttemptedStep;
        entity.CheckpointSequence++; entity.Version++; entity.UpdatedAt = now;
        entity.ResourceGroupName = resources.ResourceGroupName; entity.FoundationDeploymentId = resources.FoundationDeploymentId;
        entity.WorkloadDeploymentId = resources.WorkloadDeploymentId; entity.WorkloadResourceId = resources.WorkloadResourceId;
        entity.WorkloadRevisionName = resources.WorkloadRevisionName; entity.StableTrafficRevisionName = resources.StableTrafficRevisionName;
        entity.WorkloadIdentityResourceId = resources.WorkloadIdentityResourceId;
        entity.WorkloadIdentityClientId = resources.WorkloadIdentityClientId;
        entity.WorkloadIdentityPrincipalId = resources.WorkloadIdentityPrincipalId;
        entity.KeyVaultResourceId = resources.KeyVaultResourceId; entity.KeyVaultUri = resources.KeyVaultUri;
        entity.SqlServerResourceId = resources.SqlServerResourceId; entity.SqlServerFqdn = resources.SqlServerFqdn;
        entity.ContainerAppsEnvironmentResourceId = resources.ContainerAppsEnvironmentResourceId;
        entity.RegistryResourceId = resources.RegistryResourceId;
        entity.AcrPullDeploymentId = resources.AcrPullDeploymentId;
        entity.AcrPullRoleAssignmentId = resources.AcrPullRoleAssignmentId;
        entity.Endpoint = endpoint; entity.Health = health;
        entity.DiagnosticsJson = diagnosticsJson;
        if (assignment is not null)
        {
            ApplyAssignmentResources(assignment, resources);
            assignment.LastOperationId = entity.Id;
            assignment.State = checkpoint.Phase == AzureProviderOperationPhase.CleanupVerified
                ? AzureProviderAssignmentState.Deleted
                : AzureProviderAssignmentState.Provisioning;
            assignment.DeletedAt = checkpoint.Phase == AzureProviderOperationPhase.CleanupVerified ? now : null;
            assignment.UpdatedAt = now;
            assignment.Version++;
        }
        AddTransition(entity, checkpoint.Code, checkpoint.Code, now);
        try { await db.SaveChangesAsync(cancellationToken); } catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return null; }
        return ToModel(entity);
    }

    public async Task<AzureProviderOperation?> FinalizeAsync(Guid workspaceId, Guid operationId, string leaseToken, AzureProviderOperationStatus status, string code, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        AzureProviderOperationValidation.ValidateLeaseToken(leaseToken);
        AzureProviderOperationValidation.ValidateCode(code);
        if (status is not (AzureProviderOperationStatus.Succeeded or AzureProviderOperationStatus.Failed or AzureProviderOperationStatus.Cancelled or AzureProviderOperationStatus.RecoveryRequired or AzureProviderOperationStatus.EntitlementHeld))
            throw new ArgumentException("Invalid final operation status.", nameof(status));
        var entity = await db.AzureProviderOperations.SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == operationId, cancellationToken);
        if (entity is null) return null;
        var completionFingerprint = Hash($"{status}|{code}");
        if (entity.Status == status) return entity.CompletionLeaseTokenHash == Hash(leaseToken) && entity.CompletionFingerprint == completionFingerprint ? ToModel(entity) : null;
        if (entity.Status != AzureProviderOperationStatus.Running || !LeaseMatches(entity, leaseToken, now) || expectedVersion.HasValue && entity.Version != expectedVersion.Value) return null;
        AzureProviderResourceAssignmentEntity? assignment = null;
        if (entity.ProviderAssignmentId is { } assignmentId)
        {
            assignment = await db.AzureProviderResourceAssignments.SingleOrDefaultAsync(
                x => x.Id == assignmentId && x.WorkspaceId == workspaceId,
                cancellationToken);
            if (assignment is null)
                throw new InvalidOperationException("The Azure provider assignment binding is unavailable.");
            if (assignment.OrganizationId != entity.OrganizationId || assignment.InstanceId != entity.InstanceId)
                throw new InvalidOperationException("The Azure provider assignment binding is invalid.");
        }
        entity.Status = status; entity.UpdatedAt = now; entity.Version++;
        // Recovery-required operations stay reservable for operator reconciliation, so they are
        // never stamped as completed regardless of which transition produced the status.
        entity.CompletedAt = status is AzureProviderOperationStatus.RecoveryRequired or AzureProviderOperationStatus.EntitlementHeld ? null : now;
        entity.CompletionLeaseTokenHash = entity.LeaseTokenHash;
        entity.CompletionFingerprint = completionFingerprint;
        entity.LeaseTokenHash = null; entity.LeaseExpiresAt = null; entity.WorkerId = null;
        if (assignment is not null)
        {
            assignment.LastOperationId = entity.Id;
            assignment.State = status switch
            {
                AzureProviderOperationStatus.Succeeded when entity.Action == AzureProviderOperationAction.Delete => AzureProviderAssignmentState.Deleted,
                AzureProviderOperationStatus.Succeeded => AzureProviderAssignmentState.Active,
                AzureProviderOperationStatus.RecoveryRequired => AzureProviderAssignmentState.Unknown,
                _ => assignment.State
            };
            assignment.DeletedAt = assignment.State == AzureProviderAssignmentState.Deleted ? now : null;
            assignment.UpdatedAt = now;
            assignment.Version++;
        }
        AddTransition(entity, code, code, now);
        try { await db.SaveChangesAsync(cancellationToken); } catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return null; }
        return ToModel(entity);
    }

    public async Task<int> RecoverStaleAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        db.ChangeTracker.Clear();
        return await db.ExecuteInTransactionAsync(IsolationLevel.Unspecified, async () =>
        {
            var candidates = await db.AzureProviderOperations.AsNoTracking()
                .Where(x => x.Status == AzureProviderOperationStatus.Running && x.LeaseExpiresAt != null && x.LeaseExpiresAt <= now)
                .ToListAsync(cancellationToken);
            var recovered = 0;
            foreach (var candidate in candidates)
            {
                var changed = await db.AzureProviderOperations.Where(x => x.Id == candidate.Id && x.Status == AzureProviderOperationStatus.Running && x.Version == candidate.Version && x.LeaseExpiresAt != null && x.LeaseExpiresAt <= now)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, AzureProviderOperationStatus.RecoveryRequired)
                        .SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.Version, x => x.Version + 1)
                        .SetProperty(x => x.LeaseTokenHash, (string?)null).SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(x => x.WorkerId, (string?)null)
                        .SetProperty(x => x.CompletionLeaseTokenHash, (string?)null)
                        .SetProperty(x => x.CompletionFingerprint, (string?)null), cancellationToken);
                if (changed == 0) continue;
                recovered++;
                candidate.Status = AzureProviderOperationStatus.RecoveryRequired;
                candidate.Version++;
                AddTransition(candidate, "operation.recovery.required", "The operation lease expired before completion.", now);
            }
            await db.SaveChangesAsync(cancellationToken);
            return recovered;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<AzureProviderOperationTransition>> ListTransitionsAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken = default)
    {
        if (!await db.AzureProviderOperations.AsNoTracking()
                .AnyAsync(x => x.Id == operationId && x.WorkspaceId == workspaceId, cancellationToken))
            return [];

        return (await db.AzureProviderOperationTransitions.AsNoTracking()
                .Where(x => x.OperationId == operationId)
                .OrderBy(x => x.Sequence)
                .ToListAsync(cancellationToken))
            .Select(ToTransition)
            .ToList();
    }

    private async Task<AzureProviderOperation?> FindByKeyAsync(AzureProviderOperationRequest request, CancellationToken cancellationToken) =>
        await db.AzureProviderOperations.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == request.WorkspaceId && x.TargetKey == request.TargetKey && x.IdempotencyKey == request.IdempotencyKey, cancellationToken) is { } entity ? ToModel(entity) : null;

    private async Task ValidateRecoveryObservationRecordAuthorityAsync(
        AzureProviderRecoveryObservationRecord observation,
        CancellationToken cancellationToken)
    {
        var providerOperation = await LoadAndValidateRecoveryProviderOperationAsync(observation, cancellationToken);
        if (providerOperation.Status != AzureProviderOperationStatus.RecoveryRequired ||
            providerOperation.AttemptNumber != observation.ProviderAttemptNumber ||
            providerOperation.Version != observation.ProviderVersion ||
            providerOperation.CheckpointSequence != observation.ProviderCheckpointSequence ||
            !AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
                providerOperation.AttemptedStep, providerOperation.Phase, observation.CompletedStep, observation.ObservedPhase) ||
            providerOperation.IdempotencyKey != $"elsa-instance-operation:{observation.LifecycleOperationId:D}")
            throw new InvalidOperationException("Recovery observation provider state is stale.");
        ValidateRecoveryObservationProviderRequest(providerOperation);

        await ValidateRecoveryObservationAssignmentAsync(observation, cancellationToken);

        var lifecycleOperation = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == observation.LifecycleOperationId &&
                                       x.OrganizationId == observation.OrganizationId &&
                                       x.WorkspaceId == observation.WorkspaceId &&
                                       x.InstanceId == observation.InstanceId,
                cancellationToken);
        if (lifecycleOperation is null || lifecycleOperation.Action != observation.LifecycleAction ||
            lifecycleOperation.State != ElsaInstanceOperationState.RecoveryRequired ||
            lifecycleOperation.AttemptNumber != observation.ObservedLifecycleAttemptNumber ||
            !string.Equals(lifecycleOperation.ResolvedPlanId, observation.ResolvedPlanId, StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery observation is not bound to the retained lifecycle operation.");

        var instance = await db.ElsaInstances.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == observation.InstanceId &&
                                       x.OrganizationId == observation.OrganizationId &&
                                       x.WorkspaceId == observation.WorkspaceId,
                cancellationToken);
        if (instance is null || instance.Version != observation.ObservedInstanceVersion ||
            !string.Equals(instance.ResolvedPlanId, observation.ResolvedPlanId, StringComparison.Ordinal) ||
            !string.Equals(instance.ResolvedPlanUri, observation.ResolvedPlanUri, StringComparison.Ordinal) ||
            !string.Equals(instance.ResolvedPlanContentHash, observation.ResolvedPlanContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery observation instance state is stale.");

        await ValidateRecoveryObservationPlanAuthorityAsync(observation, cancellationToken);
    }

    private async Task ValidateRecoveryObservationStoredIntegrityAsync(
        AzureProviderRecoveryObservationRecord observation,
        CancellationToken cancellationToken)
    {
        var providerOperation = await LoadAndValidateRecoveryProviderOperationAsync(observation, cancellationToken);
        if (providerOperation.Status != AzureProviderOperationStatus.RecoveryRequired ||
            providerOperation.AttemptNumber != observation.ProviderAttemptNumber ||
            providerOperation.Version != observation.ProviderVersion ||
            providerOperation.CheckpointSequence != observation.ProviderCheckpointSequence ||
            !AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
                providerOperation.AttemptedStep, providerOperation.Phase, observation.CompletedStep, observation.ObservedPhase))
            throw new InvalidOperationException("Recovery observation provider state is stale.");
        ValidateRecoveryObservationProviderRequest(providerOperation);

        await ValidateRecoveryObservationAssignmentAsync(observation, cancellationToken);

        await ValidateRecoveryObservationPlanAuthorityAsync(observation, cancellationToken);
    }

    private async Task<AzureProviderOperationEntity> LoadAndValidateRecoveryProviderOperationAsync(
        AzureProviderRecoveryObservationRecord observation,
        CancellationToken cancellationToken)
    {
        var providerOperation = await db.AzureProviderOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == observation.ProviderOperationId &&
                                       x.WorkspaceId == observation.WorkspaceId,
                cancellationToken);
        if (providerOperation is null ||
            providerOperation.OrganizationId != observation.OrganizationId ||
            providerOperation.InstanceId != observation.InstanceId ||
            providerOperation.ProviderAssignmentId != observation.ProviderAssignmentId ||
            providerOperation.LifecycleAction != observation.LifecycleAction ||
            !string.Equals(providerOperation.OperationIdentity, observation.ProviderOperationIdentity, StringComparison.Ordinal) ||
            !string.Equals(providerOperation.RequestHash, observation.ProviderRequestHash, StringComparison.Ordinal) ||
            !string.Equals(providerOperation.TargetKey, observation.TargetKey, StringComparison.Ordinal) ||
            !string.Equals(providerOperation.ProviderScopeFingerprint, observation.ProviderScopeFingerprint, StringComparison.Ordinal) ||
            !string.Equals(providerOperation.PlanFingerprint, observation.ProviderPlanFingerprint, StringComparison.Ordinal) ||
            !string.Equals(providerOperation.TemplateFingerprint, observation.ProviderTemplateFingerprint, StringComparison.Ordinal) ||
            providerOperation.IdempotencyKey != $"elsa-instance-operation:{observation.LifecycleOperationId:D}")
            throw new InvalidOperationException("Recovery observation is not bound to the retained provider operation.");
        return providerOperation;
    }

    private async Task ValidateRecoveryObservationAssignmentAsync(
        AzureProviderRecoveryObservationRecord observation,
        CancellationToken cancellationToken)
    {
        var assignment = await db.AzureProviderResourceAssignments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == observation.ProviderAssignmentId &&
                                       x.WorkspaceId == observation.WorkspaceId,
                cancellationToken);
        if (assignment is null || assignment.OrganizationId != observation.OrganizationId ||
            assignment.InstanceId != observation.InstanceId ||
            assignment.LastOperationId != observation.ProviderOperationId ||
            !string.Equals(assignment.WorkloadName, observation.TargetKey, StringComparison.OrdinalIgnoreCase) ||
            !await ScopeMatchesOrReboundAsync(
                observation.WorkspaceId,
                observation.ProviderAssignmentId,
                observation.ProviderScopeFingerprint ?? "",
                assignment.ProviderScopeFingerprint,
                cancellationToken))
            throw new InvalidOperationException("Recovery observation is not bound to the retained provider assignment.");
    }

    private static void ValidateRecoveryObservationProviderRequest(AzureProviderOperationEntity operation)
    {
        AzureProviderOperationRequest request;
        try
        {
            var (capacity, capacityInvalid) = ReadCapacity(operation);
            if (capacityInvalid)
                throw new InvalidOperationException();
            request = new(
                operation.WorkspaceId,
                operation.TargetKey,
                operation.Action,
                operation.IdempotencyKey,
                operation.PlanFingerprint,
                operation.TemplateFingerprint,
                operation.ElsaVersion,
                operation.ReleaseLine,
                operation.Topology,
                operation.Isolation,
                operation.Location,
                operation.ImageRepository,
                operation.ImageDigest,
                operation.ReleaseManifestDigest,
                operation.ReleaseManifestSignatureDigest,
                operation.ReleaseManifestReference,
                operation.ReleaseManifestSignatureReference,
                operation.SecretReferencesJson is null
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(operation.SecretReferencesJson),
                operation.ProviderScopeFingerprint,
                operation.SqlWorkflowPackageVersion,
                operation.SqlQuartzPackageVersion,
                operation.OrganizationId,
                operation.InstanceId,
                operation.LifecycleAction,
                operation.ProviderAssignmentId,
                capacity,
                operation.ManagedHandoff);
            if (!string.Equals(
                    AzureProviderOperationValidation.ComputeRequestHash(request),
                    operation.RequestHash,
                    StringComparison.Ordinal))
                throw new InvalidOperationException();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException("Recovery observation provider request authority is invalid.");
        }
    }

    private async Task ValidateRecoveryObservationPlanAuthorityAsync(
        AzureProviderRecoveryObservationRecord observation,
        CancellationToken cancellationToken)
    {
        var lifecycleOperation = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == observation.LifecycleOperationId &&
                                       x.OrganizationId == observation.OrganizationId &&
                                       x.WorkspaceId == observation.WorkspaceId &&
                                       x.InstanceId == observation.InstanceId,
                cancellationToken);
        if (lifecycleOperation is null || lifecycleOperation.Action != observation.LifecycleAction ||
            !string.Equals(lifecycleOperation.ResolvedPlanId, observation.ResolvedPlanId, StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery observation is not bound to the retained lifecycle operation.");

        var resolvedPlan = await db.ElsaInstanceResolvedPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PlanId == lifecycleOperation.ResolvedPlanId &&
                                       x.OrganizationId == observation.OrganizationId &&
                                       x.WorkspaceId == observation.WorkspaceId &&
                                       x.InstanceId == observation.InstanceId,
                cancellationToken);
        if (resolvedPlan is null || resolvedPlan.SchemaVersion != observation.ResolvedPlanSchemaVersion ||
            !string.Equals(resolvedPlan.PlanUri, observation.ResolvedPlanUri, StringComparison.Ordinal) ||
            !string.Equals(resolvedPlan.ContentHash, observation.ResolvedPlanContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery observation is not bound to the retained resolved plan.");

        ResolvedElsaApplicationPlan typedPlan;
        try
        {
            typedPlan = ResolvedElsaApplicationPlanSerialization.Deserialize(resolvedPlan.SerializedPlan);
            if (!string.Equals(ResolvedElsaApplicationPlanSerialization.Serialize(typedPlan), resolvedPlan.SerializedPlan, StringComparison.Ordinal) ||
                !string.Equals(ResolvedElsaApplicationPlanSerialization.ComputeContentHash(typedPlan), resolvedPlan.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException("Recovery observation resolved-plan authority is invalid.");
        }

        var translation = AzureWorkloadPlanTranslator.Translate(
            typedPlan,
            new AzureWorkloadTarget(observation.TargetKey, (await db.AzureProviderOperations.AsNoTracking()
                .SingleAsync(x => x.Id == observation.ProviderOperationId, cancellationToken)).Location));
        if (!translation.IsAccepted || translation.Plan is null ||
            !string.Equals(translation.Plan.Fingerprint, observation.ProviderPlanFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Recovery observation provider plan does not match the retained resolved plan.");
    }

    private async Task<AzureProviderRecoveryObservationEntity?> FindRecoveryObservationAsync(
        AzureProviderRecoveryObservationRecord observation,
        CancellationToken cancellationToken)
    {
        var naturalKey = observation.ComputeNaturalKey();
        return await db.AzureProviderRecoveryObservations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.WorkspaceId == observation.WorkspaceId && x.NaturalKey == naturalKey,
            cancellationToken);
    }

    private static AzureProviderRecoveryObservationEntity ToRecoveryObservationEntity(
        AzureProviderRecoveryObservationRecord observation,
        DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        var naturalKey = observation.ComputeNaturalKey();
        return new()
        {
            Id = id,
            OrganizationId = observation.OrganizationId,
            WorkspaceId = observation.WorkspaceId,
            InstanceId = observation.InstanceId,
            LifecycleOperationId = observation.LifecycleOperationId,
            LifecycleAction = observation.LifecycleAction,
            ObservedLifecycleAttemptNumber = observation.ObservedLifecycleAttemptNumber,
            ObservedInstanceVersion = observation.ObservedInstanceVersion,
            ProviderOperationId = observation.ProviderOperationId,
            ProviderAssignmentId = observation.ProviderAssignmentId,
            ProviderOperationIdentity = observation.ProviderOperationIdentity,
            ProviderRequestHash = observation.ProviderRequestHash,
            ProviderAttemptNumber = observation.ProviderAttemptNumber,
            ProviderVersion = observation.ProviderVersion,
            ProviderCheckpointSequence = observation.ProviderCheckpointSequence,
            TargetKey = observation.TargetKey,
            ProviderScopeFingerprint = observation.ProviderScopeFingerprint,
            ResolvedPlanId = observation.ResolvedPlanId,
            ResolvedPlanSchemaVersion = observation.ResolvedPlanSchemaVersion,
            ResolvedPlanUri = observation.ResolvedPlanUri,
            ResolvedPlanContentHash = observation.ResolvedPlanContentHash,
            ProviderPlanFingerprint = observation.ProviderPlanFingerprint,
            ProviderTemplateFingerprint = observation.ProviderTemplateFingerprint,
            CompletedStep = observation.CompletedStep,
            ObservedPhase = observation.ObservedPhase,
            ObservedHealth = observation.ObservedHealth,
            ResourceFingerprint = observation.ResourceFingerprint,
            PostconditionFingerprint = observation.PostconditionFingerprint,
            NaturalKey = naturalKey,
            RecordDigest = observation.ComputeRecordDigest(id),
            ObservedAt = observation.ObservedAt.ToUniversalTime(),
            CreatedAt = now
        };
    }

    private static AzureProviderRecoveryObservationReceipt ToRecoveryObservationReceipt(
        AzureProviderRecoveryObservationEntity entity)
    {
        var observation = entity.ToRecord();
        var digest = entity.RecordDigest;
        if (!string.Equals(entity.NaturalKey, observation.ComputeNaturalKey(), StringComparison.Ordinal) ||
            !string.Equals(digest, observation.ComputeRecordDigest(entity.Id), StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery observation derived integrity fields are invalid.");
        return new(entity.Id, ElsaInstanceProviderRecoveryObservationReference.Create(entity.Id, digest), digest, observation);
    }

    private static AzureProviderRecoveryObservationRecord ToRecoveryObservation(
        AzureProviderRecoveryObservationEntity entity) =>
        ToRecoveryObservationReceipt(entity).Observation;

    private async Task<AzureProviderOperationEntity?> FindActiveTargetAsync(
        AzureProviderOperationRequest request,
        Guid? excludedOperationId,
        CancellationToken cancellationToken) =>
        await db.AzureProviderOperations.AsNoTracking()
            .Where(x => x.WorkspaceId == request.WorkspaceId && x.TargetKey == request.TargetKey &&
                        (!excludedOperationId.HasValue || x.Id != excludedOperationId.Value) &&
                        (x.Status == AzureProviderOperationStatus.Accepted || x.Status == AzureProviderOperationStatus.Queued || x.Status == AzureProviderOperationStatus.EntitlementHeld ||
                         x.Status == AzureProviderOperationStatus.Running || x.Status == AzureProviderOperationStatus.RecoveryRequired))
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<AzureProviderOperationEntity?> FindBoundEntitlementHeldAsync(
        AzureProviderOperationRequest request,
        CancellationToken cancellationToken) =>
        await db.AzureProviderOperations
            .Where(x => x.WorkspaceId == request.WorkspaceId &&
                        x.TargetKey == request.TargetKey &&
                        x.OrganizationId == request.OrganizationId &&
                        x.InstanceId == request.InstanceId &&
                        x.Action == AzureProviderOperationAction.Reconcile &&
                        x.Status == AzureProviderOperationStatus.EntitlementHeld &&
                        x.LifecycleAction != ElsaInstanceOperationAction.Delete)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private static AzureProviderOperation EnsureSameRequest(AzureProviderOperation operation, string hash) =>
        operation.RequestHash == hash ? operation : throw new InvalidOperationException("The idempotency key is already bound to a different request.");

    private static bool LeaseMatches(AzureProviderOperationEntity entity, string token, DateTimeOffset now) =>
        entity.LeaseTokenHash == Hash(token) && entity.LeaseExpiresAt > now;

    private void AddTransition(AzureProviderOperationEntity entity, string code, string message, DateTimeOffset now)
    {
        db.AzureProviderOperationTransitions.Add(new AzureProviderOperationTransitionEntity
        {
            Id = Guid.NewGuid(),
            OperationId = entity.Id,
            Sequence = entity.Version,
            Status = entity.Status,
            Phase = entity.Phase,
            Code = code,
            Message = message,
            OccurredAt = now
        });
    }

    private static bool ResourcesEqual(AzureProviderOperationEntity entity, AzureProviderResourceReferences resources) =>
        entity.ResourceGroupName == resources.ResourceGroupName && entity.FoundationDeploymentId == resources.FoundationDeploymentId &&
        entity.WorkloadDeploymentId == resources.WorkloadDeploymentId && entity.WorkloadResourceId == resources.WorkloadResourceId &&
        entity.WorkloadRevisionName == resources.WorkloadRevisionName && entity.StableTrafficRevisionName == resources.StableTrafficRevisionName &&
        entity.WorkloadIdentityResourceId == resources.WorkloadIdentityResourceId &&
        entity.WorkloadIdentityClientId == resources.WorkloadIdentityClientId &&
        entity.WorkloadIdentityPrincipalId == resources.WorkloadIdentityPrincipalId &&
        entity.KeyVaultResourceId == resources.KeyVaultResourceId && entity.KeyVaultUri == resources.KeyVaultUri &&
        entity.SqlServerResourceId == resources.SqlServerResourceId && entity.SqlServerFqdn == resources.SqlServerFqdn &&
        entity.ContainerAppsEnvironmentResourceId == resources.ContainerAppsEnvironmentResourceId &&
        entity.RegistryResourceId == resources.RegistryResourceId && entity.AcrPullDeploymentId == resources.AcrPullDeploymentId &&
        entity.AcrPullRoleAssignmentId == resources.AcrPullRoleAssignmentId;

    private static AzureProviderResourceReferences MergeResources(
        AzureProviderOperationEntity entity,
        AzureProviderResourceReferences incoming) =>
        new(
            incoming.ResourceGroupName ?? entity.ResourceGroupName,
            incoming.FoundationDeploymentId ?? entity.FoundationDeploymentId,
            incoming.WorkloadDeploymentId ?? entity.WorkloadDeploymentId,
            incoming.WorkloadResourceId ?? entity.WorkloadResourceId,
            incoming.WorkloadRevisionName ?? entity.WorkloadRevisionName,
            incoming.StableTrafficRevisionName ?? entity.StableTrafficRevisionName,
            incoming.WorkloadIdentityResourceId ?? entity.WorkloadIdentityResourceId,
            incoming.WorkloadIdentityClientId ?? entity.WorkloadIdentityClientId,
            incoming.WorkloadIdentityPrincipalId ?? entity.WorkloadIdentityPrincipalId,
            incoming.KeyVaultResourceId ?? entity.KeyVaultResourceId,
            incoming.KeyVaultUri ?? entity.KeyVaultUri,
            incoming.SqlServerResourceId ?? entity.SqlServerResourceId,
            incoming.SqlServerFqdn ?? entity.SqlServerFqdn,
            incoming.ContainerAppsEnvironmentResourceId ?? entity.ContainerAppsEnvironmentResourceId,
            incoming.RegistryResourceId ?? entity.RegistryResourceId,
            incoming.AcrPullDeploymentId ?? entity.AcrPullDeploymentId,
            incoming.AcrPullRoleAssignmentId ?? entity.AcrPullRoleAssignmentId);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static AzureProviderOperation ToModel(AzureProviderOperationEntity x)
    {
        var (diagnostics, diagnosticsInvalid) = ReadDiagnostics(x.DiagnosticsJson);
        var (secretReferences, secretReferencesInvalid) = ReadSecretReferences(x.SecretReferencesJson);
        var (capacity, capacityInvalid) = ReadCapacity(x);
        return new(
            x.Id, x.WorkspaceId, x.TargetKey, x.Action, x.IdempotencyKey, x.RequestHash, x.OperationIdentity,
            x.PlanFingerprint, x.TemplateFingerprint, x.ElsaVersion, x.ReleaseLine, x.Topology, x.Isolation,
            x.Location, x.ImageRepository, x.ImageDigest, x.ReleaseManifestDigest, x.ReleaseManifestSignatureDigest,
            x.Status, x.Phase, x.CheckpointSequence, x.AttemptNumber, x.Version,
            new(x.ResourceGroupName, x.FoundationDeploymentId, x.WorkloadDeploymentId, x.WorkloadResourceId, x.WorkloadRevisionName,
                x.StableTrafficRevisionName, x.WorkloadIdentityResourceId, x.WorkloadIdentityClientId, x.WorkloadIdentityPrincipalId,
                x.KeyVaultResourceId, x.KeyVaultUri, x.SqlServerResourceId, x.SqlServerFqdn,
                x.ContainerAppsEnvironmentResourceId, x.RegistryResourceId, x.AcrPullDeploymentId, x.AcrPullRoleAssignmentId),
            x.Endpoint, x.Health, diagnostics,
            x.WorkerId, x.LeaseExpiresAt, x.HeartbeatAt, x.CreatedAt, x.UpdatedAt, x.CompletedAt,
            x.ReleaseManifestReference, x.ReleaseManifestSignatureReference,
            secretReferences,
            diagnosticsInvalid || secretReferencesInvalid || capacityInvalid,
            x.ProviderScopeFingerprint,
            x.SqlWorkflowPackageVersion,
            x.SqlQuartzPackageVersion,
            x.OrganizationId,
            x.InstanceId,
            x.LifecycleAction,
            x.ProviderAssignmentId,
            x.AttemptedStep,
            capacity,
            x.ManagedHandoff);
    }

    /// <summary>
    /// Capacity columns are written together. A partially populated set is corrupt metadata:
    /// it marks the row invalid instead of restoring a plan with guessed sizing.
    /// </summary>
    private static (AzureWorkloadCapacity? Capacity, bool Invalid) ReadCapacity(AzureProviderOperationEntity x) =>
        (x.CapacityMinReplicas, x.CapacityMaxReplicas, x.CapacityCpuMillicores, x.CapacityMemoryMiB) switch
        {
            (null, null, null, null) => (null, false),
            ({ } min, { } max, { } cpu, { } memory) => (new AzureWorkloadCapacity(min, max, cpu, memory), false),
            _ => (null, true)
        };

    private static AzureProviderResourceAssignment ToModel(AzureProviderResourceAssignmentEntity x) => new(
        x.Id,
        x.WorkspaceId,
        x.OrganizationId,
        x.InstanceId,
        x.ProviderScopeFingerprint,
        x.NamingVersion,
        x.SubscriptionId,
        x.ResourceGroupName,
        x.WorkloadName,
        x.OwnershipKey,
        x.Location,
        x.State,
        new AzureProviderResourceReferences(
            x.ResourceGroupName, x.FoundationDeploymentId, x.WorkloadDeploymentId, x.WorkloadResourceId,
            x.WorkloadRevisionName, x.StableTrafficRevisionName, x.WorkloadIdentityResourceId,
            x.WorkloadIdentityClientId, x.WorkloadIdentityPrincipalId, x.KeyVaultResourceId, x.KeyVaultUri,
            x.SqlServerResourceId, x.SqlServerFqdn, x.ContainerAppsEnvironmentResourceId,
            x.RegistryResourceId, x.AcrPullDeploymentId, x.AcrPullRoleAssignmentId),
        x.LastOperationId,
        x.Version,
        x.CreatedAt,
        x.UpdatedAt,
        x.DeletedAt);

    private static void ValidateAssignmentRequest(AzureProviderResourceAssignmentRequest request)
    {
        if (request.WorkspaceId == Guid.Empty || request.OrganizationId == Guid.Empty || request.InstanceId == Guid.Empty ||
            request.ProviderScopeFingerprint is not { Length: 64 } || !request.ProviderScopeFingerprint.All(char.IsAsciiHexDigit) ||
            !Guid.TryParseExact(request.SubscriptionId, "D", out _) ||
            string.IsNullOrWhiteSpace(request.WorkloadName) || request.WorkloadName.Length > 16 ||
            string.IsNullOrWhiteSpace(request.Location) || request.Location.Any(char.IsControl))
            throw new ArgumentException("The Azure provider assignment request is invalid.", nameof(request));
        _ = AzureProviderResourceAssignmentNaming.ResourceGroupName(
            request.ResourceGroupNamePrefix, request.InstanceId, request.NamingVersion);
    }

    private static void ValidateScopeAuthority(AzureProviderAssignmentScopeAuthority authority)
    {
        if (authority.WorkspaceId == Guid.Empty || authority.InstanceId == Guid.Empty ||
            authority.ProviderScopeFingerprint is not { Length: 64 } || !authority.ProviderScopeFingerprint.All(char.IsAsciiHexDigit) ||
            !Guid.TryParseExact(authority.SubscriptionId, "D", out _))
            throw new ArgumentException("The Azure provider assignment scope authority is invalid.", nameof(authority));
        _ = AzureProviderResourceAssignmentNaming.ResourceGroupName(
            authority.ResourceGroupNamePrefix, authority.InstanceId, authority.NamingVersion);
    }

    private async Task<AzureProviderResourceAssignmentEntity?> TryGetOrRebindAssignmentAsync(
        Guid workspaceId,
        Guid instanceId,
        string providerScopeFingerprint,
        string subscriptionId,
        string resourceGroupNamePrefix,
        int namingVersion,
        AzureProviderAssignmentRebindContext? rebind,
        DateTimeOffset now,
        AzureProviderResourceAssignmentRequest? createRequest,
        CancellationToken cancellationToken)
    {
        var expectedGroup = AzureProviderResourceAssignmentNaming.ResourceGroupName(
            resourceGroupNamePrefix, instanceId, namingVersion);
        var live = await db.AzureProviderResourceAssignments
            .Where(x => x.WorkspaceId == workspaceId &&
                        x.InstanceId == instanceId &&
                        x.State != AzureProviderAssignmentState.Deleted)
            .ToListAsync(cancellationToken);
        if (live.Count == 0)
            return null;
        if (live.Count > 1)
            throw new AzureProviderAssignmentRebindException(
                AzureProviderAssignmentRebindDiagnostics.Ambiguous,
                "More than one live Azure provider assignment exists for this instance.");

        var existing = live[0];
        if (string.Equals(
                NormalizeProviderScope(existing.ProviderScopeFingerprint),
                providerScopeFingerprint,
                StringComparison.Ordinal))
        {
            if (createRequest is not null)
                EnsureSameAssignment(existing, createRequest);
            else
                EnsurePlacementMatches(existing, subscriptionId, expectedGroup, namingVersion);
            return existing;
        }

        if (!IsPlacementStable(existing, subscriptionId, expectedGroup, namingVersion, createRequest))
            throw new AzureProviderAssignmentRebindException(
                AzureProviderAssignmentRebindDiagnostics.PlacementMismatch,
                "The Azure provider assignment cannot be rebound across a placement change.");

        if (!ResourcesStayInPlacement(existing))
            throw new AzureProviderAssignmentRebindException(
                AzureProviderAssignmentRebindDiagnostics.PlacementMismatch,
                "The Azure provider assignment cannot be rebound because its resources left the reserved placement.");

        var inFlight = await db.AzureProviderOperations.AnyAsync(
            x => x.ProviderAssignmentId == existing.Id &&
                 (x.Status == AzureProviderOperationStatus.Accepted ||
                  x.Status == AzureProviderOperationStatus.Queued ||
                  x.Status == AzureProviderOperationStatus.EntitlementHeld ||
                  x.Status == AzureProviderOperationStatus.Running),
            cancellationToken);
        if (inFlight)
            throw new AzureProviderAssignmentRebindException(
                AzureProviderAssignmentRebindDiagnostics.OperationsInFlight,
                "The Azure provider assignment cannot be rebound until in-flight operations drain.");

        var fromFingerprint = NormalizeProviderScope(existing.ProviderScopeFingerprint)!;
        existing.ProviderScopeFingerprint = providerScopeFingerprint;
        existing.OwnershipKey = AzureProviderResourceAssignmentNaming.OwnershipKey(
            existing.Id, existing.InstanceId, providerScopeFingerprint);
        existing.UpdatedAt = now;
        existing.Version++;

        var trigger = rebind ?? new AzureProviderAssignmentRebindContext("azure-provider-assignment-store");
        trigger.Validate();
        db.AzureProviderAssignmentRebinds.Add(new AzureProviderAssignmentRebindEntity
        {
            Id = Guid.NewGuid(),
            AssignmentId = existing.Id,
            WorkspaceId = existing.WorkspaceId,
            InstanceId = existing.InstanceId,
            FromProviderScopeFingerprint = fromFingerprint,
            ToProviderScopeFingerprint = providerScopeFingerprint,
            TriggeredBy = trigger.TriggeredBy,
            TriggerOperationId = trigger.TriggerOperationId,
            OccurredAt = now.ToUniversalTime()
        });
        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    private static bool IsPlacementStable(
        AzureProviderResourceAssignmentEntity existing,
        string subscriptionId,
        string expectedGroup,
        int namingVersion,
        AzureProviderResourceAssignmentRequest? createRequest)
    {
        if (existing.State == AzureProviderAssignmentState.Deleted ||
            existing.NamingVersion != namingVersion ||
            !string.Equals(existing.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existing.ResourceGroupName, expectedGroup, StringComparison.Ordinal))
            return false;
        if (createRequest is null)
            return true;
        return existing.OrganizationId == createRequest.OrganizationId &&
               string.Equals(existing.WorkloadName, createRequest.WorkloadName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(existing.Location, createRequest.Location, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsurePlacementMatches(
        AzureProviderResourceAssignmentEntity existing,
        string subscriptionId,
        string expectedGroup,
        int namingVersion)
    {
        if (!IsPlacementStable(existing, subscriptionId, expectedGroup, namingVersion, createRequest: null))
            throw new AzureProviderAssignmentRebindException(
                AzureProviderAssignmentRebindDiagnostics.PlacementMismatch,
                "The Azure provider assignment cannot be rebound across a placement change.");
    }

    private static bool ResourcesStayInPlacement(AzureProviderResourceAssignmentEntity assignment)
    {
        var prefix = $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/";
        foreach (var resourceId in new[]
                 {
                     assignment.FoundationDeploymentId,
                     assignment.WorkloadDeploymentId,
                     assignment.WorkloadResourceId,
                     assignment.WorkloadIdentityResourceId,
                     assignment.KeyVaultResourceId,
                     assignment.SqlServerResourceId,
                     assignment.ContainerAppsEnvironmentResourceId
                 })
        {
            if (string.IsNullOrWhiteSpace(resourceId))
                continue;
            if (!resourceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private async Task<AzureProviderOperationEntity?> FindLatestReconcileEntityAsync(
        Guid workspaceId,
        string normalizedTargetKey,
        string? normalizedProviderScopeFingerprint,
        bool activeOnly,
        CancellationToken cancellationToken)
    {
        var candidates = await db.AzureProviderOperations.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.TargetKey == normalizedTargetKey &&
                        x.Action == AzureProviderOperationAction.Reconcile &&
                        (!activeOnly ||
                         x.Status == AzureProviderOperationStatus.Running ||
                         x.Status == AzureProviderOperationStatus.RecoveryRequired) &&
                        (x.ProviderScopeFingerprint == normalizedProviderScopeFingerprint ||
                         x.ProviderAssignmentId != null &&
                         db.AzureProviderResourceAssignments.Any(assignment =>
                             assignment.Id == x.ProviderAssignmentId &&
                             assignment.WorkspaceId == workspaceId &&
                             assignment.ProviderScopeFingerprint == normalizedProviderScopeFingerprint)))
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(32)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate.ProviderScopeFingerprint, normalizedProviderScopeFingerprint, StringComparison.Ordinal))
                return candidate;
            if (candidate.ProviderAssignmentId is { } assignmentId &&
                await ScopeMatchesOrReboundAsync(
                    workspaceId,
                    assignmentId,
                    candidate.ProviderScopeFingerprint ?? "",
                    normalizedProviderScopeFingerprint,
                    cancellationToken))
                return candidate;
        }

        return null;
    }

    private async Task<bool> ScopeMatchesOrReboundAsync(
        Guid workspaceId,
        Guid assignmentId,
        string capturedFingerprint,
        string? currentFingerprint,
        CancellationToken cancellationToken)
    {
        var captured = NormalizeProviderScope(capturedFingerprint);
        var current = NormalizeProviderScope(currentFingerprint);
        if (captured is null || current is null)
            return false;
        if (string.Equals(captured, current, StringComparison.Ordinal))
            return true;

        var edges = await db.AzureProviderAssignmentRebinds
            .Where(x => x.WorkspaceId == workspaceId && x.AssignmentId == assignmentId)
            .Select(x => new { x.FromProviderScopeFingerprint, x.ToProviderScopeFingerprint })
            .ToListAsync(cancellationToken);
        return HasRebindPath(edges.Select(x => (x.FromProviderScopeFingerprint, x.ToProviderScopeFingerprint)), captured, current);
    }

    private static bool HasRebindPath(
        IEnumerable<(string From, string To)> edges,
        string from,
        string to)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (source, target) in edges)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                continue;
            if (!adjacency.TryGetValue(source, out var targets))
            {
                targets = [];
                adjacency[source] = targets;
            }
            targets.Add(target);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var pending = new Queue<string>();
        pending.Enqueue(from);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (string.Equals(current, to, StringComparison.Ordinal))
                return true;
            if (!adjacency.TryGetValue(current, out var targets))
                continue;
            foreach (var next in targets)
            {
                if (seen.Add(next))
                    pending.Enqueue(next);
            }
        }

        return false;
    }

    private static void EnsureSameAssignment(
        AzureProviderResourceAssignmentEntity existing,
        AzureProviderResourceAssignmentRequest request)
    {
        var expectedGroup = AzureProviderResourceAssignmentNaming.ResourceGroupName(
            request.ResourceGroupNamePrefix, request.InstanceId, request.NamingVersion);
        if (existing.OrganizationId != request.OrganizationId ||
            existing.NamingVersion != request.NamingVersion ||
            !string.Equals(existing.SubscriptionId, request.SubscriptionId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existing.ResourceGroupName, expectedGroup, StringComparison.Ordinal) ||
            !string.Equals(existing.WorkloadName, request.WorkloadName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existing.Location, request.Location, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The existing Azure provider assignment does not match the requested authority.");
    }

    private static void ApplyAssignmentResources(
        AzureProviderResourceAssignmentEntity assignment,
        AzureProviderResourceReferences resources)
    {
        assignment.FoundationDeploymentId = resources.FoundationDeploymentId;
        assignment.WorkloadDeploymentId = resources.WorkloadDeploymentId;
        assignment.WorkloadResourceId = resources.WorkloadResourceId;
        assignment.WorkloadRevisionName = resources.WorkloadRevisionName;
        assignment.StableTrafficRevisionName = resources.StableTrafficRevisionName;
        assignment.WorkloadIdentityResourceId = resources.WorkloadIdentityResourceId;
        assignment.WorkloadIdentityClientId = resources.WorkloadIdentityClientId;
        assignment.WorkloadIdentityPrincipalId = resources.WorkloadIdentityPrincipalId;
        assignment.KeyVaultResourceId = resources.KeyVaultResourceId;
        assignment.KeyVaultUri = resources.KeyVaultUri;
        assignment.SqlServerResourceId = resources.SqlServerResourceId;
        assignment.SqlServerFqdn = resources.SqlServerFqdn;
        assignment.ContainerAppsEnvironmentResourceId = resources.ContainerAppsEnvironmentResourceId;
        assignment.RegistryResourceId = resources.RegistryResourceId;
        assignment.AcrPullDeploymentId = resources.AcrPullDeploymentId;
        assignment.AcrPullRoleAssignmentId = resources.AcrPullRoleAssignmentId;
    }

    private static (IReadOnlyList<AzureProviderDiagnostic> Diagnostics, bool Invalid) ReadDiagnostics(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ([], true);

        try
        {
            var diagnostics = JsonSerializer.Deserialize<List<AzureProviderDiagnostic>>(json);
            if (diagnostics is null || !AzureProviderOperationValidation.IsSafeDiagnostics(diagnostics))
                return ([], true);

            return (diagnostics.Select(x => new AzureProviderDiagnostic(x.Code, x.Message)).ToArray(), false);
        }
        catch (JsonException)
        {
            return ([], true);
        }
        catch (NotSupportedException)
        {
            return ([], true);
        }
    }

    private static (IReadOnlyDictionary<string, string> SecretReferences, bool Invalid) ReadSecretReferences(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (EmptySecretReferences, true);

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
            if (values is null)
                return (EmptySecretReferences, true);

            var normalizedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values)
            {
                if (pair.Key is null || pair.Value is null ||
                    !string.Equals(pair.Key, pair.Key.Trim().ToLowerInvariant(), StringComparison.Ordinal) ||
                    !normalizedKeys.Add(pair.Key.Trim()) ||
                    !AzureProviderOperationValidation.IsSafeSecretReference(pair.Value))
                    return (EmptySecretReferences, true);

                references.Add(pair.Key, pair.Value);
            }

            return (new ReadOnlyDictionary<string, string>(references), false);
        }
        catch (JsonException)
        {
            return (EmptySecretReferences, true);
        }
        catch (NotSupportedException)
        {
            return (EmptySecretReferences, true);
        }
    }

    private static AzureProviderOperationTransition ToTransition(AzureProviderOperationTransitionEntity x) => new(x.Id, x.OperationId, x.Sequence, x.Status, x.Phase, x.Code, x.Message, x.OccurredAt);
}
