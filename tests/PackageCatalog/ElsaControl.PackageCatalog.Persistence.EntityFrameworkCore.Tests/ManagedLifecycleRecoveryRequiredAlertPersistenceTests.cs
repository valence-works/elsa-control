using System.Data.Common;
using System.Diagnostics;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Telemetry;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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

        var beforeBound = await ReconcileUnchangedParkAsync(
            store, workspace.Id, accepted.Operation.Id, observation,
            Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter - TimeSpan.FromSeconds(1));
        var before = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        var instanceBefore = await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == accepted.Instance.Id);
        Assert.False(beforeBound.Replayed);
        Assert.NotNull(before.ReconciliationEvidenceFingerprint);
        Assert.Null(before.RequiresHumanAt);
        Assert.Empty(capture.Entered);

        var atBound = await ReconcileUnchangedParkAsync(
            store, workspace.Id, accepted.Operation.Id, observation,
            Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter);

        var parked = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        var instanceAtBound = await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == accepted.Instance.Id);
        Assert.True(atBound.Replayed);
        Assert.Equal(before.ReconciliationEvidenceFingerprint, parked.ReconciliationEvidenceFingerprint);
        Assert.Equal(before.ReconciliationVersion, parked.ReconciliationVersion);
        Assert.Equal(instanceBefore.Version, instanceAtBound.Version);
        Assert.Equal(Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter, parked.RequiresHumanAt);
        Assert.Single(capture.Entered);
        Assert.Equal(1, await db.ElsaInstanceRecoveryRequiredAlertOutbox.CountAsync());
        Assert.Equal(
            accepted.Operation.AttemptNumber,
            (await db.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking().SingleAsync()).AttemptNumber);

        var afterBound = await ReconcileUnchangedParkAsync(
            store, workspace.Id, accepted.Operation.Id, observation,
            Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter + TimeSpan.FromMinutes(1));
        var after = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        Assert.True(afterBound.Replayed);
        Assert.Equal(before.ReconciliationEvidenceFingerprint, after.ReconciliationEvidenceFingerprint);
        Assert.Equal(before.ReconciliationVersion, after.ReconciliationVersion);
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

    [Fact]
    public async Task Two_dbcontexts_racing_the_ten_minute_scan_write_exactly_one_outbox_row()
    {
        var path = Path.Combine(Path.GetTempPath(), $"elsa-rr-clock-scan-{Guid.NewGuid():N}.db");
        var options = ClockRaceOptions(path);
        try
        {
            Guid workspaceId;
            Guid operationId;
            await using (var seed = new CatalogDbContext(options))
            {
                await seed.Database.MigrateAsync();
                await seed.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                var (workspace, accepted) = await QueueManagedLifecycleRunAsync(seed, "Alert clock race scan");
                workspaceId = workspace.Id;
                operationId = accepted.Operation.Id;
                var store = new EfCoreElsaInstanceLifecycleStore(
                    seed, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));
                await store.CommitProviderSubmissionAsync(new(
                    workspace.Id,
                    accepted.Instance.Id,
                    accepted.Operation.Id,
                    accepted.Operation.AttemptNumber,
                    "provider-submission-uncertain",
                    Now));
            }

            using var capture = new RecoveryRequiredAlertCapture(workspaceId);
            var atBound = Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter;
            await using var first = new CatalogDbContext(options);
            await using var second = new CatalogDbContext(options);
            var advanced = await Task.WhenAll(
                new EfCoreElsaInstanceLifecycleStore(first, EmptyResolutionInputSource.Instance)
                    .AdvanceDueHumanRequiredClocksAsync(atBound),
                new EfCoreElsaInstanceLifecycleStore(second, EmptyResolutionInputSource.Instance)
                    .AdvanceDueHumanRequiredClocksAsync(atBound));

            Assert.Equal(1, advanced.Sum());
            Assert.Single(capture.Entered);
            await using var verify = new CatalogDbContext(options);
            Assert.Equal(1, await verify.ElsaInstanceRecoveryRequiredAlertOutbox.CountAsync());
            Assert.Equal(
                atBound,
                (await verify.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == operationId)).RequiresHumanAt);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Two_dbcontexts_racing_an_unchanged_fingerprint_write_exactly_one_outbox_row()
    {
        var path = Path.Combine(Path.GetTempPath(), $"elsa-rr-clock-replay-{Guid.NewGuid():N}.db");
        var options = ClockRaceOptions(path);
        try
        {
            Guid workspaceId;
            Guid operationId;
            var observation = UnchangedParkObservation(ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain);
            await using (var seed = new CatalogDbContext(options))
            {
                await seed.Database.MigrateAsync();
                await seed.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                var (workspace, accepted) = await QueueManagedLifecycleRunAsync(seed, "Alert clock race replay");
                workspaceId = workspace.Id;
                operationId = accepted.Operation.Id;
                var store = new EfCoreElsaInstanceLifecycleStore(
                    seed, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));
                await store.CommitProviderSubmissionAsync(new(
                    workspace.Id,
                    accepted.Instance.Id,
                    accepted.Operation.Id,
                    accepted.Operation.AttemptNumber,
                    "provider-submission-uncertain",
                    Now));
                await ReconcileUnchangedParkAsync(
                    store, workspace.Id, accepted.Operation.Id, observation,
                    Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter - TimeSpan.FromSeconds(1));
            }

            using var capture = new RecoveryRequiredAlertCapture(workspaceId);
            var atBound = Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter;
            await using var first = new CatalogDbContext(options);
            await using var second = new CatalogDbContext(options);
            var outcomes = await Task.WhenAll(
                RaceUnchangedParkAsync(first, workspaceId, operationId, observation, atBound),
                RaceUnchangedParkAsync(second, workspaceId, operationId, observation, atBound));

            Assert.Contains(true, outcomes);
            Assert.Single(capture.Entered);
            await using var verify = new CatalogDbContext(options);
            Assert.Equal(1, await verify.ElsaInstanceRecoveryRequiredAlertOutbox.CountAsync());
            var parked = await verify.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == operationId);
            Assert.Equal(atBound, parked.RequiresHumanAt);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
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

    [Fact]
    public async Task Runless_lever_uncertain_park_does_not_alert_at_nine_minutes_and_alerts_once_at_ten()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert runless lever");
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
        Assert.Equal(ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain, parked.FailureCode);
        parked.DeploymentRunId = null;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        using var capture = new RecoveryRequiredAlertCapture(workspace.Id);

        Assert.Equal(0, await store.AdvanceDueHumanRequiredClocksAsync(
            Now + TimeSpan.FromMinutes(9)));
        Assert.Null((await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id)).RequiresHumanAt);
        Assert.Empty(capture.Entered);
        Assert.Empty(db.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking());

        Assert.Equal(1, await store.AdvanceDueHumanRequiredClocksAsync(
            Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter));
        Assert.NotNull((await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id)).RequiresHumanAt);
        Assert.Single(capture.Entered);
        Assert.Equal(1, await db.ElsaInstanceRecoveryRequiredAlertOutbox.CountAsync());
    }

    [Fact]
    public async Task Runless_delete_uncertain_park_does_not_alert_at_nine_minutes_and_alerts_once_at_ten()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Alert runless delete");
        var service = new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now));
        var created = await service.CreateAsync(new ElsaInstanceCreateRequest(
            workspace.OrganizationId, workspace.Id, "Alert Delete Clock", "alert-delete-clock",
            WorkerIntent(), "alert-delete-clock-create"));
        var deletion = await service.DeleteAsync(await CreateConfirmedDeleteRequestAsync(
            db, workspace.Id, created.Instance.Id, created.Instance.Version, "alert-delete-clock"));
        await CompleteOperationAsync(db, created.Operation.Id);
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));
        var claim = await store.TryClaimNextDeletionAsync("alert-delete-clock-worker", Now);
        Assert.NotNull(claim);
        using var capture = new RecoveryRequiredAlertCapture(workspace.Id);
        var recovered = await store.RequireDeletionRecoveryAsync(new ElsaInstanceDeletionFailure(
            workspace.Id,
            created.Instance.Id,
            deletion.Operation.Id,
            claim!.Outbox.Id,
            claim.Instance.Version,
            claim.Operation.AttemptNumber,
            claim.CorrelatedRunId,
            "alert-delete-clock-worker",
            claim.LeaseToken,
            claim.LeaseVersion,
            new string('a', 64),
            ManagedElsaReasonCodeCatalog.ProviderReconciliationUnavailable,
            Now.AddMinutes(1)));

        Assert.Equal(ElsaInstanceDeletionOutcome.RecoveryRequired, recovered.Outcome);
        db.ChangeTracker.Clear();
        var parked = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == deletion.Operation.Id);
        Assert.Equal(ElsaInstanceOperationAction.Delete, parked.Action);
        Assert.Null(parked.RequiresHumanAt);
        parked.DeploymentRunId = null;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Empty(capture.Entered);
        Assert.Empty(db.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking());

        Assert.Equal(0, await store.AdvanceDueHumanRequiredClocksAsync(
            Now.AddMinutes(1) + TimeSpan.FromMinutes(9)));
        Assert.Null((await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == deletion.Operation.Id)).RequiresHumanAt);
        Assert.Empty(capture.Entered);

        Assert.Equal(1, await store.AdvanceDueHumanRequiredClocksAsync(
            Now.AddMinutes(1) + ManagedElsaReasonCodeCatalog.HumanRequiredAfter));
        Assert.NotNull((await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == deletion.Operation.Id)).RequiresHumanAt);
        Assert.Null((await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == deletion.Operation.Id)).DeploymentRunId);
        Assert.Single(capture.Entered);
        Assert.Equal(1, await db.ElsaInstanceRecoveryRequiredAlertOutbox.CountAsync());
    }

    [Fact]
    public async Task Outbox_dispatcher_delivers_a_row_left_undelivered_after_commit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert dispatcher crash");
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
        var parked = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        db.ElsaInstanceRecoveryRequiredAlertOutbox.Add(new ElsaInstanceRecoveryRequiredAlertOutboxEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = parked.OrganizationId,
            WorkspaceId = parked.WorkspaceId,
            InstanceId = parked.InstanceId!.Value,
            OperationId = parked.Id,
            AttemptNumber = parked.AttemptNumber,
            DedupeIdentity = ManagedLifecycleRecoveryRequiredAlert.ComputeDedupeIdentity(
                parked.WorkspaceId, parked.InstanceId.Value, parked.Id, parked.AttemptNumber),
            CreatedAt = Now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        using var capture = new RecoveryRequiredAlertCapture(workspace.Id);
        var dispatcher = new EfCoreRecoveryRequiredAlertOutboxDispatcher(
            db, new ActivityRecoveryRequiredAlertSender(), new FixedTimeProvider(Now));

        Assert.Equal(1, await dispatcher.DispatchPendingAsync());
        var delivered = await db.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(Now, delivered.SentAt);
        Assert.Equal(1, delivered.DeliveryAttempts);
        Assert.Null(delivered.NextAttemptAt);
        Assert.Single(capture.Entered);
        Assert.Equal(0, await dispatcher.DispatchPendingAsync());
        Assert.Single(capture.Entered);
    }

    [Fact]
    public async Task Outbox_dispatcher_retries_with_backoff_then_marks_delivered()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert dispatcher retry");
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
        var parked = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        db.ElsaInstanceRecoveryRequiredAlertOutbox.Add(new ElsaInstanceRecoveryRequiredAlertOutboxEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = parked.OrganizationId,
            WorkspaceId = parked.WorkspaceId,
            InstanceId = parked.InstanceId!.Value,
            OperationId = parked.Id,
            AttemptNumber = parked.AttemptNumber,
            DedupeIdentity = ManagedLifecycleRecoveryRequiredAlert.ComputeDedupeIdentity(
                parked.WorkspaceId, parked.InstanceId.Value, parked.Id, parked.AttemptNumber),
            CreatedAt = Now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var sender = new FailFirstAlertSender();
        var clock = new MutableTimeProvider(Now);
        var dispatcher = new EfCoreRecoveryRequiredAlertOutboxDispatcher(db, sender, clock);

        Assert.Equal(0, await dispatcher.DispatchPendingAsync());
        var delayed = await db.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking().SingleAsync();
        Assert.Null(delayed.SentAt);
        Assert.Equal(1, delayed.DeliveryAttempts);
        Assert.Equal(Now + RecoveryRequiredAlertBackoff.Delay(1), delayed.NextAttemptAt);

        Assert.Equal(0, await dispatcher.DispatchPendingAsync());
        clock.Advance(RecoveryRequiredAlertBackoff.Delay(1));
        Assert.Equal(1, await dispatcher.DispatchPendingAsync());
        var delivered = await db.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(clock.GetUtcNow(), delivered.SentAt);
        Assert.Equal(2, delivered.DeliveryAttempts);
        Assert.Null(delivered.NextAttemptAt);
        Assert.Equal(1, sender.Successes);
    }

    [Fact]
    public async Task Two_dbcontexts_racing_the_outbox_dispatcher_mark_exactly_one_delivery()
    {
        var path = Path.Combine(Path.GetTempPath(), $"elsa-rr-dispatch-{Guid.NewGuid():N}.db");
        var options = ClockRaceOptions(path);
        try
        {
            Guid workspaceId;
            Guid outboxId;
            await using (var seed = new CatalogDbContext(options))
            {
                await seed.Database.MigrateAsync();
                await seed.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                var (workspace, accepted) = await QueueManagedLifecycleRunAsync(seed, "Alert dispatcher race");
                workspaceId = workspace.Id;
                var store = new EfCoreElsaInstanceLifecycleStore(
                    seed, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));
                await store.CommitProviderSubmissionAsync(new(
                    workspace.Id,
                    accepted.Instance.Id,
                    accepted.Operation.Id,
                    accepted.Operation.AttemptNumber,
                    "provider-submission-uncertain",
                    Now));
                seed.ChangeTracker.Clear();
                var parked = await seed.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
                var row = new ElsaInstanceRecoveryRequiredAlertOutboxEntity
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = parked.OrganizationId,
                    WorkspaceId = parked.WorkspaceId,
                    InstanceId = parked.InstanceId!.Value,
                    OperationId = parked.Id,
                    AttemptNumber = parked.AttemptNumber,
                    DedupeIdentity = ManagedLifecycleRecoveryRequiredAlert.ComputeDedupeIdentity(
                        parked.WorkspaceId, parked.InstanceId.Value, parked.Id, parked.AttemptNumber),
                    CreatedAt = Now
                };
                seed.ElsaInstanceRecoveryRequiredAlertOutbox.Add(row);
                await seed.SaveChangesAsync();
                outboxId = row.Id;
            }

            using var capture = new RecoveryRequiredAlertCapture(workspaceId);
            await using var first = new CatalogDbContext(options);
            await using var second = new CatalogDbContext(options);
            var delivered = await Task.WhenAll(
                new EfCoreRecoveryRequiredAlertOutboxDispatcher(
                    first, new ActivityRecoveryRequiredAlertSender(), new FixedTimeProvider(Now))
                    .DispatchPendingAsync(),
                new EfCoreRecoveryRequiredAlertOutboxDispatcher(
                    second, new ActivityRecoveryRequiredAlertSender(), new FixedTimeProvider(Now))
                    .DispatchPendingAsync());

            Assert.Equal(1, delivered.Sum());
            await using var verify = new CatalogDbContext(options);
            var deliveredRow = await verify.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking().SingleAsync(x => x.Id == outboxId);
            Assert.NotNull(deliveredRow.SentAt);
            Assert.True(capture.Entered.Count is 1 or 2);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Screen_alert_and_evaluator_agree_via_requires_human_at()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Alert parity");
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
        var instance = await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == accepted.Instance.Id);
        var parked = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        AssertParity(instance, parked, capture.Entered.Count, humanRequired: false, expectedSeverity: null);

        Assert.Equal(1, await store.AdvanceDueHumanRequiredClocksAsync(
            Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter));
        instance = await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == accepted.Instance.Id);
        parked = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        AssertParity(
            instance,
            parked,
            capture.Entered.Count,
            humanRequired: true,
            expectedSeverity: ManagedLifecycleOperationalHealthAlertSeverity.Warning);
    }

    [Fact]
    public async Task Failed_scan_commit_writes_no_outbox_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        Guid workspaceId;
        await using (var seed = CreateMigratedContext(connection))
        {
            await seed.Database.MigrateAsync();
            var (workspace, accepted) = await QueueManagedLifecycleRunAsync(seed, "Alert scan rollback");
            workspaceId = workspace.Id;
            var store = new EfCoreElsaInstanceLifecycleStore(
                seed, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));
            await store.CommitProviderSubmissionAsync(new(
                workspace.Id,
                accepted.Instance.Id,
                accepted.Operation.Id,
                accepted.Operation.AttemptNumber,
                "provider-submission-uncertain",
                Now));
        }

        using var capture = new RecoveryRequiredAlertCapture(workspaceId);
        await using var failing = CreateAlertContext(connection, new FailCommitInterceptor());
        var failingStore = new EfCoreElsaInstanceLifecycleStore(
            failing, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failingStore.AdvanceDueHumanRequiredClocksAsync(
                Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter));

        await using var verify = CreateMigratedContext(connection);
        Assert.Empty(verify.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking());
        Assert.Null((await verify.ElsaInstanceOperations.AsNoTracking().SingleAsync()).RequiresHumanAt);
        Assert.Empty(capture.Entered);
    }

    [Fact]
    public async Task Retried_scan_commit_writes_exactly_one_outbox_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        Guid workspaceId;
        await using (var seed = CreateMigratedContext(connection))
        {
            await seed.Database.MigrateAsync();
            var (workspace, accepted) = await QueueManagedLifecycleRunAsync(seed, "Alert scan retry");
            workspaceId = workspace.Id;
            var store = new EfCoreElsaInstanceLifecycleStore(
                seed, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));
            await store.CommitProviderSubmissionAsync(new(
                workspace.Id,
                accepted.Instance.Id,
                accepted.Operation.Id,
                accepted.Operation.AttemptNumber,
                "provider-submission-uncertain",
                Now));
        }

        using var capture = new RecoveryRequiredAlertCapture(workspaceId);
        var interceptor = new FailFirstCommitInterceptor();
        await using var retrying = CreateAlertContext(
            connection,
            exception => exception is TransientCommitException,
            interceptor);
        var retryingStore = new EfCoreElsaInstanceLifecycleStore(
            retrying, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));

        Assert.Equal(1, await retryingStore.AdvanceDueHumanRequiredClocksAsync(
            Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter));
        Assert.Equal(2, interceptor.Attempts);
        Assert.Single(capture.Entered);
        await using var verify = CreateMigratedContext(connection);
        Assert.Equal(1, await verify.ElsaInstanceRecoveryRequiredAlertOutbox.CountAsync());
        Assert.NotNull((await verify.ElsaInstanceOperations.AsNoTracking().SingleAsync()).RequiresHumanAt);
    }

    private static void AssertParity(
        ElsaInstanceEntity instance,
        ElsaInstanceOperationEntity operation,
        int alertCount,
        bool humanRequired,
        ManagedLifecycleOperationalHealthAlertSeverity? expectedSeverity)
    {
        var summary = new ElsaInstanceOperationSummary(
            operation.Id,
            operation.InstanceId ?? Guid.Empty,
            operation.Action,
            operation.State,
            operation.ExpectedVersion,
            operation.AttemptNumber,
            operation.AcceptedAt,
            operation.StartedAt,
            operation.CompletedAt,
            null,
            null,
            operation.DeploymentRunId,
            operation.FailureCode,
            null,
            null,
            operation.ReconciliationDiagnosticCode,
            null,
            null,
            operation.UpdatedAt,
            operation.ReasonEnteredAt,
            operation.RequiresHumanAt);
        var mapped = ElsaInstance.Hydrate(
            instance.Id,
            instance.OrganizationId,
            instance.WorkspaceId,
            instance.Name,
            instance.Slug,
            WorkerIntent(),
            instance.ObservedLifecycle,
            instance.Health,
            instance.Version,
            deletedAt: instance.DeletedAt);
        var projected = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(mapped, summary);
        var parkReason = ManagedElsaReasonCodeCatalog.SelectCurrentReason(
            operation.FailureCode,
            operation.ReconciliationDiagnosticCode);
        var evaluated = new ManagedLifecycleOperationalHealthEvaluator().Evaluate(
            new ManagedLifecycleOperationalHealthSnapshot(
                operation.WorkspaceId,
                operation.InstanceId ?? Guid.Empty,
                instance.DesiredLifecycle,
                instance.ObservedLifecycle,
                instance.Health,
                operation: new ManagedLifecycleOperationSnapshot(
                    operation.Id,
                    operation.State,
                    operation.AttemptNumber,
                    operation.AcceptedAt,
                    operation.StartedAt is { } started && started >= operation.AcceptedAt ? started : null,
                    parkReason,
                    operation.HeartbeatAt is { } heartbeat && heartbeat >= operation.AcceptedAt ? heartbeat : null,
                    requiresHumanAt: operation.RequiresHumanAt)));

        if (humanRequired)
        {
            Assert.NotNull(operation.RequiresHumanAt);
            Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, projected);
            Assert.Equal(ManagedElsaInstanceCustomerProjection.NeedsAttentionLabel,
                ManagedElsaInstanceCustomerProjection.CustomerLabel(projected));
            Assert.Equal(1, alertCount);
            Assert.Equal(ManagedLifecycleOperationalHealthStatus.RecoveryRequired, evaluated.Status);
            var alert = Assert.Single(
                evaluated.Alerts,
                candidate => candidate.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired);
            Assert.Equal(expectedSeverity, alert.Severity);
            return;
        }

        Assert.Null(operation.RequiresHumanAt);
        Assert.NotEqual(ElsaObservedLifecycle.RecoveryRequired, projected);
        Assert.Equal(0, alertCount);
        Assert.NotEqual(ManagedLifecycleOperationalHealthStatus.RecoveryRequired, evaluated.Status);
        Assert.DoesNotContain(
            evaluated.Alerts,
            candidate => candidate.Code == ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired);
    }

    private static CatalogDbContext CreateAlertContext(
        SqliteConnection connection,
        params IInterceptor[] interceptors) =>
        CreateAlertContext(connection, isTransient: null, interceptors);

    private static CatalogDbContext CreateAlertContext(
        SqliteConnection connection,
        Func<Exception, bool>? isTransient,
        params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(
                connection,
                sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly),
                isTransient);
        if (interceptors.Length > 0)
            options.AddInterceptors(interceptors);
        return new CatalogDbContext(options.Options);
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

    private static async Task<bool> RaceUnchangedParkAsync(
        CatalogDbContext db,
        Guid workspaceId,
        Guid operationId,
        ElsaInstanceProviderObservation observation,
        DateTimeOffset at)
    {
        try
        {
            await ReconcileUnchangedParkAsync(
                new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance),
                workspaceId,
                operationId,
                observation,
                at);
            return true;
        }
        catch (ElsaInstanceLifecycleConflictException)
        {
            return false;
        }
    }

    private static DbContextOptions<CatalogDbContext> ClockRaceOptions(string path) =>
        new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(
                $"Data Source={path};Default Timeout=30",
                sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;

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

    private sealed class FailFirstAlertSender : IRecoveryRequiredAlertSender
    {
        private bool _failed;
        public int Successes { get; private set; }

        public void Send(RecoveryRequiredAlertDispatch item)
        {
            if (!_failed)
            {
                _failed = true;
                throw new InvalidOperationException("The RecoveryRequired alert sender failed.");
            }

            Successes++;
            ManagedLifecycleRecoveryRequiredAlert.RecordEntered(
                item.WorkspaceId,
                item.InstanceId,
                item.OperationId,
                item.AttemptNumber,
                item.RunId);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class TransientCommitException : Exception;

    private sealed class FailCommitInterceptor : DbTransactionInterceptor
    {
        public override InterceptionResult TransactionCommitting(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result) =>
            throw new InvalidOperationException("The catalog transaction commit failed.");

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The catalog transaction commit failed.");
    }

    private sealed class FailFirstCommitInterceptor : DbTransactionInterceptor
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        public override InterceptionResult TransactionCommitting(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result) =>
            FailFirst(result);

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            new(FailFirst(result));

        private InterceptionResult FailFirst(InterceptionResult result)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
                throw new TransientCommitException();
            return result;
        }
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
