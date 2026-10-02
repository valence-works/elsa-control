using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
        db.ChangeTracker.Clear();

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
        Assert.StartsWith("sha256:", fired.OperatorSubject);
        Assert.DoesNotContain(events, x => x.EventType.Contains("alert", StringComparison.OrdinalIgnoreCase));
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
        db.ChangeTracker.Clear();

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
        Assert.Equal(StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode, operation.ReconciliationDiagnosticCode);
        Assert.Null(operation.DeploymentRunId);
        var enteredAt = operation.UpdatedAt;

        Assert.Null(await store.GetTargetAsync(workspace.Id, parked.Operation.Id));
        Assert.DoesNotContain(
            await store.ListPendingProviderOperationsAsync(64),
            pending => pending.OperationId == parked.Operation.Id);
        Assert.Null(await store.GetTargetAsync(workspace.Id, parked.Operation.Id));

        var afterTicks = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == parked.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, afterTicks.State);
        Assert.Equal(StagingRecoveryLifecycleLeverStoreDefaults.UncertainCode, afterTicks.FailureCode);
        Assert.Equal(enteredAt, afterTicks.UpdatedAt);
        Assert.Null(afterTicks.DeploymentRunId);

        var reset = await store.ResetLeverParkedReconcileAsync(created.Instance.Id, "api-key");
        Assert.Equal(ElsaInstanceOperationState.Succeeded, reset.Operation.State);
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
        setup.ChangeTracker.Clear();

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
        db.ChangeTracker.Clear();

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
        Assert.Equal(ElsaInstanceHealth.Healthy, reset.Instance.Health);

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
        Assert.DoesNotContain(
            await db.ElsaInstanceAuditEvents.AsNoTracking()
                .Where(x => x.InstanceId == created.Instance.Id)
                .Select(x => x.EventType)
                .ToListAsync(),
            eventType => eventType.Contains("alert", StringComparison.OrdinalIgnoreCase));

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
        db.ChangeTracker.Clear();

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
}
