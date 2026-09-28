namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Shared find-by-name freshness rules for an ARM deployment Control submitted.
/// A local wait running out is never a verdict; a found name is not proof on its
/// own because names are reused across attempts.
/// </summary>
public static class AzureNamedDeploymentFreshness
{
    /// <summary>Allowance for clock skew between Control and ARM when rejecting a previous same-name record.</summary>
    public static readonly TimeSpan TimestampSkew = TimeSpan.FromMinutes(1);

    public const int MinimumArmIntervalSeconds = 60;
    public const int MaximumArmIntervalSeconds = 300;
    public const int MaximumAutoResumes = 3;

    public static bool IsFresh(DateTimeOffset? deploymentTimestamp, DateTimeOffset attemptedStepStartedAt) =>
        deploymentTimestamp is { } timestamp && timestamp >= attemptedStepStartedAt - TimestampSkew;

    /// <summary>
    /// Prefer the write-ahead attempted-step timestamp. Parked operations that
    /// predate that column fall back to the recovery-required clock, then UpdatedAt.
    /// </summary>
    public static DateTimeOffset FreshnessBaseline(
        DateTimeOffset? attemptedStepStartedAt,
        DateTimeOffset? statusChangedAt,
        DateTimeOffset updatedAt) =>
        attemptedStepStartedAt ?? statusChangedAt ?? updatedAt;

    /// <summary>
    /// Accepts either Control's SHA-256 plan fingerprint or Bicep <c>uniqueString(planInput)</c>.
    /// A different SHA-256 is a plan mismatch; a missing output is not proof.
    /// </summary>
    public static bool MatchesPlanFingerprint(string? outputFingerprint, string planFingerprint)
    {
        if (string.IsNullOrWhiteSpace(outputFingerprint) || string.IsNullOrWhiteSpace(planFingerprint))
            return false;
        if (string.Equals(outputFingerprint, planFingerprint, StringComparison.OrdinalIgnoreCase))
            return true;
        if (outputFingerprint.Length == 64 && outputFingerprint.All(Uri.IsHexDigit))
            return false;
        return outputFingerprint.Length is > 0 and <= 64 &&
               outputFingerprint.All(char.IsAsciiLetterOrDigit);
    }

    public static int NextBackoffSeconds(int currentBackoffSeconds) =>
        currentBackoffSeconds < MinimumArmIntervalSeconds
            ? MinimumArmIntervalSeconds
            : Math.Min(MaximumArmIntervalSeconds, currentBackoffSeconds * 2);

    public static bool IsArmReadDue(
        DateTimeOffset now,
        DateTimeOffset? lastArmObservedAt,
        int backoffSeconds) =>
        lastArmObservedAt is null ||
        now >= lastArmObservedAt.Value + TimeSpan.FromSeconds(Math.Max(backoffSeconds, MinimumArmIntervalSeconds));

    public static bool IsConfirmedCompletedResume(
        AzureProviderRunnerStep? attemptedStep,
        AzureProviderRunnerStep completedStep) =>
        attemptedStep == completedStep;
}
