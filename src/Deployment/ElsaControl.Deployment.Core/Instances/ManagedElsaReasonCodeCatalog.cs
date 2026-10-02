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
    public const string ProviderIdentityBindingMissing = "provider.identity-binding-missing";
    public const string AzureDeploymentFailed = "azure.deployment.failed";
    public const string AzureDeploymentWaitExceeded = "azure.deployment.wait-exceeded";
    public const string AzureDeploymentCanceled = "azure.deployment.canceled";
    public const string AzureRecoveryAutoResumeExhausted = "azure.recovery.auto-resume-exhausted";
    public const string AzureRecoveryAutoResumeClaimConflict = "azure.recovery.auto-resume.claim-conflict";
    public const string AzureRecoveryAutoResumeAccepted = "azure.recovery.auto-resume.accepted";
    public const string AzureRecoveryAutoResumeConflict = "azure.recovery.auto-resume.conflict";
    public const string AzureRecoveryAutoResumeRejected = "azure.recovery.auto-resume.rejected";
    public const string StagingLeverRecoveryRequired = "staging.lever.recovery-required";

    public static readonly FrozenDictionary<string, ManagedElsaReasonCode> ByCode =
        new ManagedElsaReasonCode[]
        {
            new(ProviderSubmissionAccepted, ManagedElsaReasonClass.HealthyHandOff),
            new(ProviderReconciliationInProgress, ManagedElsaReasonClass.HealthyHandOff),
            new(ProviderReconciliationConverged, ManagedElsaReasonClass.HealthyHandOff),
            new(AzureRecoveryAutoResumeAccepted, ManagedElsaReasonClass.HealthyHandOff),
            new(ProviderSubmissionUncertain, ManagedElsaReasonClass.Temporary),
            new(ProviderReconciliationUnknown, ManagedElsaReasonClass.Temporary),
            new(ProviderReconciliationUnavailable, ManagedElsaReasonClass.Temporary),
            new(ProviderReconciliationHealthUnknown, ManagedElsaReasonClass.Temporary),
            new(ProviderReconciliationRetrySafe, ManagedElsaReasonClass.Temporary),
            new(AzureRecoveryAutoResumeClaimConflict, ManagedElsaReasonClass.Temporary),
            new(AzureDeploymentFailed, ManagedElsaReasonClass.AutoResuming),
            new(AzureDeploymentWaitExceeded, ManagedElsaReasonClass.AutoResuming),
            new(AzureDeploymentCanceled, ManagedElsaReasonClass.AutoResuming),
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
            new(StagingLeverRecoveryRequired, ManagedElsaReasonClass.NeedsPerson)
        }.ToFrozenDictionary(entry => entry.Code, StringComparer.Ordinal);

    public static IReadOnlyList<string> DefinedCodes { get; } =
        typeof(ManagedElsaReasonCodeCatalog)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

    public static bool TryGet(string? code, [NotNullWhen(true)] out ManagedElsaReasonCode? entry)
    {
        entry = null;
        return !string.IsNullOrWhiteSpace(code) && ByCode.TryGetValue(code, out entry);
    }

    public static ManagedElsaReasonClass Classify(string? code) =>
        TryGet(code, out var entry) ? entry.Class : ManagedElsaReasonClass.NeedsPerson;

    public static bool RequiresHuman(
        string? parkReason,
        string? failureCode,
        DateTimeOffset? reasonEnteredAt,
        DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(failureCode) && Evaluate(failureCode, reasonEnteredAt, now))
            return true;

        var reason = FirstReason(parkReason, failureCode);
        return reason is null || Evaluate(reason, reasonEnteredAt, now);
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
        reasonEnteredAt is not { } origin ||
        now.ToUniversalTime() - origin.ToUniversalTime() >= HumanRequiredAfter;

    private static string? FirstReason(string? parkReason, string? failureCode)
    {
        if (!string.IsNullOrWhiteSpace(parkReason))
            return parkReason;
        return string.IsNullOrWhiteSpace(failureCode) ? null : failureCode;
    }
}

public readonly record struct ManagedElsaReasonClockState(
    DateTimeOffset? ReasonEnteredAt,
    DateTimeOffset? RequiresHumanAt);

/// <summary>
/// Advances <see cref="ManagedElsaReasonClockState.ReasonEnteredAt"/> only when
/// the catalog class changes or a resume/Recover happens. Sets
/// <see cref="ManagedElsaReasonClockState.RequiresHumanAt"/> once when the
/// catalog says a person is required. #662 owns compare-and-set plus outbox.
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

        var previousClass = ManagedElsaReasonCodeCatalog.Classify(previousCode);
        var nextClass = ManagedElsaReasonCodeCatalog.Classify(nextCode);
        var enteredAt = reasonEnteredAt is null || previousClass != nextClass ? now : reasonEnteredAt;
        if (requiresHumanAt is not null)
            return new(enteredAt, requiresHumanAt);

        return new(
            enteredAt,
            ManagedElsaReasonCodeCatalog.RequiresHuman(nextCode, enteredAt, now) ? now : null);
    }
}
