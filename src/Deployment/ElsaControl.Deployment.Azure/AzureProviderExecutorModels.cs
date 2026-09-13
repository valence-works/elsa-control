namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Coarse lifecycle operations understood by the Azure provider runner. The runner maps these
/// steps to the checked-in Bicep and runbook authority; it does not receive raw ARM payloads.
/// </summary>
public enum AzureProviderRunnerStep
{
    Foundation,
    AcrPull,
    SeedSecrets,
    SqlBootstrap,
    Workload,
    Health,
    Promotion,
    RestoreStableTraffic,
    Cleanup,
    /// <summary>Creates the short-lived SQL firewall rule before bootstrap.</summary>
    SqlFirewallCreate,
    /// <summary>Runs the SQL bootstrap script after the firewall is proven present.</summary>
    SqlBootstrapScript,
    /// <summary>Removes the short-lived SQL firewall rule after bootstrap.</summary>
    SqlFirewallCleanup
}

public enum AzureProviderRunnerOutcome
{
    Completed,
    /// <summary>
    /// The runner observed the same postcondition required for <see cref="Completed"/> without
    /// issuing a new mutation. A no-op must return the same complete safe resource observations
    /// as a completed step; absence of evidence is not convergence.
    /// </summary>
    NoOp,
    Failed,
    Uncertain
}

/// <summary>
/// Safe execution context passed to the provider implementation. All persisted values are
/// already bounded by <see cref="AzureProviderOperationValidation"/>; secret values are not
/// representable and the plan contains references only.
/// </summary>
public sealed record AzureProviderRunnerCommand(
    AzureProviderRunnerStep Step,
    AzureWorkloadPlan Plan,
    AzureProviderResourceReferences Resources,
    string? StableTrafficRevisionName,
    bool IsResume,
    int AttemptNumber,
    AzureProviderExecutionContext Context,
    AzureProviderResourceAssignment? Assignment = null);

/// <summary>
/// Safe durable correlation supplied to every runner step. Target Azure scope remains explicit
/// runner configuration, while this context binds every mutation and observation to the exact
/// accepted operation and immutable plan/template identities.
/// </summary>
public sealed record AzureProviderExecutionContext(
    Guid WorkspaceId,
    Guid OrganizationId,
    Guid InstanceId,
    Guid OperationId,
    string OperationIdentity,
    string IdempotencyKey,
    string TargetKey,
    string ProviderAssignmentId,
    string PlanFingerprint,
    string TemplateFingerprint,
    string? ProviderScopeFingerprint);

/// <summary>
/// Safe result returned by a provider step. A failed result means the provider knows the step
/// did not complete. An uncertain result means an external side effect may have committed and
/// the durable operation must be recovered before another mutation is attempted. For a
/// reference-only checkpoint, a null endpoint or Unknown health means that the step did not
/// provide a new observation; the prior durable observation is retained.
/// </summary>
public sealed record AzureProviderRunnerResult(
    AzureProviderRunnerOutcome Outcome,
    AzureProviderOperationPhase Phase,
    AzureProviderResourceReferences Resources,
    AzureProviderHealth Health,
    string? Endpoint,
    IReadOnlyList<AzureProviderDiagnostic> Diagnostics,
    string Code,
    string Message,
    bool OwnedResourcesAbsent = false,
    bool StableTrafficRestored = false);

public sealed record AzureProviderExecutionRequest(
    AzureProviderOperationRequest Operation,
    AzureWorkloadPlan Plan);

public enum AzureProviderExecutionOutcome
{
    Succeeded,
    NoOp,
    InProgress,
    Failed,
    RecoveryRequired
}

public sealed record AzureProviderExecutionResult(
    AzureProviderOperation Operation,
    AzureProviderExecutionOutcome Outcome,
    string Code,
    string Message)
{
    public bool Succeeded => Outcome is AzureProviderExecutionOutcome.Succeeded or AzureProviderExecutionOutcome.NoOp;
}

/// <summary>
/// A read-only observation made for an explicitly accepted lifecycle recovery. The
/// observation identifies at most one completed provider step; it is never a whole
/// operation result. Later lifecycle steps still run through the normal executor
/// checkpoints and health/traffic gates.
/// </summary>
public enum AzureProviderRecoveryObservationKind
{
    Confirmed,
    InProgress,
    Unknown,
    Ambiguous
}

