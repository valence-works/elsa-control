using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests;

public sealed class ManagedLifecycleOperationalHealthEvaluatorTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid InstanceId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid OperationId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid RunId = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Ready_healthy_running_without_active_work_is_healthy()
    {
        var result = Evaluate(Snapshot());

        Assert.Equal(ManagedLifecycleOperationalHealthStatus.Healthy, result.Status);
        Assert.Equal(ManagedLifecycleOperationalHealthDiagnosticCodes.Healthy, result.DiagnosticCode);
        Assert.Empty(result.Alerts);
    }

    [Fact]
    public void Failed_operation_is_failed_and_preserves_only_its_stable_code()
    {
        var snapshot = Snapshot(operation: Operation(
            ElsaInstanceOperationState.Failed,
            attemptNumber: 1,
            diagnosticCode: "provider.apply.failed"));

        var result = Evaluate(snapshot);

        Assert.Equal(ManagedLifecycleOperationalHealthStatus.Failed, result.Status);
        Assert.Equal("provider.apply.failed", result.DiagnosticCode);
        Assert.Contains(result.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.OperationFailed);
    }

    [Fact]
    public void Failed_run_is_failed()
    {
        var snapshot = Snapshot(run: Run(
            WorkspaceDeploymentRunStatus.Failed,
            attemptNumber: 1,
            diagnosticCode: "deployment.apply.failed"));

        var result = Evaluate(snapshot);

        Assert.Equal(ManagedLifecycleOperationalHealthStatus.Failed, result.Status);
        Assert.Equal("deployment.apply.failed", result.DiagnosticCode);
        Assert.Contains(result.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.RunFailed);
    }

    [Fact]
    public void Recovery_required_takes_precedence_over_failed_work_and_unknown_provider()
    {
        var snapshot = Snapshot(
            providerObservationKind: ElsaInstanceProviderObservationKind.Unknown,
            operation: Operation(ElsaInstanceOperationState.RecoveryRequired, diagnosticCode: "provider.uncertain"),
            run: Run(WorkspaceDeploymentRunStatus.Failed, diagnosticCode: "deployment.apply.failed"));

        var result = Evaluate(snapshot);

        Assert.Equal(ManagedLifecycleOperationalHealthStatus.RecoveryRequired, result.Status);
        Assert.Equal(ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired, result.DiagnosticCode);
        Assert.Contains(result.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired);
    }

    [Fact]
    public void Active_operation_past_its_deadline_is_stale()
    {
        var snapshot = Snapshot(operation: Operation(
            ElsaInstanceOperationState.Running,
            acceptedAt: Now.AddMinutes(-21),
            startedAt: Now.AddMinutes(-20)));

        var result = Evaluate(snapshot, new ManagedLifecycleOperationalHealthOptions
        {
            OperationDeadline = TimeSpan.FromMinutes(10),
            RunDeadline = TimeSpan.FromMinutes(10)
        });

        Assert.Equal(ManagedLifecycleOperationalHealthStatus.Stale, result.Status);
        Assert.Equal(ManagedLifecycleOperationalHealthDiagnosticCodes.Stale, result.DiagnosticCode);
        Assert.Contains(result.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.StaleWork);
    }

    [Fact]
    public void Work_becomes_stale_only_after_the_exact_deadline_boundary()
    {
        var options = new ManagedLifecycleOperationalHealthOptions
        {
            OperationDeadline = TimeSpan.FromMinutes(10)
        };
        var atBoundary = Evaluate(Snapshot(operation: Operation(
            ElsaInstanceOperationState.Running,
            acceptedAt: Now.AddMinutes(-11),
            startedAt: Now.AddMinutes(-10))), options);
        var afterBoundary = new ManagedLifecycleOperationalHealthEvaluator(
                options,
                new StaticTimeProvider(Now.AddTicks(1)))
            .Evaluate(Snapshot(operation: Operation(
                ElsaInstanceOperationState.Running,
                acceptedAt: Now.AddMinutes(-11),
                startedAt: Now.AddMinutes(-10))));

        Assert.NotEqual(ManagedLifecycleOperationalHealthStatus.Stale, atBoundary.Status);
        Assert.Equal(ManagedLifecycleOperationalHealthStatus.Stale, afterBoundary.Status);
    }

    [Fact]
    public void Active_run_past_its_deadline_is_stale()
    {
        var snapshot = Snapshot(run: Run(
            WorkspaceDeploymentRunStatus.Running,
            queuedAt: Now.AddMinutes(-21),
            startedAt: Now.AddMinutes(-20)));

        var result = Evaluate(snapshot, new ManagedLifecycleOperationalHealthOptions
        {
            OperationDeadline = TimeSpan.FromMinutes(10),
            RunDeadline = TimeSpan.FromMinutes(10)
        });

        Assert.Equal(ManagedLifecycleOperationalHealthStatus.Stale, result.Status);
        Assert.Contains(result.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.StaleWork);
    }

    [Fact]
    public void Unknown_provider_observation_is_unknown_even_when_last_projection_was_ready()
    {
        var snapshot = Snapshot(providerObservationKind: ElsaInstanceProviderObservationKind.Unknown);

        var result = Evaluate(snapshot);

        Assert.Equal(ManagedLifecycleOperationalHealthStatus.Unknown, result.Status);
        Assert.Equal(ManagedLifecycleOperationalHealthDiagnosticCodes.ProviderUnknown, result.DiagnosticCode);
        Assert.Contains(result.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.ReconciliationUnknown);
    }

    [Fact]
    public void Degraded_provider_projection_is_degraded()
    {
        var snapshot = Snapshot(
            observedLifecycle: ElsaObservedLifecycle.Degraded,
            health: ElsaInstanceHealth.Degraded);

        var result = Evaluate(snapshot);

        Assert.Equal(ManagedLifecycleOperationalHealthStatus.Degraded, result.Status);
        Assert.Equal(ManagedLifecycleOperationalHealthDiagnosticCodes.Degraded, result.DiagnosticCode);
        Assert.Contains(result.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.UnhealthyEndpoint);
    }

    [Fact]
    public void Retry_exhaustion_emits_a_stable_alert_and_deterministic_dedupe_identity()
    {
        var snapshot = Snapshot(operation: Operation(
            ElsaInstanceOperationState.Failed,
            attemptNumber: 3,
            diagnosticCode: "provider.apply.failed"));
        var options = new ManagedLifecycleOperationalHealthOptions { MaxAttempts = 3 };

        var first = Evaluate(snapshot, options);
        var second = Evaluate(snapshot, options);

        Assert.Equal(ManagedLifecycleOperationalHealthStatus.Failed, first.Status);
        var firstAlert = Assert.Single(first.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.RetryExhausted);
        var secondAlert = Assert.Single(second.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.RetryExhausted);
        Assert.Equal(firstAlert.DedupeIdentity, secondAlert.DedupeIdentity);
        Assert.DoesNotContain("provider.apply.failed", firstAlert.DedupeIdentity);
    }

    [Fact]
    public void Retry_exhaustion_alert_identity_is_not_affected_by_clock_time()
    {
        var snapshot = Snapshot(operation: Operation(
            ElsaInstanceOperationState.Running,
            attemptNumber: 2,
            acceptedAt: Now.AddMinutes(-2),
            startedAt: Now.AddMinutes(-1)));
        var options = new ManagedLifecycleOperationalHealthOptions
        {
            MaxAttempts = 2,
            OperationDeadline = TimeSpan.FromMinutes(10)
        };

        var first = new ManagedLifecycleOperationalHealthEvaluator(options, new StaticTimeProvider(Now))
            .Evaluate(snapshot);
        var second = new ManagedLifecycleOperationalHealthEvaluator(options, new StaticTimeProvider(Now.AddMinutes(1)))
            .Evaluate(snapshot);

        var firstAlert = Assert.Single(first.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.RetryExhausted);
        var secondAlert = Assert.Single(second.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.RetryExhausted);
        Assert.Equal(firstAlert.DedupeIdentity, secondAlert.DedupeIdentity);
    }

    [Fact]
    public void Latest_operation_progress_keeps_old_work_from_being_marked_stale()
    {
        var snapshot = Snapshot(operation: Operation(
            ElsaInstanceOperationState.Running,
            acceptedAt: Now.AddMinutes(-30),
            startedAt: Now.AddMinutes(-20),
            heartbeatAt: Now.AddMinutes(-15),
            lastProgressAt: Now.AddMinutes(-1)));

        var result = Evaluate(snapshot, new ManagedLifecycleOperationalHealthOptions
        {
            OperationDeadline = TimeSpan.FromMinutes(10)
        });

        Assert.NotEqual(ManagedLifecycleOperationalHealthStatus.Stale, result.Status);
        Assert.DoesNotContain(result.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.StaleWork);
    }

    [Fact]
    public void Latest_run_heartbeat_is_used_for_staleness()
    {
        var snapshot = Snapshot(run: Run(
            WorkspaceDeploymentRunStatus.Running,
            queuedAt: Now.AddMinutes(-30),
            startedAt: Now.AddMinutes(-20),
            heartbeatAt: Now.AddMinutes(-1)));

        var result = Evaluate(snapshot, new ManagedLifecycleOperationalHealthOptions
        {
            RunDeadline = TimeSpan.FromMinutes(10)
        });

        Assert.NotEqual(ManagedLifecycleOperationalHealthStatus.Stale, result.Status);
    }

    [Fact]
    public void Old_reconciliation_is_stale_but_fresh_unknown_reconciliation_remains_unknown()
    {
        var stale = Evaluate(Snapshot(
            providerObservationKind: ElsaInstanceProviderObservationKind.Unknown,
            reconciledAt: Now.AddMinutes(-21)), new ManagedLifecycleOperationalHealthOptions
            {
                ReconciliationDeadline = TimeSpan.FromMinutes(10)
            });
        var unknown = Evaluate(Snapshot(
            providerObservationKind: ElsaInstanceProviderObservationKind.Unknown,
            reconciledAt: Now.AddMinutes(-1)), new ManagedLifecycleOperationalHealthOptions
            {
                ReconciliationDeadline = TimeSpan.FromMinutes(10)
            });

        Assert.Equal(ManagedLifecycleOperationalHealthStatus.Stale, stale.Status);
        Assert.Contains(stale.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.ReconciliationStale);
        Assert.Equal(ManagedLifecycleOperationalHealthStatus.Unknown, unknown.Status);
        Assert.Contains(unknown.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.ReconciliationUnknown);
        Assert.DoesNotContain(unknown.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.ReconciliationStale);
    }

    [Fact]
    public void Old_confirmed_healthy_observation_does_not_imply_stuck_reconciliation()
    {
        var result = Evaluate(Snapshot(reconciledAt: Now.AddHours(-1)), new ManagedLifecycleOperationalHealthOptions
        {
            ReconciliationDeadline = TimeSpan.FromMinutes(10)
        });

        Assert.Equal(ManagedLifecycleOperationalHealthStatus.Healthy, result.Status);
        Assert.DoesNotContain(result.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.ReconciliationStale);
    }

    [Fact]
    public void Recovery_and_retry_exhaustion_are_both_reported_as_alerts()
    {
        var result = Evaluate(
            Snapshot(operation: Operation(ElsaInstanceOperationState.RecoveryRequired, attemptNumber: 3)),
            new ManagedLifecycleOperationalHealthOptions { MaxAttempts = 3 });

        Assert.Equal(ManagedLifecycleOperationalHealthStatus.RecoveryRequired, result.Status);
        Assert.Contains(result.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired);
        var retryAlert = Assert.Single(result.Alerts, alert => alert.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.RetryExhausted);
        Assert.Equal(ManagedLifecycleOperationalHealthAlertSeverity.Critical, retryAlert.Severity);
    }

    [Fact]
    public void Snapshot_timestamps_are_normalized_to_utc_and_cannot_precede_their_base()
    {
        var acceptedAt = new DateTimeOffset(2026, 9, 2, 14, 0, 0, TimeSpan.FromHours(2));
        var operation = Operation(
            ElsaInstanceOperationState.Running,
            acceptedAt: acceptedAt,
            startedAt: acceptedAt.AddMinutes(1),
            heartbeatAt: acceptedAt.AddMinutes(2),
            lastProgressAt: acceptedAt.AddMinutes(3));

        Assert.Equal(TimeSpan.Zero, operation.AcceptedAt.Offset);
        Assert.Equal(TimeSpan.Zero, operation.StartedAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, operation.HeartbeatAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, operation.LastProgressAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, Snapshot(reconciledAt: acceptedAt).ReconciledAt!.Value.Offset);
        Assert.Throws<ArgumentException>(() => Operation(
            ElsaInstanceOperationState.Running,
            acceptedAt: acceptedAt,
            startedAt: acceptedAt.AddMinutes(-1)));
        Assert.Throws<ArgumentException>(() => Operation(
            ElsaInstanceOperationState.Running,
            acceptedAt: acceptedAt,
            heartbeatAt: acceptedAt.AddMinutes(-1)));
        Assert.Throws<ArgumentException>(() => Run(
            WorkspaceDeploymentRunStatus.Running,
            queuedAt: acceptedAt,
            lastProgressAt: acceptedAt.AddMinutes(-1)));
    }

    [Fact]
    public void Unsafe_diagnostic_code_is_rejected_at_the_safe_input_boundary()
    {
        var exception = Assert.Throws<ArgumentException>(() => Operation(
            ElsaInstanceOperationState.Failed,
            diagnosticCode: "provider failure with details"));

        Assert.Equal("diagnosticCode", exception.ParamName);
    }

    [Fact]
    public void Invalid_deadlines_and_attempt_budget_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedLifecycleOperationalHealthOptions
        {
            OperationDeadline = TimeSpan.Zero
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedLifecycleOperationalHealthOptions
        {
            MaxAttempts = 0
        }.Validate());
    }

    private static ManagedLifecycleOperationalHealthEvaluator Evaluator(ManagedLifecycleOperationalHealthOptions? options = null) =>
        new(options, new StaticTimeProvider(Now));

    private static ManagedLifecycleOperationalHealthResult Evaluate(
        ManagedLifecycleOperationalHealthSnapshot snapshot,
        ManagedLifecycleOperationalHealthOptions? options = null) =>
        Evaluator(options).Evaluate(snapshot);

    private static ManagedLifecycleOperationalHealthSnapshot Snapshot(
        ElsaDesiredLifecycle desiredLifecycle = ElsaDesiredLifecycle.Running,
        ElsaObservedLifecycle observedLifecycle = ElsaObservedLifecycle.Ready,
        ElsaInstanceHealth health = ElsaInstanceHealth.Healthy,
        ElsaInstanceProviderObservationKind? providerObservationKind = ElsaInstanceProviderObservationKind.Confirmed,
        ManagedLifecycleOperationSnapshot? operation = null,
        ManagedLifecycleRunSnapshot? run = null,
        DateTimeOffset? reconciledAt = null,
        string? providerDiagnosticCode = null) =>
        new(
            WorkspaceId,
            InstanceId,
            desiredLifecycle,
            observedLifecycle,
            health,
            providerObservationKind,
            operation,
            run,
            providerDiagnosticCode,
            reconciledAt);

    private static ManagedLifecycleOperationSnapshot Operation(
        ElsaInstanceOperationState state,
        int attemptNumber = 1,
        DateTimeOffset? acceptedAt = null,
        DateTimeOffset? startedAt = null,
        string? diagnosticCode = null,
        DateTimeOffset? heartbeatAt = null,
        DateTimeOffset? lastProgressAt = null) =>
        new(OperationId, state, attemptNumber, acceptedAt ?? Now, startedAt, diagnosticCode, heartbeatAt, lastProgressAt);

    private static ManagedLifecycleRunSnapshot Run(
        WorkspaceDeploymentRunStatus status,
        int attemptNumber = 1,
        DateTimeOffset? queuedAt = null,
        DateTimeOffset? startedAt = null,
        string? diagnosticCode = null,
        DateTimeOffset? heartbeatAt = null,
        DateTimeOffset? lastProgressAt = null) =>
        new(RunId, status, attemptNumber, queuedAt ?? Now, startedAt, diagnosticCode, heartbeatAt, lastProgressAt);

    private sealed class StaticTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
