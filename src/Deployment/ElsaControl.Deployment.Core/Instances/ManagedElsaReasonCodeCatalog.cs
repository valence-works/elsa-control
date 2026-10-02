using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// One keyed reason-code catalog for parks, customer projection, and
/// operator alerting (#657). #646 extends this same record with activity
/// severity and customer label; do not add a second table.
/// </summary>
public sealed record ManagedElsaReasonCode(
    string Code,
    bool RequiresHuman,
    TimeSpan? UncertainHealthyWindow = null);

/// <summary>
/// Shared reason-code catalog. Customer projection and the #657 alert both
/// call <see cref="RequiresHuman"/> so every human-required park is visible
/// and raises exactly one alert.
/// </summary>
public static class ManagedElsaReasonCodeCatalog
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

    public static readonly FrozenDictionary<string, ManagedElsaReasonCode> ByCode =
        new ManagedElsaReasonCode[]
        {
            new(ProviderSubmissionAccepted, RequiresHuman: false),
            new(ElsaInstanceProviderReconciliationService.InProgressCode, RequiresHuman: false),
            new(ElsaInstanceProviderReconciliationService.HealthUnknownCode, RequiresHuman: false),
            new(ElsaInstanceProviderReconciliationService.UnavailableCode, RequiresHuman: false),
            new(ElsaInstanceProviderReconciliationService.UnknownCode, RequiresHuman: false),
            new(
                ProviderSubmissionUncertain,
                RequiresHuman: true,
                UncertainHealthyWindow: SubmissionUncertainHealthyWindow),
            new(ElsaInstanceProviderReconciliationService.AutoResumeExhaustedCode, RequiresHuman: true),
            new(ElsaInstanceProviderReconciliationService.FailedCode, RequiresHuman: true),
            new(ElsaInstanceProviderReconciliationService.HealthFailedCode, RequiresHuman: true),
            new(ElsaInstanceProviderReconciliationService.AmbiguousCode, RequiresHuman: true),
            new(ElsaInstanceProviderReconciliationService.CorrelationMismatchCode, RequiresHuman: true),
            new(ElsaInstanceProviderReconciliationService.RetrySafeCode, RequiresHuman: true),
            new(AzureDeploymentFailed, RequiresHuman: true),
            new(AzureDeploymentWaitExceeded, RequiresHuman: true),
            new(AzureDeploymentCanceled, RequiresHuman: true)
        }.ToFrozenDictionary(entry => entry.Code, StringComparer.Ordinal);

    public static bool TryGet(string? code, [NotNullWhen(true)] out ManagedElsaReasonCode? entry)
    {
        entry = null;
        return !string.IsNullOrWhiteSpace(code) && ByCode.TryGetValue(code, out entry);
    }

    public static bool RequiresHuman(
        string? parkReason,
        string? failureCode,
        DateTimeOffset? parkedAt,
        DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(failureCode) && Evaluate(failureCode, parkedAt, now))
            return true;

        var reason = FirstReason(parkReason, failureCode);
        return reason is null || Evaluate(reason, parkedAt, now);
    }

    private static bool Evaluate(string code, DateTimeOffset? parkedAt, DateTimeOffset now)
    {
        if (!ByCode.TryGetValue(code, out var entry))
            return true;

        if (entry.UncertainHealthyWindow is { } window &&
            !HasExceededUncertainWindow(parkedAt, now, window))
            return false;

        return entry.RequiresHuman;
    }

    private static bool HasExceededUncertainWindow(
        DateTimeOffset? parkedAt,
        DateTimeOffset now,
        TimeSpan window) =>
        parkedAt is not { } origin ||
        now.ToUniversalTime() - origin.ToUniversalTime() >= window;

    private static string? FirstReason(string? parkReason, string? failureCode)
    {
        if (!string.IsNullOrWhiteSpace(parkReason))
            return parkReason;
        return string.IsNullOrWhiteSpace(failureCode) ? null : failureCode;
    }
}
