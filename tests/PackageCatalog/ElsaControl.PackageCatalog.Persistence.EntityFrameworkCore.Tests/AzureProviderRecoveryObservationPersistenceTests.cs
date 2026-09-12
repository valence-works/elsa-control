using System.Data.Common;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed partial class AzureProviderRecoveryObservationPersistenceTests
{
    [Theory]
    [InlineData(AzureProviderRunnerStep.SqlFirewallCreate, AzureProviderOperationPhase.SeedSecretsObserved, AzureProviderRunnerStep.SqlFirewallCreate, AzureProviderOperationPhase.SqlFirewallReady, true)]
    [InlineData(AzureProviderRunnerStep.SqlBootstrapScript, AzureProviderOperationPhase.SqlFirewallReady, AzureProviderRunnerStep.SqlBootstrapScript, AzureProviderOperationPhase.SqlBootstrapReady, true)]
    [InlineData(AzureProviderRunnerStep.SqlFirewallCleanup, AzureProviderOperationPhase.SqlBootstrapReady, AzureProviderRunnerStep.SqlFirewallCleanup, AzureProviderOperationPhase.FoundationReady, true)]
    [InlineData(AzureProviderRunnerStep.SqlFirewallCleanup, AzureProviderOperationPhase.SqlBootstrapReady, AzureProviderRunnerStep.SqlBootstrapScript, AzureProviderOperationPhase.SqlBootstrapReady, true)]
    [InlineData(AzureProviderRunnerStep.SqlBootstrapScript, AzureProviderOperationPhase.SqlFirewallReady, AzureProviderRunnerStep.SqlFirewallCleanup, AzureProviderOperationPhase.FoundationReady, false)]
    [InlineData(AzureProviderRunnerStep.SqlFirewallCreate, AzureProviderOperationPhase.SeedSecretsObserved, AzureProviderRunnerStep.SqlBootstrapScript, AzureProviderOperationPhase.SqlBootstrapReady, false)]
    public async Task Sql_recovery_ledger_accepts_only_the_exact_durable_stage_or_proven_cleanup_replay(
        AzureProviderRunnerStep attemptedStep,
        AzureProviderOperationPhase currentPhase,
        AzureProviderRunnerStep completedStep,
        AzureProviderOperationPhase observedPhase,
        bool accepted)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var fixture = await SeedProviderObservationAsync(db);
        var operation = await db.AzureProviderOperations.SingleAsync(x => x.Id == fixture.Operation.Id);
        operation.AttemptedStep = attemptedStep;
        operation.Phase = currentPhase;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var observation = fixture.Observation with { CompletedStep = completedStep, ObservedPhase = observedPhase };
        var store = (IAzureProviderRecoveryObservationStore)fixture.OperationStore;

        if (!accepted)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateOrGetAsync(observation));
            Assert.Empty(await db.AzureProviderRecoveryObservations.ToListAsync());
            return;
        }

        var receipt = await store.CreateOrGetAsync(observation);
        var replay = await store.CreateOrGetAsync(observation);
        Assert.Equal(receipt.RecordId, replay.RecordId);
        Assert.Equal(receipt.Digest, replay.Digest);
        var retained = await store.GetAndValidateRecordedAsync(
            observation.OrganizationId, observation.WorkspaceId, observation.InstanceId,
            observation.LifecycleOperationId, observation.ObservedLifecycleAttemptNumber,
            receipt.Reference, receipt.Digest);
        Assert.NotNull(retained);
        Assert.Equal(completedStep, retained.CompletedStep);
        Assert.Equal(observedPhase, retained.ObservedPhase);
    }

    [Fact]
    public async Task Checkpoints_follow_explicit_phase_order_and_reject_backwards_observations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var fixture = await SeedProviderObservationAsync(db);
        var store = fixture.OperationStore;
        var operation = Assert.IsType<AzureProviderOperation>(await store.ClaimRecoveryAsync(
            fixture.Workspace.Id, fixture.Operation.Id, "phase-worker", "phase-lease",
            TimeSpan.FromMinutes(5), fixture.Observation.ObservedAt, fixture.Operation.Version));
        foreach (var phase in new[]
                 {
                     AzureProviderOperationPhase.FoundationSubmitted,
                     AzureProviderOperationPhase.FoundationObserved,
                     AzureProviderOperationPhase.AcrPullObserved,
                     AzureProviderOperationPhase.SeedSecretsObserved,
                     AzureProviderOperationPhase.SqlFirewallReady,
                     AzureProviderOperationPhase.SqlBootstrapReady,
                     AzureProviderOperationPhase.FoundationReady
                 })
        {
            operation = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
                fixture.Workspace.Id, operation.Id, "phase-lease",
                new(phase, "azure.phase.observed", "The provider phase was observed.",
                    operation.Resources, operation.Endpoint, operation.Health, []),
                fixture.Observation.ObservedAt, operation.Version));
            db.ChangeTracker.Clear();
            var persisted = await store.GetAsync(fixture.Workspace.Id, operation.Id);
            Assert.Equal(phase, persisted!.Phase);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CheckpointAsync(
            fixture.Workspace.Id, operation.Id, "phase-lease",
            new(AzureProviderOperationPhase.FoundationObserved, "azure.phase.observed",
                "The provider phase was observed.", operation.Resources, operation.Endpoint, operation.Health, []),
            fixture.Observation.ObservedAt, operation.Version));
        db.ChangeTracker.Clear();
        Assert.Equal(AzureProviderOperationPhase.FoundationReady,
            (await store.GetAsync(fixture.Workspace.Id, operation.Id))!.Phase);
    }

    [Fact]
    public async Task Attempted_provider_step_round_trips_through_sqlite_and_survives_uncertain_finalize()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db);
        var store = fixture.OperationStore;
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimRecoveryAsync(
            fixture.Workspace.Id,
            fixture.Operation.Id,
            "marker-worker",
            "marker-lease",
            TimeSpan.FromMinutes(5),
            fixture.Observation.ObservedAt,
            fixture.Operation.Version));
        var marked = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
            fixture.Workspace.Id,
            fixture.Operation.Id,
            "marker-lease",
            new(
                claimed.Phase,
                "azure.step.attempted",
                "The Azure lifecycle step was marked before its remote call.",
                claimed.Resources,
                claimed.Endpoint,
                claimed.Health,
                [],
                AttemptedStep: AzureProviderRunnerStep.AcrPull),
            fixture.Observation.ObservedAt,
            claimed.Version));

        var finalized = Assert.IsType<AzureProviderOperation>(await store.FinalizeAsync(
            fixture.Workspace.Id,
            fixture.Operation.Id,
            "marker-lease",
            AzureProviderOperationStatus.RecoveryRequired,
            "azure.step.uncertain",
            fixture.Observation.ObservedAt,
            marked.Version));

        db.ChangeTracker.Clear();
        var reloaded = await store.GetAsync(fixture.Workspace.Id, fixture.Operation.Id);
        Assert.Equal(AzureProviderRunnerStep.AcrPull, finalized.AttemptedStep);
        Assert.Equal(AzureProviderRunnerStep.AcrPull, reloaded!.AttemptedStep);
    }

    [Fact]
    public async Task Recovery_observation_is_immutable_and_unchanged_polls_replay_the_original_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db);
        var workspace = fixture.Workspace;
        var instanceId = fixture.InstanceId;
        var lifecycleOperationId = fixture.LifecycleOperationId;
        var resolvedPlan = fixture.ResolvedPlan;
        var operation = fixture.Operation;
        var now = DateTimeOffset.Parse("2026-09-05T17:00:00Z");
        var operationStore = fixture.OperationStore;
        var assignment = fixture.Assignment;
        var observation = fixture.Observation with { ObservedAt = now };
        var observationStore = (IAzureProviderRecoveryObservationStore)operationStore;
        var first = await observationStore.CreateOrGetAsync(observation);
        var second = await observationStore.CreateOrGetAsync(observation with
        {
            ObservedAt = now.AddMinutes(5)
        });

        Assert.Equal(first.RecordId, second.RecordId);
        Assert.Equal(first.Reference, second.Reference);
        Assert.Equal(first.Digest, second.Digest);
        Assert.Equal(1, await db.AzureProviderRecoveryObservations.CountAsync());
        Assert.Equal(first.Digest, observation.ComputeRecordDigest(first.RecordId));
        Assert.True(ElsaInstanceProviderRecoveryObservationReference.TryParse(
            first.Reference, out var referenceId, out var referenceDigest));
        Assert.Equal(first.RecordId, referenceId);
        Assert.Equal(first.Digest, referenceDigest);

        var loaded = await observationStore.GetAndValidateRecordedAsync(
            workspace.OrganizationId,
            workspace.Id,
            instanceId,
            lifecycleOperationId,
            observation.ObservedLifecycleAttemptNumber,
            first.Reference,
            first.Digest);
        Assert.NotNull(loaded);
        Assert.Equal(observation.PostconditionFingerprint, loaded!.PostconditionFingerprint);

        var persisted = await db.AzureProviderRecoveryObservations.SingleAsync();
        persisted.PostconditionFingerprint = new string('f', 64);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE AzureProviderRecoveryObservations SET PostconditionFingerprint = 'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff'"));
    }

    [Fact]
    public async Task Concurrent_natural_key_inserts_return_one_immutable_receipt_after_both_reads_race()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"elsa-control-recovery-observation-{Guid.NewGuid():N}.db");
        try
        {
            ObservationFixture fixture;
            await using (var setupConnection = new SqliteConnection($"Data Source={databasePath};Pooling=False;Default Timeout=30"))
            {
                await setupConnection.OpenAsync();
                await using var setupDb = CreateMigratedContext(setupConnection);
                await setupDb.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
                await setupDb.Database.MigrateAsync();
                fixture = await SeedProviderObservationAsync(setupDb);
            }

            var barrier = new NaturalKeyReadBarrier(2);
            await using var firstDb = CreateMigratedContext(databasePath, barrier);
            await using var secondDb = CreateMigratedContext(databasePath, barrier);
            var firstStore = (IAzureProviderRecoveryObservationStore)new AzureProviderOperationStore(firstDb);
            var secondStore = (IAzureProviderRecoveryObservationStore)new AzureProviderOperationStore(secondDb);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var observation = fixture.Observation with
            {
                ObservedAt = fixture.Observation.ObservedAt.AddMinutes(1)
            };

            var receipts = await Task.WhenAll(
                firstStore.CreateOrGetAsync(observation, timeout.Token),
                secondStore.CreateOrGetAsync(observation with { ObservedAt = observation.ObservedAt.AddMinutes(1) }, timeout.Token));

            Assert.Equal(2, barrier.NaturalKeyBarrierArrivals);
            Assert.Equal(2, barrier.ObservationInsertAttempts);
            Assert.Equal(receipts[0].RecordId, receipts[1].RecordId);
            Assert.Equal(receipts[0].Reference, receipts[1].Reference);
            Assert.Equal(receipts[0].Digest, receipts[1].Digest);

            await using var verificationDb = CreateMigratedContext(databasePath);
            Assert.Equal(1, await verificationDb.AzureProviderRecoveryObservations.CountAsync());
            var persisted = await verificationDb.AzureProviderRecoveryObservations.SingleAsync();
            Assert.Equal(receipts[0].RecordId, persisted.Id);
            Assert.Equal(receipts[0].Digest, persisted.RecordDigest);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Recovery_observation_rejects_stale_provider_and_lifecycle_authority()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db);
        var store = (IAzureProviderRecoveryObservationStore)fixture.OperationStore;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateOrGetAsync(
            fixture.Observation with { ProviderVersion = fixture.Operation.Version - 1 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateOrGetAsync(
            fixture.Observation with { ObservedLifecycleAttemptNumber = 2 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateOrGetAsync(
            fixture.Observation with { ProviderRequestHash = new string('9', 64) }));
        Assert.Equal(0, await db.AzureProviderRecoveryObservations.CountAsync());
    }

    [Theory]
    [InlineData("NaturalKey", false)]
    [InlineData("RecordDigest", false)]
    [InlineData("RecordDigest", true)]
    public async Task Recovery_observation_rejects_corrupted_derived_fields(string field, bool replay)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var fixture = await SeedProviderObservationAsync(db);
        var store = (IAzureProviderRecoveryObservationStore)fixture.OperationStore;
        var receipt = await store.CreateOrGetAsync(fixture.Observation);

        // Simulate a damaged retained row, bypassing the separately tested append-only trigger.
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER TR_AzureProviderRecoveryObservations_AppendOnly_Update");
        if (field == "NaturalKey")
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AzureProviderRecoveryObservations SET NaturalKey = {new string('f', 64)} WHERE Id = {receipt.RecordId}");
        else
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AzureProviderRecoveryObservations SET RecordDigest = {"sha256:" + new string('f', 64)} WHERE Id = {receipt.RecordId}");
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (replay)
                await store.CreateOrGetAsync(fixture.Observation);
            else
                await store.GetAndValidateRecordedAsync(
                    fixture.Workspace.OrganizationId, fixture.Workspace.Id, fixture.InstanceId,
                    fixture.LifecycleOperationId, fixture.Observation.ObservedLifecycleAttemptNumber,
                    receipt.Reference, receipt.Digest);
        });
    }

    [Fact]
    public async Task Recovery_observation_rejects_assignment_owned_by_another_operation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var fixture = await SeedProviderObservationAsync(db);
        var assignment = await db.AzureProviderResourceAssignments.SingleAsync();
        assignment.LastOperationId = Guid.NewGuid();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IAzureProviderRecoveryObservationStore)fixture.OperationStore).CreateOrGetAsync(fixture.Observation));
        Assert.Equal(0, await db.AzureProviderRecoveryObservations.CountAsync());
    }

    [Fact]
    public async Task Recovery_observation_rejects_a_retained_plan_that_does_not_translate_to_provider_plan()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db);
        var mismatchedFingerprint = new string('1', 64);
        var mismatchedRequest = CreateOperationRequest(fixture.Operation, mismatchedFingerprint);
        var mismatchedRequestHash = AzureProviderOperationValidation.ComputeRequestHash(mismatchedRequest);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AzureProviderOperations SET PlanFingerprint = {mismatchedFingerprint}, RequestHash = {mismatchedRequestHash} WHERE Id = {fixture.Operation.Id}");
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IAzureProviderRecoveryObservationStore)fixture.OperationStore).CreateOrGetAsync(
                fixture.Observation with
                {
                    ProviderPlanFingerprint = mismatchedFingerprint,
                    ProviderRequestHash = mismatchedRequestHash
                }));
        Assert.Equal(0, await db.AzureProviderRecoveryObservations.CountAsync());
    }

    [Fact]
    public async Task Recovery_observation_natural_key_allows_distinct_postconditions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db);
        var store = (IAzureProviderRecoveryObservationStore)fixture.OperationStore;
        var first = await store.CreateOrGetAsync(fixture.Observation);
        var second = await store.CreateOrGetAsync(fixture.Observation with
        {
            PostconditionFingerprint = new string('c', 64),
            ObservedAt = fixture.Observation.ObservedAt.AddMinutes(1)
        });

        Assert.NotEqual(first.RecordId, second.RecordId);
        Assert.NotEqual(first.Observation.ComputeNaturalKey(), second.Observation.ComputeNaturalKey());
        Assert.Equal(2, await db.AzureProviderRecoveryObservations.CountAsync());
    }

    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(false, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1)]
    [InlineData(true, true, 2)]
    public async Task Recovery_acceptance_requires_observation_store_and_binds_pre_post_versions(
        bool storeAvailable, bool downgradeReference, int authorityMode)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db);
        var observationStore = (IAzureProviderRecoveryObservationStore)fixture.OperationStore;
        await AddLifecycleRunAsync(db, fixture);
        var lifecycleStore = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance,
            new FixedTimeProvider(fixture.Observation.ObservedAt.AddMinutes(1)),
            recoveryObservationStore: observationStore);
        var target = Assert.IsType<ElsaInstanceProviderReconciliationTarget>(
            await lifecycleStore.GetTargetAsync(fixture.Workspace.Id, fixture.LifecycleOperationId));
        var observation = fixture.Observation with { ObservedInstanceVersion = target.Instance.Version };
        var receipt = await observationStore.CreateOrGetAsync(observation);
        await lifecycleStore.CommitAsync(new(
            fixture.Workspace.Id, fixture.InstanceId, fixture.LifecycleOperationId,
            target.Instance.Version, target.Operation.AttemptNumber, target.ReconciliationVersion,
            new string('7', 64), target.Instance, target.Operation,
            ElsaInstanceProviderReconciliationService.RetrySafeCode, true,
            receipt.Reference, receipt.Digest, observation.ObservedAt.AddMinutes(1)));
        var reconciled = Assert.IsType<ElsaInstance>(
            await lifecycleStore.GetInstanceAsync(fixture.Workspace.Id, fixture.InstanceId));
        Assert.Equal(observation.ObservedInstanceVersion + 1, reconciled.Version);
        var recoveryKey = "recover-observation-test";
        if (downgradeReference)
        {
            var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == fixture.LifecycleOperationId);
            operation.ReconciliationRetryEvidenceReference = "https://evidence.example.test/generic-retry";
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            if (authorityMode == 1)
            {
                // The bound assignment remains the only Azure authority when the
                // retained operation key is no longer correlated to this lifecycle.
                const string unrelatedOperationKey = "provider-unrelated-operation";
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AzureProviderOperations SET IdempotencyKey = {unrelatedOperationKey} WHERE Id = {fixture.Operation.Id}");
            }
            else if (authorityMode == 2)
            {
                // A malformed correlated operation must still require the ledger;
                // its workspace/key marker is not enough to authorize a downgrade.
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AzureProviderOperations SET OrganizationId = {Guid.NewGuid()}, InstanceId = {Guid.NewGuid()} WHERE Id = {fixture.Operation.Id}");
            }
            db.ChangeTracker.Clear();
            var error = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
                new ElsaInstanceLifecycleService(lifecycleStore)
                    .RecoverAsync(new(fixture.Workspace.Id, fixture.InstanceId, reconciled.Version, recoveryKey)));
            Assert.Equal("Provider reconciliation has not established an opaque retry observation.", error.Message);
            Assert.Equal(0, await db.ElsaInstanceRecoveryRequests.CountAsync());
            return;
        }
        if (!storeAvailable)
        {
            var unavailableStore = new EfCoreElsaInstanceLifecycleStore(
                db, EmptyResolutionInputSource.Instance,
                new FixedTimeProvider(observation.ObservedAt.AddMinutes(2)));
            var error = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
                new ElsaInstanceLifecycleService(unavailableStore)
                    .RecoverAsync(new(fixture.Workspace.Id, fixture.InstanceId, reconciled.Version, recoveryKey)));
            Assert.Equal("Provider reconciliation retry observation storage is unavailable.", error.Message);
            Assert.Equal(0, await db.ElsaInstanceRecoveryRequests.CountAsync());
            var unchanged = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync();
            Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, unchanged.State);
            Assert.Equal(observation.ObservedLifecycleAttemptNumber, unchanged.AttemptNumber);
            return;
        }
        var accepted = await new ElsaInstanceLifecycleService(
                lifecycleStore, new FixedTimeProvider(observation.ObservedAt.AddMinutes(2)))
            .RecoverAsync(new(fixture.Workspace.Id, fixture.InstanceId, reconciled.Version, recoveryKey));
        Assert.Equal(reconciled.Version + 1, accepted.Instance.Version);
        db.ChangeTracker.Clear();
        var recovery = await db.ElsaInstanceRecoveryRequests.AsNoTracking().SingleAsync();

        var binding = new AzureProviderRecoveryObservationBinding(
            recovery.Id,
            fixture.Workspace.OrganizationId,
            fixture.Workspace.Id,
            fixture.InstanceId,
            fixture.LifecycleOperationId,
            observation.ObservedLifecycleAttemptNumber,
            reconciled.Version,
            accepted.Operation.AttemptNumber,
            accepted.Instance.Version,
            recovery.IdempotencyScope,
            recoveryKey,
            recovery.RequestHash,
            receipt.Reference,
            receipt.Digest);
        var consumed = await observationStore.GetAndValidateForAcceptedRecoveryAsync(binding);

        Assert.NotNull(consumed);
        Assert.Equal(observation.ObservedInstanceVersion, consumed!.ObservedInstanceVersion);
        Assert.Equal(1, consumed.ObservedLifecycleAttemptNumber);

        var pending = Assert.Single(await lifecycleStore.ListPendingProviderOperationsAsync(10));
        Assert.NotNull(pending.Submission);
        Assert.NotNull(pending.Recovery);
        Assert.False(pending.HandoffInvalid);

        // Simulate storage corruption outside the ordinary append-only boundary.
        // Partial recovery metadata must never downgrade into ordinary submission.
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER TR_ElsaInstanceRecoveryRequests_AppendOnly_Update");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ElsaInstanceRecoveryRequests SET RecoveryObservationReference = NULL WHERE Id = {recovery.Id}");
        db.ChangeTracker.Clear();
        pending = Assert.Single(await lifecycleStore.ListPendingProviderOperationsAsync(10));
        Assert.Null(pending.Submission);
        Assert.Null(pending.Recovery);
        Assert.True(pending.HandoffInvalid);
    }

    [Theory]
    [InlineData(AzureProviderOperationStatus.Running)]
    [InlineData(AzureProviderOperationStatus.Succeeded)]
    public async Task Accepted_recovery_replay_accepts_the_exact_claimed_provider_successor(
        AzureProviderOperationStatus postClaimStatus)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db);
        var observationStore = (IAzureProviderRecoveryObservationStore)fixture.OperationStore;
        var binding = await AcceptRecoveryAsync(db, fixture, observationStore);
        var claimed = Assert.IsType<AzureProviderOperation>(await fixture.OperationStore.ClaimRecoveryAsync(
            fixture.Workspace.Id,
            fixture.Operation.Id,
            "postclaim-worker",
            "postclaim-lease",
            TimeSpan.FromMinutes(5),
            fixture.Observation.ObservedAt.AddMinutes(3),
            fixture.Operation.Version));
        if (postClaimStatus == AzureProviderOperationStatus.Succeeded)
        {
            Assert.IsType<AzureProviderOperation>(await fixture.OperationStore.FinalizeAsync(
                fixture.Workspace.Id,
                fixture.Operation.Id,
                "postclaim-lease",
                postClaimStatus,
                "azure.operation.succeeded",
                fixture.Observation.ObservedAt.AddMinutes(3),
                claimed.Version));
        }
        db.ChangeTracker.Clear();

        Assert.NotNull(await observationStore.GetAndValidateForAcceptedRecoveryReplayAsync(binding));
        Assert.Null(await observationStore.GetAndValidateForAcceptedRecoveryReplayAsync(
            binding with { IdempotencyKey = "postclaim-recovery-other" }));

        // The provider hand-off may have been persisted before reconciliation crashed;
        // the accepted recovery ledger is allowed to replay while lifecycle recovery is
        // still awaiting the normal post-claim hand-off.
        var lifecycleStore = new EfCoreElsaInstanceLifecycleStore(
            db,
            EmptyResolutionInputSource.Instance,
            new FixedTimeProvider(fixture.Observation.ObservedAt.AddMinutes(4)),
            recoveryObservationStore: observationStore);
        await lifecycleStore.CommitProviderSubmissionAsync(new(
            fixture.Workspace.Id,
            fixture.InstanceId,
            fixture.LifecycleOperationId,
            binding.AcceptedLifecycleAttemptNumber,
            "azure.operation.succeeded",
            fixture.Observation.ObservedAt.AddMinutes(4),
            fixture.Assignment.Id.ToString("D")));
        db.ChangeTracker.Clear();

        Assert.NotNull(await observationStore.GetAndValidateForAcceptedRecoveryReplayAsync(binding));
    }

    [Theory]
    [InlineData(AzureProviderOperationStatus.Running, ElsaInstanceProviderRecoveryOutcome.InProgress)]
    [InlineData(AzureProviderOperationStatus.Succeeded, ElsaInstanceProviderRecoveryOutcome.Succeeded)]
    public async Task Azure_adapter_replays_a_persisted_accepted_recovery_after_a_real_claim(
        AzureProviderOperationStatus postClaimStatus,
        ElsaInstanceProviderRecoveryOutcome expectedOutcome)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db);
        var observationStore = (IAzureProviderRecoveryObservationStore)fixture.OperationStore;
        var binding = await AcceptRecoveryAsync(db, fixture, observationStore);
        var claimed = Assert.IsType<AzureProviderOperation>(await fixture.OperationStore.ClaimRecoveryAsync(
            fixture.Workspace.Id,
            fixture.Operation.Id,
            "adapter-replay-worker",
            "adapter-replay-lease",
            TimeSpan.FromMinutes(5),
            fixture.Observation.ObservedAt.AddMinutes(3),
            fixture.Operation.Version));
        if (postClaimStatus == AzureProviderOperationStatus.Succeeded)
        {
            Assert.IsType<AzureProviderOperation>(await fixture.OperationStore.FinalizeAsync(
                fixture.Workspace.Id,
                fixture.Operation.Id,
                "adapter-replay-lease",
                postClaimStatus,
                "azure.operation.succeeded",
                fixture.Observation.ObservedAt.AddMinutes(3),
                claimed.Version));
        }

        // Force the adapter and its EF store to rehydrate every accepted value from durable
        // state. No mock recovery store, executor, or observer participates in this path.
        db.ChangeTracker.Clear();
        await using var adapterDb = CreateMigratedContext(connection);
        var adapterStore = new AzureProviderOperationStore(adapterDb);
        var provider = CreateProvider(fixture, adapterStore);
        var result = await provider.RecoverAsync(CreateRecoveryRequest(fixture, binding));

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(
            postClaimStatus == AzureProviderOperationStatus.Succeeded
                ? "azure.operation.no-op"
                : "azure.operation.in-progress",
            result.Code);
        var persisted = await adapterStore.GetAsync(fixture.Workspace.Id, fixture.Operation.Id);
        Assert.Equal(postClaimStatus, persisted!.Status);
        Assert.Equal(fixture.Observation.ProviderAttemptNumber + 1, persisted.AttemptNumber);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("stale")]
    public async Task Azure_adapter_rejects_foreign_or_stale_persisted_recovery_proof(string mismatch)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db);
        var observationStore = (IAzureProviderRecoveryObservationStore)fixture.OperationStore;
        var binding = await AcceptRecoveryAsync(db, fixture, observationStore);
        _ = Assert.IsType<AzureProviderOperation>(await fixture.OperationStore.ClaimRecoveryAsync(
            fixture.Workspace.Id,
            fixture.Operation.Id,
            "adapter-negative-worker",
            "adapter-negative-lease",
            TimeSpan.FromMinutes(5),
            fixture.Observation.ObservedAt.AddMinutes(3),
            fixture.Operation.Version));
        db.ChangeTracker.Clear();
        await using var adapterDb = CreateMigratedContext(connection);
        var adapterStore = new AzureProviderOperationStore(adapterDb);

        var invalidBinding = mismatch == "foreign"
            ? binding with { RecoveryRequestId = Guid.NewGuid() }
            : binding with
            {
                ObservedLifecycleAttemptNumber = binding.ObservedLifecycleAttemptNumber + 1,
                AcceptedLifecycleAttemptNumber = binding.AcceptedLifecycleAttemptNumber + 1
            };
        var result = await CreateProvider(fixture, adapterStore).RecoverAsync(
            CreateRecoveryRequest(fixture, invalidBinding));

        Assert.Equal(ElsaInstanceProviderRecoveryOutcome.Rejected, result.Outcome);
        Assert.Equal("azure.recovery.observation-invalid", result.Code);
        var persisted = await adapterStore.GetAsync(fixture.Workspace.Id, fixture.Operation.Id);
        Assert.Equal(AzureProviderOperationStatus.Running, persisted!.Status);
        Assert.Equal(fixture.Observation.ProviderAttemptNumber + 1, persisted.AttemptNumber);
    }

    [Theory]
    [InlineData(AzureProviderOperationStatus.Accepted)]
    [InlineData(AzureProviderOperationStatus.Queued)]
    [InlineData(AzureProviderOperationStatus.EntitlementHeld)]
    [InlineData(AzureProviderOperationStatus.RecoveryRequired)]
    [InlineData(AzureProviderOperationStatus.Failed)]
    [InlineData(AzureProviderOperationStatus.Cancelled)]
    public async Task Accepted_recovery_replay_rejects_non_postclaim_provider_status(
        AzureProviderOperationStatus status)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db);
        var observationStore = (IAzureProviderRecoveryObservationStore)fixture.OperationStore;
        var binding = await AcceptRecoveryAsync(db, fixture, observationStore);
        var providerOperation = await db.AzureProviderOperations.SingleAsync(x => x.Id == fixture.Operation.Id);
        providerOperation.Status = status;
        providerOperation.AttemptNumber = checked(fixture.Observation.ProviderAttemptNumber + 1);
        providerOperation.Version = checked(fixture.Observation.ProviderVersion + 1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Null(await observationStore.GetAndValidateForAcceptedRecoveryReplayAsync(binding));
    }

    [Theory]
    [InlineData(2, 1, 0, false)]
    [InlineData(1, 0, 0, false)]
    [InlineData(1, 1, 1, true)]
    public async Task Accepted_recovery_replay_requires_claimed_successor_tuple(
        int attemptDelta, long versionDelta, long checkpointDelta, bool expectedValid)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db);
        var observationStore = (IAzureProviderRecoveryObservationStore)fixture.OperationStore;
        var binding = await AcceptRecoveryAsync(db, fixture, observationStore);
        var providerOperation = await db.AzureProviderOperations.SingleAsync(x => x.Id == fixture.Operation.Id);
        providerOperation.Status = AzureProviderOperationStatus.Succeeded;
        providerOperation.AttemptNumber = checked(fixture.Observation.ProviderAttemptNumber + attemptDelta);
        providerOperation.Version = checked(fixture.Observation.ProviderVersion + versionDelta);
        providerOperation.CheckpointSequence = checked(fixture.Observation.ProviderCheckpointSequence + checkpointDelta);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var replay = await observationStore.GetAndValidateForAcceptedRecoveryReplayAsync(binding);
        if (expectedValid)
            Assert.NotNull(replay);
        else
            Assert.Null(replay);
    }

    [Fact]
    public async Task Accepted_recovery_replay_rejects_a_checkpoint_regression()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db);
        var providerEntity = await db.AzureProviderOperations.SingleAsync(x => x.Id == fixture.Operation.Id);
        providerEntity.CheckpointSequence = 1;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        fixture = fixture with
        {
            Operation = fixture.Operation with { CheckpointSequence = 1 },
            Observation = fixture.Observation with { ProviderCheckpointSequence = 1 }
        };

        var observationStore = (IAzureProviderRecoveryObservationStore)fixture.OperationStore;
        var binding = await AcceptRecoveryAsync(db, fixture, observationStore);
        var claimed = Assert.IsType<AzureProviderOperation>(await fixture.OperationStore.ClaimRecoveryAsync(
            fixture.Workspace.Id,
            fixture.Operation.Id,
            "checkpoint-worker",
            "checkpoint-lease",
            TimeSpan.FromMinutes(5),
            fixture.Observation.ObservedAt.AddMinutes(3),
            fixture.Operation.Version));
        Assert.IsType<AzureProviderOperation>(await fixture.OperationStore.FinalizeAsync(
            fixture.Workspace.Id,
            fixture.Operation.Id,
            "checkpoint-lease",
            AzureProviderOperationStatus.Succeeded,
            "azure.operation.succeeded",
            fixture.Observation.ObservedAt.AddMinutes(3),
            claimed.Version));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AzureProviderOperations SET CheckpointSequence = 0 WHERE Id = {fixture.Operation.Id}");
        db.ChangeTracker.Clear();

        Assert.Null(await observationStore.GetAndValidateForAcceptedRecoveryReplayAsync(binding));
    }

    [Fact]
    public async Task Accepted_recovery_replay_rejects_assignment_owned_by_another_operation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db);
        var observationStore = (IAzureProviderRecoveryObservationStore)fixture.OperationStore;
        var binding = await AcceptRecoveryAsync(db, fixture, observationStore);
        var providerOperation = await db.AzureProviderOperations.SingleAsync(x => x.Id == fixture.Operation.Id);
        providerOperation.Status = AzureProviderOperationStatus.Succeeded;
        providerOperation.AttemptNumber = checked(fixture.Observation.ProviderAttemptNumber + 1);
        providerOperation.Version = checked(fixture.Observation.ProviderVersion + 1);
        var assignment = await db.AzureProviderResourceAssignments.SingleAsync(x => x.Id == fixture.Assignment.Id);
        assignment.LastOperationId = Guid.NewGuid();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            observationStore.GetAndValidateForAcceptedRecoveryReplayAsync(binding));
    }

    [Fact]
    public async Task Recovery_acceptance_rejects_unrelated_instance_version_drift_after_observation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db);
        await AddLifecycleRunAsync(db, fixture);
        var observationStore = (IAzureProviderRecoveryObservationStore)fixture.OperationStore;
        var observedInstance = await db.ElsaInstances.SingleAsync(x => x.Id == fixture.InstanceId);
        var observation = fixture.Observation with { ObservedInstanceVersion = observedInstance.Version };
        var receipt = await observationStore.CreateOrGetAsync(observation);
        var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == fixture.LifecycleOperationId);
        operation.FailureCode = ElsaInstanceProviderReconciliationService.RetrySafeCode;
        operation.ReconciliationRetryEvidenceReference = receipt.Reference;
        operation.ReconciliationRetryEvidenceDigest = receipt.Digest;
        operation.ReconciledInstanceVersion = observation.ObservedInstanceVersion + 1;
        var instance = await db.ElsaInstances.SingleAsync(x => x.Id == fixture.InstanceId);
        instance.Name = "Recovery observation instance drift one";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        instance = await db.ElsaInstances.SingleAsync(x => x.Id == fixture.InstanceId);
        instance.Name = "Recovery observation instance drift two";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var lifecycleStore = new EfCoreElsaInstanceLifecycleStore(
            db,
            EmptyResolutionInputSource.Instance,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-09-05T18:00:00Z")),
            recoveryObservationStore: observationStore);
        var current = await lifecycleStore.GetInstanceAsync(fixture.Workspace.Id, fixture.InstanceId);
        Assert.NotNull(current);

        await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            new ElsaInstanceLifecycleService(
                    lifecycleStore,
                    new FixedTimeProvider(DateTimeOffset.Parse("2026-09-05T18:01:00Z")))
                .RecoverAsync(new(fixture.Workspace.Id, fixture.InstanceId, current!.Version, "drifted-recovery")));

        Assert.Equal(0, await db.ElsaInstanceRecoveryRequests.CountAsync());
    }

    private static async Task<AzureProviderRecoveryObservationBinding> AcceptRecoveryAsync(
        CatalogDbContext db,
        ObservationFixture fixture,
        IAzureProviderRecoveryObservationStore observationStore,
        bool lifecycleRunAlreadyAdded = false)
    {
        if (!lifecycleRunAlreadyAdded)
            await AddLifecycleRunAsync(db, fixture);
        var lifecycleStore = new EfCoreElsaInstanceLifecycleStore(
            db,
            EmptyResolutionInputSource.Instance,
            new FixedTimeProvider(fixture.Observation.ObservedAt.AddMinutes(1)),
            recoveryObservationStore: observationStore);
        var target = Assert.IsType<ElsaInstanceProviderReconciliationTarget>(
            await lifecycleStore.GetTargetAsync(fixture.Workspace.Id, fixture.LifecycleOperationId));
        var observation = fixture.Observation with { ObservedInstanceVersion = target.Instance.Version };
        var receipt = await observationStore.CreateOrGetAsync(observation);
        await lifecycleStore.CommitAsync(new(
            fixture.Workspace.Id,
            fixture.InstanceId,
            fixture.LifecycleOperationId,
            target.Instance.Version,
            target.Operation.AttemptNumber,
            target.ReconciliationVersion,
            new string('7', 64),
            target.Instance,
            target.Operation,
            ElsaInstanceProviderReconciliationService.RetrySafeCode,
            true,
            receipt.Reference,
            receipt.Digest,
            observation.ObservedAt.AddMinutes(1)));
        var reconciled = Assert.IsType<ElsaInstance>(
            await lifecycleStore.GetInstanceAsync(fixture.Workspace.Id, fixture.InstanceId));
        const string recoveryKey = "postclaim-recovery";
        var accepted = await new ElsaInstanceLifecycleService(
                lifecycleStore,
                new FixedTimeProvider(observation.ObservedAt.AddMinutes(2)))
            .RecoverAsync(new(
                fixture.Workspace.Id,
                fixture.InstanceId,
                reconciled.Version,
                recoveryKey));
        Assert.Equal(reconciled.Version + 1, accepted.Instance.Version);
        db.ChangeTracker.Clear();
        var recovery = await db.ElsaInstanceRecoveryRequests.SingleAsync();
        return new(
            recovery.Id,
            fixture.Workspace.OrganizationId,
            fixture.Workspace.Id,
            fixture.InstanceId,
            fixture.LifecycleOperationId,
            observation.ObservedLifecycleAttemptNumber,
            observation.ObservedInstanceVersion + 1,
            recovery.AttemptNumber,
            accepted.Instance.Version,
            recovery.IdempotencyScope,
            recovery.IdempotencyKey,
            recovery.RequestHash,
            receipt.Reference,
            receipt.Digest);
    }

    private static AzureElsaInstanceProvider CreateProvider(
        ObservationFixture fixture,
        AzureProviderOperationStore? operationStore = null)
    {
        operationStore ??= fixture.OperationStore;
        return new(
            new AzureProviderOperationService(operationStore),
            operationStore,
            operationStore,
            options: new AzureElsaInstanceProviderOptions
            {
                Enabled = true,
                TemplateFingerprint = fixture.Operation.TemplateFingerprint,
                ProviderScopeFingerprint = fixture.Operation.ProviderScopeFingerprint,
                SubscriptionId = fixture.Assignment.SubscriptionId,
                ResourceGroupNamePrefix = "rg-recovery"
            },
            recoveryObservationStore: operationStore);
    }

    private static ElsaInstanceProviderRecoveryRequest CreateRecoveryRequest(
        ObservationFixture fixture,
        AzureProviderRecoveryObservationBinding binding)
    {
        var resolvedPlan = ResolvedElsaApplicationPlanSerialization.Deserialize(
            fixture.ResolvedPlan.SerializedPlan);
        var submission = new ElsaInstanceProviderSubmission(
            binding.WorkspaceId,
            binding.InstanceId,
            binding.LifecycleOperationId,
            binding.AcceptedLifecycleAttemptNumber,
            ElsaDesiredLifecycle.Running,
            resolvedPlan,
            new(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid()),
            fixture.Operation.Location,
            binding.OrganizationId,
            ElsaInstanceOperationAction.Reconcile,
            fixture.Assignment.Id.ToString("D"));
        var envelope = new ElsaInstanceProviderRecoveryEnvelope(
            binding.RecoveryRequestId,
            binding.OrganizationId,
            binding.WorkspaceId,
            binding.InstanceId,
            binding.LifecycleOperationId,
            binding.ObservedLifecycleAttemptNumber,
            binding.ObservedInstanceVersion,
            binding.AcceptedLifecycleAttemptNumber,
            binding.AcceptedInstanceVersion,
            binding.IdempotencyScope,
            binding.IdempotencyKey,
            binding.RequestHash,
            binding.Reference,
            binding.Digest);
        return new(submission, envelope);
    }

    private sealed record ObservationFixture(
        Workspace Workspace,
        Guid InstanceId,
        Guid LifecycleOperationId,
        ElsaInstanceResolvedPlanEntity ResolvedPlan,
        AzureProviderResourceAssignment Assignment,
        AzureProviderOperation Operation,
        AzureProviderOperationStore OperationStore,
        AzureProviderRecoveryObservationRecord Observation);

    private static async Task<ObservationFixture> SeedProviderObservationAsync(CatalogDbContext db)
    {
        var (workspace, instanceId, lifecycleOperationId, resolvedPlan, providerPlan) = await SeedLifecycleAuthorityAsync(db);
        var now = DateTimeOffset.Parse("2026-09-05T16:00:00Z");
        var operationStore = new AzureProviderOperationStore(db);
        var assignment = await ((IAzureProviderResourceAssignmentStore)operationStore).CreateOrGetAsync(
            new(
                workspace.Id,
                workspace.OrganizationId,
                instanceId,
                new string('a', 64),
                "11111111-1111-1111-1111-111111111111",
                "rg-recovery",
                $"e{instanceId:N}"[..16],
                "westeurope"),
            now);
        var operation = await operationStore.CreateOrGetAsync(
            new(
                workspace.Id,
                assignment.WorkloadName,
                AzureProviderOperationAction.Reconcile,
                $"elsa-instance-operation:{lifecycleOperationId:D}",
                providerPlan.Fingerprint,
                new string('b', 64),
                providerPlan.ElsaVersion,
                providerPlan.ReleaseLine,
                providerPlan.Topology,
                providerPlan.Isolation,
                providerPlan.Location,
                providerPlan.ImageRepository,
                $"sha256:{providerPlan.ImageDigest}",
                providerPlan.ReleaseManifestDigest,
                providerPlan.ReleaseManifestSignatureDigest,
                providerPlan.ReleaseManifestReference,
                providerPlan.ReleaseManifestSignatureReference,
                providerPlan.SecretReferences,
                ProviderScopeFingerprint: new string('a', 64),
                SqlWorkflowPackageVersion: providerPlan.SqlWorkflowPackageVersion,
                SqlQuartzPackageVersion: providerPlan.SqlQuartzPackageVersion,
                OrganizationId: workspace.OrganizationId,
                InstanceId: instanceId,
                LifecycleAction: ElsaInstanceOperationAction.Reconcile,
                ProviderAssignmentId: assignment.Id),
            now);
        var claimed = Assert.IsType<AzureProviderOperation>(await operationStore.ClaimAsync(
            workspace.Id, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now, operation.Version));
        operation = Assert.IsType<AzureProviderOperation>(await operationStore.FinalizeAsync(
            workspace.Id,
            operation.Id,
            "lease",
            AzureProviderOperationStatus.RecoveryRequired,
            "operation.recovery.required",
            now,
            claimed.Version));
        var observation = CreateObservation(workspace, instanceId, lifecycleOperationId, resolvedPlan, assignment, operation, now);
        return new(workspace, instanceId, lifecycleOperationId, resolvedPlan, assignment, operation, operationStore, observation);
    }

    private static async Task AddLifecycleRunAsync(CatalogDbContext db, ObservationFixture fixture)
    {
        var now = fixture.Observation.ObservedAt;
        var applicationId = Guid.NewGuid();
        var environmentId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        db.DeploymentApplications.Add(new DeploymentApplicationEntity
        {
            Id = applicationId,
            WorkspaceId = fixture.Workspace.Id,
            Name = $"Recovery application [{fixture.InstanceId:N}]",
            CreatedAt = now,
            UpdatedAt = now
        });
        db.DeploymentEnvironments.Add(new DeploymentEnvironmentEntity
        {
            Id = environmentId,
            WorkspaceId = fixture.Workspace.Id,
            ApplicationId = applicationId,
            ElsaInstanceId = fixture.InstanceId,
            Name = "managed",
            Tier = EnvironmentTier.Production,
            DeploymentStatus = DeploymentStatus.Blocked,
            DriftStatus = DriftStatus.Unknown,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.DeploymentRuns.Add(new DeploymentRunEntity
        {
            Id = runId,
            WorkspaceId = fixture.Workspace.Id,
            ElsaInstanceId = fixture.InstanceId,
            ApplicationId = applicationId,
            EnvironmentId = environmentId,
            EngineId = Guid.NewGuid(),
            SourceRevisionId = Guid.NewGuid(),
            Status = WorkspaceDeploymentRunStatus.RecoveryRequired,
            ValidationOutcome = DeploymentValidationOutcome.Passed,
            ConfirmationId = Guid.NewGuid(),
            ActorAccountId = Guid.NewGuid(),
            QueuedAt = now,
            CreatedAt = now,
            AttemptNumber = 1,
            RecoveryReason = "provider.unknown"
        });
        var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == fixture.LifecycleOperationId);
        operation.DeploymentRunId = runId;
        var instance = await db.ElsaInstances.SingleAsync(x => x.Id == fixture.InstanceId);
        instance.PlacementAssignmentId = fixture.Assignment.Id.ToString("D");
        db.ElsaInstanceLifecycleOutbox.Add(new ElsaInstanceLifecycleOutboxEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = fixture.Workspace.OrganizationId,
            WorkspaceId = fixture.Workspace.Id,
            InstanceId = fixture.InstanceId,
            OperationId = fixture.LifecycleOperationId,
            Action = operation.Action,
            RequestHash = operation.RequestHash,
            CreatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private static AzureProviderResourceReferences CompleteResources(
        AzureProviderResourceAssignment assignment,
        string workloadName) =>
        new(
            assignment.ResourceGroupName,
            $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.Resources/deployments/foundation",
            $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.Resources/deployments/workload",
            $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.App/containerApps/{workloadName}",
            $"{workloadName}-revision",
            $"{workloadName}-stable",
            $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/{workloadName}-identity",
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.KeyVault/vaults/{workloadName}-kv",
            $"https://{workloadName}-kv.vault.azure.net/",
            $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.Sql/servers/{workloadName}-sql",
            $"{workloadName}-sql.database.windows.net",
            $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.App/managedEnvironments/{workloadName}-aca",
            $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.ContainerRegistry/registries/registry",
            $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.Resources/deployments/acr-pull",
            $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.ContainerRegistry/registries/registry/providers/Microsoft.Authorization/roleAssignments/33333333-3333-3333-3333-333333333333");

    private static AzureProviderResourceReferences FoundationResources(
        AzureProviderResourceAssignment assignment,
        string planFingerprint) =>
        new(
            assignment.ResourceGroupName,
            $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.Resources/deployments/elsa-{assignment.WorkloadName}-{planFingerprint[..12]}-foundation",
            WorkloadIdentityResourceId: $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/{assignment.WorkloadName}-identity",
            WorkloadIdentityClientId: "11111111-1111-1111-1111-111111111111",
            WorkloadIdentityPrincipalId: "22222222-2222-2222-2222-222222222222",
            KeyVaultResourceId: $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.KeyVault/vaults/{assignment.WorkloadName}-kv",
            KeyVaultUri: $"https://{assignment.WorkloadName}-kv.vault.azure.net/",
            SqlServerResourceId: $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.Sql/servers/{assignment.WorkloadName}-sql",
            SqlServerFqdn: $"{assignment.WorkloadName}-sql.database.windows.net",
            ContainerAppsEnvironmentResourceId: $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.App/managedEnvironments/{assignment.WorkloadName}-aca");

    private static async Task<AzureProviderOperation> FinalizeCorrelatedUnknownAsync(
        ObservationFixture fixture,
        AzureWorkloadPlan plan,
        string workerId,
        string leaseId,
        DateTimeOffset now)
    {
        var store = fixture.OperationStore;
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimRecoveryAsync(
            fixture.Workspace.Id,
            fixture.Operation.Id,
            workerId,
            leaseId,
            TimeSpan.FromMinutes(5),
            now,
            fixture.Operation.Version));
        var marked = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
            fixture.Workspace.Id,
            fixture.Operation.Id,
            leaseId,
            new(
                AzureProviderOperationPhase.FoundationSubmitted,
                "azure.step.attempted",
                "The Azure lifecycle step was marked before its remote call.",
                FoundationResources(fixture.Assignment, plan.Fingerprint),
                null,
                AzureProviderHealth.Unknown,
                [],
                AttemptedStep: AzureProviderRunnerStep.Foundation),
            now,
            claimed.Version));

        return Assert.IsType<AzureProviderOperation>(await store.FinalizeAsync(
            fixture.Workspace.Id,
            fixture.Operation.Id,
            leaseId,
            AzureProviderOperationStatus.RecoveryRequired,
            "azure.step.uncertain",
            now,
            marked.Version));
    }

    private static AzureProviderOperationRequest CreateOperationRequest(
        AzureProviderOperation operation,
        string planFingerprint) => new(
            operation.WorkspaceId,
            operation.TargetKey,
            operation.Action,
            operation.IdempotencyKey,
            planFingerprint,
            operation.TemplateFingerprint,
            operation.ElsaVersion,
            operation.ReleaseLine,
            operation.Topology,
            operation.Isolation,
            operation.Location,
            operation.ImageRepository,
            operation.ImageDigest,
            operation.ReleaseManifestDigest,
            operation.ReleaseManifestSignatureDigest,
            operation.ReleaseManifestReference,
            operation.ReleaseManifestSignatureReference,
            operation.SafeSecretReferences,
            operation.ProviderScopeFingerprint,
            operation.SqlWorkflowPackageVersion,
            operation.SqlQuartzPackageVersion,
            operation.OrganizationId,
            operation.InstanceId,
            operation.LifecycleAction,
            operation.ProviderAssignmentId);

    private static AzureProviderRecoveryObservationRecord CreateObservation(
        Workspace workspace,
        Guid instanceId,
        Guid lifecycleOperationId,
        ElsaInstanceResolvedPlanEntity resolvedPlan,
        AzureProviderResourceAssignment assignment,
        AzureProviderOperation operation,
        DateTimeOffset observedAt) =>
        new(
            workspace.OrganizationId,
            workspace.Id,
            instanceId,
            lifecycleOperationId,
            ElsaInstanceOperationAction.Reconcile,
            1,
            1,
            operation.Id,
            operation.OperationIdentity,
            operation.RequestHash,
            operation.AttemptNumber,
            operation.Version,
            operation.CheckpointSequence,
            assignment.Id,
            assignment.WorkloadName,
            operation.ProviderScopeFingerprint,
            resolvedPlan.PlanId,
            resolvedPlan.SchemaVersion,
            resolvedPlan.PlanUri,
            resolvedPlan.ContentHash,
            operation.PlanFingerprint,
            operation.TemplateFingerprint,
            AzureProviderRunnerStep.Foundation,
            AzureProviderOperationPhase.FoundationObserved,
            AzureProviderHealth.Unknown,
            new string('d', 64),
            new string('e', 64),
            observedAt);

    private static async Task<(Workspace Workspace, Guid InstanceId, Guid LifecycleOperationId, ElsaInstanceResolvedPlanEntity Plan, AzureWorkloadPlan ProviderPlan)> SeedLifecycleAuthorityAsync(
        CatalogDbContext db)
    {
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Recovery observation workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var instanceId = Guid.NewGuid();
        var lifecycleOperationId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-09-05T16:00:00Z");
        var plan = CreateAzurePlan();
        var planId = "plan_recovery_01";
        var planUri = $"https://control.example.test/api/workspaces/{workspace.Id:D}/instances/{instanceId:D}/resolved-plans/{planId}";
        var serialized = ResolvedElsaApplicationPlanSerialization.Serialize(plan);
        var contentHash = ResolvedElsaApplicationPlanSerialization.ComputeContentHash(plan);
        var providerPlan = AssertProviderPlan(plan, $"e{instanceId:N}"[..16]);

        db.ElsaInstances.Add(new ElsaInstanceEntity
        {
            Id = instanceId,
            OrganizationId = workspace.OrganizationId,
            WorkspaceId = workspace.Id,
            Name = "Recovery observation instance",
            Slug = "recovery-observation-instance",
            DistributionId = "future-runtime",
            ReleaseLine = "5.0",
            RequestedVersion = "5.0.0-preview.1",
            Channel = "preview",
            PatchUpdates = "preview",
            MinorUpdates = "stable",
            MajorMigrations = "explicit-migration",
            TopologyId = "combined",
            FeaturePresetId = "preview",
            FeatureOverridesJson = "{}",
            PackagePolicy = "public",
            TargetMode = "managed",
            RegionCode = "westeurope",
            IsolationProfile = "dedicated",
            CapacityProfile = "standard-small",
            NetworkOutcome = "public",
            DomainOutcome = "managed",
            DesiredLifecycle = ElsaDesiredLifecycle.Running,
            ObservedLifecycle = ElsaObservedLifecycle.Unknown,
            Health = ElsaInstanceHealth.Unknown,
            ResolvedPlanId = planId,
            ResolvedPlanSchemaVersion = 1,
            ResolvedPlanContentHash = contentHash,
            ResolvedPlanUri = planUri,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        });
        var resolvedPlan = new ElsaInstanceResolvedPlanEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = workspace.OrganizationId,
            WorkspaceId = workspace.Id,
            InstanceId = instanceId,
            PlanId = planId,
            SchemaVersion = 1,
            ContentHash = contentHash,
            PlanUri = planUri,
            SerializedPlan = serialized,
            CreatedAt = now
        };
        db.ElsaInstanceResolvedPlans.Add(resolvedPlan);
        db.ElsaInstanceOperations.Add(new ElsaInstanceOperationEntity
        {
            Id = lifecycleOperationId,
            InstanceId = instanceId,
            OrganizationId = workspace.OrganizationId,
            WorkspaceId = workspace.Id,
            Action = ElsaInstanceOperationAction.Reconcile,
            IdempotencyScope = "instances",
            IdempotencyKey = "recovery-observation-lifecycle",
            RequestHash = new string('f', 64),
            ExpectedVersion = 1,
            State = ElsaInstanceOperationState.RecoveryRequired,
            AttemptNumber = 1,
            AcceptedAt = now,
            ResolvedPlanId = planId,
            FailureCode = "provider.unknown",
            FailureSummary = "provider.unknown",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (workspace, instanceId, lifecycleOperationId, resolvedPlan, providerPlan);
    }

    private static ResolvedElsaApplicationPlan CreateBasePlan() => new(
        "1",
        new("future-runtime", "5.0", "5.0.0-preview.1", "https://example.test/repo", "0123456789abcdef0123456789abcdef01234567", "https://example.test/manifests/5.0.0-preview.1.json", "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"),
        new("server-studio", [new("server", ["server"], new("paid", "registry.example.test/elsa", "registry.example.test/elsa/server@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), ["elsa.server"], [], [])]),
        [],
        new([]),
        new([], []),
        new("public", "unrestricted", false, [], []),
        "dedicated",
        new("preview", "preview", "stable", "automatic-within-minor", "explicit-approval", "explicit-migration"),
        [],
        [new(ReleaseManifestEvidenceKinds.Manifest, "https://example.test/manifests/5.0.0-preview.1.json", "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", ReleaseManifestEvidenceContract.DescriptionFor(ReleaseManifestEvidenceKinds.Manifest))]);

    private static ResolvedElsaApplicationPlan CreateAzurePlan()
    {
        var basePlan = CreateBasePlan();
        const string imageDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string manifestDigest = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        const string signatureDigest = "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        var component = basePlan.Topology.Components.Single();
        return basePlan with
        {
            Release = basePlan.Release with
            {
                ComponentDeclarations = new(
                    "1",
                    imageDigest,
                    [
                        new(AzureWorkloadPlanTranslator.SqlWorkflowPackageId, "5.0.0-preview.1"),
                        new(AzureWorkloadPlanTranslator.SqlQuartzPackageId, "5.0.0-preview.1")
                    ])
            },
            Topology = basePlan.Topology with
            {
                Id = AzureWorkloadPlanTranslator.SupportedTopology,
                Components =
                [
                    component with
                    {
                        Image = component.Image with
                        {
                            Repository = AzureWorkloadPlanTranslator.SupportedRepository,
                            Reference = $"{AzureWorkloadPlanTranslator.SupportedRepository}@{imageDigest}",
                            Digest = imageDigest
                        }
                    }
                ]
            },
            Isolation = AzureWorkloadPlanTranslator.SupportedIsolation,
            Configuration = new(
            [
                new("database:connectionstring", "string", true, true, false, null, null, "secret://vault/database-connection", null),
                new("identity:signingkey", "string", true, true, false, null, null, "secret://vault/identity-signing-key", null),
                new("admin:password", "string", true, true, false, null, null, "secret://vault/admin-password", null)
            ]),
            Evidence =
            [
                new(ReleaseManifestEvidenceKinds.Manifest, basePlan.Release.ReleaseManifestReference, manifestDigest,
                    ReleaseManifestEvidenceContract.DescriptionFor(ReleaseManifestEvidenceKinds.Manifest)),
                new(ReleaseManifestEvidenceKinds.Signature, "https://example.test/signatures/5.0.0-preview.1.sig", signatureDigest,
                    ReleaseManifestEvidenceContract.DescriptionFor(ReleaseManifestEvidenceKinds.Signature))
            ]
        };
    }

    private static AzureWorkloadPlan AssertProviderPlan(ResolvedElsaApplicationPlan plan, string workloadName)
    {
        var translation = AzureWorkloadPlanTranslator.Translate(plan, new(workloadName, "westeurope"));
        Assert.True(translation.IsAccepted, string.Join("; ", translation.Findings.Select(x => $"{x.Code}:{x.Scope}")));
        return Assert.IsType<AzureWorkloadPlan>(translation.Plan);
    }

    private static CatalogDbContext CreateMigratedContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);

    private static CatalogDbContext CreateMigratedContext(
        string databasePath,
        DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite($"Data Source={databasePath};Pooling=False;Default Timeout=30", sqlite =>
                sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly));
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
        return new(options.Options);
    }

    private static void DeleteDatabase(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-shm", $"{databasePath}-wal" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private sealed class NaturalKeyReadBarrier(int participantCount) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _naturalKeyReads;
        private int _naturalKeyBarrierArrivals;
        private int _observationInsertAttempts;

        public int NaturalKeyBarrierArrivals => Volatile.Read(ref _naturalKeyBarrierArrivals);
        public int ObservationInsertAttempts => Volatile.Read(ref _observationInsertAttempts);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsObservationInsert(command.CommandText))
                Interlocked.Increment(ref _observationInsertAttempts);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsObservationInsert(command.CommandText))
                Interlocked.Increment(ref _observationInsertAttempts);
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!IsNaturalKeyLookup(command.CommandText))
                return result;

            var read = Interlocked.Increment(ref _naturalKeyReads);
            if (read > participantCount)
                return result;

            if (Interlocked.Increment(ref _naturalKeyBarrierArrivals) == participantCount)
                _released.TrySetResult();
            await _released.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return result;
        }

        private static bool IsNaturalKeyLookup(string commandText) =>
            commandText.Contains("AzureProviderRecoveryObservations", StringComparison.OrdinalIgnoreCase) &&
            commandText.Contains("NaturalKey", StringComparison.OrdinalIgnoreCase) &&
            commandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase);

        private static bool IsObservationInsert(string commandText) =>
            commandText.Contains("AzureProviderRecoveryObservations", StringComparison.OrdinalIgnoreCase) &&
            commandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class EmptyResolutionInputSource : IElsaInstanceLifecycleResolutionInputSource
    {
        public static EmptyResolutionInputSource Instance { get; } = new();

        public Task<ElsaInstanceLifecycleResolutionInput?> GetAsync(
            ElsaInstance instance,
            ElsaInstanceOperation operation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ElsaInstanceLifecycleResolutionInput?>(null);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
