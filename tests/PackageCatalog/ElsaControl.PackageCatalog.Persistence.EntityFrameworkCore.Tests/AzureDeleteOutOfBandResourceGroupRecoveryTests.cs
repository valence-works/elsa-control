using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed partial class ElsaInstanceLifecycleStoreTests
{
    [Fact]
    public async Task Out_of_band_rg_absence_reconciles_stale_inventory_and_completes_delete()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var fixture = await SeedCorrelationInvalidDeleteAsync(
            db, "oob-rg-absent", retainWorkload: true, assignmentDeleted: false);
        Assert.Equal(1, await ElsaInstanceAllowanceAccounting.CountDedicatedAsync(db, fixture.Workspace.OrganizationId));
        var observer = new StubEngineResourceGroupObserver { State = AzureProviderFreshResourceGroupState.Absent };
        var (service, provider) = CreateOutOfBandRecoverServices(db, observer, Now.AddMinutes(4));

        var recovered = await service.RecoverAsync(new(
            fixture.Workspace.Id,
            fixture.Instance.Id,
            fixture.Instance.Version,
            "recover-oob-rg-absent",
            "operator recover"));
        var replay = await service.RecoverAsync(new(
            fixture.Workspace.Id,
            fixture.Instance.Id,
            fixture.Instance.Version,
            "recover-oob-rg-absent",
            "operator recover"));
        var batch = await new ElsaInstanceDeletionWorker(
                new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance,
                    new FixedTimeProvider(Now.AddMinutes(5))),
                provider,
                new FixedTimeProvider(Now.AddMinutes(5)))
            .ProcessAvailableAsync("oob-rg-absent-worker");

        Assert.False(recovered.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(recovered.Operation.AttemptNumber, replay.Operation.AttemptNumber);
        var result = Assert.Single(batch.Results);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Deleted, result.Outcome);
        Assert.Equal("deletion.provider-confirmed-absent", result.FailureCode);
        var instance = await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == fixture.Instance.Id);
        Assert.Equal(ElsaObservedLifecycle.Deleted, instance.ObservedLifecycle);
        Assert.Equal(ElsaInstanceOperationState.Succeeded,
            (await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == fixture.Deletion.Operation.Id)).State);
        var assignment = Assert.Single(await db.AzureProviderResourceAssignments.AsNoTracking().ToListAsync());
        Assert.Equal(AzureProviderAssignmentState.Deleted, assignment.State);
        Assert.Null(assignment.WorkloadResourceId);
        Assert.Null(assignment.KeyVaultResourceId);
        Assert.Null(assignment.SqlServerResourceId);
        Assert.Null(assignment.RegistryResourceId);
        Assert.Equal(0, await ElsaInstanceAllowanceAccounting.CountDedicatedAsync(db, fixture.Workspace.OrganizationId));
        Assert.Equal(1, await db.ElsaInstanceAuditEvents.CountAsync(x =>
            x.InstanceId == fixture.Instance.Id &&
            x.EventType == "lifecycle.delete-out-of-band-resource-group-absent" &&
            x.DiagnosticCode == ManagedElsaReasonCodeCatalog.DeletionOutOfBandResourceGroupAbsent));
        var observed = Assert.Single(observer.Calls);
        Assert.Equal("11111111-1111-1111-1111-111111111111", observed.SubscriptionId);
        Assert.Equal(assignment.ResourceGroupName, observed.ResourceGroupName);
        Assert.DoesNotContain("vault", observed.ResourceGroupName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sql", observed.ResourceGroupName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("control", observed.ResourceGroupName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Out_of_band_rg_present_keeps_recovery_authority_unavailable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var fixture = await SeedCorrelationInvalidDeleteAsync(
            db, "oob-rg-present", retainWorkload: true, assignmentDeleted: false);
        var observer = new StubEngineResourceGroupObserver { State = AzureProviderFreshResourceGroupState.Present };
        var (service, _) = CreateOutOfBandRecoverServices(db, observer, Now.AddMinutes(4));
        var beforeAssignment = await db.AzureProviderResourceAssignments.AsNoTracking().SingleAsync();
        var beforeRecoveryCount = await db.ElsaInstanceRecoveryRequests.CountAsync();
        var beforeAudit = await db.ElsaInstanceAuditEvents.CountAsync();

        var conflict = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            service.RecoverAsync(new(
                fixture.Workspace.Id,
                fixture.Instance.Id,
                fixture.Instance.Version,
                "recover-oob-rg-present")));

        Assert.Equal(ElsaInstanceLifecycleConflictReason.RecoveryAuthorityUnavailable, conflict.Reason);
        Assert.Single(observer.Calls);
        var afterAssignment = await db.AzureProviderResourceAssignments.AsNoTracking().SingleAsync();
        Assert.Equal(beforeAssignment.State, afterAssignment.State);
        Assert.Equal(beforeAssignment.WorkloadResourceId, afterAssignment.WorkloadResourceId);
        Assert.Equal(beforeAssignment.Version, afterAssignment.Version);
        Assert.Equal(beforeRecoveryCount, await db.ElsaInstanceRecoveryRequests.CountAsync());
        Assert.Equal(beforeAudit, await db.ElsaInstanceAuditEvents.CountAsync());
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired,
            (await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == fixture.Deletion.Operation.Id)).State);
    }

    [Theory]
    [InlineData(AzureProviderFreshResourceGroupState.Unknown)]
    [InlineData(null)]
    public async Task Out_of_band_rg_observation_error_keeps_recovery_authority_unavailable(
        AzureProviderFreshResourceGroupState? state)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var fixture = await SeedCorrelationInvalidDeleteAsync(
            db, $"oob-rg-error-{state?.ToString() ?? "throw"}", retainWorkload: true, assignmentDeleted: false);
        var observer = new StubEngineResourceGroupObserver
        {
            State = state ?? AzureProviderFreshResourceGroupState.Unknown,
            Error = state is null ? new HttpRequestException("arm unavailable") : null
        };
        var (service, _) = CreateOutOfBandRecoverServices(db, observer, Now.AddMinutes(4));
        var beforeAssignment = await db.AzureProviderResourceAssignments.AsNoTracking().SingleAsync();
        var beforeVersion = (await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == fixture.Instance.Id)).Version;

        var conflict = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            service.RecoverAsync(new(
                fixture.Workspace.Id,
                fixture.Instance.Id,
                fixture.Instance.Version,
                "recover-oob-rg-error")));

        Assert.Equal(ElsaInstanceLifecycleConflictReason.RecoveryAuthorityUnavailable, conflict.Reason);
        Assert.Single(observer.Calls);
        var afterAssignment = await db.AzureProviderResourceAssignments.AsNoTracking().SingleAsync();
        Assert.Equal(beforeAssignment.WorkloadResourceId, afterAssignment.WorkloadResourceId);
        Assert.Equal(beforeAssignment.State, afterAssignment.State);
        Assert.Equal(beforeVersion,
            (await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == fixture.Instance.Id)).Version);
        Assert.Equal(0, await db.ElsaInstanceAuditEvents.CountAsync(x =>
            x.EventType == "lifecycle.delete-out-of-band-resource-group-absent"));
    }

    private static (ElsaInstanceLifecycleService Service, AzureElsaInstanceProvider Provider) CreateOutOfBandRecoverServices(
        CatalogDbContext db,
        IAzureEngineResourceGroupAbsenceObserver observer,
        DateTimeOffset now)
    {
        var operationStore = new AzureProviderOperationStore(db);
        var provider = new AzureElsaInstanceProvider(
            new AzureProviderOperationService(operationStore, new FixedTimeProvider(now)),
            operationStore,
            operationStore,
            timeProvider: new FixedTimeProvider(now),
            options: new AzureElsaInstanceProviderOptions
            {
                Enabled = true,
                TemplateFingerprint = new string('b', 64),
                ProviderScopeFingerprint = new string('a', 64),
                SubscriptionId = "11111111-1111-1111-1111-111111111111",
                ResourceGroupNamePrefix = "rg-correlation"
            },
            engineResourceGroupObserver: observer);
        var service = new ElsaInstanceLifecycleService(
            CreateStore(db),
            new FixedTimeProvider(now),
            staleDeleteInventory: provider);
        return (service, provider);
    }

    private sealed class StubEngineResourceGroupObserver : IAzureEngineResourceGroupAbsenceObserver
    {
        public AzureProviderFreshResourceGroupState State { get; set; }
        public Exception? Error { get; set; }
        public List<(string SubscriptionId, string ResourceGroupName)> Calls { get; } = [];

        public Task<AzureProviderFreshResourceGroupState> ObserveAsync(
            string subscriptionId,
            string resourceGroupName,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((subscriptionId, resourceGroupName));
            if (Error is not null)
                throw Error;
            return Task.FromResult(State);
        }
    }
}
