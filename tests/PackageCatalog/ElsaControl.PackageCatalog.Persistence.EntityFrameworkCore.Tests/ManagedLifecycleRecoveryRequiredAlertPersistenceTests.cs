using System.Diagnostics;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Telemetry;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed partial class ElsaInstanceLifecycleStoreTests
{
    [Fact]
    public async Task Provider_submission_handoff_does_not_write_the_recovery_required_alert_event()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert handoff");
        using var capture = new RecoveryRequiredAlertCapture(workspace.Id);
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));

        await store.CommitProviderSubmissionAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-operation-accepted",
            Now));
        await store.CommitProviderSubmissionAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-operation-accepted",
            Now.AddMinutes(1)));

        Assert.Equal(
            ElsaInstanceOperationState.RecoveryRequired,
            (await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id)).State);
        Assert.Empty(capture.Entered);
    }

    [Fact]
    public async Task Stale_run_entry_does_not_alert_because_the_park_is_temporary()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert stale run");
        using var capture = new RecoveryRequiredAlertCapture(workspace.Id);
        var workspaceStore = new DeploymentWorkspaceStore(db);
        Assert.NotNull(await workspaceStore.ClaimNextQueuedRunAsync("stale-alert-worker", Now));

        Assert.Equal(1, await workspaceStore.MarkStaleRunningRunsRecoveryRequiredAsync(
            Now.AddMinutes(10), TimeSpan.FromMinutes(5)));
        Assert.Equal(0, await workspaceStore.MarkStaleRunningRunsRecoveryRequiredAsync(
            Now.AddMinutes(11), TimeSpan.FromMinutes(5)));

        var stored = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == accepted.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, stored.State);
        Assert.Equal(
            ManagedElsaReasonCodeCatalog.ProviderReconciliationRequired,
            stored.ReconciliationDiagnosticCode);
        Assert.Null(stored.RequiresHumanAt);
        Assert.Empty(capture.Entered);
        Assert.Empty(db.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking());
    }

    [Fact]
    public async Task Instance_and_health_reads_do_not_write_the_recovery_required_alert_event()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert read");
        using var capture = new RecoveryRequiredAlertCapture(workspace.Id);
        var workspaceStore = new DeploymentWorkspaceStore(db);
        Assert.NotNull(await workspaceStore.ClaimNextQueuedRunAsync("stale-read-worker", Now));
        Assert.Equal(1, await workspaceStore.MarkStaleRunningRunsRecoveryRequiredAsync(
            Now.AddMinutes(10), TimeSpan.FromMinutes(5)));
        capture.Clear();

        var store = CreateStore(db);
        Assert.NotNull(await store.GetInstanceAsync(workspace.Id, accepted.Instance.Id));
        _ = await store.GetResultAsync(workspace.Id, accepted.Operation.Id);
        _ = await store.ListPendingProviderOperationsAsync(16);
        _ = new ManagedLifecycleOperationalHealthEvaluator().Evaluate(
            new ManagedLifecycleOperationalHealthSnapshot(
                workspace.Id,
                accepted.Instance.Id,
                ElsaDesiredLifecycle.Running,
                ElsaObservedLifecycle.Unknown,
                ElsaInstanceHealth.Unknown,
                operation: new ManagedLifecycleOperationSnapshot(
                    accepted.Operation.Id,
                    ElsaInstanceOperationState.RecoveryRequired,
                    1,
                    Now)));

        Assert.Empty(capture.Entered);
    }

    [Fact]
    public async Task Deletion_recovery_writes_exactly_one_alert_event()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Alert deletion recovery");
        var service = new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now));
        var created = await service.CreateAsync(new ElsaInstanceCreateRequest(
            workspace.OrganizationId, workspace.Id, "Alert Delete Elsa", "alert-delete-elsa",
            WorkerIntent(), "alert-delete-create"));
        var deletion = await service.DeleteAsync(await CreateConfirmedDeleteRequestAsync(
            db, workspace.Id, created.Instance.Id, created.Instance.Version, "alert-delete"));
        await CompleteOperationAsync(db, created.Operation.Id);
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));
        var claim = await store.TryClaimNextDeletionAsync("alert-delete-worker", Now);
        Assert.NotNull(claim);
        using var capture = new RecoveryRequiredAlertCapture(workspace.Id);
        var failure = new ElsaInstanceDeletionFailure(
            workspace.Id,
            created.Instance.Id,
            deletion.Operation.Id,
            claim!.Outbox.Id,
            claim.Instance.Version,
            claim.Operation.AttemptNumber,
            claim.CorrelatedRunId,
            "alert-delete-worker",
            claim.LeaseToken,
            claim.LeaseVersion,
            new string('a', 64),
            "deletion.provider.unavailable",
            Now.AddMinutes(1));

        var recovered = await store.RequireDeletionRecoveryAsync(failure);

        Assert.Equal(ElsaInstanceDeletionOutcome.RecoveryRequired, recovered.Outcome);
        Assert.Single(capture.Entered);
        Assert.Equal(
            ManagedLifecycleTelemetry.RecoveryRequiredEnteredActivityName,
            capture.Entered[0].OperationName);
        Assert.Equal(
            ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired,
            capture.Entered[0].GetTagItem(ManagedLifecycleTelemetry.DiagnosticCodeTag));
        Assert.Equal(workspace.Id.ToString("D"),
            capture.Entered[0].GetTagItem(ManagedLifecycleTelemetry.WorkspaceIdTag));
        Assert.Equal(created.Instance.Id.ToString("D"),
            capture.Entered[0].GetTagItem(ManagedLifecycleTelemetry.InstanceIdTag));
        Assert.Equal(deletion.Operation.Id.ToString("D"),
            capture.Entered[0].GetTagItem(ManagedLifecycleTelemetry.OperationIdTag));
    }

    [Fact]
    public async Task Uncertain_provider_submission_park_does_not_emit_at_entry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert uncertain submission");
        using var capture = new RecoveryRequiredAlertCapture(workspace.Id);
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));

        await store.CommitProviderSubmissionAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-submission-uncertain",
            Now));

        Assert.Equal(
            ElsaInstanceOperationState.RecoveryRequired,
            (await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id)).State);
        Assert.Equal(
            "provider.submission.uncertain",
            (await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id)).FailureCode);
        var parked = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        Assert.Null(parked.RequiresHumanAt);
        Assert.Empty(capture.Entered);
        Assert.Empty(db.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking());
    }

    [Fact]
    public async Task Uncertain_provider_submission_park_alerts_once_after_ten_minutes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert uncertain clock");
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));
        await store.CommitProviderSubmissionAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-submission-uncertain",
            Now));
        using var capture = new RecoveryRequiredAlertCapture(workspace.Id);
        var observation = UnchangedParkObservation(ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain);

        await ReconcileUnchangedParkAsync(
            store, workspace.Id, accepted.Operation.Id, observation,
            Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter - TimeSpan.FromSeconds(1));
        Assert.Null((await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id)).RequiresHumanAt);
        Assert.Empty(capture.Entered);

        await ReconcileUnchangedParkAsync(
            store, workspace.Id, accepted.Operation.Id, observation,
            Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter);

        var parked = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        Assert.Equal(Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter, parked.RequiresHumanAt);
        Assert.Single(capture.Entered);
        Assert.Equal(1, await db.ElsaInstanceRecoveryRequiredAlertOutbox.CountAsync());
        Assert.Equal(
            accepted.Operation.AttemptNumber,
            (await db.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking().SingleAsync()).AttemptNumber);

        await ReconcileUnchangedParkAsync(
            store, workspace.Id, accepted.Operation.Id, observation,
            Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter + TimeSpan.FromMinutes(1));
        Assert.Single(capture.Entered);
        Assert.Equal(1, await db.ElsaInstanceRecoveryRequiredAlertOutbox.CountAsync());
    }

    [Fact]
    public async Task Unchanged_auto_resuming_fingerprint_alerts_once_after_ten_minutes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert unchanged failed");
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));
        await store.CommitProviderSubmissionAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-operation-accepted",
            Now));
        using var capture = new RecoveryRequiredAlertCapture(workspace.Id);
        var observation = UnchangedParkObservation(ManagedElsaReasonCodeCatalog.AzureDeploymentFailed);

        await ReconcileUnchangedParkAsync(store, workspace.Id, accepted.Operation.Id, observation, Now.AddMinutes(1));
        Assert.Null((await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id)).RequiresHumanAt);
        Assert.Empty(capture.Entered);
        Assert.Empty(db.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking());

        await ReconcileUnchangedParkAsync(
            store, workspace.Id, accepted.Operation.Id, observation,
            Now.AddMinutes(1) + ManagedElsaReasonCodeCatalog.HumanRequiredAfter - TimeSpan.FromSeconds(1));
        Assert.Null((await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id)).RequiresHumanAt);
        Assert.Empty(capture.Entered);

        await ReconcileUnchangedParkAsync(
            store, workspace.Id, accepted.Operation.Id, observation,
            Now.AddMinutes(1) + ManagedElsaReasonCodeCatalog.HumanRequiredAfter);

        var parked = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        Assert.Equal(Now.AddMinutes(1) + ManagedElsaReasonCodeCatalog.HumanRequiredAfter, parked.RequiresHumanAt);
        Assert.Single(capture.Entered);
        Assert.Equal(1, await db.ElsaInstanceRecoveryRequiredAlertOutbox.CountAsync());
    }

    [Fact]
    public async Task Runless_uncertain_park_scan_alerts_once_after_ten_minutes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert runless uncertain");
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));
        await store.CommitProviderSubmissionAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-submission-uncertain",
            Now));
        db.ChangeTracker.Clear();
        var parked = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == accepted.Operation.Id);
        parked.DeploymentRunId = null;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.DoesNotContain(
            await store.ListPendingProviderOperationsAsync(16),
            pending => pending.OperationId == accepted.Operation.Id);
        using var capture = new RecoveryRequiredAlertCapture(workspace.Id);

        Assert.Equal(0, await store.AdvanceDueHumanRequiredClocksAsync(
            Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter - TimeSpan.FromSeconds(1)));
        Assert.Null((await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id)).RequiresHumanAt);
        Assert.Empty(capture.Entered);
        Assert.Empty(db.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking());

        Assert.Equal(1, await store.AdvanceDueHumanRequiredClocksAsync(
            Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter));
        var flagged = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        Assert.Equal(Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter, flagged.RequiresHumanAt);
        Assert.Null(flagged.DeploymentRunId);
        Assert.Single(capture.Entered);
        Assert.Equal(1, await db.ElsaInstanceRecoveryRequiredAlertOutbox.CountAsync());

        Assert.Equal(0, await store.AdvanceDueHumanRequiredClocksAsync(
            Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter + TimeSpan.FromMinutes(1)));
        Assert.Single(capture.Entered);
        Assert.Equal(1, await db.ElsaInstanceRecoveryRequiredAlertOutbox.CountAsync());
    }

    [Theory]
    [MemberData(nameof(AutoResumingParkCodes))]
    public async Task Azure_park_code_reaches_the_lifecycle_reason_in_one_reconcile_without_alerting(
        string parkCode)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, $"Alert {parkCode}");
        var workspaceStore = new DeploymentWorkspaceStore(db);
        Assert.NotNull(await workspaceStore.ClaimNextQueuedRunAsync("alert-auto-resume-worker", Now));
        Assert.Equal(1, await workspaceStore.MarkStaleRunningRunsRecoveryRequiredAsync(
            Now.AddMinutes(10), TimeSpan.FromMinutes(5)));
        db.ChangeTracker.Clear();
        using var capture = new RecoveryRequiredAlertCapture(workspace.Id);

        var reconciled = await new ElsaInstanceProviderReconciliationService(
                CreateStore(db),
                new QueueProviderPort(new ElsaInstanceProviderObservation(
                    ElsaInstanceProviderObservationKind.Confirmed,
                    ElsaObservedLifecycle.Provisioning,
                    ElsaInstanceProviderHealthGate.Unknown,
                    "alert-auto-resuming-park")
                {
                    ReasonCode = parkCode
                }),
                new FixedTimeProvider(Now.AddMinutes(1)))
            .ReconcileAsync(workspace.Id, accepted.Operation.Id);

        Assert.Equal(parkCode, reconciled.DiagnosticCode);
        var parked = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        Assert.Equal(parkCode, parked.ReconciliationDiagnosticCode);
        Assert.Null(parked.RequiresHumanAt);
        Assert.Empty(capture.Entered);
        Assert.Empty(db.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking());
    }

    [Fact]
    public async Task Auto_resume_exhausted_park_writes_exactly_one_alert()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert exhausted");
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));
        await store.CommitProviderSubmissionAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-operation-accepted",
            Now));
        using var capture = new RecoveryRequiredAlertCapture(workspace.Id);

        await ReconcileParkAsync(
            store,
            workspace.Id,
            accepted,
            ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted,
            Now.AddMinutes(1));

        var parked = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        Assert.Equal(Now.AddMinutes(1), parked.RequiresHumanAt);
        Assert.Single(capture.Entered);
        Assert.Equal(1, await db.ElsaInstanceRecoveryRequiredAlertOutbox.CountAsync());
    }

    [Fact]
    public async Task Unknown_park_code_writes_exactly_one_alert()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert unknown park");
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));
        await store.CommitProviderSubmissionAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-operation-accepted",
            Now));
        using var capture = new RecoveryRequiredAlertCapture(workspace.Id);

        await ReconcileParkAsync(store, workspace.Id, accepted, "azure.recovery.not-in-catalog", Now.AddMinutes(1));

        var parked = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        Assert.Equal(Now.AddMinutes(1), parked.RequiresHumanAt);
        Assert.Single(capture.Entered);
        Assert.Equal(1, await db.ElsaInstanceRecoveryRequiredAlertOutbox.CountAsync());
    }

    [Fact]
    public async Task Recover_then_a_second_human_required_park_writes_exactly_one_more_outbox_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert recover then repark");
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));
        await store.CommitProviderSubmissionAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-operation-accepted",
            Now));
        using var capture = new RecoveryRequiredAlertCapture(workspace.Id);

        await ReconcileParkAsync(
            store, workspace.Id, accepted, ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted, Now.AddMinutes(1));
        Assert.Single(capture.Entered);
        Assert.Equal(1, await db.ElsaInstanceRecoveryRequiredAlertOutbox.CountAsync());

        var retrySafe = new ElsaInstanceProviderObservation(
            ElsaInstanceProviderObservationKind.Ambiguous,
            ElsaObservedLifecycle.Unknown,
            ElsaInstanceProviderHealthGate.Unknown,
            "alert-recover-retry",
            new ElsaInstanceProviderRetryEvidence(
                "https://evidence.example/retry/alert-recover",
                "sha256:" + new string('a', 64)));
        await new ElsaInstanceProviderReconciliationService(
                store, new StableObservationPort(retrySafe), new FixedTimeProvider(Now.AddMinutes(2)))
            .ReconcileAsync(workspace.Id, accepted.Operation.Id);
        var current = await store.GetInstanceAsync(workspace.Id, accepted.Instance.Id);
        Assert.NotNull(current);
        await new ElsaInstanceLifecycleService(store, new FixedTimeProvider(Now.AddMinutes(3)))
            .RecoverAsync(new(workspace.Id, accepted.Instance.Id, current!.Version, "alert-recover"));
        Assert.Single(capture.Entered);
        Assert.Null((await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id)).RequiresHumanAt);

        db.ChangeTracker.Clear();
        var workspaceStore = new DeploymentWorkspaceStore(db);
        Assert.NotNull(await workspaceStore.ClaimNextQueuedRunAsync("alert-recover-worker", Now.AddMinutes(3)));
        Assert.Equal(1, await workspaceStore.MarkStaleRunningRunsRecoveryRequiredAsync(
            Now.AddMinutes(13), TimeSpan.FromMinutes(5)));
        Assert.Single(capture.Entered);
        db.ChangeTracker.Clear();

        await ReconcileParkAsync(
            store,
            workspace.Id,
            accepted,
            ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted,
            Now.AddMinutes(14));

        Assert.Equal(2, capture.Entered.Count);
        Assert.Equal(2, await db.ElsaInstanceRecoveryRequiredAlertOutbox.CountAsync());
        Assert.Equal(
            new[] { accepted.Operation.AttemptNumber, accepted.Operation.AttemptNumber + 1 },
            await db.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking()
                .OrderBy(row => row.AttemptNumber)
                .Select(row => row.AttemptNumber)
                .ToArrayAsync());
    }

    private static async Task ReconcileParkAsync(
        EfCoreElsaInstanceLifecycleStore store,
        Guid workspaceId,
        ElsaInstanceLifecycleAcceptance accepted,
        string diagnosticCode,
        DateTimeOffset at) =>
        await ReconcileUnchangedParkAsync(
            store,
            workspaceId,
            accepted.Operation.Id,
            UnchangedParkObservation(diagnosticCode),
            at);

    private static Task<ElsaInstanceProviderReconciliationResult> ReconcileUnchangedParkAsync(
        EfCoreElsaInstanceLifecycleStore store,
        Guid workspaceId,
        Guid operationId,
        ElsaInstanceProviderObservation observation,
        DateTimeOffset at) =>
        new ElsaInstanceProviderReconciliationService(
                store,
                new StableObservationPort(observation),
                new FixedTimeProvider(at))
            .ReconcileAsync(workspaceId, operationId);

    private static ElsaInstanceProviderObservation UnchangedParkObservation(string reasonCode) =>
        new(
            ElsaInstanceProviderObservationKind.Confirmed,
            ElsaObservedLifecycle.Provisioning,
            ElsaInstanceProviderHealthGate.Unknown,
            "unchanged-park-evidence")
        {
            ReasonCode = reasonCode
        };

    private sealed class StableObservationPort(ElsaInstanceProviderObservation observation)
        : IElsaInstanceProviderReconciliationPort
    {
        public Task<ElsaInstanceProviderObservation> ObserveAsync(
            ElsaInstanceProviderReconciliationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(observation.Correlate(request));
    }

    private sealed class RecoveryRequiredAlertCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly object _gate = new();
        private readonly List<Activity> _entered = [];

        public RecoveryRequiredAlertCapture(Guid workspaceId)
        {
            var workspace = workspaceId.ToString("D");
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ManagedLifecycleTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName != ManagedLifecycleTelemetry.RecoveryRequiredEnteredActivityName)
                        return;
                    if (!string.Equals(
                            activity.GetTagItem(ManagedLifecycleTelemetry.WorkspaceIdTag) as string,
                            workspace,
                            StringComparison.Ordinal))
                        return;
                    lock (_gate)
                        _entered.Add(activity);
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Entered
        {
            get
            {
                lock (_gate)
                    return _entered.ToArray();
            }
        }

        public void Clear()
        {
            lock (_gate)
                _entered.Clear();
        }

        public void Dispose() => _listener.Dispose();
    }
}