public sealed record AzureProviderRecoveryObservation(
    AzureProviderRecoveryObservationKind Kind,
    AzureProviderRunnerStep? CompletedStep,
    AzureProviderResourceReferences Resources,
    AzureProviderHealth Health,
    string? Endpoint,
    string Code,
    string Message)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Health) || Resources is null)
            throw new ArgumentException("The Azure recovery observation is invalid.");
        AzureProviderOperationValidation.ValidateCode(Code);
        AzureProviderOperationValidation.ValidateMessage(Message);
        AzureProviderOperationValidation.ValidateReferences(Resources);
        AzureProviderOperationValidation.ValidateEndpoint(Endpoint);
        if (Kind == AzureProviderRecoveryObservationKind.Confirmed && CompletedStep is null)
            throw new ArgumentException("A confirmed recovery observation must identify a completed step.", nameof(CompletedStep));
        if (CompletedStep is { } completedStep &&
            !AzureProviderRecoveryObservationSupport.IsSupportedCompletedStep(completedStep))
            throw new ArgumentException("The observed recovery step cannot be resumed.", nameof(CompletedStep));
        if (Kind != AzureProviderRecoveryObservationKind.Confirmed && CompletedStep is not null)
            throw new ArgumentException("An uncertain recovery observation cannot identify a completed step.", nameof(CompletedStep));
        if (Kind != AzureProviderRecoveryObservationKind.Confirmed &&
            (Health != AzureProviderHealth.Unknown || Endpoint is not null))
            throw new ArgumentException("An uncertain recovery observation must remain value-free and unknown.", nameof(Health));
    }
}

/// <summary>
/// Exact provider identity and retained plan supplied after lifecycle acceptance.
/// Provider adapters must reject a mismatch before making any remote observation.
/// </summary>
public sealed record AzureProviderRecoveryRequest(
    AzureProviderOperation Operation,
    AzureWorkloadPlan Plan,
    AzureProviderResourceAssignment? Assignment = null)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Operation);
        ArgumentNullException.ThrowIfNull(Plan);
        if (Operation.Id == Guid.Empty || Operation.WorkspaceId == Guid.Empty ||
            Operation.Action != AzureProviderOperationAction.Reconcile ||
            Operation.Status is not (AzureProviderOperationStatus.RecoveryRequired or AzureProviderOperationStatus.Running or AzureProviderOperationStatus.Succeeded))
            throw new InvalidOperationException("The Azure recovery operation identity is invalid.");
        if (Operation.OrganizationId is null || Operation.InstanceId is null ||
            Operation.LifecycleAction is null || Operation.ProviderAssignmentId is null ||
            !string.Equals(Operation.TargetKey, Plan.WorkloadName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Operation.PlanFingerprint, Plan.Fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The Azure recovery operation is not bound to its retained plan.");
        try
        {
            AzureProviderRecoveryObservationRecord.RequireFingerprint(
                Operation.PlanFingerprint, nameof(Operation.PlanFingerprint));
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("The Azure recovery operation is not bound to its retained plan.");
        }
        // After a placement-stable rebind the assignment fingerprint is the current
        // runner scope. The retained operation keeps the fingerprint it was admitted
        // with; lineage is verified by the caller, not this identity check.
        if (Assignment is { } assignment &&
            (assignment.Id != Operation.ProviderAssignmentId ||
             assignment.OrganizationId != Operation.OrganizationId ||
             assignment.WorkspaceId != Operation.WorkspaceId ||
             assignment.InstanceId != Operation.InstanceId ||
             assignment.LastOperationId != Operation.Id ||
             !string.Equals(assignment.WorkloadName, Operation.TargetKey, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The Azure recovery assignment is not bound to its retained operation.");
        try
        {
            // Fingerprint equality alone is not sufficient at this adapter seam. Validate every
            // retained provider-plan field against the durable operation before a concrete
            // observer is allowed to issue even a read-only Azure command.
            AzureProviderExecutor.ValidateExecutionRequest(
                new AzureProviderExecutionRequest(
                    AzureProviderOperationService.CreateOperationRequest(Operation),
                    Plan));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException("The Azure recovery operation is not bound to its retained plan.");
        }
        AzureProviderOperationValidation.ValidateReferences(Operation.Resources);
    }
}

/// <summary>
/// Read-only remote observation authority for explicit recovery. Implementations
/// must not apply, retry, delete, or otherwise mutate Azure resources.
/// </summary>
public interface IAzureProviderRecoveryObserver
{
    Task<AzureProviderRecoveryObservation> ObserveAsync(
        AzureProviderRecoveryRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapter over the checked-in Azure Bicep/runbook lifecycle. Implementations may use Azure CLI,
/// an SDK or a remote worker, but they must return only the safe result contract above.
/// </summary>
public interface IAzureProviderRunner
{
    Task<AzureProviderRunnerResult> RunAsync(
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken = default);
}
