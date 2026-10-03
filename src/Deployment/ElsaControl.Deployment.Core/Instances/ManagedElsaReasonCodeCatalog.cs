using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace ElsaControl.Deployment.Core.Instances;

public enum ManagedElsaReasonClass
{
    HealthyHandOff,
    AutoResuming,
    Temporary,
    NeedsPerson
}

/// <summary>
/// One keyed reason-code catalog for parks, customer projection, and
/// operator alerting (#657 / #662). #646 extends this same record with
/// activity severity and customer label; do not add a second table.
/// </summary>
public sealed record ManagedElsaReasonCode(
    string Code,
    ManagedElsaReasonClass Class);

/// <summary>
/// Shared lifecycle and park reason-code catalog. Customer projection and
/// the #657 alert both call <see cref="RequiresHuman"/> so every
/// human-required park is visible and raises exactly one alert.
/// </summary>
public static class ManagedElsaReasonCodeCatalog
{
    /// <summary>
    /// Inclusive 10-minute clock shared with the #641 stale rule. Temporary
    /// and auto-resuming parks become human-required at this bound.
    /// </summary>
    public static readonly TimeSpan HumanRequiredAfter = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan SubmissionUncertainHealthyWindow = HumanRequiredAfter;

    public const string ProviderSubmissionAccepted = "provider.submission.accepted";
    public const string ProviderSubmissionUncertain = "provider.submission.uncertain";
    public const string ProviderSubmissionRejected = "provider.submission.rejected";
    public const string ProviderReconciliationInProgress = "provider.reconciliation.in-progress";
    public const string ProviderReconciliationConverged = "provider.reconciliation.converged";
    public const string ProviderReconciliationUnknown = "provider.reconciliation.unknown";
    public const string ProviderReconciliationUnavailable = "provider.reconciliation.unavailable";
    public const string ProviderReconciliationHealthUnknown = "provider.reconciliation.health-unknown";
    public const string ProviderReconciliationRetrySafe = "provider.reconciliation.retry-safe";
    public const string ProviderReconciliationAmbiguous = "provider.reconciliation.ambiguous";
    public const string ProviderReconciliationCorrelationMismatch = "provider.reconciliation.correlation-mismatch";
    public const string ProviderReconciliationHealthFailed = "provider.reconciliation.health-failed";
    public const string ProviderReconciliationFailed = "provider.reconciliation.failed";
    public const string ProviderReconciliationCancelled = "provider.reconciliation.cancelled";
    public const string ProviderReconciliationRequired = "provider.reconciliation.required";
    public const string ProviderIdentityBindingMissing = "provider.identity-binding-missing";
    public const string AzureDeploymentFailed = "azure.deployment.failed";
    public const string AzureDeploymentWaitExceeded = "azure.deployment.wait-exceeded";
    public const string AzureDeploymentCanceled = "azure.deployment.canceled";
    public const string AzureRecoveryAutoResumeExhausted = "azure.recovery.auto-resume-exhausted";
    public const string AzureRecoveryAutoResumeClaimConflict = "azure.recovery.auto-resume.claim-conflict";
    public const string AzureRecoveryAutoResumeAccepted = "azure.recovery.auto-resume.accepted";
    public const string AzureRecoveryAutoResumeConflict = "azure.recovery.auto-resume.conflict";
    public const string AzureRecoveryAutoResumeRejected = "azure.recovery.auto-resume.rejected";
    public const string AzureRecoveryFoundationObserved = "azure.recovery.foundation-observed";
    public const string AzureRecoveryFoundationOutputsUnavailable = "azure.recovery.foundation-outputs-unavailable";
    public const string AzureRecoveryFoundationOutputsInvalid = "azure.recovery.foundation-outputs-invalid";
    public const string AzureRecoveryWorkloadInProgress = "azure.recovery.workload-in-progress";
    public const string AzureRecoveryWorkloadObserved = "azure.recovery.workload-observed";
    public const string AzureRecoveryWorkloadOutputsUnavailable = "azure.recovery.workload-outputs-unavailable";
    public const string AzureRecoveryWorkloadRevisionUnavailable = "azure.recovery.workload-revision-unavailable";
    public const string AzureRecoveryWorkloadOutputsInvalid = "azure.recovery.workload-outputs-invalid";
    public const string AzureRecoveryAcrPullObserved = "azure.recovery.acr-pull-observed";
    public const string AzureRecoverySeedSecretsObserved = "azure.recovery.seed-secrets-observed";
    public const string AzureRecoverySeedSecretsAbsent = "azure.recovery.seed-secrets-absent";
    public const string AzureRecoverySeedSecretsPartial = "azure.recovery.seed-secrets-partial";
    public const string AzureRecoverySqlFirewallCreateObserved = "azure.recovery.sql-firewall-create-observed";
    public const string AzureRecoverySqlFirewallCleanupObserved = "azure.recovery.sql-firewall-cleanup-observed";
    public const string AzureRecoverySqlBootstrapObserved = "azure.recovery.sql-bootstrap-observed";
    public const string AzureRecoveryObservationInProgress = "azure.recovery.observation-in-progress";
    public const string AzureRecoveryObservationInsufficient = "azure.recovery.observation-insufficient";
    public const string AzureRecoveryObservationUnavailable = "azure.recovery.observation-unavailable";
    public const string AzureRecoveryObservationInvalid = "azure.recovery.observation-invalid";
    public const string AzureRecoveryObservationFailed = "azure.recovery.observation-failed";
    public const string AzureRecoveryObservationAmbiguous = "azure.recovery.observation-ambiguous";
    public const string AzureRecoveryCheckpointUncertain = "azure.recovery.checkpoint-uncertain";
    public const string AzureRecoveryStepUnsupported = "azure.recovery.step-unsupported";
    public const string AzureRecoveryOperationUnavailable = "azure.recovery.operation-unavailable";
    public const string AzureRecoveryUnavailable = "azure.recovery.unavailable";
    public const string AzureRecoveryAssignmentInvalid = "azure.recovery.assignment-invalid";
    public const string AzureRecoveryAssignmentMismatch = "azure.recovery.assignment-mismatch";
    public const string AzureRecoveryIdentityMismatch = "azure.recovery.identity-mismatch";
    public const string AzureRecoveryPlanUnavailable = "azure.recovery.plan-unavailable";
    public const string AzureRecoveryPlanMismatch = "azure.recovery.plan-mismatch";
    public const string AzureRecoveryStateInvalid = "azure.recovery.state-invalid";
    public const string AzurePromotionUncertain = "azure.promotion.uncertain";
    public const string AzurePromotionRollbackUncertain = "azure.promotion.rollback-uncertain";
    public const string StagingLeverRecoveryRequired = "staging.lever.recovery-required";
    public const string StagingLeverFired = "staging.lever.fired";
    public const string StagingLeverReset = "staging.lever.reset";
    public const string DeletionLocalAbsent = "deletion.local.absent";
    public const string DeletionProviderConfirmedAbsent = "deletion.provider-confirmed-absent";
    public const string DeletionProviderUnavailable = "deletion.provider.unavailable";
    public const string DeletionAzureProviderUnavailable = "deletion.provider-unavailable";
    public const string DeletionProviderCleanupPending = "deletion.provider-cleanup-pending";
    public const string DeletionProviderProgressStale = "lifecycle.deletion.provider-progress-stale";
    public const string DeletionBlockedByOperationInFlight = "lifecycle.deletion.blocked-by-operation-in-flight";
    public const string DeletionProviderAssignmentUnavailable = "deletion.provider-assignment-unavailable";
    public const string DeletionProviderEvidenceUnavailable = "deletion.provider-evidence-unavailable";
    public const string DeletionProviderPlanUnavailable = "deletion.provider-plan-unavailable";
    public const string DeletionRecoveryCapabilityUnavailable = "deletion.recovery.capability-unavailable";
    public const string DeletionRecoveryAssignmentIncomplete = "deletion.recovery.assignment-incomplete";
    public const string DeletionClaimConflict = "deletion.claim.conflict";
    public const string DeletionCorrelationInvalid = "deletion.correlation.invalid";
    public const string DeletionItemInvalid = "deletion.item.invalid";
    public const string DeletionProviderAssignmentInvalid = "deletion.provider-assignment-invalid";
    public const string DeletionProviderCorrelationInvalid = "deletion.provider-correlation-invalid";
    public const string DeletionProviderCleanupFailed = "deletion.provider-cleanup-failed";
    public const string DeletionRecoveryAuthorityUnavailable = "deletion.recovery.authority-unavailable";
    public const string DeletionRecoveryPlanUnavailable = "deletion.recovery.plan-unavailable";
    public const string DeletionRecoveryAssignmentInvalid = "deletion.recovery.assignment-invalid";
    public const string DeletionRecoveryClaimLost = "deletion.recovery.claim-lost";
    public const string DeletionPredecessorRecoverySuperseded = "deletion.predecessor-recovery-superseded";
    public const string DeletionProviderUncertain = "deletion.provider.uncertain";
    public const string DeletionProviderUnknown = "deletion.provider.unknown";
    public const string DeletionProviderAbsent = "deletion.provider.absent";
    public const string DeletionProviderAmbiguous = "deletion.provider.ambiguous";
    public const string DeletionProviderFinalizationPending = "deletion.provider-finalization-pending";
    public const string AssignmentRebindOperationsInFlight = "assignment.rebind.operations-inflight";
    public const string AssignmentRebindPlacementMismatch = "assignment.rebind.placement-mismatch";
    public const string AssignmentRebindAmbiguous = "assignment.rebind.ambiguous";

