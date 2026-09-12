using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Safe submission contract for the Azure provider. The plan is already below the resolved-plan
/// admission boundary; it contains only immutable identities, placement facts and secret
/// locators. It must never be populated from an unadmitted customer payload.
/// </summary>
public sealed record AzureProviderOperationSubmission(
    string IdempotencyKey,
    string TemplateFingerprint,
    AzureWorkloadPlan Plan,
    string? ProviderScopeFingerprint = null,
    Guid? OrganizationId = null,
    Guid? InstanceId = null,
    ElsaInstanceOperationAction? LifecycleAction = null,
    Guid? ProviderAssignmentId = null);

public sealed record AzureProviderOperationStatusResponse(
    AzureProviderOperation Operation,
    IReadOnlyList<AzureProviderOperationTransition> Transitions);

/// <summary>
/// The provider-facing result of a durable operation reservation. Replay status comes from the
/// store's atomic create-or-get decision rather than from executor attempt counters.
/// </summary>
public sealed record AzureProviderOperationSubmissionResult(
    AzureProviderOperation Operation,
    bool Replayed);

public interface IAzureProviderOperationService
{
    Task<AzureProviderOperation> SubmitAsync(
        Guid workspaceId,
        AzureProviderOperationSubmission submission,
        CancellationToken cancellationToken = default);

    Task<AzureProviderOperation> SubmitDeleteAsync(
        Guid workspaceId,
        AzureProviderOperationSubmission submission,
        CancellationToken cancellationToken = default);

    Task<AzureProviderOperationStatusResponse?> GetStatusAsync(
        Guid workspaceId,
        Guid operationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional richer submission contract used by lifecycle adapters that need to distinguish a
/// newly reserved provider operation from a replay. The legacy operation service remains stable
/// for callers that only need the durable operation.
/// </summary>
public interface IAzureProviderOperationReplayService
{
    Task<AzureProviderOperationSubmissionResult> SubmitWithReplayAsync(
        Guid workspaceId,
        AzureProviderOperationSubmission submission,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates durable Azure operations from an admitted provider plan. The operation store retains
/// the safe provider-plan fields needed for a worker to recover after a process restart; raw
/// resolved-plan JSON, credentials and secret values are never accepted.
/// </summary>
public sealed class AzureProviderOperationService(
    IAzureProviderOperationStore store,
    TimeProvider? timeProvider = null) : IAzureProviderOperationService, IAzureProviderOperationReplayService
{
    private const int MaximumIdempotencyKeyLength = 512;
    private const string DeleteIdempotencySuffix = ":delete";
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AzureProviderOperation> SubmitAsync(
        Guid workspaceId,
        AzureProviderOperationSubmission submission,
        CancellationToken cancellationToken = default) =>
        (await SubmitWithReplayAsync(workspaceId, submission, cancellationToken)).Operation;

    public Task<AzureProviderOperationSubmissionResult> SubmitWithReplayAsync(
        Guid workspaceId,
        AzureProviderOperationSubmission submission,
        CancellationToken cancellationToken = default) =>
        SubmitCoreWithReplayAsync(workspaceId, submission, AzureProviderOperationAction.Reconcile, cancellationToken);

    public async Task<AzureProviderOperation> SubmitDeleteAsync(
        Guid workspaceId,
        AzureProviderOperationSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var deleteIdempotencyKey = CreateDeleteIdempotencyKey(submission.IdempotencyKey);
        // Idempotency is scoped to a lifecycle action. A cleanup request must never
        // accidentally reuse the successful reconcile operation for the same target.
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var operation = await SubmitCoreAsync(
                workspaceId,
                submission with { IdempotencyKey = deleteIdempotencyKey },
                AzureProviderOperationAction.Delete,
                cancellationToken);
            if (operation.Status is not (AzureProviderOperationStatus.Failed or AzureProviderOperationStatus.Cancelled))
                return operation;

            // A confirmed cleanup failure may still leave owned resources behind. Derive the
            // next durable key from the terminal operation so concurrent/replayed callers select
            // the same retry instead of permanently replaying the failed attempt.
            deleteIdempotencyKey = CreateDeleteRetryIdempotencyKey(operation);
        }

        throw new InvalidOperationException("The bounded Azure cleanup retry chain was exhausted.");
    }

    private static string CreateDeleteRetryIdempotencyKey(AzureProviderOperation operation)
    {
        var seed = $"{operation.IdempotencyKey}:retry:{operation.Id:N}";
        if (seed.Length <= MaximumIdempotencyKeyLength)
            return seed;

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
        return $"delete-retry:sha256:{digest}";
    }

    private static string CreateDeleteIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) ||
            idempotencyKey.Length > MaximumIdempotencyKeyLength ||
            idempotencyKey.Any(char.IsControl))
            throw new ArgumentException("A bounded safe idempotency key is required.", nameof(idempotencyKey));

        var normalized = idempotencyKey.Trim();
        if (normalized.Length + DeleteIdempotencySuffix.Length <= MaximumIdempotencyKeyLength)
            return normalized + DeleteIdempotencySuffix;

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return $"delete:sha256:{digest}";
    }

