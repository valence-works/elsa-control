using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed partial class ElsaInstanceLifecycleStoreTests
{
    [PosixFact]
    public async Task Delete_recovery_claim_rejects_a_stale_lifecycle_version_without_mutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        await using var fixture = await SeedDeleteRecoveryClaimAsync(db);

        var before = await fixture.OperationStore.GetAsync(
            fixture.WorkspaceId, fixture.ProviderOperationId);
        var result = await fixture.Store.ClaimDeleteRecoveryAsync(
            fixture.Request with { InstanceVersion = fixture.Request.InstanceVersion + 1 },
            TimeSpan.FromMinutes(5), fixture.Now);

        Assert.Null(result);
        var after = await fixture.OperationStore.GetAsync(fixture.WorkspaceId, fixture.ProviderOperationId);
        AssertClaimDidNotMutate(before, after);
    }

    [PosixFact]
    public async Task Delete_recovery_claim_rejects_a_stale_lifecycle_lease_without_mutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        await using var fixture = await SeedDeleteRecoveryClaimAsync(db);

        var before = await fixture.OperationStore.GetAsync(
            fixture.WorkspaceId, fixture.ProviderOperationId);
        var result = await fixture.Store.ClaimDeleteRecoveryAsync(
            fixture.Request with { LeaseVersion = fixture.Request.LeaseVersion + 1 },
            TimeSpan.FromMinutes(5), fixture.Now);

        Assert.Null(result);
        var after = await fixture.OperationStore.GetAsync(fixture.WorkspaceId, fixture.ProviderOperationId);
        AssertClaimDidNotMutate(before, after);
    }

    [PosixFact]
    public async Task Delete_recovery_claim_rejects_a_foreign_recovery_ledger_identity_without_mutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        await using var fixture = await SeedDeleteRecoveryClaimAsync(db);
        await using var foreign = await SeedDeleteRecoveryClaimAsync(db);

        var before = await fixture.OperationStore.GetAsync(
            fixture.WorkspaceId, fixture.ProviderOperationId);
        var foreignBefore = await foreign.OperationStore.GetAsync(foreign.WorkspaceId, foreign.ProviderOperationId);
        var result = await fixture.Store.ClaimDeleteRecoveryAsync(
            fixture.Request with { RecoveryRequestId = foreign.Request.RecoveryRequestId },
            TimeSpan.FromMinutes(5), fixture.Now);

        Assert.Null(result);
        var after = await fixture.OperationStore.GetAsync(fixture.WorkspaceId, fixture.ProviderOperationId);
        AssertClaimDidNotMutate(before, after);
        AssertClaimDidNotMutate(foreignBefore,
            await foreign.OperationStore.GetAsync(foreign.WorkspaceId, foreign.ProviderOperationId));
    }

    [PosixFact]
    public async Task Delete_recovery_claim_rejects_a_stale_provider_version_without_mutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        await using var fixture = await SeedDeleteRecoveryClaimAsync(db);

        var provider = await db.AzureProviderOperations.SingleAsync(x => x.Id == fixture.ProviderOperationId);
        provider.Version++;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await fixture.Store.ClaimDeleteRecoveryAsync(
            fixture.Request, TimeSpan.FromMinutes(5), fixture.Now);

        Assert.Null(result);
        var after = await fixture.OperationStore.GetAsync(fixture.WorkspaceId, fixture.ProviderOperationId);
        Assert.NotNull(after);
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, after!.Status);
        Assert.Equal(fixture.ProviderVersion + 1, after.Version);
    }

    [PosixFact]
    public async Task Delete_recovery_claim_is_single_use_when_uncertainty_is_repeated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        await using var fixture = await SeedDeleteRecoveryClaimAsync(db);

        var first = await fixture.Store.ClaimDeleteRecoveryAsync(
            fixture.Request, TimeSpan.FromMinutes(5), fixture.Now);
        Assert.NotNull(first);
        var uncertain = await fixture.OperationStore.FinalizeAsync(
            fixture.WorkspaceId, fixture.ProviderOperationId, fixture.Request.LeaseToken,
            AzureProviderOperationStatus.RecoveryRequired, "cleanup.uncertain",
            fixture.Now.AddSeconds(1), first.Version);
        Assert.NotNull(uncertain);
        var second = await fixture.Store.ClaimDeleteRecoveryAsync(
            fixture.Request, TimeSpan.FromMinutes(5), fixture.Now.AddMinutes(1));

        Assert.NotNull(first);
        Assert.Null(second);
        var persisted = await fixture.OperationStore.GetAsync(fixture.WorkspaceId, fixture.ProviderOperationId);
        Assert.NotNull(persisted);
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, persisted!.Status);
        Assert.Equal(uncertain.Version, persisted.Version);
        Assert.Equal(fixture.ProviderAttemptNumber + 1, persisted.AttemptNumber);
        Assert.Equal(1, await db.AzureProviderOperationTransitions.AsNoTracking()
            .CountAsync(x => x.OperationId == fixture.ProviderOperationId &&
                             x.Code == "operation.delete-recovery.claimed"));
    }

    [PosixFact]
    public async Task Concurrent_delete_recovery_claims_mutate_one_provider_operation()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"elsa-control-delete-claim-{Guid.NewGuid():N}.db");
        DeleteClaimFixture? fixture = null;
        try
        {
            await using (var setupConnection = new SqliteConnection(
                             $"Data Source={databasePath};Pooling=False;Default Timeout=30"))
            {
                await setupConnection.OpenAsync();
                await using var setupDb = CreateMigratedContext(setupConnection);
                await setupDb.Database.MigrateAsync();
                fixture = await SeedDeleteRecoveryClaimAsync(setupDb);
            }
            var claimFixture = fixture ?? throw new InvalidOperationException("The claim fixture was not created.");

            await using var firstDb = CreateMigratedContext(databasePath);
            await using var secondDb = CreateMigratedContext(databasePath);
            var firstStore = (IAzureProviderDeleteRecoveryStore)new AzureProviderOperationStore(firstDb);
            var secondStore = (IAzureProviderDeleteRecoveryStore)new AzureProviderOperationStore(secondDb);
            using var start = new Barrier(2);
            Task<AzureProviderOperation?> RaceClaimAsync(IAzureProviderDeleteRecoveryStore store) =>
                Task.Run(async () =>
                {
                    Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(30)));
                    return await store.ClaimDeleteRecoveryAsync(
                        claimFixture.Request, TimeSpan.FromMinutes(5), claimFixture.Now);
                });
            var first = RaceClaimAsync(firstStore);
            var second = RaceClaimAsync(secondStore);
            var results = await Task.WhenAll(first, second);

            Assert.Single(results, x => x is not null);
            await using var verificationDb = CreateMigratedContext(databasePath);
            var persisted = await verificationDb.AzureProviderOperations.AsNoTracking()
                .SingleAsync(x => x.Id == claimFixture.ProviderOperationId);
            Assert.Equal(AzureProviderOperationStatus.Running, persisted.Status);
            Assert.Equal(claimFixture.ProviderVersion + 1, persisted.Version);
            Assert.Equal(1, await verificationDb.AzureProviderOperationTransitions.AsNoTracking()
                .CountAsync(x => x.OperationId == claimFixture.ProviderOperationId &&
                                 x.Code == "operation.delete-recovery.claimed"));
        }
        finally
        {
            if (fixture is not null)
                await fixture.DisposeAsync();
            DeleteDatabase(databasePath);
        }
    }

    private static async Task<DeleteClaimFixture> SeedDeleteRecoveryClaimAsync(CatalogDbContext db)
    {
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Delete claim authority workspace");
        await CompleteManagedRunAsync(db, accepted.Operation.Id, accepted.Instance.Id);
        var scope = new AzureProviderTargetScope(
            "11111111-1111-1111-1111-111111111111",
            "rg-delete-claim",
            "22222222-2222-2222-2222-222222222222",
            "registry-rg",
            "registry1",
            "westeurope");
        var tools = await CreateCleanupToolsAsync(scope, accepted.Instance.Id);
        var runnerOptions = tools.Options;
        var providerOptions = new AzureElsaInstanceProviderOptions
        {
            Enabled = true,
            TemplateFingerprint = runnerOptions.ComputeTemplateAuthorityFingerprint(),
            ProviderScopeFingerprint = runnerOptions.ComputeProviderScopeFingerprint(scope),
            SubscriptionId = scope.SubscriptionId,
            ResourceGroupNamePrefix = scope.ResourceGroupName
        };

        var operationStore = new AzureProviderOperationStore(db);
        var provider = CreateProvider(operationStore, providerOptions, Now.AddMinutes(2));
        var run = await db.DeploymentRuns.AsNoTracking().SingleAsync(x => x.ElsaInstanceId == accepted.Instance.Id);
        var target = new ElsaInstanceLifecycleDeploymentTarget(
            run.ApplicationId, run.EnvironmentId, run.EngineId, run.SourceRevisionId,
            run.ConfirmationId, run.ActorAccountId);
        var resolved = AzureProviderResolution(workspace.Id, accepted.Instance.Id);
        await provider.SubmitAsync(new(
            workspace.Id, accepted.Instance.Id, accepted.Operation.Id, accepted.Operation.AttemptNumber,
            ElsaDesiredLifecycle.Running, resolved.Plan!, target, scope.Location, workspace.OrganizationId,
            ElsaInstanceOperationAction.Reconcile, accepted.Operation.Id.ToString("D")));
        var reconcile = Assert.IsType<AzureProviderOperation>(await operationStore.GetLatestReconcileAsync(
            workspace.Id, $"e{accepted.Instance.Id:N}"[..16], providerOptions.ProviderScopeFingerprint));
        var assignmentId = Assert.IsType<Guid>(reconcile.ProviderAssignmentId);
        var assignment = Assert.IsType<AzureProviderResourceAssignment>(await
            ((IAzureProviderResourceAssignmentStore)operationStore).GetAsync(workspace.Id, assignmentId));
        var claimed = Assert.IsType<AzureProviderOperation>(await operationStore.ClaimAsync(
            workspace.Id, reconcile.Id, "delete-claim-setup", "delete-claim-setup-lease",
            TimeSpan.FromMinutes(5), Now.AddMinutes(2), reconcile.Version));
        var checkpointed = Assert.IsType<AzureProviderOperation>(await operationStore.CheckpointAsync(
            workspace.Id, reconcile.Id, "delete-claim-setup-lease",
            new(AzureProviderOperationPhase.HealthVerified, "health.verified",
                "The retained provider workload is healthy.", new(ResourceGroupName: assignment.ResourceGroupName),
                "https://runtime.example.test", AzureProviderHealth.Healthy, []),
            Now.AddMinutes(2), claimed.Version));
        _ = await operationStore.FinalizeAsync(
            workspace.Id, reconcile.Id, "delete-claim-setup-lease", AzureProviderOperationStatus.Succeeded,
            "operation.succeeded", Now.AddMinutes(2), checkpointed.Version);
        var instance = await db.ElsaInstances.SingleAsync(x => x.Id == accepted.Instance.Id);
        instance.PlacementAssignmentId = assignment.Id.ToString("D");
        instance.Version++;
        await db.SaveChangesAsync();

        var current = Assert.IsType<ElsaInstance>(await CreateStore(db).GetInstanceAsync(
            workspace.Id, accepted.Instance.Id));
        var deletion = await new ElsaInstanceLifecycleService(
                CreateStore(db), new FixedTimeProvider(Now.AddMinutes(3)))
            .DeleteAsync(await CreateConfirmedDeleteRequestAsync(
                db, workspace.Id, accepted.Instance.Id, current.Version, "delete-claim", Now.AddMinutes(3)));
        var deletionWorker = new ElsaInstanceDeletionWorker(
            new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance,
                new FixedTimeProvider(Now.AddMinutes(3))),
            CreateProvider(operationStore, providerOptions, Now.AddMinutes(3)),
            new FixedTimeProvider(Now.AddMinutes(3)));
        Assert.Empty((await deletionWorker.ProcessAvailableAsync("delete-claim-worker")).Results);
        var deleteOperation = await db.AzureProviderOperations.AsNoTracking()
            .SingleAsync(x => x.Action == AzureProviderOperationAction.Delete &&
                x.WorkspaceId == workspace.Id && x.InstanceId == accepted.Instance.Id);

        Assert.Equal(1, await CreateProviderWorker(operationStore, runnerOptions, scope, Now.AddMinutes(4))
            .ProcessOnceAsync());
        var lifecycleWorker = new ElsaInstanceDeletionWorker(
            new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance,
                new FixedTimeProvider(Now.AddMinutes(5))),
            CreateProvider(operationStore, providerOptions, Now.AddMinutes(5)),
            new FixedTimeProvider(Now.AddMinutes(5)));
        var failed = await lifecycleWorker.ProcessAvailableAsync("delete-claim-worker");
        Assert.Single(failed.Results);
        var service = new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now.AddMinutes(6)));
        current = Assert.IsType<ElsaInstance>(await CreateStore(db).GetInstanceAsync(workspace.Id, accepted.Instance.Id));
        var recovered = await service.RecoverAsync(new(
            workspace.Id, accepted.Instance.Id, current.Version, "delete-claim-recovery"));
        Assert.Equal(deletion.Operation.Id, recovered.Operation.Id);

        var claimStore = new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance,
            new FixedTimeProvider(Now.AddMinutes(7)));
        var item = Assert.IsType<ElsaInstanceDeletionWorkItem>(await claimStore.TryClaimNextDeletionAsync(
            "delete-claim-worker", Now.AddMinutes(7)));
        var recovery = await db.ElsaInstanceRecoveryRequests.AsNoTracking()
            .SingleAsync(x => x.OperationId == deletion.Operation.Id);
        var authority = Assert.IsType<AzureProviderDeleteRecoveryAuthority>(
            ParseAuthority(recovery.AzureDeleteRecoveryAuthority));
        var request = new AzureProviderDeleteRecoveryClaimRequest(
            recovery.Id, workspace.Id, accepted.Instance.Id, deletion.Operation.Id,
            recovery.AttemptNumber, item.Instance.Version, "delete-claim-worker", item.LeaseToken, item.LeaseVersion);
        return new(
            workspace.Id, accepted.Instance.Id, deleteOperation.Id, request, authority.ProviderVersion,
            authority.ProviderAttemptNumber, operationStore, (IAzureProviderDeleteRecoveryStore)operationStore,
            Now.AddMinutes(7), tools);
    }

    private static AzureProviderDeleteRecoveryAuthority ParseAuthority(string? serialized)
    {
        Assert.True(AzureProviderDeleteRecoveryAuthority.TryParse(serialized, out var authority));
        return authority!;
    }

    private static void AssertClaimDidNotMutate(
        AzureProviderOperation? before,
        AzureProviderOperation? after)
    {
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before!.Id, after!.Id);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Phase, after.Phase);
        Assert.Equal(before.AttemptNumber, after.AttemptNumber);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.CheckpointSequence, after.CheckpointSequence);
        Assert.Equal(before.WorkerId, after.WorkerId);
        Assert.Equal(before.LeaseExpiresAt, after.LeaseExpiresAt);
        Assert.Equal(before.HeartbeatAt, after.HeartbeatAt);
    }

    private sealed record DeleteClaimFixture(
        Guid WorkspaceId,
        Guid InstanceId,
        Guid ProviderOperationId,
        AzureProviderDeleteRecoveryClaimRequest Request,
        long ProviderVersion,
        int ProviderAttemptNumber,
        AzureProviderOperationStore OperationStore,
        IAzureProviderDeleteRecoveryStore Store,
        DateTimeOffset Now,
        CleanupTools Tools) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Tools.DisposeAsync();
    }

    private static void DeleteDatabase(string databasePath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static CatalogDbContext CreateMigratedContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite($"Data Source={databasePath};Pooling=False;Default Timeout=30", sqlite =>
                sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        return new CatalogDbContext(options);
    }
}
