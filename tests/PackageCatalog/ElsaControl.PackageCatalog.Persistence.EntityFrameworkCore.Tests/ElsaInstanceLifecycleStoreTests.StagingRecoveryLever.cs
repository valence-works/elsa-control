using System.Data.Common;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed partial class ElsaInstanceLifecycleStoreTests
{
    [Fact]
    public async Task Staging_recovery_lever_accepts_reconcile_and_parks_recovery_required_through_real_transitions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Staging recovery lever workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "staging-recovery-lever",
                CreateIntent(), "create-staging-recovery-lever"));
        await CompleteOperationAsync(db, created.Operation.Id);
        await SetObservedAsync(db, created.Instance.Id, ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy);

        Assert.False(ElsaInstanceOperation.CanTransition(
            ElsaInstanceOperationState.Accepted,
            ElsaInstanceOperationState.RecoveryRequired));
        Assert.True(ElsaInstanceOperation.CanTransition(
            ElsaInstanceOperationState.Queued,
            ElsaInstanceOperationState.RecoveryRequired));

        var commit = await CreateStore(db).AcceptReconcileAndRequireRecoveryAsync(
            created.Instance.Id,
            "api-key");

        Assert.Equal(ElsaInstanceOperationAction.Reconcile, commit.Operation.Action);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, commit.Operation.State);
        Assert.Equal(ElsaObservedLifecycle.Unknown, commit.Instance.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Unknown, commit.Instance.Health);

        var operation = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == commit.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, operation.State);
        Assert.Equal(StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode, operation.FailureCode);
        Assert.Equal(StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode, operation.ReconciliationDiagnosticCode);
        Assert.Equal(
            ManagedElsaReasonClass.NeedsPerson,
            ManagedElsaReasonCodeCatalog.Classify(
                ManagedElsaReasonCodeCatalog.SelectCurrentReason(
                    operation.FailureCode,
                    operation.ReconciliationDiagnosticCode)));
        Assert.NotNull(operation.ReasonEnteredAt);
        Assert.Equal(operation.ReasonEnteredAt, operation.RequiresHumanAt);
        Assert.Null(operation.DeploymentRunId);

        var events = await db.ElsaInstanceAuditEvents.AsNoTracking()
            .Where(x => x.OperationId == commit.Operation.Id)
            .OrderBy(x => x.Sequence)
            .ToListAsync();
        Assert.Equal(
            StagingRecoveryLifecycleLeverStoreDefaults.AcceptedEventType,
            events[0].EventType);
        Assert.Contains(
            events,
            x => x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.RecoveryRequiredEventType);
        var fired = Assert.Single(
            events,
            x => x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.FiredEventType);
        Assert.Equal(StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode, fired.DiagnosticCode);
        Assert.Equal(
            StagingRecoveryLifecycleLeverStoreDefaults.FormatFiredSnapshot(
                ElsaObservedLifecycle.Ready,
                ElsaInstanceHealth.Healthy),
            fired.PriorState);
        Assert.StartsWith("sha256:", fired.OperatorSubject);
    }

    [Fact]
    public async Task Staging_recovery_lever_uncertain_park_has_no_provider_target_for_reconcile_ticks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Uncertain staging recovery lever workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "uncertain-recovery-lever",
                CreateIntent(), "create-uncertain-recovery-lever"));
        await CompleteOperationAsync(db, created.Operation.Id);
        await SetObservedAsync(db, created.Instance.Id, ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy);

        var store = CreateStore(db);
        var parked = await store.AcceptReconcileAndRequireRecoveryAsync(
            created.Instance.Id,
            "api-key",
            StagingRecoveryLifecycleLeverStoreDefaults.UncertainCode);

        Assert.Equal(StagingRecoveryLifecycleLeverStoreDefaults.UncertainCode, parked.Reason);
        var operation = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == parked.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, operation.State);
        Assert.Equal(StagingRecoveryLifecycleLeverStoreDefaults.UncertainCode, operation.FailureCode);
        Assert.Equal(StagingRecoveryLifecycleLeverStoreDefaults.UncertainCode, operation.ReconciliationDiagnosticCode);
        Assert.Equal(
            ManagedElsaReasonClass.Temporary,
            ManagedElsaReasonCodeCatalog.Classify(
                ManagedElsaReasonCodeCatalog.SelectCurrentReason(
                    operation.FailureCode,
                    operation.ReconciliationDiagnosticCode)));
        Assert.Null(operation.DeploymentRunId);
        Assert.NotNull(operation.ReasonEnteredAt);
        Assert.Null(operation.RequiresHumanAt);
        var enteredAt = operation.ReasonEnteredAt;

        Assert.Null(await store.GetTargetAsync(workspace.Id, parked.Operation.Id));
        Assert.DoesNotContain(
            await store.ListPendingProviderOperationsAsync(64),
            pending => pending.OperationId == parked.Operation.Id);
        Assert.Null(await store.GetTargetAsync(workspace.Id, parked.Operation.Id));

        var afterTicks = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == parked.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, afterTicks.State);
        Assert.Equal(StagingRecoveryLifecycleLeverStoreDefaults.UncertainCode, afterTicks.FailureCode);
        Assert.Equal(StagingRecoveryLifecycleLeverStoreDefaults.UncertainCode, afterTicks.ReconciliationDiagnosticCode);
        Assert.Equal(enteredAt, afterTicks.ReasonEnteredAt);
        Assert.Null(afterTicks.RequiresHumanAt);
        Assert.Null(afterTicks.DeploymentRunId);

        var reset = await store.ResetLeverParkedReconcileAsync(created.Instance.Id, "api-key");
        Assert.Equal(ElsaInstanceOperationState.Succeeded, reset.Operation.State);
        var afterReset = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == reset.Operation.Id);
        Assert.Null(afterReset.ReasonEnteredAt);
        Assert.Null(afterReset.RequiresHumanAt);
    }

    [Fact]
    public async Task Staging_recovery_lever_concurrent_fires_have_one_winner()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = CreateMigratedContext(connection);
        await setup.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(setup, "Concurrent staging recovery lever workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(setup), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "concurrent-recovery-lever",
                CreateIntent(), "create-concurrent-recovery-lever"));
        await CompleteOperationAsync(setup, created.Operation.Id);
        await SetObservedAsync(setup, created.Instance.Id, ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy);

        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        await using var firstDb = new CatalogDbContext(options);
        await using var secondDb = new CatalogDbContext(options);
        var firstStore = CreateStore(firstDb);
        var secondStore = CreateStore(secondDb);

        var results = await Task.WhenAll(
            CaptureAsync(() => firstStore.AcceptReconcileAndRequireRecoveryAsync(created.Instance.Id, "first")),
            CaptureAsync(() => secondStore.AcceptReconcileAndRequireRecoveryAsync(created.Instance.Id, "second")));

        Assert.Equal(1, results.Count(x => x.Error is null));
        var conflict = Assert.Single(results, x => x.Error is not null).Error;
        var refused = Assert.IsType<ElsaInstanceLifecycleConflictException>(conflict);
        Assert.Equal(ElsaInstanceLifecycleConflictReason.OperationActive, refused.Reason);

        await using var verify = new CatalogDbContext(options);
        Assert.Equal(
            1,
            await verify.ElsaInstanceOperations.CountAsync(x =>
                x.InstanceId == created.Instance.Id &&
                x.Action == ElsaInstanceOperationAction.Reconcile &&
                x.State == ElsaInstanceOperationState.RecoveryRequired));
        Assert.Equal(
            1,
            await verify.ElsaInstanceAuditEvents.CountAsync(x =>
                x.InstanceId == created.Instance.Id &&
                x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.FiredEventType));
    }

    [Fact]
    public async Task Staging_recovery_lever_refuses_when_an_operation_is_already_active()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Active staging recovery lever workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "active-recovery-lever",
                CreateIntent(), "create-active-recovery-lever"));

        var error = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            CreateStore(db).AcceptReconcileAndRequireRecoveryAsync(created.Instance.Id, "operator"));

        Assert.Equal(ElsaInstanceLifecycleConflictReason.OperationActive, error.Reason);
        Assert.Equal(
            0,
            await db.ElsaInstanceAuditEvents.CountAsync(x =>
                x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.FiredEventType));
    }

    [Fact]
    public async Task Staging_recovery_lever_reset_clears_the_park_through_real_transitions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Staging recovery lever reset workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "staging-recovery-reset",
                CreateIntent(), "create-staging-recovery-reset"));
        await CompleteOperationAsync(db, created.Operation.Id);
        await SetObservedAsync(db, created.Instance.Id, ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy);
        await AttachCurrentDeploymentAsync(db, created.Instance.Id);

        var store = CreateStore(db);
        var parked = await store.AcceptReconcileAndRequireRecoveryAsync(created.Instance.Id, "api-key");
        var refused = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            new ElsaInstanceLifecycleService(store, new FixedTimeProvider(Now))
                .RecoverAsync(new ElsaInstanceLifecycleRequest(
                    workspace.Id,
                    created.Instance.Id,
                    parked.Instance.Version,
                    "recover-after-lever")));
        Assert.Equal(ElsaInstanceLifecycleConflictReason.InvalidState, refused.Reason);
        Assert.Contains("Provider reconciliation has not established that retry is safe.", refused.Message, StringComparison.Ordinal);

        Assert.True(ElsaInstanceOperation.CanTransition(
            ElsaInstanceOperationState.RecoveryRequired,
            ElsaInstanceOperationState.Succeeded));
        var reset = await store.ResetLeverParkedReconcileAsync(created.Instance.Id, "api-key");

        Assert.Equal(parked.Operation.Id, reset.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.Succeeded, reset.Operation.State);
        Assert.Equal(ElsaObservedLifecycle.Ready, reset.Instance.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Unknown, reset.Instance.Health);

        var operation = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == reset.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.Succeeded, operation.State);
        Assert.Equal(StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode, operation.FailureCode);
        Assert.Null(operation.ReconciliationRetryEvidenceReference);
        Assert.Null(operation.ReconciliationRetryEvidenceDigest);
        Assert.NotEqual(ElsaInstanceProviderReconciliationService.RetrySafeCode, operation.FailureCode);

        var resetEvent = Assert.Single(
            await db.ElsaInstanceAuditEvents.AsNoTracking()
                .Where(x => x.OperationId == reset.Operation.Id &&
                            x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.ResetEventType)
                .ToListAsync());
        Assert.Equal(StagingRecoveryLifecycleLeverStoreDefaults.ResetCode, resetEvent.DiagnosticCode);
        var afterReset = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == reset.Operation.Id);
        Assert.Null(afterReset.ReasonEnteredAt);
        Assert.Null(afterReset.RequiresHumanAt);

        var monitor = new ElsaInstanceHealthMonitor(
            new ElsaInstanceHealthMonitorOptions { Enabled = true, MaxJitter = TimeSpan.Zero },
            new FixedTimeProvider(Now));
        var probe = new HealthyLeverProbe();
        var beforeObservation = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            store.AcceptReconcileAndRequireRecoveryAsync(created.Instance.Id, "api-key"));
        Assert.Equal(ElsaInstanceLifecycleConflictReason.InvalidState, beforeObservation.Reason);

        await ObserveHealthyThroughMonitorAsync(store, monitor, probe, workspace.Id, created.Instance.Id, expectedTransitions: 0);
        var beforeThreshold = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            store.AcceptReconcileAndRequireRecoveryAsync(created.Instance.Id, "api-key"));
        Assert.Equal(ElsaInstanceLifecycleConflictReason.InvalidState, beforeThreshold.Reason);

        await ObserveHealthyThroughMonitorAsync(store, monitor, probe, workspace.Id, created.Instance.Id, expectedTransitions: 1);
        var second = await store.AcceptReconcileAndRequireRecoveryAsync(created.Instance.Id, "api-key");
        Assert.NotEqual(parked.Operation.Id, second.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, second.Operation.State);
        Assert.Equal(
            2,
            await db.ElsaInstanceAuditEvents.CountAsync(x =>
                x.InstanceId == created.Instance.Id &&
                x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.FiredEventType));
    }

    [Fact]
    public async Task Staging_recovery_lever_reset_refuses_a_non_lever_recovery_required_park()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Non lever recovery park workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "non-lever-recovery-park",
                CreateIntent(), "create-non-lever-recovery-park"));
        await CompleteOperationAsync(db, created.Operation.Id);
        await SetObservedAsync(db, created.Instance.Id, ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy);

        var parked = await CreateStore(db).AcceptReconcileAndRequireRecoveryAsync(created.Instance.Id, "api-key");
        db.ChangeTracker.Clear();
        var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == parked.Operation.Id);
        operation.FailureCode = ElsaInstanceProviderReconciliationService.AmbiguousCode;
        operation.ReconciliationDiagnosticCode = ElsaInstanceProviderReconciliationService.AmbiguousCode;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var error = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            CreateStore(db).ResetLeverParkedReconcileAsync(created.Instance.Id, "api-key"));

        Assert.Equal(ElsaInstanceLifecycleConflictReason.InvalidState, error.Reason);
        var persisted = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == parked.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, persisted.State);
        Assert.Equal(ElsaInstanceProviderReconciliationService.AmbiguousCode, persisted.FailureCode);
        Assert.Equal(
            0,
            await db.ElsaInstanceAuditEvents.CountAsync(x =>
                x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.ResetEventType));
    }

    [Fact]
    public async Task Staging_recovery_lever_refuses_when_the_instance_is_not_ready()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Not ready staging recovery lever workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "not-ready-recovery-lever",
                CreateIntent(), "create-not-ready-recovery-lever"));
        await CompleteOperationAsync(db, created.Operation.Id);
        await SetObservedAsync(db, created.Instance.Id, ElsaObservedLifecycle.Stopped, ElsaInstanceHealth.Healthy);

        var error = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            CreateStore(db).AcceptReconcileAndRequireRecoveryAsync(created.Instance.Id, "operator"));

        Assert.Equal(ElsaInstanceLifecycleConflictReason.InvalidState, error.Reason);
        Assert.Equal(
            0,
            await db.ElsaInstanceOperations.CountAsync(x =>
                x.InstanceId == created.Instance.Id &&
                x.Action == ElsaInstanceOperationAction.Reconcile));
        Assert.Equal(
            0,
            await db.ElsaInstanceAuditEvents.CountAsync(x =>
                x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.FiredEventType));
    }

    [Fact]
    public async Task Staging_recovery_lever_refuses_when_the_instance_is_ready_but_not_healthy()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Ready not healthy staging recovery lever workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "ready-unhealthy-lever",
                CreateIntent(), "create-ready-unhealthy-lever"));
        await CompleteOperationAsync(db, created.Operation.Id);
        await SetObservedAsync(db, created.Instance.Id, ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Degraded);

        var error = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            CreateStore(db).AcceptReconcileAndRequireRecoveryAsync(created.Instance.Id, "operator"));

        Assert.Equal(ElsaInstanceLifecycleConflictReason.InvalidState, error.Reason);
        Assert.Equal(
            0,
            await db.ElsaInstanceOperations.CountAsync(x =>
                x.InstanceId == created.Instance.Id &&
                x.Action == ElsaInstanceOperationAction.Reconcile));
        Assert.Equal(
            0,
            await db.ElsaInstanceAuditEvents.CountAsync(x =>
                x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.FiredEventType));
    }

    [Fact]
    public async Task Staging_recovery_lever_reset_refuses_a_real_run_backed_uncertain_park()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Real run backed uncertain park");
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));
        await store.CommitProviderSubmissionAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-submission-uncertain",
            Now));

        var parked = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == accepted.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, parked.State);
        Assert.Equal(ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain, parked.FailureCode);
        Assert.NotNull(parked.DeploymentRunId);
        Assert.Equal(
            0,
            await db.ElsaInstanceAuditEvents.CountAsync(x =>
                x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.FiredEventType));

        await AssertResetRefusedWithoutWritesAsync(db, accepted.Instance.Id, accepted.Operation.Id);
    }

    [Fact]
    public async Task Staging_recovery_lever_reset_refuses_a_lever_park_when_only_a_deployment_run_is_set()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Lever park with deployment run workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "lever-park-with-run",
                CreateIntent(), "create-lever-park-with-run"));
        await CompleteOperationAsync(db, created.Operation.Id);
        await SetObservedAsync(db, created.Instance.Id, ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy);

        var parked = await CreateStore(db).AcceptReconcileAndRequireRecoveryAsync(created.Instance.Id, "api-key");
        db.ChangeTracker.Clear();
        var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == parked.Operation.Id);
        operation.DeploymentRunId = Guid.NewGuid();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await AssertResetRefusedWithoutWritesAsync(db, created.Instance.Id, parked.Operation.Id);
        Assert.Equal(
            1,
            await db.ElsaInstanceAuditEvents.CountAsync(x =>
                x.OperationId == parked.Operation.Id &&
                x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.FiredEventType));
    }

    [Fact]
    public async Task Staging_recovery_lever_reset_refuses_a_lever_shaped_park_without_a_fired_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Lever shaped park without fired workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "lever-shaped-no-fired",
                CreateIntent(), "create-lever-shaped-no-fired"));
        await CompleteOperationAsync(db, created.Operation.Id);
        await SetObservedAsync(db, created.Instance.Id, ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy);

        var operationId = await SeedLeverShapedParkAsync(
            db,
            created.Instance,
            StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode);

        await AssertResetRefusedWithoutWritesAsync(db, created.Instance.Id, operationId);
    }

    [Fact]
    public async Task Staging_recovery_lever_reset_refuses_a_park_with_a_provider_operation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Provider op lever park workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "provider-op-lever-park",
                CreateIntent(), "create-provider-op-lever-park"));
        await CompleteOperationAsync(db, created.Operation.Id);
        await SetObservedAsync(db, created.Instance.Id, ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy);

        var parked = await CreateStore(db).AcceptReconcileAndRequireRecoveryAsync(created.Instance.Id, "api-key");
        await AddProviderOperationAsync(db, workspace.Id, parked.Operation.Id);

        await AssertResetRefusedWithoutWritesAsync(db, created.Instance.Id, parked.Operation.Id);
    }

    [Fact]
    public async Task Staging_recovery_lever_concurrent_resets_have_one_winner()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = CreateMigratedContext(connection);
        await setup.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(setup, "Concurrent staging recovery lever reset workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(setup), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "concurrent-recovery-reset",
                CreateIntent(), "create-concurrent-recovery-reset"));
        await CompleteOperationAsync(setup, created.Operation.Id);
        await SetObservedAsync(setup, created.Instance.Id, ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy);
        var parked = await CreateStore(setup).AcceptReconcileAndRequireRecoveryAsync(created.Instance.Id, "setup");
        setup.ChangeTracker.Clear();

        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        await using var firstDb = new CatalogDbContext(options);
        await using var secondDb = new CatalogDbContext(options);

        var results = await Task.WhenAll(
            CaptureAsync(() => CreateStore(firstDb).ResetLeverParkedReconcileAsync(created.Instance.Id, "first")),
            CaptureAsync(() => CreateStore(secondDb).ResetLeverParkedReconcileAsync(created.Instance.Id, "second")));

        Assert.Equal(1, results.Count(x => x.Error is null));
        var conflict = Assert.Single(results, x => x.Error is not null).Error;
        var refused = Assert.IsType<ElsaInstanceLifecycleConflictException>(conflict);
        Assert.True(
            refused.Reason is ElsaInstanceLifecycleConflictReason.InvalidState
                or ElsaInstanceLifecycleConflictReason.OperationActive,
            refused.Reason.ToString());

        await using var verify = new CatalogDbContext(options);
        Assert.Equal(
            ElsaInstanceOperationState.Succeeded,
            (await verify.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == parked.Operation.Id)).State);
        Assert.Equal(
            1,
            await verify.ElsaInstanceAuditEvents.CountAsync(x =>
                x.InstanceId == created.Instance.Id &&
                x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.ResetEventType));
    }

    [Fact]
    public void Staging_recovery_lever_maps_concurrency_to_operation_active()
    {
        var mapped = EfCoreElsaInstanceLifecycleStore.MapStagingRecoveryLeverPersistenceFailure(
            new DbUpdateConcurrencyException("concurrency"));

        var conflict = Assert.IsType<ElsaInstanceLifecycleConflictException>(mapped);
        Assert.Equal(ElsaInstanceLifecycleConflictReason.OperationActive, conflict.Reason);
    }

    [Fact]
    public void Staging_recovery_lever_maps_unique_violation_to_operation_active()
    {
        var mapped = EfCoreElsaInstanceLifecycleStore.MapStagingRecoveryLeverPersistenceFailure(
            new DbUpdateException("unique", new SqliteException("UNIQUE constraint failed", 19, 2067)));

        var conflict = Assert.IsType<ElsaInstanceLifecycleConflictException>(mapped);
        Assert.Equal(ElsaInstanceLifecycleConflictReason.OperationActive, conflict.Reason);
    }

    [Fact]
    public void Staging_recovery_lever_maps_generic_db_exception_to_persistence_unavailable()
    {
        var inner = new SqliteException("disk I/O error", 10);
        var mapped = EfCoreElsaInstanceLifecycleStore.MapStagingRecoveryLeverPersistenceFailure(inner);

        var persistence = Assert.IsType<StagingRecoveryLifecycleLeverPersistenceException>(mapped);
        Assert.Same(inner, persistence.InnerException);
    }

    [Fact]
    public void Staging_recovery_lever_maps_retry_limit_exceeded_to_persistence_unavailable()
    {
        var exhausted = new RetryLimitExceededException("The execution strategy retries were exhausted.");
        var mapped = EfCoreElsaInstanceLifecycleStore.MapStagingRecoveryLeverPersistenceFailure(exhausted);

        var persistence = Assert.IsType<StagingRecoveryLifecycleLeverPersistenceException>(mapped);
        Assert.Same(exhausted, persistence.InnerException);
    }

    [Fact]
    public async Task Staging_recovery_lever_fire_classifies_concurrency_as_conflict() =>
        await AssertFireClassifiesAsync(
            new DbUpdateConcurrencyException("concurrency"),
            expected: typeof(ElsaInstanceLifecycleConflictException));

    [Fact]
    public async Task Staging_recovery_lever_fire_classifies_unique_violation_as_conflict() =>
        await AssertFireClassifiesAsync(
            new DbUpdateException("unique", new SqliteException("UNIQUE constraint failed", 19, 2067)),
            expected: typeof(ElsaInstanceLifecycleConflictException));

    [Fact]
    public async Task Staging_recovery_lever_fire_classifies_generic_db_exception_as_unavailable() =>
        await AssertFireClassifiesAsync(
            new SqliteException("disk I/O error", 10),
            expected: typeof(StagingRecoveryLifecycleLeverPersistenceException));

    [Fact]
    public async Task Staging_recovery_lever_fire_classifies_retry_limit_exceeded_as_unavailable() =>
        await AssertFireClassifiesAsync(
            new RetryLimitExceededException("The execution strategy retries were exhausted."),
            expected: typeof(StagingRecoveryLifecycleLeverPersistenceException));

    [Fact]
    public async Task Staging_recovery_lever_reset_classifies_generic_db_exception_as_unavailable() =>
        await AssertResetClassifiesAsync(
            new SqliteException("disk I/O error", 10),
            expected: typeof(StagingRecoveryLifecycleLeverPersistenceException));

    [Fact]
    public async Task Staging_recovery_lever_reset_classifies_retry_limit_exceeded_as_unavailable() =>
        await AssertResetClassifiesAsync(
            new RetryLimitExceededException("The execution strategy retries were exhausted."),
            expected: typeof(StagingRecoveryLifecycleLeverPersistenceException));

    private const string LeverEndpoint = "https://e1234567890abcde-app.region.azurecontainerapps.io";

    private static async Task AssertFireClassifiesAsync(Exception thrown, Type expected)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        Guid instanceId;
        await using (var setup = CreateMigratedContext(connection))
        {
            await setup.Database.MigrateAsync();
            var workspace = await CreateWorkspaceAsync(setup, "Lever persistence classification workspace");
            var created = await new ElsaInstanceLifecycleService(CreateStore(setup), new FixedTimeProvider(Now))
                .CreateAsync(new ElsaInstanceCreateRequest(
                    workspace.OrganizationId, workspace.Id, "Managed Elsa", "lever-persistence-fire",
                    CreateIntent(), "create-lever-persistence-fire"));
            await CompleteOperationAsync(setup, created.Operation.Id);
            await SetObservedAsync(setup, created.Instance.Id, ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy);
            instanceId = created.Instance.Id;
        }

        await using var db = CreateThrowingContext(connection, thrown);
        var error = await Assert.ThrowsAsync(expected, () =>
            CreateStore(db).AcceptReconcileAndRequireRecoveryAsync(instanceId, "operator"));
        if (error is ElsaInstanceLifecycleConflictException conflict)
            Assert.Equal(ElsaInstanceLifecycleConflictReason.OperationActive, conflict.Reason);
        Assert.Equal(0, await db.ElsaInstanceAuditEvents.CountAsync(x =>
            x.InstanceId == instanceId &&
            x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.FiredEventType));
    }

    private static async Task AssertResetClassifiesAsync(Exception thrown, Type expected)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        Guid instanceId;
        await using (var setup = CreateMigratedContext(connection))
        {
            await setup.Database.MigrateAsync();
            var workspace = await CreateWorkspaceAsync(setup, "Lever persistence reset workspace");
            var created = await new ElsaInstanceLifecycleService(CreateStore(setup), new FixedTimeProvider(Now))
                .CreateAsync(new ElsaInstanceCreateRequest(
                    workspace.OrganizationId, workspace.Id, "Managed Elsa", "lever-persistence-reset",
                    CreateIntent(), "create-lever-persistence-reset"));
            await CompleteOperationAsync(setup, created.Operation.Id);
            await SetObservedAsync(setup, created.Instance.Id, ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy);
            await CreateStore(setup).AcceptReconcileAndRequireRecoveryAsync(created.Instance.Id, "setup");
            instanceId = created.Instance.Id;
        }

        await using var db = CreateThrowingContext(connection, thrown);
        var error = await Assert.ThrowsAsync(expected, () =>
            CreateStore(db).ResetLeverParkedReconcileAsync(instanceId, "operator"));
        if (error is ElsaInstanceLifecycleConflictException conflict)
            Assert.Equal(ElsaInstanceLifecycleConflictReason.OperationActive, conflict.Reason);
        Assert.Equal(0, await db.ElsaInstanceAuditEvents.CountAsync(x =>
            x.InstanceId == instanceId &&
            x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.ResetEventType));
    }

    private static CatalogDbContext CreateThrowingContext(SqliteConnection connection, Exception thrown)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(
                connection,
                sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .AddInterceptors(new ThrowOnSaveInterceptor(thrown))
            .Options;
        return new CatalogDbContext(options);
    }

    private sealed class ThrowOnSaveInterceptor(Exception thrown) : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result) =>
            throw thrown;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            throw thrown;
    }

    private static async Task AttachCurrentDeploymentAsync(CatalogDbContext db, Guid instanceId)
    {
        db.ChangeTracker.Clear();
        var instance = await db.ElsaInstances.SingleAsync(x => x.Id == instanceId);
        instance.CurrentDeploymentId = "deployment-lever";
        instance.CurrentDeploymentEndpointUri = LeverEndpoint;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task ObserveHealthyThroughMonitorAsync(
        IElsaInstanceHealthMonitorStore store,
        ElsaInstanceHealthMonitor monitor,
        IElsaInstanceProviderHealthProbePort probe,
        Guid workspaceId,
        Guid instanceId,
        int expectedTransitions)
    {
        var target = await store.GetHealthMonitorTargetAsync(workspaceId, instanceId);
        Assert.NotNull(target);
        var evaluation = await monitor.EvaluateAsync(target, store, probe);
        Assert.Equal(
            expectedTransitions == 0
                ? ElsaInstanceHealthMonitorOutcome.Unchanged
                : ElsaInstanceHealthMonitorOutcome.Transitioned,
            evaluation.Outcome);
        var after = await store.GetHealthMonitorTargetAsync(workspaceId, instanceId);
        Assert.NotNull(after);
        Assert.Equal(
            expectedTransitions == 0 ? ElsaInstanceHealth.Unknown : ElsaInstanceHealth.Healthy,
            after.Health);
        Assert.Equal(ElsaObservedLifecycle.Ready, after.ObservedLifecycle);
    }

    private sealed class HealthyLeverProbe : IElsaInstanceProviderHealthProbePort
    {
        public Task<ElsaInstanceHealthProbeResult> ProbeAsync(
            ElsaInstanceHealthProbeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ElsaInstanceHealthProbeResult(ElsaInstanceHealth.Healthy, "azure.health.healthy"));
    }

    private static async Task SetObservedAsync(
        CatalogDbContext db,
        Guid instanceId,
        ElsaObservedLifecycle lifecycle,
        ElsaInstanceHealth health)
    {
        db.ChangeTracker.Clear();
        var instance = await db.ElsaInstances.SingleAsync(x => x.Id == instanceId);
        instance.ObservedLifecycle = lifecycle;
        instance.Health = health;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<Guid> SeedLeverShapedParkAsync(
        CatalogDbContext db,
        ElsaInstance instance,
        string failureCode,
        Guid? deploymentRunId = null)
    {
        db.ChangeTracker.Clear();
        var id = Guid.NewGuid();
        db.ElsaInstanceOperations.Add(new ElsaInstanceOperationEntity
        {
            Id = id,
            InstanceId = instance.Id,
            OrganizationId = instance.OrganizationId,
            WorkspaceId = instance.WorkspaceId,
            Action = ElsaInstanceOperationAction.Reconcile,
            IdempotencyScope = $"instance/{instance.Id:D}/operations",
            IdempotencyKey = $"seed-park-{id:N}",
            RequestHash = new string('a', 64),
            ExpectedVersion = instance.Version,
            State = ElsaInstanceOperationState.RecoveryRequired,
            AttemptNumber = 1,
            AcceptedAt = Now,
            FailureCode = failureCode,
            FailureSummary = failureCode,
            ReconciliationDiagnosticCode = failureCode,
            DeploymentRunId = deploymentRunId,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return id;
    }

    private static async Task AddProviderOperationAsync(CatalogDbContext db, Guid workspaceId, Guid operationId)
    {
        db.ChangeTracker.Clear();
        var now = Now;
        db.AzureProviderOperations.Add(new AzureProviderOperationEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            TargetKey = $"lever-reset-{operationId:N}",
            Action = AzureProviderOperationAction.Reconcile,
            IdempotencyKey = AzureProviderOperationValidation.LifecycleIdempotencyKey(operationId),
            RequestHash = new string('a', 64),
            OperationIdentity = new string('b', 64),
            PlanFingerprint = new string('c', 64),
            TemplateFingerprint = new string('d', 64),
            ElsaVersion = "3.10.4",
            ReleaseLine = "3.10",
            Topology = "combined",
            Isolation = "dedicated",
            Location = "westeurope",
            ImageRepository = "example.azurecr.io/elsa",
            ImageDigest = "sha256:" + new string('e', 64),
            SecretReferencesJson = "{}",
            Status = AzureProviderOperationStatus.Succeeded,
            Phase = AzureProviderOperationPhase.TrafficPromoted,
            Health = AzureProviderHealth.Healthy,
            DiagnosticsJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
            StatusChangedAt = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task AssertResetRefusedWithoutWritesAsync(
        CatalogDbContext db,
        Guid instanceId,
        Guid operationId)
    {
        var error = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            CreateStore(db).ResetLeverParkedReconcileAsync(instanceId, "api-key"));

        Assert.Equal(ElsaInstanceLifecycleConflictReason.InvalidState, error.Reason);
        var persisted = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == operationId);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, persisted.State);
        Assert.Equal(
            0,
            await db.ElsaInstanceAuditEvents.CountAsync(x =>
                x.EventType == StagingRecoveryLifecycleLeverStoreDefaults.ResetEventType));
    }
}