    public async Task<AzureProviderOperationStatusResponse?> GetStatusAsync(
        Guid workspaceId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (operationId == Guid.Empty)
            throw new ArgumentException("Operation ID is required.", nameof(operationId));

        var operation = await store.GetAsync(workspaceId, operationId, cancellationToken);
        if (operation is null)
            return null;

        return new AzureProviderOperationStatusResponse(
            operation,
            await store.ListTransitionsAsync(workspaceId, operationId, cancellationToken));
    }

    private async Task<AzureProviderOperation> SubmitCoreAsync(
        Guid workspaceId,
        AzureProviderOperationSubmission submission,
        AzureProviderOperationAction action,
        CancellationToken cancellationToken) =>
        (await SubmitCoreWithReplayAsync(workspaceId, submission, action, cancellationToken)).Operation;

    private async Task<AzureProviderOperationSubmissionResult> SubmitCoreWithReplayAsync(
        Guid workspaceId,
        AzureProviderOperationSubmission submission,
        AzureProviderOperationAction action,
        CancellationToken cancellationToken)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(submission.Plan);

        var plan = submission.Plan;
        ValidateSubmissionPlan(plan, nameof(submission));
        if (!IsFingerprint(submission.TemplateFingerprint))
            throw new ArgumentException("A compiled template fingerprint is required.", nameof(submission));

