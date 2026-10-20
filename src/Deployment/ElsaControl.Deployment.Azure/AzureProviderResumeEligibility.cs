namespace ElsaControl.Deployment.Azure;

/// <summary>
/// The one pre-claim gate for resuming a RecoveryRequired Azure operation from a recovery
/// observation. The reconciler's auto-resume charge (<see cref="CanAutoResume"/>), the provider
/// resume (<see cref="IsRecordAuthoritative"/>), the accepted-recovery ledger
/// (<see cref="IsBoundToReconciledInstanceVersion"/>) and the executor (<see cref="IsEligible"/>)
/// all evaluate these functions, so an auto-resume is never charged for a resume the provider
/// would refuse (#860).
/// </summary>
public static class AzureProviderResumeEligibility
{
    /// <summary>
    /// Whether a reconcile-time observation may charge an automatic resume: the observation
    /// satisfies the executor gate for the current checkpoint, the receipt is authoritative for
    /// the current provider tuple, and the receipt stays bound to the reconciled instance version
    /// whether or not the reconcile that hands it out changes the aggregate.
    /// </summary>
    public static bool CanAutoResume(
        AzureProviderOperation operation,
        AzureProviderRecoveryObservation observation,
        AzureProviderRecoveryObservationRecord receipt,
        int instanceVersion)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return IsEligible(operation, observation) &&
               IsRecordAuthoritative(receipt, operation) &&
               instanceVersion < int.MaxValue &&
               IsBoundToReconciledInstanceVersion(receipt.ObservedInstanceVersion, instanceVersion) &&
               IsBoundToReconciledInstanceVersion(receipt.ObservedInstanceVersion, instanceVersion + 1);
    }

    /// <summary>
    /// The executor's eligibility for one observation: the operation awaits recovery, the observed
    /// step is a compatible boundary for the attempted step and phase, and the durable handles
    /// match that step. A Failed observation retries its own step; a Confirmed one checkpoints it.
    /// </summary>
    public static bool IsEligible(
        AzureProviderOperation operation,
        AzureProviderRecoveryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(observation);
        if (operation.Status != AzureProviderOperationStatus.RecoveryRequired ||
            observation.CompletedStep is not { } step)
            return false;

        if (observation.Kind == AzureProviderRecoveryObservationKind.Failed)
            return AzureProviderRecoveryObservationSupport.IsFailedStepRetryBoundary(
                       operation.AttemptedStep, operation.Phase, step, operation.Phase) &&
                   step switch
                   {
                       AzureProviderRunnerStep.Foundation => AzureProviderRecoveryObservationSupport.IsFoundationOnlyEligible(operation),
                       AzureProviderRunnerStep.AcrPull => AzureProviderRecoveryObservationSupport.IsAcrPullEligible(operation),
                       AzureProviderRunnerStep.Workload => AzureProviderRecoveryObservationSupport.IsWorkloadEligible(operation),
                       _ => false
                   };

        if (observation.Kind != AzureProviderRecoveryObservationKind.Confirmed)
            return false;

        // The legacy combined SQL step, and later health/traffic steps, are intentionally not
        // recoverable from a single observation. A confirmed Workload resumes at Health.
        AzureProviderOperationPhase observedPhase;
        try
        {
            observedPhase = AzureProviderRecoveryObservationSupport.RecoveryPhase(step);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
                   operation.AttemptedStep, operation.Phase, step, observedPhase) &&
               step switch
               {
                   AzureProviderRunnerStep.Foundation => AzureProviderRecoveryObservationSupport.IsFoundationOnlyEligible(operation),
                   AzureProviderRunnerStep.AcrPull =>
                       AzureProviderRecoveryObservationSupport.IsAcrPullEligible(operation) ||
                       AzureProviderRecoveryObservationSupport.IsSeedSecretsEligible(operation),
                   AzureProviderRunnerStep.SeedSecrets => AzureProviderRecoveryObservationSupport.IsSeedSecretsEligible(operation),
                   AzureProviderRunnerStep.SqlFirewallCreate => AzureProviderRecoveryObservationSupport.IsSqlFirewallCreateEligible(operation),
                   AzureProviderRunnerStep.SqlBootstrapScript => AzureProviderRecoveryObservationSupport.IsSqlBootstrapScriptEligible(operation),
                   AzureProviderRunnerStep.SqlFirewallCleanup => AzureProviderRecoveryObservationSupport.IsSqlFirewallCleanupEligible(operation),
                   AzureProviderRunnerStep.Workload => AzureProviderRecoveryObservationSupport.IsWorkloadEligible(operation),
                   _ => false
               };
    }

    /// <summary>
    /// Whether a recorded observation still describes the current, unclaimed provider operation:
    /// the same retained operation, request, attempt, version, checkpoint, placement, plan and
    /// template, at a compatible boundary. Lifecycle and submission identity are checked by the
    /// caller that owns them.
    /// </summary>
    public static bool IsRecordAuthoritative(
        AzureProviderRecoveryObservationRecord record,
        AzureProviderOperation operation)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(operation);
        return record.ProviderOperationId == operation.Id &&
               record.OrganizationId == operation.OrganizationId &&
               record.WorkspaceId == operation.WorkspaceId &&
               record.InstanceId == operation.InstanceId &&
               record.LifecycleAction == operation.LifecycleAction &&
               string.Equals(record.ProviderOperationIdentity, operation.OperationIdentity, StringComparison.Ordinal) &&
               string.Equals(record.ProviderRequestHash, operation.RequestHash, StringComparison.Ordinal) &&
               record.ProviderAttemptNumber == operation.AttemptNumber &&
               record.ProviderVersion == operation.Version &&
               record.ProviderCheckpointSequence == operation.CheckpointSequence &&
               record.ProviderAssignmentId == operation.ProviderAssignmentId &&
               string.Equals(record.TargetKey, operation.TargetKey, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(record.ProviderPlanFingerprint, operation.PlanFingerprint, StringComparison.Ordinal) &&
               string.Equals(record.ProviderTemplateFingerprint, operation.TemplateFingerprint, StringComparison.Ordinal) &&
               AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
                   operation.AttemptedStep, operation.Phase, record.CompletedStep, record.ObservedPhase);
    }

    /// <summary>
    /// A receipt is minted at the instance version its reconcile read. That reconcile commits the
    /// next version when it changes the aggregate and keeps the version when it is a no-op, so an
    /// accepted recovery binds the receipt to the reconciled version or the one after it.
    /// </summary>
    public static bool IsBoundToReconciledInstanceVersion(
        int receiptInstanceVersion,
        int reconciledInstanceVersion) =>
        receiptInstanceVersion >= 1 &&
        (reconciledInstanceVersion == receiptInstanceVersion ||
         receiptInstanceVersion < int.MaxValue && reconciledInstanceVersion == receiptInstanceVersion + 1);
}
