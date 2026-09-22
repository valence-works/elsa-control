using System.Security.Cryptography;
using System.Text;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Safe, typed metadata captured by read-only provider reconciliation before an
/// explicit recovery request. It describes one provider postcondition only; it
/// is not a second lifecycle state machine or a serialized provider response.
/// </summary>
public sealed record AzureProviderRecoveryObservationRecord(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid InstanceId,
    Guid LifecycleOperationId,
    ElsaInstanceOperationAction LifecycleAction,
    int ObservedLifecycleAttemptNumber,
    int ObservedInstanceVersion,
    Guid ProviderOperationId,
    string ProviderOperationIdentity,
    string ProviderRequestHash,
    int ProviderAttemptNumber,
    long ProviderVersion,
    long ProviderCheckpointSequence,
    Guid ProviderAssignmentId,
    string TargetKey,
    string? ProviderScopeFingerprint,
    string ResolvedPlanId,
    int ResolvedPlanSchemaVersion,
    string ResolvedPlanUri,
    string ResolvedPlanContentHash,
    string ProviderPlanFingerprint,
    string ProviderTemplateFingerprint,
    AzureProviderRunnerStep CompletedStep,
    AzureProviderOperationPhase ObservedPhase,
    AzureProviderHealth ObservedHealth,
    string ResourceFingerprint,
    string PostconditionFingerprint,
    DateTimeOffset ObservedAt)
{
    public void Validate()
    {
        if (OrganizationId == Guid.Empty || WorkspaceId == Guid.Empty || InstanceId == Guid.Empty ||
            LifecycleOperationId == Guid.Empty || ProviderOperationId == Guid.Empty ||
            ProviderAssignmentId == Guid.Empty)
            throw new ArgumentException("Recovery observation ownership is invalid.");
        if (!Enum.IsDefined(LifecycleAction) || LifecycleAction == ElsaInstanceOperationAction.Delete ||
            !Enum.IsDefined(CompletedStep) || !AzureProviderRecoveryObservationSupport.IsSupportedCompletedStep(CompletedStep) ||
            !Enum.IsDefined(ObservedPhase) || !Enum.IsDefined(ObservedHealth))
            throw new ArgumentException("Recovery observation enum values are invalid.");
        if (ObservedLifecycleAttemptNumber < 1 || ObservedInstanceVersion < 1 ||
            ProviderAttemptNumber < 1 || ProviderVersion < 1 || ProviderCheckpointSequence < 0 ||
            ResolvedPlanSchemaVersion < 1)
            throw new ArgumentException("Recovery observation versions are invalid.");
        RequireSafeToken(ProviderOperationIdentity, nameof(ProviderOperationIdentity), 64);
        RequireFingerprint(ProviderRequestHash, nameof(ProviderRequestHash));
        RequireSafeToken(TargetKey, nameof(TargetKey), 128);
        ElsaResolvedPlanReference planReference;
        try
        {
            planReference = new ElsaResolvedPlanReference(
                ResolvedPlanId,
                ResolvedPlanSchemaVersion,
                ResolvedPlanContentHash,
                ResolvedPlanUri);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Resolved plan reference is invalid.", nameof(ResolvedPlanUri), exception);
        }
        if (!string.Equals(planReference.PlanId, ResolvedPlanId, StringComparison.Ordinal) ||
            !string.Equals(planReference.ContentHash, ResolvedPlanContentHash, StringComparison.Ordinal) ||
            !string.Equals(planReference.PlanUri, ResolvedPlanUri, StringComparison.Ordinal))
            throw new ArgumentException("Resolved plan reference is not canonical.", nameof(ResolvedPlanUri));
        RequireFingerprint(ProviderPlanFingerprint, nameof(ProviderPlanFingerprint));
        RequireFingerprint(ProviderTemplateFingerprint, nameof(ProviderTemplateFingerprint));
        RequireFingerprint(ResourceFingerprint, nameof(ResourceFingerprint));
        RequireFingerprint(PostconditionFingerprint, nameof(PostconditionFingerprint));
        if (ProviderScopeFingerprint is not null)
            RequireFingerprint(ProviderScopeFingerprint, nameof(ProviderScopeFingerprint));
        if (ObservedAt == default)
            throw new ArgumentException("Recovery observation time is required.", nameof(ObservedAt));
    }

    /// <summary>
    /// The natural idempotency key deliberately excludes the generated record ID
    /// and polling time. Unchanged polls therefore address one immutable row.
    /// </summary>
    public string ComputeNaturalKey()
    {
        Validate();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(includeRecordId: null))));
    }

    public static string ComputeNaturalKey(AzureProviderRecoveryObservationRecord observation) =>
        observation?.ComputeNaturalKey() ?? throw new ArgumentNullException(nameof(observation));

    public string ComputeRecordDigest(Guid recordId)
    {
        if (recordId == Guid.Empty)
            throw new ArgumentException("Recovery observation record ID is required.", nameof(recordId));
        Validate();
        return "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(recordId))));
    }

    public static string ComputeResourceFingerprint(AzureProviderResourceReferences resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        AzureProviderOperationValidation.ValidateReferences(resources);
        var canonical = string.Join('\n',
            resources.ResourceGroupName,
            resources.FoundationDeploymentId,
            resources.WorkloadDeploymentId,
            resources.WorkloadResourceId,
            resources.WorkloadRevisionName,
            resources.StableTrafficRevisionName,
            resources.WorkloadIdentityResourceId,
            resources.WorkloadIdentityClientId,
            resources.WorkloadIdentityPrincipalId,
            resources.KeyVaultResourceId,
            resources.KeyVaultUri,
            resources.SqlServerResourceId,
            resources.SqlServerFqdn,
            resources.ContainerAppsEnvironmentResourceId,
            resources.RegistryResourceId,
            resources.AcrPullDeploymentId,
            resources.AcrPullRoleAssignmentId);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string ComputePostconditionFingerprint(
        AzureProviderRecoveryObservation observation,
        string resourceFingerprint)
    {
        ArgumentNullException.ThrowIfNull(observation);
        observation.Validate();
        RequireFingerprint(resourceFingerprint, nameof(resourceFingerprint));
        var canonical = string.Join('\n', observation.Kind, observation.CompletedStep,
            observation.Health, resourceFingerprint, observation.Code);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private string Canonical(Guid? includeRecordId) => string.Join('\n',
        includeRecordId?.ToString("N"),
        OrganizationId.ToString("N"),
        WorkspaceId.ToString("N"),
        InstanceId.ToString("N"),
        LifecycleOperationId.ToString("N"),
        LifecycleAction,
        ObservedLifecycleAttemptNumber,
        ObservedInstanceVersion,
        ProviderOperationId.ToString("N"),
        ProviderOperationIdentity,
        ProviderRequestHash,
        ProviderAttemptNumber,
        ProviderVersion,
        ProviderCheckpointSequence,
        ProviderAssignmentId.ToString("N"),
        TargetKey,
        ProviderScopeFingerprint,
        ResolvedPlanId,
        ResolvedPlanSchemaVersion,
        ResolvedPlanUri,
        ResolvedPlanContentHash,
        ProviderPlanFingerprint,
        ProviderTemplateFingerprint,
        CompletedStep,
        ObservedPhase,
        ObservedHealth,
        ResourceFingerprint,
        PostconditionFingerprint);

    private static void RequireSafeToken(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength ||
            value.Any(ch => !(char.IsAsciiDigit(ch) || ch is >= 'a' and <= 'z' or '.' or '-' or '_' or ':')))
            throw new ArgumentException($"{name} is invalid.", name);
    }

    internal static void RequireFingerprint(string? value, string name)
    {
        if (value is null || value.Length != 64 || value.AsSpan().ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException($"{name} must be a SHA-256 fingerprint.", name);
    }
}

/// <summary>
/// Recovery steps that have an explicit phase mapping. The concrete Azure observer currently
/// proves foundation and the preceding ACR Pull checkpoint; later steps remain valid extension
/// points for observers that can prove their own postconditions.
/// </summary>
public static class AzureProviderRecoveryObservationSupport
{
    public static bool IsSupportedCompletedStep(AzureProviderRunnerStep step) => step switch
    {
        AzureProviderRunnerStep.Foundation or
        AzureProviderRunnerStep.AcrPull or
        AzureProviderRunnerStep.SeedSecrets or
        AzureProviderRunnerStep.SqlBootstrap or
        AzureProviderRunnerStep.SqlFirewallCreate or
        AzureProviderRunnerStep.SqlBootstrapScript or
        AzureProviderRunnerStep.SqlFirewallCleanup or
        AzureProviderRunnerStep.Workload or
        AzureProviderRunnerStep.Health or
        AzureProviderRunnerStep.Promotion => true,
        _ => false
    };

    /// <summary>
    /// Foundation-only observation is safe only while the durable operation still contains no
    /// handles produced by a later provider step. ACR and secret-seeding uncertainty share the
    /// historical FoundationSubmitted phase, so those handles are the durable discriminator.
    /// </summary>
    public static bool IsFoundationOnlyEligible(AzureProviderOperation operation) =>
        (operation.Phase is AzureProviderOperationPhase.Planned or AzureProviderOperationPhase.FoundationSubmitted) &&
        (operation.AttemptedStep is null or AzureProviderRunnerStep.Foundation) &&
        operation.Resources.RegistryResourceId is null &&
        operation.Resources.AcrPullDeploymentId is null &&
        operation.Resources.AcrPullRoleAssignmentId is null &&
        operation.Resources.WorkloadDeploymentId is null &&
        operation.Resources.WorkloadResourceId is null &&
        operation.Resources.WorkloadRevisionName is null &&
        operation.Resources.StableTrafficRevisionName is null;

    /// <summary>
    /// Maps an observed completed step to the only durable phase it can authorize. This is
    /// shared by pre-claim consumers so a provider-specific observer cannot invent a lifecycle
    /// phase or silently skip one of the SQL checkpoints.
    /// </summary>
    public static AzureProviderOperationPhase RecoveryPhase(AzureProviderRunnerStep completedStep) => completedStep switch
    {
        AzureProviderRunnerStep.Foundation => AzureProviderOperationPhase.FoundationObserved,
        AzureProviderRunnerStep.AcrPull => AzureProviderOperationPhase.AcrPullObserved,
        AzureProviderRunnerStep.SeedSecrets => AzureProviderOperationPhase.SeedSecretsObserved,
        AzureProviderRunnerStep.SqlBootstrap => AzureProviderOperationPhase.FoundationReady,
        AzureProviderRunnerStep.SqlFirewallCreate => AzureProviderOperationPhase.SqlFirewallReady,
        AzureProviderRunnerStep.SqlBootstrapScript => AzureProviderOperationPhase.SqlBootstrapReady,
        AzureProviderRunnerStep.SqlFirewallCleanup => AzureProviderOperationPhase.FoundationReady,
        AzureProviderRunnerStep.Workload => AzureProviderOperationPhase.WorkloadReady,
        AzureProviderRunnerStep.Health => AzureProviderOperationPhase.HealthVerified,
        AzureProviderRunnerStep.Promotion => AzureProviderOperationPhase.TrafficPromoted,
        _ => throw new ArgumentException("The observed recovery step cannot be resumed.", nameof(completedStep))
    };

    /// <summary>
    /// Checks the immutable marker/phase boundary before a recovery observation is accepted.
    /// A legacy null marker means the original Foundation step only. The one deliberate
    /// mismatch is a SQL cleanup retry after the bootstrap script has already been observed;
    /// it advances no phase and therefore cannot cause the script to run again.
    /// </summary>
    public static bool IsCompatibleBoundary(
        AzureProviderRunnerStep? attemptedStep,
        AzureProviderOperationPhase currentPhase,
        AzureProviderRunnerStep completedStep,
        AzureProviderOperationPhase observedPhase)
    {
        if (completedStep is not (AzureProviderRunnerStep.Foundation or AzureProviderRunnerStep.AcrPull or
            AzureProviderRunnerStep.SeedSecrets or
            AzureProviderRunnerStep.SqlFirewallCreate or AzureProviderRunnerStep.SqlBootstrapScript or
            AzureProviderRunnerStep.SqlFirewallCleanup))
            return false;

        AzureProviderOperationPhase expectedPhase;
        try
        {
            expectedPhase = RecoveryPhase(completedStep);
        }
        catch (ArgumentException)
        {
            return false;
        }

        try
        {
            if (observedPhase != expectedPhase ||
                AzureProviderOperationPhaseOrdering.Compare(observedPhase, currentPhase) < 0)
                return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (attemptedStep == AzureProviderRunnerStep.SqlBootstrap ||
            completedStep == AzureProviderRunnerStep.SqlBootstrap)
            return false;

        if (attemptedStep == AzureProviderRunnerStep.SeedSecrets &&
            currentPhase != AzureProviderOperationPhase.AcrPullObserved)
            return false;

        if (attemptedStep is AzureProviderRunnerStep.SqlFirewallCreate or
                AzureProviderRunnerStep.SqlBootstrapScript or
                AzureProviderRunnerStep.SqlFirewallCleanup)
        {
            var validSourcePhase = attemptedStep switch
            {
                AzureProviderRunnerStep.SqlFirewallCreate =>
                    currentPhase is AzureProviderOperationPhase.FoundationSubmitted or AzureProviderOperationPhase.SeedSecretsObserved,
                AzureProviderRunnerStep.SqlBootstrapScript => currentPhase == AzureProviderOperationPhase.SqlFirewallReady,
                AzureProviderRunnerStep.SqlFirewallCleanup => currentPhase == AzureProviderOperationPhase.SqlBootstrapReady,
                _ => false
            };
            if (!validSourcePhase)
                return false;
        }

        return attemptedStep is null
            ? completedStep == AzureProviderRunnerStep.Foundation
            : attemptedStep == completedStep ||
              (attemptedStep == AzureProviderRunnerStep.SeedSecrets &&
               completedStep == AzureProviderRunnerStep.AcrPull &&
               currentPhase == AzureProviderOperationPhase.AcrPullObserved) ||
              (attemptedStep == AzureProviderRunnerStep.SqlFirewallCleanup &&
               completedStep == AzureProviderRunnerStep.SqlBootstrapScript &&
               currentPhase == AzureProviderOperationPhase.SqlBootstrapReady);
    }

    private static bool HasFoundationAndRegistry(AzureProviderOperation operation) =>
        operation.Resources.ResourceGroupName is not null &&
        operation.Resources.FoundationDeploymentId is not null &&
        operation.Resources.WorkloadIdentityResourceId is not null &&
        operation.Resources.WorkloadIdentityClientId is not null &&
        operation.Resources.WorkloadIdentityPrincipalId is not null &&
        operation.Resources.KeyVaultResourceId is not null &&
        operation.Resources.KeyVaultUri is not null &&
        operation.Resources.SqlServerResourceId is not null &&
        operation.Resources.SqlServerFqdn is not null &&
        operation.Resources.ContainerAppsEnvironmentResourceId is not null &&
        operation.Resources.RegistryResourceId is not null &&
        operation.Resources.AcrPullDeploymentId is not null &&
        operation.Resources.AcrPullRoleAssignmentId is not null &&
        operation.Resources.WorkloadDeploymentId is null &&
        operation.Resources.WorkloadResourceId is null &&
        operation.Resources.WorkloadRevisionName is null &&
        operation.Resources.StableTrafficRevisionName is null;

    /// <summary>
    /// Secret-seeding recovery is valid only after the independently observed registry boundary
    /// and before SQL, workload or traffic has retained any later provider handle.
    /// </summary>
    public static bool IsSeedSecretsEligible(AzureProviderOperation operation) =>
        operation.Phase == AzureProviderOperationPhase.AcrPullObserved &&
        operation.AttemptedStep == AzureProviderRunnerStep.SeedSecrets &&
        HasFoundationAndRegistry(operation);

    /// <summary>SQL firewall creation may be recovered only from the pre-SQL foundation phase.</summary>
    public static bool IsSqlFirewallCreateEligible(AzureProviderOperation operation) =>
        (operation.Phase is AzureProviderOperationPhase.FoundationSubmitted or AzureProviderOperationPhase.SeedSecretsObserved) &&
        operation.AttemptedStep == AzureProviderRunnerStep.SqlFirewallCreate &&
        HasFoundationAndRegistry(operation);

    /// <summary>
    /// SQL script evidence is valid after firewall creation. The second shape is the narrow
    /// cleanup-only replay where the script is already proven and cleanup remains attempted.
    /// </summary>
    public static bool IsSqlBootstrapScriptEligible(AzureProviderOperation operation) =>
        (operation.Phase == AzureProviderOperationPhase.SqlFirewallReady &&
         operation.AttemptedStep == AzureProviderRunnerStep.SqlBootstrapScript ||
         operation.Phase == AzureProviderOperationPhase.SqlBootstrapReady &&
         operation.AttemptedStep == AzureProviderRunnerStep.SqlFirewallCleanup) &&
        HasFoundationAndRegistry(operation);

    /// <summary>SQL firewall cleanup is valid only after an independently proven script.</summary>
    public static bool IsSqlFirewallCleanupEligible(AzureProviderOperation operation) =>
        operation.Phase == AzureProviderOperationPhase.SqlBootstrapReady &&
        operation.AttemptedStep == AzureProviderRunnerStep.SqlFirewallCleanup &&
        HasFoundationAndRegistry(operation);

    /// <summary>
    /// ACR Pull is an independently observable checkpoint after foundation. It is eligible only
    /// while the operation has not retained any workload or traffic handle; proving it never
    /// authorizes secret seeding, SQL bootstrap, workload, health, or traffic completion.
    /// </summary>
    public static bool IsAcrPullEligible(AzureProviderOperation operation) =>
        (operation.Phase is AzureProviderOperationPhase.Planned or AzureProviderOperationPhase.FoundationSubmitted) &&
        operation.AttemptedStep == AzureProviderRunnerStep.AcrPull &&
        operation.Resources.ResourceGroupName is not null &&
        operation.Resources.FoundationDeploymentId is not null &&
        operation.Resources.WorkloadIdentityResourceId is not null &&
        operation.Resources.WorkloadIdentityClientId is not null &&
        operation.Resources.WorkloadIdentityPrincipalId is not null &&
        operation.Resources.KeyVaultResourceId is not null &&
        operation.Resources.KeyVaultUri is not null &&
        operation.Resources.SqlServerResourceId is not null &&
        operation.Resources.SqlServerFqdn is not null &&
        operation.Resources.ContainerAppsEnvironmentResourceId is not null &&
        operation.Resources.RegistryResourceId is not null &&
        operation.Resources.AcrPullDeploymentId is not null &&
        operation.Resources.WorkloadDeploymentId is null &&
        operation.Resources.WorkloadResourceId is null &&
        operation.Resources.WorkloadRevisionName is null &&
        operation.Resources.StableTrafficRevisionName is null;
}

public sealed record AzureProviderRecoveryObservationReceipt(
    Guid RecordId,
    string Reference,
    string Digest,
    AzureProviderRecoveryObservationRecord Observation);

/// <summary>
/// The immutable evidence captured before recovery is accepted, together with the
/// append-only recovery envelope that consumed it. The observed attempt/version
/// deliberately describe the pre-Recover snapshot; accepted values describe the
/// incremented lifecycle operation and aggregate after the acceptance transaction.
/// </summary>
public sealed record AzureProviderRecoveryObservationBinding(
    Guid RecoveryRequestId,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid InstanceId,
    Guid LifecycleOperationId,
    int ObservedLifecycleAttemptNumber,
    int ObservedInstanceVersion,
    int AcceptedLifecycleAttemptNumber,
    int AcceptedInstanceVersion,
    string IdempotencyScope,
    string IdempotencyKey,
    string RequestHash,
    string Reference,
    string Digest)
{
    public void Validate()
    {
        try
        {
            new ElsaInstanceProviderRecoveryEnvelope(
                RecoveryRequestId,
                OrganizationId,
                WorkspaceId,
                InstanceId,
                LifecycleOperationId,
                ObservedLifecycleAttemptNumber,
                ObservedInstanceVersion,
                AcceptedLifecycleAttemptNumber,
                AcceptedInstanceVersion,
                IdempotencyScope,
                IdempotencyKey,
                RequestHash,
                Reference,
                Digest).Validate();
        }
        catch (InvalidOperationException)
        {
            // Preserve the Azure boundary's stable, value-free validation error.
            throw new ArgumentException("Recovery observation binding is invalid.");
        }
    }
}

public interface IAzureProviderRecoveryObservationStore
{
    Task<AzureProviderRecoveryObservationReceipt> CreateOrGetAsync(
        AzureProviderRecoveryObservationRecord observation,
        CancellationToken cancellationToken = default);

    Task<AzureProviderRecoveryObservationRecord?> GetAndValidateRecordedAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid instanceId,
        Guid lifecycleOperationId,
        int observedLifecycleAttemptNumber,
        string reference,
        string digest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a proof after explicit recovery acceptance. This method binds the
    /// proof to the append-only recovery ledger before checking the post-acceptance
    /// lifecycle version, so the pre-Recover provider/lifecycle tuple is not
    /// incorrectly compared with legitimate incremented state.
    /// </summary>
    Task<AzureProviderRecoveryObservationRecord?> GetAndValidateForAcceptedRecoveryAsync(
        AzureProviderRecoveryObservationBinding binding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the same accepted proof after the provider has advanced the retained
    /// operation through its recovery claim. The pre-claim method remains strict about
    /// the provider RecoveryRequired tuple; this replay method only permits the exact
    /// claimed Running/Succeeded successor.
    /// </summary>
    Task<AzureProviderRecoveryObservationRecord?> GetAndValidateForAcceptedRecoveryReplayAsync(
        AzureProviderRecoveryObservationBinding binding,
        CancellationToken cancellationToken = default);
}