        var operationRequest = CreateOperationRequest(
            workspaceId,
            submission.IdempotencyKey,
            submission.TemplateFingerprint,
            plan,
            action,
            submission.ProviderScopeFingerprint,
            submission.OrganizationId,
            submission.InstanceId,
            submission.LifecycleAction,
            submission.ProviderAssignmentId);
        var result = await store.CreateOrGetWithResultAsync(operationRequest, _timeProvider.GetUtcNow(), cancellationToken);
        return new(result.Operation, result.Replayed);
    }

    public static AzureProviderOperationRequest CreateOperationRequest(
        Guid workspaceId,
        string idempotencyKey,
        string templateFingerprint,
        AzureWorkloadPlan plan,
        AzureProviderOperationAction action = AzureProviderOperationAction.Reconcile,
        string? providerScopeFingerprint = null,
        Guid? organizationId = null,
        Guid? instanceId = null,
        ElsaInstanceOperationAction? lifecycleAction = null,
        Guid? providerAssignmentId = null) =>
        new(
            workspaceId,
            plan.WorkloadName,
            action,
            idempotencyKey,
            plan.Fingerprint,
            templateFingerprint,
            plan.ElsaVersion,
            plan.ReleaseLine,
            plan.Topology,
            plan.Isolation,
            plan.Location,
            plan.ImageRepository,
            $"sha256:{plan.ImageDigest}",
            plan.ReleaseManifestDigest,
            plan.ReleaseManifestSignatureDigest,
            plan.ReleaseManifestReference,
            plan.ReleaseManifestSignatureReference,
            new ReadOnlyDictionary<string, string>((plan.SecretReferences ?? new Dictionary<string, string>()).ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase)),
            providerScopeFingerprint,
            plan.SqlWorkflowPackageVersion,
            plan.SqlQuartzPackageVersion,
            organizationId,
            instanceId,
            lifecycleAction,
            providerAssignmentId,
            plan.Capacity,
            plan.ManagedHandoff);

    internal static AzureProviderOperationRequest CreateOperationRequest(AzureProviderOperation operation) =>
        new(
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
            operation.SafeSecretReferences,
            operation.ProviderScopeFingerprint,
            operation.SqlWorkflowPackageVersion,
            operation.SqlQuartzPackageVersion,
            operation.OrganizationId,
            operation.InstanceId,
            operation.LifecycleAction,
            operation.ProviderAssignmentId,
            operation.Capacity,
            operation.ManagedHandoff);

    /// <summary>
    /// Rebuilds the admitted plan from the persisted operation columns. An operation retained
    /// before capacity joined the projection restores with no capacity: it stays restorable for
    /// observation and cleanup, while the runner refuses to deploy a workload from it. One retained
    /// before the managed handoff joined restores with the handoff off, which is what it deployed.
    /// </summary>
    internal static AzureWorkloadPlan? TryRestorePlan(AzureProviderOperation operation)
    {
        if (operation is null || operation.Id == Guid.Empty || operation.PersistedMetadataInvalid)
            return null;

        try
        {
            var operationRequest = AzureProviderOperationValidation.Normalize(CreateOperationRequest(operation));
            if (!string.Equals(
                    operation.RequestHash,
                    AzureProviderOperationValidation.ComputeRequestHash(operationRequest),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    operation.OperationIdentity,
                    AzureProviderOperationValidation.ComputeOperationIdentity(operationRequest),
                    StringComparison.OrdinalIgnoreCase))
                return null;

            var plan = new AzureWorkloadPlan(
                operationRequest.TargetKey,
                operationRequest.Location,
                operationRequest.ElsaVersion,
                operationRequest.ReleaseLine,
                operationRequest.Topology,
                operationRequest.Isolation,
                operationRequest.ImageRepository,
                operationRequest.ImageDigest["sha256:".Length..],
                operationRequest.ReleaseManifestReference!,
                operationRequest.ReleaseManifestDigest!,
                operationRequest.ReleaseManifestSignatureReference!,
                operationRequest.ReleaseManifestSignatureDigest!,
                operationRequest.SecretReferences!,
                operationRequest.PlanFingerprint,
                operationRequest.SqlWorkflowPackageVersion,
                operationRequest.SqlQuartzPackageVersion,
                operationRequest.Capacity,
                operationRequest.ManagedHandoff);

            AzureProviderExecutor.ValidateExecutionRequest(
                new AzureProviderExecutionRequest(operationRequest, plan));
            return plan;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void ValidateSubmissionPlan(AzureWorkloadPlan plan, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(plan.WorkloadName) ||
            !string.Equals(plan.WorkloadName, plan.WorkloadName.Trim(), StringComparison.Ordinal) ||
            plan.WorkloadName.Length is < 3 or > 16 ||
            !char.IsAsciiLetterOrDigit(plan.WorkloadName[0]) ||
            !char.IsAsciiLetterOrDigit(plan.WorkloadName[^1]) ||
            plan.WorkloadName.Any(x => !char.IsAsciiLetterOrDigit(x) && x != '-'))
            throw new ArgumentException("The provider workload name is invalid.", parameterName);
        if (string.IsNullOrWhiteSpace(plan.Location) ||
            !string.Equals(plan.Location, plan.Location.Trim(), StringComparison.Ordinal) ||
            plan.Location.Any(char.IsControl))
            throw new ArgumentException("The provider location is invalid.", parameterName);
        if (!IsFingerprint(plan.Fingerprint))
            throw new ArgumentException("The provider plan fingerprint is invalid.", parameterName);
        if (!string.Equals(
                plan.ImageRepository,
                AzureWorkloadPlanTranslator.SupportedRepository,
                StringComparison.Ordinal))
            throw new ArgumentException("The provider image repository is invalid.", parameterName);
        if (plan.ImageDigest is null || plan.ImageDigest.Length != 64 || !plan.ImageDigest.All(Uri.IsHexDigit))
            throw new ArgumentException("The provider image digest is invalid.", parameterName);
        if (!AzureProviderOperationValidation.IsSafeImmutableEvidenceReference(
                plan.ReleaseManifestReference, plan.ReleaseManifestDigest) ||
            !AzureProviderOperationValidation.IsSafeImmutableEvidenceReference(
                plan.ReleaseManifestSignatureReference, plan.ReleaseManifestSignatureDigest))
            throw new ArgumentException("Provider evidence references must be safe immutable locators.", parameterName);
        if (!AzureProviderOperationValidation.IsSafeSecretReferences(plan.SecretReferences))
            throw new ArgumentException("Provider secret references must be safe immutable locators.", parameterName);
        if (!AzureProviderOperationValidation.IsSafePackageVersion(plan.SqlWorkflowPackageVersion) ||
            !AzureProviderOperationValidation.IsSafePackageVersion(plan.SqlQuartzPackageVersion))
            throw new ArgumentException("Provider release package metadata is required and must use exact NuGet versions.", parameterName);
        if (plan.Capacity is not null && AzureContainerAppsCapacity.Map(plan.Capacity) is null)
            throw new ArgumentException("The provider capacity has no exact Azure Container Apps mapping.", parameterName);
        if (plan.ManagedHandoff && plan.Capacity is not { MaxReplicas: 1 })
            throw new ArgumentException("The managed handoff requires a single-replica workload.", parameterName);
    }

    private static bool IsFingerprint(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
}