    public static readonly FrozenDictionary<string, ManagedElsaReasonCode> ByCode =
        new ManagedElsaReasonCode[]
        {
            new(ProviderSubmissionAccepted, ManagedElsaReasonClass.HealthyHandOff),
            new(ProviderReconciliationInProgress, ManagedElsaReasonClass.HealthyHandOff),
            new(ProviderReconciliationConverged, ManagedElsaReasonClass.HealthyHandOff),
            new(ProviderSubmissionUncertain, ManagedElsaReasonClass.Temporary),
            new(ProviderReconciliationUnknown, ManagedElsaReasonClass.Temporary),
            new(ProviderReconciliationUnavailable, ManagedElsaReasonClass.Temporary),
            new(ProviderReconciliationHealthUnknown, ManagedElsaReasonClass.Temporary),
            new(ProviderReconciliationRetrySafe, ManagedElsaReasonClass.Temporary),
            new(ProviderReconciliationRequired, ManagedElsaReasonClass.Temporary),
            new(AzureRecoveryAutoResumeClaimConflict, ManagedElsaReasonClass.Temporary),
            new(AzureRecoveryObservationUnavailable, ManagedElsaReasonClass.Temporary),
            new(AzureRecoveryObservationInvalid, ManagedElsaReasonClass.Temporary),
            new(AzureRecoveryObservationFailed, ManagedElsaReasonClass.Temporary),
            new(AzureRecoveryOperationUnavailable, ManagedElsaReasonClass.Temporary),
            new(AzureRecoveryUnavailable, ManagedElsaReasonClass.Temporary),
            new(AzurePromotionRollbackUncertain, ManagedElsaReasonClass.Temporary),
            new(AzureDeploymentFailed, ManagedElsaReasonClass.AutoResuming),
            new(AzureDeploymentWaitExceeded, ManagedElsaReasonClass.AutoResuming),
            new(AzureDeploymentCanceled, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoveryAutoResumeAccepted, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoveryFoundationObserved, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoveryFoundationOutputsUnavailable, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoveryWorkloadInProgress, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoveryWorkloadObserved, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoveryWorkloadOutputsUnavailable, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoveryWorkloadRevisionUnavailable, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoveryAcrPullObserved, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoverySeedSecretsObserved, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoverySeedSecretsAbsent, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoverySqlFirewallCreateObserved, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoverySqlFirewallCleanupObserved, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoverySqlBootstrapObserved, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoveryObservationInProgress, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoveryObservationInsufficient, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoveryCheckpointUncertain, ManagedElsaReasonClass.AutoResuming),
            new(AzurePromotionUncertain, ManagedElsaReasonClass.AutoResuming),
            new(AzureRecoveryAutoResumeExhausted, ManagedElsaReasonClass.NeedsPerson),
            new(ProviderReconciliationAmbiguous, ManagedElsaReasonClass.NeedsPerson),
            new(ProviderReconciliationCorrelationMismatch, ManagedElsaReasonClass.NeedsPerson),
            new(ProviderReconciliationHealthFailed, ManagedElsaReasonClass.NeedsPerson),
            new(ProviderReconciliationFailed, ManagedElsaReasonClass.NeedsPerson),
            new(ProviderReconciliationCancelled, ManagedElsaReasonClass.NeedsPerson),
            new(ProviderSubmissionRejected, ManagedElsaReasonClass.NeedsPerson),
            new(ProviderIdentityBindingMissing, ManagedElsaReasonClass.NeedsPerson),
            new(AzureRecoveryAutoResumeConflict, ManagedElsaReasonClass.NeedsPerson),
            new(AzureRecoveryAutoResumeRejected, ManagedElsaReasonClass.NeedsPerson),
            new(AzureRecoveryObservationAmbiguous, ManagedElsaReasonClass.NeedsPerson),
            new(AzureRecoveryFoundationOutputsInvalid, ManagedElsaReasonClass.NeedsPerson),
            new(AzureRecoveryWorkloadOutputsInvalid, ManagedElsaReasonClass.NeedsPerson),
            new(AzureRecoverySeedSecretsPartial, ManagedElsaReasonClass.NeedsPerson),
            new(AzureRecoveryStepUnsupported, ManagedElsaReasonClass.NeedsPerson),
            new(AzureRecoveryAssignmentInvalid, ManagedElsaReasonClass.NeedsPerson),
            new(AzureRecoveryAssignmentMismatch, ManagedElsaReasonClass.NeedsPerson),
            new(AzureRecoveryIdentityMismatch, ManagedElsaReasonClass.NeedsPerson),
            new(AzureRecoveryPlanUnavailable, ManagedElsaReasonClass.NeedsPerson),
            new(AzureRecoveryPlanMismatch, ManagedElsaReasonClass.NeedsPerson),
            new(AzureRecoveryStateInvalid, ManagedElsaReasonClass.NeedsPerson),
            new(StagingLeverRecoveryRequired, ManagedElsaReasonClass.NeedsPerson),
            new(StagingLeverFired, ManagedElsaReasonClass.HealthyHandOff),
            new(StagingLeverReset, ManagedElsaReasonClass.HealthyHandOff),
            new(DeletionLocalAbsent, ManagedElsaReasonClass.HealthyHandOff),
            new(DeletionProviderConfirmedAbsent, ManagedElsaReasonClass.HealthyHandOff),
            new(DeletionProviderUnavailable, ManagedElsaReasonClass.Temporary),
            new(DeletionAzureProviderUnavailable, ManagedElsaReasonClass.Temporary),
            new(DeletionProviderCleanupPending, ManagedElsaReasonClass.Temporary),
            new(DeletionProviderProgressStale, ManagedElsaReasonClass.NeedsPerson),
            new(DeletionBlockedByOperationInFlight, ManagedElsaReasonClass.NeedsPerson),
            new(DeletionProviderAssignmentUnavailable, ManagedElsaReasonClass.Temporary),
            new(DeletionProviderEvidenceUnavailable, ManagedElsaReasonClass.Temporary),
            new(DeletionProviderPlanUnavailable, ManagedElsaReasonClass.Temporary),
            new(DeletionRecoveryCapabilityUnavailable, ManagedElsaReasonClass.Temporary),
            new(DeletionRecoveryAssignmentIncomplete, ManagedElsaReasonClass.Temporary),
            new(DeletionClaimConflict, ManagedElsaReasonClass.Temporary),
            new(DeletionProviderUncertain, ManagedElsaReasonClass.Temporary),
            new(DeletionProviderUnknown, ManagedElsaReasonClass.Temporary),
            new(DeletionProviderFinalizationPending, ManagedElsaReasonClass.Temporary),
            new(DeletionProviderAbsent, ManagedElsaReasonClass.HealthyHandOff),
            new(DeletionCorrelationInvalid, ManagedElsaReasonClass.NeedsPerson),
            new(DeletionItemInvalid, ManagedElsaReasonClass.NeedsPerson),
            new(DeletionProviderAssignmentInvalid, ManagedElsaReasonClass.NeedsPerson),
            new(DeletionProviderCorrelationInvalid, ManagedElsaReasonClass.NeedsPerson),
            new(DeletionProviderCleanupFailed, ManagedElsaReasonClass.NeedsPerson),
            new(DeletionProviderAmbiguous, ManagedElsaReasonClass.NeedsPerson),
            new(DeletionRecoveryAssignmentInvalid, ManagedElsaReasonClass.NeedsPerson),
            new(DeletionRecoveryClaimLost, ManagedElsaReasonClass.NeedsPerson),
            new(DeletionPredecessorRecoverySuperseded, ManagedElsaReasonClass.NeedsPerson),
            new(DeletionRecoveryAuthorityUnavailable, ManagedElsaReasonClass.NeedsPerson),
            new(DeletionRecoveryPlanUnavailable, ManagedElsaReasonClass.NeedsPerson),
            new(AssignmentRebindOperationsInFlight, ManagedElsaReasonClass.Temporary),
            new(AssignmentRebindPlacementMismatch, ManagedElsaReasonClass.NeedsPerson),
            new(AssignmentRebindAmbiguous, ManagedElsaReasonClass.NeedsPerson)
        }.ToFrozenDictionary(entry => entry.Code, StringComparer.Ordinal);

