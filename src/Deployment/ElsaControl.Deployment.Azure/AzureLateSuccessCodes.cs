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
    public const string Retrying = ManagedElsaReasonCodeCatalog.AzureRecoveryRetrying;
    public const string NeedsOperator = ManagedElsaReasonCodeCatalog.AzureRecoveryNeedsOperator;

    public static string DeploymentOutcome(string? armProvisioningState) =>
        string.Equals(armProvisioningState, "Canceled", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(armProvisioningState, "Cancelled", StringComparison.OrdinalIgnoreCase)
            ? DeploymentCanceled
            : DeploymentFailed;

    public static bool IsOperatorVisible(string? code) =>
        code is DeploymentFailed or DeploymentCanceled or AutoResumeExhausted
            or AutoResumeClaimConflict or AutoResumeAccepted or AutoResumeConflict
            or AutoResumeRejected or Retrying or NeedsOperator;

    public static bool IsTerminalDeploymentFailure(string? code) =>
        code is DeploymentFailed or DeploymentCanceled or Retrying or NeedsOperator
            or AutoResumeExhausted;

    /// <summary>
    /// A charged auto-resume came back with a refusal that another ARM read cannot change, so
    /// the operation waits for an operator and no further auto-resume is charged.
    /// </summary>
    public static bool IsAutoResumeBlocked(AzureProviderOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation.AutoResumeOutcomes.Contains(NeedsOperator, StringComparer.Ordinal);
    }

    /// <summary>
    /// Provider resume refusals that a later ARM read cannot change: a rejected request, or a
    /// pre-claim authority, plan, identity or eligibility mismatch. Anything else (an ARM read
    /// that failed or is still in progress, a lost claim, or an outcome after the claim) keeps
    /// the ordinary auto-resume backoff and budget.
    /// </summary>
    public static bool IsNonTransientResumeRefusal(ElsaInstanceProviderRecoveryOutcome outcome, string? code) =>
        outcome == ElsaInstanceProviderRecoveryOutcome.Rejected ||
        outcome == ElsaInstanceProviderRecoveryOutcome.RecoveryRequired &&
        code is ManagedElsaReasonCodeCatalog.AzureRecoveryObservationInsufficient
            or ManagedElsaReasonCodeCatalog.AzureRecoveryObservationInvalid
            or ManagedElsaReasonCodeCatalog.AzureRecoveryObservationUnavailable
            or ManagedElsaReasonCodeCatalog.AzureRecoveryStepUnsupported
            or ManagedElsaReasonCodeCatalog.AzureRecoveryOperationUnavailable
            or ManagedElsaReasonCodeCatalog.AzureRecoveryUnavailable
            or ManagedElsaReasonCodeCatalog.AzureRecoveryAssignmentInvalid
            or ManagedElsaReasonCodeCatalog.AzureRecoveryAssignmentMismatch
            or ManagedElsaReasonCodeCatalog.AzureRecoveryIdentityMismatch
            or ManagedElsaReasonCodeCatalog.AzureRecoveryPlanUnavailable
            or ManagedElsaReasonCodeCatalog.AzureRecoveryPlanMismatch
            or ManagedElsaReasonCodeCatalog.AzureRecoveryStateInvalid
            or ManagedElsaReasonCodeCatalog.AzureRecoveryFoundationOutputsInvalid
            or ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadOutputsInvalid;
}
