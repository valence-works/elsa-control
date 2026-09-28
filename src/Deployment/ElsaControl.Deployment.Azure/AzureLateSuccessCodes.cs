namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Stable machine reason codes for late Azure success observation. Each value is
/// a safe operation code of at most 128 characters.
/// </summary>
public static class AzureLateSuccessCodes
{
    public const string DeploymentFailed = "azure.deployment.failed";
    public const string DeploymentCanceled = "azure.deployment.canceled";
    public const string AutoResumeExhausted = "azure.recovery.auto-resume-exhausted";
    public const string AutoResumeAccepted = "azure.recovery.auto-resume.accepted";
    public const string AutoResumeConflict = "azure.recovery.auto-resume.conflict";
    public const string AutoResumeRejected = "azure.recovery.auto-resume.rejected";

    public static string DeploymentOutcome(string? armProvisioningState) =>
        string.Equals(armProvisioningState, "Canceled", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(armProvisioningState, "Cancelled", StringComparison.OrdinalIgnoreCase)
            ? DeploymentCanceled
            : DeploymentFailed;

    public static bool IsOperatorVisible(string? code) =>
        code is DeploymentFailed or DeploymentCanceled or AutoResumeExhausted
            or AutoResumeAccepted or AutoResumeConflict or AutoResumeRejected;
}