    public static IReadOnlyList<string> DefinedCodes { get; } =
        typeof(ManagedElsaReasonCodeCatalog)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

    public static IReadOnlyList<string> AutoResumingCodes { get; } =
        ByCode.Values
            .Where(entry => entry.Class == ManagedElsaReasonClass.AutoResuming)
            .Select(entry => entry.Code)
            .ToArray();

    public static bool TryGet(string? code, [NotNullWhen(true)] out ManagedElsaReasonCode? entry)
    {
        entry = null;
        return !string.IsNullOrWhiteSpace(code) && ByCode.TryGetValue(code, out entry);
    }

    public static ManagedElsaReasonClass Classify(string? code) =>
        TryGet(code, out var entry) ? entry.Class : ManagedElsaReasonClass.NeedsPerson;

    /// <summary>
    /// The operation's current park reason. Failure wins only for the
    /// intentional uncertain / lever parks; otherwise the latest diagnostic
    /// is current and the run reason is fallback only.
    /// </summary>
    public static string? SelectCurrentReason(
        string? failureCode,
        string? diagnosticCode,
        string? runRecoveryReason = null)
    {
        if (IsOperationOwnedPark(failureCode))
            return failureCode;
        if (!string.IsNullOrWhiteSpace(diagnosticCode))
            return diagnosticCode;
        if (!string.IsNullOrWhiteSpace(failureCode))
            return failureCode;
        return TryGet(runRecoveryReason, out _) ? runRecoveryReason : null;
    }

