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
        using var capture = new RecoveryRequiredAlertCapture();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert handoff");
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
        using var capture = new RecoveryRequiredAlertCapture();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert stale run");
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
        using var capture = new RecoveryRequiredAlertCapture();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert read");
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

    private sealed class RecoveryRequiredAlertCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly object _gate = new();
        private readonly List<Activity> _entered = [];

        public RecoveryRequiredAlertCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ManagedLifecycleTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName != ManagedLifecycleTelemetry.RecoveryRequiredEnteredActivityName)
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
