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
    public async Task Stale_run_entry_writes_exactly_one_alert_event_and_a_refresh_writes_none()
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
        Assert.Single(capture.Entered);
        Assert.Equal(
            ManagedLifecycleTelemetry.RecoveryRequiredEnteredActivityName,
            capture.Entered[0].OperationName);
        Assert.Equal(workspace.Id.ToString("D"),
            capture.Entered[0].GetTagItem(ManagedLifecycleTelemetry.WorkspaceIdTag));
        Assert.Equal(accepted.Instance.Id.ToString("D"),
            capture.Entered[0].GetTagItem(ManagedLifecycleTelemetry.InstanceIdTag));
        Assert.Equal(accepted.Operation.Id.ToString("D"),
            capture.Entered[0].GetTagItem(ManagedLifecycleTelemetry.OperationIdTag));
        Assert.Equal(
            ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired,
            capture.Entered[0].GetTagItem(ManagedLifecycleTelemetry.DiagnosticCodeTag));
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
        // Temporary class (Architect + CEO): no alert at entry. The 10-minute
        // RequiresHumanAt CAS + outbox row is #662 work after #660 lands the columns.
        Assert.Empty(capture.Entered);
    }

    [Fact(Skip = "Blocked on #660 ReasonEnteredAt/RequiresHumanAt. After rebase this PR becomes the single CAS + post-commit outbox site: uncertain stays quiet at 9:59 and writes exactly one alert row at 10:00.")]
    public void Uncertain_provider_submission_park_alerts_once_after_ten_minutes()
    {
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