    public static bool RequiresHuman(
        string? parkReason,
        string? failureCode,
        DateTimeOffset? reasonEnteredAt,
        DateTimeOffset now)
    {
        var reason = SelectCurrentReason(failureCode, parkReason);
        return Evaluate(reason, reasonEnteredAt, now);
    }

    public static bool RequiresHuman(string? code, DateTimeOffset? reasonEnteredAt, DateTimeOffset now) =>
        Evaluate(code, reasonEnteredAt, now);

    private static bool Evaluate(string? code, DateTimeOffset? reasonEnteredAt, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(code) || !ByCode.TryGetValue(code, out var entry))
            return true;

        return entry.Class switch
        {
            ManagedElsaReasonClass.HealthyHandOff => false,
            ManagedElsaReasonClass.NeedsPerson => true,
            ManagedElsaReasonClass.Temporary or ManagedElsaReasonClass.AutoResuming =>
                HasExceededWindow(reasonEnteredAt, now),
            _ => true
        };
    }

    private static bool HasExceededWindow(DateTimeOffset? reasonEnteredAt, DateTimeOffset now) =>
        reasonEnteredAt is { } origin &&
        now.ToUniversalTime() - origin.ToUniversalTime() >= HumanRequiredAfter;

    private static bool IsOperationOwnedPark(string? failureCode) =>
        string.Equals(failureCode, ProviderSubmissionUncertain, StringComparison.Ordinal) ||
        string.Equals(failureCode, StagingLeverRecoveryRequired, StringComparison.Ordinal);
}

