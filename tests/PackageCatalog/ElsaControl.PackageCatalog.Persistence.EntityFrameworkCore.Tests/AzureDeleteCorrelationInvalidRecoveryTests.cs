using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed partial class ElsaInstanceLifecycleStoreTests
{
    [Fact]
    public async Task Correlation_invalid_delete_recovery_fails_closed_when_assignment_inventory_may_exist()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var fixture = await SeedCorrelationInvalidDeleteAsync(
            db, "correlation-invalid-fail-closed", retainWorkload: true, assignmentDeleted: false);

        var beforeOperation = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.Deletion.Operation.Id);
        var beforeInstance = await db.ElsaInstances.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.Instance.Id);
        var beforeRecoveryCount = await db.ElsaInstanceRecoveryRequests.CountAsync();

        var conflict = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now.AddMinutes(4)))
                .RecoverAsync(new(
                    fixture.Workspace.Id,
                    fixture.Instance.Id,
                    fixture.Instance.Version,
                    "recover-correlation-invalid-unsafe")));

        Assert.Equal(ElsaInstanceLifecycleConflictReason.RecoveryAuthorityUnavailable, conflict.Reason);
        db.ChangeTracker.Clear();
        var afterOperation = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.Deletion.Operation.Id);
        var afterInstance = await db.ElsaInstances.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.Deletion.Instance.Id);
        Assert.Equal(beforeOperation.State, afterOperation.State);
        Assert.Equal(beforeOperation.AttemptNumber, afterOperation.AttemptNumber);
        Assert.Equal(beforeOperation.UpdatedAt, afterOperation.UpdatedAt);
        Assert.Equal(beforeInstance.Version, afterInstance.Version);
        Assert.Equal(beforeInstance.ObservedLifecycle, afterInstance.ObservedLifecycle);
        Assert.Equal(beforeRecoveryCount, await db.ElsaInstanceRecoveryRequests.CountAsync());
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, afterOperation.State);
        Assert.Equal("deletion.provider-correlation-invalid", afterOperation.DeletionDiagnosticCode);
    }

    [Fact]
    public async Task Correlation_invalid_delete_recovery_fails_closed_when_deleted_assignment_retains_workload()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var fixture = await SeedCorrelationInvalidDeleteAsync(
            db, "correlation-invalid-retained", retainWorkload: true, assignmentDeleted: true);

        var conflict = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now.AddMinutes(4)))
                .RecoverAsync(new(
                    fixture.Workspace.Id,
                    fixture.Instance.Id,
                    fixture.Instance.Version,
                    "recover-correlation-invalid-retained")));

        Assert.Equal(ElsaInstanceLifecycleConflictReason.RecoveryAuthorityUnavailable, conflict.Reason);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired,
            (await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == fixture.Deletion.Operation.Id)).State);
        Assert.Equal(0, await db.ElsaInstanceRecoveryRequests.CountAsync());
    }

    [Fact]
    public async Task Correlation_invalid_delete_recovery_accepts_confirmed_absent_assignment_and_replays()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var fixture = await SeedCorrelationInvalidDeleteAsync(
            db, "correlation-invalid-absent", retainWorkload: false, assignmentDeleted: true);

        var service = new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now.AddMinutes(4)));
        var recovered = await service.RecoverAsync(new(
            fixture.Workspace.Id,
            fixture.Instance.Id,
            fixture.Instance.Version,
            "recover-correlation-invalid-absent",
            "operator recover"));
        var replay = await service.RecoverAsync(new(
            fixture.Workspace.Id,
            fixture.Instance.Id,
            fixture.Instance.Version,
            "recover-correlation-invalid-absent",
            "operator recover"));

        Assert.Equal(fixture.Deletion.Operation.Id, recovered.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.Queued, recovered.Operation.State);
        Assert.Equal(fixture.Deletion.Operation.AttemptNumber + 1, recovered.Operation.AttemptNumber);
        Assert.False(recovered.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(recovered.Operation.AttemptNumber, replay.Operation.AttemptNumber);
        Assert.Equal(recovered.Instance.Version, replay.Instance.Version);
        var recovery = Assert.Single(await db.ElsaInstanceRecoveryRequests.AsNoTracking().ToListAsync());
        Assert.Null(recovery.AzureDeleteRecoveryAuthority);
        Assert.Equal(fixture.Deletion.Operation.Id, recovery.OperationId);
        Assert.Equal(1, await db.ElsaInstanceAuditEvents.CountAsync(x =>
            x.InstanceId == fixture.Instance.Id && x.EventType == "lifecycle.operation-updated"));
    }

    [Fact]
    public async Task Correlation_invalid_delete_recovery_rejects_wrong_binding()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var fixture = await SeedCorrelationInvalidDeleteAsync(
            db, "correlation-invalid-binding", retainWorkload: false, assignmentDeleted: true);
        var service = new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now.AddMinutes(4)));
        var request = new ElsaInstanceLifecycleRequest(
            fixture.Workspace.Id,
            fixture.Instance.Id,
            fixture.Instance.Version,
            "recover-correlation-invalid-binding",
            ExpectedOperationId: fixture.Deletion.Operation.Id);

        var wrongOperation = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            service.RecoverAsync(request with { ExpectedOperationId = Guid.NewGuid() }));
        var wrongVersion = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            service.RecoverAsync(request with { ExpectedVersion = fixture.Deletion.Instance.Version + 1 }));
        var missingInstance = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.RecoverAsync(request with { InstanceId = Guid.NewGuid() }));

        Assert.Equal(ElsaInstanceLifecycleConflictReason.InvalidState, wrongOperation.Reason);
        Assert.Equal(ElsaInstanceLifecycleConflictReason.VersionConflict, wrongVersion.Reason);
        Assert.NotNull(missingInstance);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired,
            (await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == fixture.Deletion.Operation.Id)).State);
        Assert.Equal(0, await db.ElsaInstanceRecoveryRequests.CountAsync());
    }

    [Fact]
    public async Task Correlation_invalid_delete_recovery_finalizes_when_assignment_proves_absence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var fixture = await SeedCorrelationInvalidDeleteAsync(
            db, "correlation-invalid-finalize", retainWorkload: false, assignmentDeleted: true);
        await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now.AddMinutes(4)))
            .RecoverAsync(new(
                fixture.Workspace.Id,
                fixture.Instance.Id,
                fixture.Instance.Version,
                "recover-correlation-invalid-finalize"));

        var operationStore = new AzureProviderOperationStore(db);
        var provider = new AzureElsaInstanceProvider(
            new AzureProviderOperationService(operationStore, new FixedTimeProvider(Now.AddMinutes(5))),
            operationStore,
            operationStore,
            timeProvider: new FixedTimeProvider(Now.AddMinutes(5)),
            options: new AzureElsaInstanceProviderOptions
            {
                Enabled = true,
                TemplateFingerprint = new string('b', 64),
                ProviderScopeFingerprint = new string('a', 64),
                SubscriptionId = "11111111-1111-1111-1111-111111111111",
                ResourceGroupNamePrefix = "rg-correlation"
            });
        var batch = await new ElsaInstanceDeletionWorker(
                new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance,
                    new FixedTimeProvider(Now.AddMinutes(5))),
                provider,
                new FixedTimeProvider(Now.AddMinutes(5)))
            .ProcessAvailableAsync("correlation-invalid-worker");

        var result = Assert.Single(batch.Results);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Deleted, result.Outcome);
        Assert.Equal("deletion.provider-confirmed-absent", result.FailureCode);
        Assert.Equal(ElsaObservedLifecycle.Deleted,
            (await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == fixture.Instance.Id)).ObservedLifecycle);
        Assert.Equal(ElsaInstanceOperationState.Succeeded,
            (await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == fixture.Deletion.Operation.Id)).State);
        Assert.Equal(1, await db.ElsaInstanceAuditEvents.CountAsync(x =>
            x.InstanceId == fixture.Instance.Id && x.EventType == "lifecycle.deleted"));
    }

    private static async Task<CorrelationInvalidDeleteFixture> SeedCorrelationInvalidDeleteAsync(
        CatalogDbContext db,
        string workspaceName,
        bool retainWorkload,
        bool assignmentDeleted)
    {
        var workspace = await CreateWorkspaceAsync(db, workspaceName);
        var created = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Correlation Elsa", $"correlation-{Guid.NewGuid():N}",
                WorkerIntent(), $"create-{Guid.NewGuid():N}"));
        await CompleteOperationAsync(db, created.Operation.Id);
        var instance = await db.ElsaInstances.SingleAsync(x => x.Id == created.Instance.Id);
        instance.ObservedLifecycle = ElsaObservedLifecycle.Unknown;
        instance.Health = ElsaInstanceHealth.Unknown;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var assignmentStore = (IAzureProviderResourceAssignmentStore)new AzureProviderOperationStore(db);
        var assignment = await assignmentStore.CreateOrGetAsync(
            new(
                workspace.Id,
                workspace.OrganizationId,
                created.Instance.Id,
                new string('a', 64),
                "11111111-1111-1111-1111-111111111111",
                "rg-correlation",
                $"e{created.Instance.Id:N}"[..16],
                "westeurope"),
            Now);
        var assignmentEntity = await db.AzureProviderResourceAssignments.SingleAsync(x => x.Id == assignment.Id);
        assignmentEntity.State = assignmentDeleted
            ? AzureProviderAssignmentState.Deleted
            : AzureProviderAssignmentState.Active;
        assignmentEntity.LastOperationId = Guid.NewGuid();
        assignmentEntity.WorkloadResourceId = retainWorkload
            ? "/subscriptions/retained/resourceGroups/retained/providers/Microsoft.App/containerApps/retained"
            : null;
        assignmentEntity.DeletedAt = assignmentDeleted ? Now : null;
        instance = await db.ElsaInstances.SingleAsync(x => x.Id == created.Instance.Id);
        instance.PlacementAssignmentId = assignment.Id.ToString("D");
        instance.Version++;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var current = Assert.IsType<ElsaInstance>(await CreateStore(db)
            .GetInstanceAsync(workspace.Id, created.Instance.Id));
        var deletion = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now.AddMinutes(2)))
            .DeleteAsync(await CreateConfirmedDeleteRequestAsync(
                db, workspace.Id, created.Instance.Id, current.Version, $"delete-{workspaceName}", Now.AddMinutes(2)));
        var port = new QueueCleanupPort(new ElsaInstanceCleanupObservation(
            ElsaInstanceCleanupObservationKind.Ambiguous, deletion.Operation.Id,
            deletion.Operation.AttemptNumber, "deletion.provider-correlation-invalid"));
        var failed = await new ElsaInstanceDeletionWorker(
                new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance,
                    new FixedTimeProvider(Now.AddMinutes(3))),
                port,
                new FixedTimeProvider(Now.AddMinutes(3)))
            .ProcessAvailableAsync("correlation-invalid-setup");
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Failed, Assert.Single(failed.Results).Outcome);
        db.ChangeTracker.Clear();
        var stored = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == deletion.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, stored.State);
        Assert.Equal("deletion.provider-correlation-invalid", stored.DeletionDiagnosticCode);
        Assert.Null(stored.DeploymentRunId);
        var storedInstance = await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == created.Instance.Id);
        Assert.Equal(ElsaDesiredLifecycle.Deleting, storedInstance.DesiredLifecycle);
        Assert.Equal(ElsaObservedLifecycle.Unknown, storedInstance.ObservedLifecycle);
        var currentInstance = Assert.IsType<ElsaInstance>(await CreateStore(db)
            .GetInstanceAsync(workspace.Id, created.Instance.Id));
        return new(workspace, currentInstance, deletion);
    }

    private sealed record CorrelationInvalidDeleteFixture(
        Workspace Workspace,
        ElsaInstance Instance,
        ElsaInstanceLifecycleAcceptance Deletion);
}
