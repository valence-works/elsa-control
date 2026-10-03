using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Workspace;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Classifies an immutable, safe managed lifecycle projection. The evaluator only
/// reads existing lifecycle/operation/run states; it never transitions or repairs
/// them, so the lifecycle state machine remains the single recovery authority.
/// </summary>
public sealed class ManagedLifecycleOperationalHealthEvaluator
{
    private readonly ManagedLifecycleOperationalHealthOptions _options;
    private readonly TimeProvider _timeProvider;

    public ManagedLifecycleOperationalHealthEvaluator(
        ManagedLifecycleOperationalHealthOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new ManagedLifecycleOperationalHealthOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ManagedLifecycleOperationalHealthResult Evaluate(ManagedLifecycleOperationalHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var (status, diagnosticCode) = Classify(snapshot, now);
        var alerts = CreateAlerts(snapshot, now);
        var dedupeIdentity = ManagedLifecycleOperationalHealthIdentity.Compute(snapshot, status, diagnosticCode);

        return new ManagedLifecycleOperationalHealthResult(
            status,
            diagnosticCode,
            dedupeIdentity,
            now,
            alerts);
    }

    private (ManagedLifecycleOperationalHealthStatus Status, string DiagnosticCode) Classify(
        ManagedLifecycleOperationalHealthSnapshot snapshot,
        DateTimeOffset now)
    {
        // Human-required parks follow RequiresHumanAt, not RecoveryRequired
        // state alone. Auto-resuming and temporary parks stay off this status
        // until the catalog sets the flag.
        if (IsHumanRequired(snapshot))
            return (
                ManagedLifecycleOperationalHealthStatus.RecoveryRequired,
                ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired);

        // Explicit operation/run failures are authoritative over the current
        // provider projection. Their optional codes are already bounded at input.
        if (snapshot.Operation?.State == ElsaInstanceOperationState.Failed)
            return (
                ManagedLifecycleOperationalHealthStatus.Failed,
                snapshot.Operation.DiagnosticCode ?? ManagedLifecycleOperationalHealthDiagnosticCodes.OperationFailed);

        if (snapshot.Run?.Status == WorkspaceDeploymentRunStatus.Failed)
            return (
                ManagedLifecycleOperationalHealthStatus.Failed,
                snapshot.Run.DiagnosticCode ?? ManagedLifecycleOperationalHealthDiagnosticCodes.RunFailed);

        // The existing guard defines unfinished lifecycle work. Waiting successors
        // are included because they are blocking work even though they do not hold
        // the execution reservation themselves.
        if (IsStaleWork(snapshot, now))
            return (
                ManagedLifecycleOperationalHealthStatus.Stale,
                ManagedLifecycleOperationalHealthDiagnosticCodes.Stale);

        if (IsStaleReconciliation(snapshot, now))
            return (
                ManagedLifecycleOperationalHealthStatus.Stale,
                ManagedLifecycleOperationalHealthDiagnosticCodes.ReconciliationStale);

        // An unconfirmed or explicitly unknown provider report cannot be upgraded
        // by a retained projection. Explicit operation/run failure above remains
        // actionable because it is control-plane evidence, not provider inference.
        if (IsUnknownReconciliation(snapshot))
            return (
                ManagedLifecycleOperationalHealthStatus.Unknown,
                snapshot.ProviderDiagnosticCode ?? ManagedLifecycleOperationalHealthDiagnosticCodes.ProviderUnknown);

        if (snapshot.ObservedLifecycle == ElsaObservedLifecycle.Failed ||
            snapshot.Health == ElsaInstanceHealth.Unreachable)
            return (
                ManagedLifecycleOperationalHealthStatus.Failed,
                snapshot.ProviderDiagnosticCode ?? ManagedLifecycleOperationalHealthDiagnosticCodes.Failed);

        if (snapshot.ObservedLifecycle == ElsaObservedLifecycle.Degraded ||
            snapshot.Health == ElsaInstanceHealth.Degraded)
            return (
                ManagedLifecycleOperationalHealthStatus.Degraded,
                snapshot.ProviderDiagnosticCode ?? ManagedLifecycleOperationalHealthDiagnosticCodes.Degraded);

        var hasActiveOperation = snapshot.Operation is not null &&
                                 ElsaInstanceOperationGuard.IsBlocking(snapshot.Operation.State);
        var hasActiveRun = snapshot.Run is not null && IsActiveRun(snapshot.Run.Status);
        if (hasActiveOperation || hasActiveRun)
            return (
                ManagedLifecycleOperationalHealthStatus.Degraded,
                snapshot.ProviderDiagnosticCode ?? ManagedLifecycleOperationalHealthDiagnosticCodes.WorkActive);

        if (snapshot.DesiredLifecycle == ElsaDesiredLifecycle.Running &&
            snapshot.ObservedLifecycle == ElsaObservedLifecycle.Ready &&
            snapshot.Health == ElsaInstanceHealth.Healthy)
            return (
                ManagedLifecycleOperationalHealthStatus.Healthy,
                ManagedLifecycleOperationalHealthDiagnosticCodes.Healthy);

        // A tombstone has no runtime health to report. Other known, non-ready
        // projections (including an intentional stop) are degraded until a caller
        // chooses a richer product-level status vocabulary.
        if (snapshot.ObservedLifecycle == ElsaObservedLifecycle.Deleted)
            return (
                ManagedLifecycleOperationalHealthStatus.Unknown,
                ManagedLifecycleOperationalHealthDiagnosticCodes.Unknown);

        return (
            ManagedLifecycleOperationalHealthStatus.Degraded,
            snapshot.ProviderDiagnosticCode ?? ManagedLifecycleOperationalHealthDiagnosticCodes.Degraded);
    }

    private IReadOnlyList<ManagedLifecycleOperationalHealthAlert> CreateAlerts(
        ManagedLifecycleOperationalHealthSnapshot snapshot,
        DateTimeOffset now)
    {
        var alerts = new List<ManagedLifecycleOperationalHealthAlert>();

        if (IsHumanRequired(snapshot))
            alerts.Add(CreateAlert(
                snapshot,
                ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired,
                SeverityForReason(snapshot.Operation?.DiagnosticCode)));

        if (snapshot.Operation?.State == ElsaInstanceOperationState.Failed)
            alerts.Add(CreateAlert(
                snapshot,
                ManagedLifecycleOperationalHealthDiagnosticCodes.OperationFailed,
                ManagedLifecycleOperationalHealthAlertSeverity.Critical));

        if (snapshot.Run?.Status == WorkspaceDeploymentRunStatus.Failed)
            alerts.Add(CreateAlert(
                snapshot,
                ManagedLifecycleOperationalHealthDiagnosticCodes.RunFailed,
                ManagedLifecycleOperationalHealthAlertSeverity.Critical));

        if (IsStaleWork(snapshot, now))
            alerts.Add(CreateAlert(
                snapshot,
                ManagedLifecycleOperationalHealthDiagnosticCodes.StaleWork,
                ManagedLifecycleOperationalHealthAlertSeverity.Warning));

        if (IsStaleReconciliation(snapshot, now))
            alerts.Add(CreateAlert(
                snapshot,
                ManagedLifecycleOperationalHealthDiagnosticCodes.ReconciliationStale,
                ManagedLifecycleOperationalHealthAlertSeverity.Warning));

        if (IsUnknownReconciliation(snapshot))
            alerts.Add(CreateAlert(
                snapshot,
                ManagedLifecycleOperationalHealthDiagnosticCodes.ReconciliationUnknown,
                ManagedLifecycleOperationalHealthAlertSeverity.Warning));

        if (IsUnhealthyEndpointProjection(snapshot))
            alerts.Add(CreateAlert(
                snapshot,
                ManagedLifecycleOperationalHealthDiagnosticCodes.UnhealthyEndpoint,
                snapshot.Health is ElsaInstanceHealth.Unreachable || snapshot.ObservedLifecycle == ElsaObservedLifecycle.Failed
                    ? ManagedLifecycleOperationalHealthAlertSeverity.Critical
                    : ManagedLifecycleOperationalHealthAlertSeverity.Warning));

        if (IsRetryExhausted(snapshot))
            alerts.Add(CreateAlert(
                snapshot,
                ManagedLifecycleOperationalHealthDiagnosticCodes.RetryExhausted,
                ManagedLifecycleOperationalHealthAlertSeverity.Critical));

        return alerts.ToArray();
    }

    private static bool IsHumanRequired(ManagedLifecycleOperationalHealthSnapshot snapshot) =>
        snapshot.Operation is { State: ElsaInstanceOperationState.RecoveryRequired, RequiresHumanAt: not null };

    private static ManagedLifecycleOperationalHealthAlertSeverity SeverityForReason(string? diagnosticCode) =>
        ManagedElsaReasonCodeCatalog.Classify(diagnosticCode) switch
        {
            ManagedElsaReasonClass.Temporary or ManagedElsaReasonClass.AutoResuming =>
                ManagedLifecycleOperationalHealthAlertSeverity.Warning,
            _ => ManagedLifecycleOperationalHealthAlertSeverity.Critical
        };

    private bool IsRetryExhausted(ManagedLifecycleOperationalHealthSnapshot snapshot)
    {
        var operationExhausted = snapshot.Operation is not null &&
                                 snapshot.Operation.AttemptNumber >= _options.MaxAttempts &&
                                 snapshot.Operation.State is not
                                     (ElsaInstanceOperationState.Succeeded or ElsaInstanceOperationState.Cancelled);
        var runExhausted = snapshot.Run is not null &&
                           snapshot.Run.AttemptNumber >= _options.MaxAttempts &&
                           snapshot.Run.Status is not
                               (WorkspaceDeploymentRunStatus.Succeeded or
                                WorkspaceDeploymentRunStatus.RolledBack or
                                WorkspaceDeploymentRunStatus.Cancelled or
                                WorkspaceDeploymentRunStatus.Blocked);
        return operationExhausted || runExhausted;
    }

    private static bool IsUnknownReconciliation(ManagedLifecycleOperationalHealthSnapshot snapshot) =>
        snapshot.ProviderObservationKind is ElsaInstanceProviderObservationKind.Unknown or
        ElsaInstanceProviderObservationKind.Ambiguous ||
        snapshot.ObservedLifecycle == ElsaObservedLifecycle.Unknown ||
        snapshot.Health == ElsaInstanceHealth.Unknown;

    private bool IsStaleReconciliation(
        ManagedLifecycleOperationalHealthSnapshot snapshot,
        DateTimeOffset now) =>
        IsUnknownReconciliation(snapshot) &&
        snapshot.ReconciledAt is { } reconciledAt &&
        IsPastDeadline(reconciledAt, now, _options.ReconciliationDeadline);

    private bool IsStaleWork(
        ManagedLifecycleOperationalHealthSnapshot snapshot,
        DateTimeOffset now)
    {
        var staleOperation = snapshot.Operation is not null &&
                             ElsaInstanceOperationGuard.IsBlocking(snapshot.Operation.State) &&
                             IsPastDeadline(LatestProgress(snapshot.Operation.HeartbeatAt,
                                 snapshot.Operation.LastProgressAt,
                                 snapshot.Operation.StartedAt ?? snapshot.Operation.AcceptedAt),
                                 now,
                                 _options.OperationDeadline);
        var staleRun = snapshot.Run is not null &&
                       IsActiveRun(snapshot.Run.Status) &&
                       IsPastDeadline(LatestProgress(snapshot.Run.HeartbeatAt,
                           snapshot.Run.LastProgressAt,
                           snapshot.Run.StartedAt ?? snapshot.Run.QueuedAt),
                           now,
                           _options.RunDeadline);
        return staleOperation || staleRun;
    }

    private static bool IsUnhealthyEndpointProjection(ManagedLifecycleOperationalHealthSnapshot snapshot) =>
        snapshot.ObservedLifecycle is ElsaObservedLifecycle.Degraded or ElsaObservedLifecycle.Failed ||
        snapshot.Health is ElsaInstanceHealth.Degraded or ElsaInstanceHealth.Unreachable;

    private static ManagedLifecycleOperationalHealthAlert CreateAlert(
        ManagedLifecycleOperationalHealthSnapshot snapshot,
        string code,
        ManagedLifecycleOperationalHealthAlertSeverity severity)
    {
        var identity = ManagedLifecycleOperationalHealthIdentity.ComputeAlert(snapshot, code);
        return new ManagedLifecycleOperationalHealthAlert(
            code,
            severity,
            identity);
    }

    private static bool IsActiveRun(WorkspaceDeploymentRunStatus status) =>
        status is WorkspaceDeploymentRunStatus.Queued or WorkspaceDeploymentRunStatus.Running;

    private static bool IsPastDeadline(DateTimeOffset startedAt, DateTimeOffset now, TimeSpan deadline) =>
        now - startedAt > deadline;

    private static DateTimeOffset LatestProgress(
        DateTimeOffset? heartbeatAt,
        DateTimeOffset? lastProgressAt,
        DateTimeOffset baseTimestamp)
    {
        var latest = baseTimestamp;
        if (heartbeatAt is { } heartbeat && heartbeat > latest)
            latest = heartbeat;
        if (lastProgressAt is { } progress && progress > latest)
            latest = progress;
        return latest;
    }
}