public readonly record struct ManagedElsaReasonClockState(
    DateTimeOffset? ReasonEnteredAt,
    DateTimeOffset? RequiresHumanAt);

/// <summary>
/// Advances <see cref="ManagedElsaReasonClockState.ReasonEnteredAt"/> only when
/// the park leaves the deferred 10-minute window or a resume/Recover happens.
/// Same-class parks keep the clock. Temporary and auto-resuming Azure
/// observations also share that window so a flap between them does not
/// restart it. A missing
/// <see cref="ManagedElsaReasonClockState.ReasonEnteredAt"/> starts the clock
/// now. Sets <see cref="ManagedElsaReasonClockState.RequiresHumanAt"/> once
/// when the catalog says a person is required. #662 owns compare-and-set plus
/// outbox.
/// </summary>
public static class ManagedElsaReasonClock
{
    public static ManagedElsaReasonClockState Advance(
        string? previousCode,
        string? nextCode,
        DateTimeOffset? reasonEnteredAt,
        DateTimeOffset? requiresHumanAt,
        DateTimeOffset now,
        bool restartClock)
    {
        now = now.ToUniversalTime();
        if (restartClock)
        {
            if (string.IsNullOrWhiteSpace(nextCode))
                return new(now, null);

            return new(
                now,
                ManagedElsaReasonCodeCatalog.RequiresHuman(nextCode, now, now) ? now : null);
        }

        var enteredAt = reasonEnteredAt is null || !SharesDeferredHumanRequiredClock(previousCode, nextCode)
            ? now
            : reasonEnteredAt;
        if (requiresHumanAt is not null)
            return new(enteredAt, requiresHumanAt);

        return new(
            enteredAt,
            ManagedElsaReasonCodeCatalog.RequiresHuman(nextCode, enteredAt, now) ? now : null);
    }

    private static bool SharesDeferredHumanRequiredClock(string? previousCode, string? nextCode)
    {
        var left = ManagedElsaReasonCodeCatalog.Classify(previousCode);
        var right = ManagedElsaReasonCodeCatalog.Classify(nextCode);
        if (left == right)
            return true;

        // Azure Temporary ↔ AutoResuming flaps share the 10-minute window.
        // A stale Temporary park that later sees an Azure auto-resume is a
        // new observation and restarts the clock.
        return IsDeferredHumanRequired(left) &&
               IsDeferredHumanRequired(right) &&
               IsAzureObservation(previousCode) &&
               IsAzureObservation(nextCode);
    }

    private static bool IsDeferredHumanRequired(ManagedElsaReasonClass value) =>
        value is ManagedElsaReasonClass.Temporary or ManagedElsaReasonClass.AutoResuming;

    private static bool IsAzureObservation(string? code) =>
        !string.IsNullOrWhiteSpace(code) &&
        code.StartsWith("azure.", StringComparison.Ordinal);
}
