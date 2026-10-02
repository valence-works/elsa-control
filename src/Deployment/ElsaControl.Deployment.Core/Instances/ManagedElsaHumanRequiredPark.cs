using System.Collections.Frozen;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Shared classifier for parks that need a human. Customer projection and
/// operator alerting (#657) use this same set: every human-required park is
/// visible to the customer and raises exactly one alert.
/// </summary>
public static class ManagedElsaHumanRequiredPark
{
    /// <summary>
    /// Inclusive: <c>provider.submission.uncertain</c> older than this window
    /// is a human-required park. Younger than this stays on the normal
    /// in-progress projection.
    /// </summary>
    public static readonly TimeSpan SubmissionUncertainHealthyWindow = TimeSpan.FromMinutes(10);

    public const string ProviderSubmissionAccepted = "provider.submission.accepted";
    public const string ProviderSubmissionUncertain = "provider.submission.uncertain";
    public const string AzureDeploymentFailed = "azure.deployment.failed";
    public const string AzureDeploymentWaitExceeded = "azure.deployment.wait-exceeded";
    public const string AzureDeploymentCanceled = "azure.deployment.canceled";

    public static readonly FrozenSet<string> HealthyContinuationReasons = new[]
    {
        ProviderSubmissionAccepted,
        ElsaInstanceProviderReconciliationService.InProgressCode,
        ElsaInstanceProviderReconciliationService.HealthUnknownCode
    }.ToFrozenSet(StringComparer.Ordinal);

    public static readonly FrozenSet<string> TransientUncertaintyReasons = new[]
    {
        ElsaInstanceProviderReconciliationService.UnavailableCode,
        ElsaInstanceProviderReconciliationService.UnknownCode
    }.ToFrozenSet(StringComparer.Ordinal);

    public static readonly FrozenSet<string> HumanRequiredReasons = new[]
    {
        ElsaInstanceProviderReconciliationService.AutoResumeExhaustedCode,
        ElsaInstanceProviderReconciliationService.FailedCode,
        ElsaInstanceProviderReconciliationService.HealthFailedCode,
        ElsaInstanceProviderReconciliationService.AmbiguousCode,
        ElsaInstanceProviderReconciliationService.CorrelationMismatchCode,
        ElsaInstanceProviderReconciliationService.RetrySafeCode,
        AzureDeploymentFailed,
        AzureDeploymentWaitExceeded,
        AzureDeploymentCanceled
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool RequiresHuman(
        string? parkReason,
        string? failureCode,
        DateTimeOffset? parkedAt,
        DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(failureCode) &&
            !IsAgeBoundedUncertain(failureCode) &&
            !HealthyContinuationReasons.Contains(failureCode) &&
            !TransientUncertaintyReasons.Contains(failureCode))
            return true;

        var reason = FirstReason(parkReason, failureCode);
        if (reason is null)
            return true;

        if (HealthyContinuationReasons.Contains(reason) || TransientUncertaintyReasons.Contains(reason))
            return false;

        if (IsAgeBoundedUncertain(reason))
            return HasExceededUncertainWindow(parkedAt, now);

        return true;
    }

    public static ManagedElsaRecoveryParkKind Classify(
        string? parkReason,
        string? failureCode,
        DateTimeOffset? parkedAt,
        DateTimeOffset now)
    {
        if (RequiresHuman(parkReason, failureCode, parkedAt, now))
            return ManagedElsaRecoveryParkKind.HumanRequired;

        var reason = FirstReason(parkReason, failureCode);
        return reason is not null && TransientUncertaintyReasons.Contains(reason)
            ? ManagedElsaRecoveryParkKind.TransientUncertainty
            : ManagedElsaRecoveryParkKind.HealthyContinuation;
    }

    private static bool IsAgeBoundedUncertain(string reason) =>
        string.Equals(reason, ProviderSubmissionUncertain, StringComparison.Ordinal);

    private static bool HasExceededUncertainWindow(DateTimeOffset? parkedAt, DateTimeOffset now) =>
        parkedAt is not { } origin ||
        now.ToUniversalTime() - origin.ToUniversalTime() >= SubmissionUncertainHealthyWindow;

    private static string? FirstReason(string? parkReason, string? failureCode)
    {
        if (!string.IsNullOrWhiteSpace(parkReason))
            return parkReason;
        return string.IsNullOrWhiteSpace(failureCode) ? null : failureCode;
    }
}

public enum ManagedElsaRecoveryParkKind
{
    HealthyContinuation,
    TransientUncertainty,
    HumanRequired
}
