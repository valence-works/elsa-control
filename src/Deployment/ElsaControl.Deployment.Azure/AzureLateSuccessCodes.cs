using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Stable machine reason codes for late Azure success observation. Each value is
/// a catalog constant so #660 completeness stays one table.
/// </summary>
public static class AzureLateSuccessCodes
{
    public const string DeploymentFailed = ManagedElsaReasonCodeCatalog.AzureDeploymentFailed;
    public const string DeploymentCanceled = ManagedElsaReasonCodeCatalog.AzureDeploymentCanceled;
    public const string AutoResumeExhausted = ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted;
    public const string AutoResumeClaimConflict = ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeClaimConflict;
    public const string AutoResumeAccepted = ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeAccepted;
    public const string AutoResumeConflict = ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeConflict;
    public const string AutoResumeRejected = ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeRejected;

    public static string DeploymentOutcome(string? armProvisioningState) =>
        string.Equals(armProvisioningState, "Canceled", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(armProvisioningState, "Cancelled", StringComparison.OrdinalIgnoreCase)
            ? DeploymentCanceled
            : DeploymentFailed;

    public static bool IsOperatorVisible(string? code) =>
        code is DeploymentFailed or DeploymentCanceled or AutoResumeExhausted
            or AutoResumeClaimConflict or AutoResumeAccepted or AutoResumeConflict
            or AutoResumeRejected;
}
