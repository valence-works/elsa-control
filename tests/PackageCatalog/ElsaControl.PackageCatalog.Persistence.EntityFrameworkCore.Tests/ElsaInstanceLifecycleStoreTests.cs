using System.Data.Common;
using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;
using ElsaControl.RuntimeBuilder.Abstractions;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed partial class ElsaInstanceLifecycleStoreTests
{
    [Theory]
    [InlineData(1205)]
    [InlineData(2601)]
    [InlineData(2627)]
    [InlineData(3960)]
    public void SqlServer_lifecycle_reservation_conflicts_are_the_only_retryable_error_numbers(int number)
    {
        Assert.True(EfCoreDatabaseExceptionPolicy.IsSqlServerLifecycleReservationConflictNumber(number));
        Assert.False(EfCoreDatabaseExceptionPolicy.IsSqlServerLifecycleReservationConflictNumber(547));
        Assert.False(EfCoreDatabaseExceptionPolicy.IsSqlServerLifecycleReservationConflictNumber(-2));
    }

    [Theory]
    [InlineData(ElsaInstanceOperationAction.UpdateIntent)]
    [InlineData(ElsaInstanceOperationAction.Start)]
    [InlineData(ElsaInstanceOperationAction.Stop)]
    [InlineData(ElsaInstanceOperationAction.Delete)]
    public async Task Authoritative_replay_policy_accepts_exact_non_create_envelope_only(
        ElsaInstanceOperationAction action)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Replay policy workspace");
        var service = new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now));
        var created = await service.CreateAsync(new ElsaInstanceCreateRequest(
            workspace.OrganizationId, workspace.Id, "Managed Elsa", "replay-policy-elsa",
            CreateIntent(), "create-replay-policy"));
        await CompleteOperationAsync(db, created.Operation.Id);
        db.ChangeTracker.Clear();

        var expected = await CreateStore(db).GetInstanceAsync(workspace.Id, created.Instance.Id);
        Assert.NotNull(expected);
        var operation = ElsaInstanceOperation.Create(
            expected!.Id,
            action,
            $"instance/{expected.Id:D}/{action}",
            "mutation-replay-policy",
            RequestHash("mutation-replay-policy"),
            expected.Version);
        var existing = new ElsaInstanceOperationEntity
        {
            Id = Guid.NewGuid(),
            InstanceId = operation.InstanceId,
            OrganizationId = expected.OrganizationId,
            WorkspaceId = expected.WorkspaceId,
            Action = operation.Action,
            IdempotencyScope = operation.IdempotencyScope,
            IdempotencyKey = operation.IdempotencyKey,
            RequestHash = operation.RequestHash
        };

        Assert.True(EfCoreElsaInstanceLifecycleStore.IsExactAuthoritativeReplay(
            expected, expected, operation, existing));
        existing.RequestHash = RequestHash("different");
        Assert.False(EfCoreElsaInstanceLifecycleStore.IsExactAuthoritativeReplay(
            expected, expected, operation, existing));
        existing.RequestHash = operation.RequestHash;
        existing.Action = action == ElsaInstanceOperationAction.Start
            ? ElsaInstanceOperationAction.Stop
            : ElsaInstanceOperationAction.Start;
        Assert.False(EfCoreElsaInstanceLifecycleStore.IsExactAuthoritativeReplay(
            expected, expected, operation, existing));
        existing.Action = operation.Action;
        existing.InstanceId = Guid.NewGuid();
        Assert.False(EfCoreElsaInstanceLifecycleStore.IsExactAuthoritativeReplay(
            expected, expected, operation, existing));
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-30T10:00:00Z");

    [Fact]
    public void Active_run_reservation_index_includes_unmanaged_runs()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = CreateMigratedContext(connection);
        var index = db.Model.FindEntityType(typeof(DeploymentRunEntity))!
            .GetIndexes()
            .Single(x => x.Properties.Select(property => property.Name)
                .SequenceEqual(["WorkspaceId", "EnvironmentId"]));

        Assert.Equal("Status IN ('Queued', 'Running', 'RecoveryRequired')", index.GetFilter());
    }

    [Fact]
    public void Sql_server_resolved_plan_trigger_migration_is_idempotent_and_append_only()
    {
        // SQL Server is not a local integration dependency; assert the exact
        // generated SQL operation so the production migration remains reviewable.
        var migration = new AddElsaInstanceWorkerPersistence();
        var up = Assert.Single(migration.UpOperations.OfType<SqlOperation>(),
            x => x.Sql.Contains("TR_ElsaInstanceResolvedPlans_AppendOnly", StringComparison.Ordinal));
        var down = Assert.Single(migration.DownOperations.OfType<SqlOperation>(),
            x => x.Sql.Contains("TR_ElsaInstanceResolvedPlans_AppendOnly", StringComparison.Ordinal));

        Assert.Contains("IF OBJECT_ID(N'dbo.TR_ElsaInstanceResolvedPlans_AppendOnly', N'TR') IS NULL", up.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER dbo.TR_ElsaInstanceResolvedPlans_AppendOnly", up.Sql, StringComparison.Ordinal);
        Assert.Contains("ON dbo.ElsaInstanceResolvedPlans", up.Sql, StringComparison.Ordinal);
        Assert.Contains("INSTEAD OF UPDATE, DELETE", up.Sql, StringComparison.Ordinal);
        Assert.Contains("DROP TRIGGER IF EXISTS dbo.TR_ElsaInstanceResolvedPlans_AppendOnly", down.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_server_recovery_ledger_migration_preserves_existing_rows_and_is_append_only()
    {
        var migration = new AddElsaInstanceRecoveryRequestLedger();
        var dataMigration = Assert.Single(migration.UpOperations.OfType<SqlOperation>(),
            x => x.Sql.Contains("INSERT INTO ElsaInstanceRecoveryRequests", StringComparison.Ordinal));
        var trigger = Assert.Single(migration.UpOperations.OfType<SqlOperation>(),
            x => x.Sql.Contains("TR_ElsaInstanceRecoveryRequests_AppendOnly", StringComparison.Ordinal));

        Assert.Contains("RecoveryIdempotencyScope IS NOT NULL", dataMigration.Sql, StringComparison.Ordinal);
        Assert.Contains("INSTEAD OF UPDATE, DELETE", trigger.Sql, StringComparison.Ordinal);
        Assert.Contains("THROW 51015", trigger.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_server_preview_consent_migration_adds_nullable_digest_columns()
    {
        var migration = new AddPreviewManifestConsent();
        var columns = migration.UpOperations.OfType<AddColumnOperation>()
            .Where(x => x.Name == "PreviewManifestDigest")
            .ToArray();

        Assert.Equal(
            ["ElsaInstances", "ElsaInstanceIntentRevisions"],
            columns.Select(x => x.Table).ToArray());
        Assert.All(columns, column =>
        {
            Assert.Equal(typeof(string), column.ClrType);
            Assert.Equal("nvarchar(71)", column.ColumnType);
            Assert.Equal(71, column.MaxLength);
            Assert.True(column.IsNullable);
        });
    }

    [Fact]
    public void Worker_store_requires_a_governed_resolution_source_at_composition()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EfCoreElsaInstanceLifecycleStore(null!, null!));
    }

    [Fact]
    public async Task DbContext_rejects_unsafe_deletion_evidence_metadata()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Unsafe deletion evidence workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Unsafe evidence Elsa", "unsafe-evidence-elsa",
                WorkerIntent(), "unsafe-evidence-create"));
        var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == created.Operation.Id);
        operation.DeletionEvidenceReference = "https://user:secret@evidence.example/proof?token=secret";
        operation.DeletionEvidenceDigest = Digest('a');

        await Assert.ThrowsAsync<ArgumentException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData("https://user:secret@evidence.example/proof", "Verified producer release manifest.")]
    [InlineData("https://evidence.example/proof?token=secret", "Verified producer release manifest.")]
    [InlineData("https://evidence.example/proof#secret", "Verified producer release manifest.")]
    [InlineData("https://evidence.example/proof", "legacy free-form description")]
    public async Task Resolved_plan_projection_fails_closed_for_unsafe_evidence(
        string evidenceReference,
        string evidenceDescription)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Unsafe plan evidence workspace");
        var accepted = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Unsafe evidence Elsa", "unsafe-plan-evidence-elsa",
                WorkerIntent(), "unsafe-plan-evidence-create"));
        var resolved = SuccessfulResolution(workspace.Id, accepted.Instance.Id);
        var plan = resolved.Plan! with
        {
            Evidence =
            [
                new(ReleaseManifestEvidenceKinds.Manifest, evidenceReference, Digest('c'), evidenceDescription)
            ]
        };
        var serialized = ResolvedElsaApplicationPlanSerialization.Serialize(plan);
        var contentHash = ResolvedElsaApplicationPlanSerialization.ComputeContentHash(plan);
        db.Set<ElsaInstanceResolvedPlanEntity>().Add(new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = workspace.OrganizationId,
            WorkspaceId = workspace.Id,
            InstanceId = accepted.Instance.Id,
            PlanId = "unsafe_plan",
            SchemaVersion = 1,
            ContentHash = contentHash,
            PlanUri = $"https://control.example.test/api/workspaces/{workspace.Id:D}/instances/{accepted.Instance.Id:D}/resolved-plans/unsafe_plan",
            SerializedPlan = serialized,
            CreatedAt = Now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var projected = await new EfCoreManagedElsaInstanceApiStore(db)
            .GetResolvedPlanAsync(workspace.Id, accepted.Instance.Id, "unsafe_plan");

        Assert.Null(projected);
    }

    [Fact]
    public async Task Create_commits_instance_revision_operation_and_outbox_and_replays_exactly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Lifecycle workspace");
        var instanceId = Guid.NewGuid();
        var request = new ElsaInstanceCreateRequest(
            workspace.OrganizationId,
            workspace.Id,
            "Managed Elsa",
            "managed-elsa",
            CreateIntent(),
            "create-lifecycle",
            instanceId);
        var service = new ElsaInstanceLifecycleService(CreateStore(db));

        var first = await service.CreateAsync(request);
        db.ChangeTracker.Clear();

        Assert.False(first.Replayed);
        Assert.Equal(instanceId, first.Instance.Id);
        Assert.NotNull(first.Instance.DesiredStateRevisionId);
        Assert.Equal(first.Operation.Id, first.Outbox.OperationId);
        Assert.Equal(first.Operation.RequestHash, first.Outbox.RequestHash);
        Assert.Equal(first.Instance.DesiredStateRevisionId!.Value.Value,
            (await db.ElsaInstanceOperations.SingleAsync(x => x.Id == first.Operation.Id)).DesiredStateRevisionId);
        Assert.Equal(1, await db.ElsaInstances.CountAsync());
        Assert.Equal(1, await db.ElsaInstanceIntentRevisions.CountAsync());
        Assert.Equal(1, await db.ElsaInstanceOperations.CountAsync());
        Assert.Equal(1, await db.ElsaInstanceLifecycleOutbox.CountAsync());
        Assert.Equal(1, await db.ElsaInstanceAuditEvents.CountAsync());

        var reloaded = await CreateStore(db)
            .GetInstanceAsync(workspace.Id, instanceId);
        Assert.NotNull(reloaded);
        Assert.Equal("3.10", reloaded!.ReleaseIntent.ReleaseLine);
        Assert.Equal(ElsaFeatureOverrideKind.Number, reloaded.ApplicationIntent.FeatureOverrides["replicas"].Kind);
        Assert.Equal("3", reloaded.ApplicationIntent.FeatureOverrides["replicas"].Value);
        Assert.Equal(first.Instance.ComputeCanonicalIntentHash(), reloaded.ComputeCanonicalIntentHash());

        var replay = await service.CreateAsync(request);

        Assert.True(replay.Replayed);
        Assert.Equal(first.Instance.Id, replay.Instance.Id);
        Assert.Equal(first.Operation.Id, replay.Operation.Id);
        Assert.Equal(first.Outbox.Id, replay.Outbox.Id);
        Assert.Equal(first.Instance.Version, replay.Instance.Version);
        Assert.Equal(1, await db.ElsaInstanceIntentRevisions.CountAsync());
        Assert.Equal(1, await db.ElsaInstanceOperations.CountAsync());
        Assert.Equal(1, await db.ElsaInstanceLifecycleOutbox.CountAsync());
    }

    [Fact]
    public async Task Preview_consent_is_persisted_on_current_instance_and_immutable_revision()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Preview consent workspace");
        const string digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var intent = CreateIntent() with
        {
            Release = new ElsaReleaseIntent("server-studio", "3.10", "3.10.4", previewManifestDigest: digest)
        };

        var created = await new ElsaInstanceLifecycleService(CreateStore(db)).CreateAsync(
            new ElsaInstanceCreateRequest(workspace.OrganizationId, workspace.Id,
                "Preview Elsa", "preview-elsa", intent, "preview-consent-create"));
        db.ChangeTracker.Clear();

        await using (var reloadedDb = CreateMigratedContext(connection))
        {
            var persisted = await reloadedDb.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == created.Instance.Id);
            var revision = await reloadedDb.ElsaInstanceIntentRevisions.AsNoTracking().SingleAsync(x => x.InstanceId == created.Instance.Id);
            Assert.Equal(digest, persisted.PreviewManifestDigest);
            Assert.Equal(digest, revision.PreviewManifestDigest);
            Assert.Equal(intent.ComputeCanonicalHash(), revision.ContentHash);
            Assert.Equal(digest, (await CreateStore(reloadedDb).GetInstanceAsync(workspace.Id, created.Instance.Id))!.ReleaseIntent.PreviewManifestDigest);
            await CompleteOperationAsync(reloadedDb, created.Operation.Id);
        }

        var revokedIntent = intent with
        {
            Release = new ElsaReleaseIntent(
                "server-studio", "3.10", "3.10.4", "stable",
                "automatic-within-minor", "explicit-approval", "explicit-migration")
        };
        await using (var updateDb = CreateMigratedContext(connection))
        {
            var updated = await new ElsaInstanceLifecycleService(CreateStore(updateDb), new FixedTimeProvider(Now))
                .UpdateIntentAsync(new ElsaInstanceIntentUpdateRequest(
                    workspace.Id, created.Instance.Id,
                    revokedIntent, created.Instance.Version, "preview-consent-revoked"));
            var revisions = await updateDb.ElsaInstanceIntentRevisions.AsNoTracking()
                .Where(x => x.InstanceId == created.Instance.Id)
                .OrderBy(x => x.RevisionNumber)
                .ToArrayAsync();
            Assert.Equal(2, revisions.Length);
            Assert.Equal(revokedIntent.ComputeCanonicalHash(), revisions[1].ContentHash);
            Assert.Null(revisions[1].PreviewManifestDigest);
            Assert.Null((await CreateStore(updateDb).GetInstanceAsync(workspace.Id, created.Instance.Id))!.ReleaseIntent.PreviewManifestDigest);
            Assert.Equal(created.Instance.Version + 1, updated.Instance.Version);
        }
    }

    [Fact]
    public async Task Worker_uses_durable_preview_consent_after_context_reload()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Preview worker workspace");
        const string digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var intent = WorkerIntent() with
        {
            Release = new ElsaReleaseIntent(
                "future-runtime", "5.0", "5.0.0-preview.1", "preview",
                "automatic-within-minor", "explicit-approval", "explicit-migration", digest)
        };
        var created = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now)).CreateAsync(
            new ElsaInstanceCreateRequest(workspace.OrganizationId, workspace.Id,
                "Preview worker Elsa", "preview-worker-elsa", intent, "preview-worker-create"));
        var target = await AddManagedEnvironmentAsync(db, workspace, created.Instance.Id);
        db.ChangeTracker.Clear();

        string? observedConsent = null;
        await using var reloadedDb = CreateMigratedContext(connection);
        var source = new DurableConsentResolutionInputSource(target, instance => observedConsent = instance.ReleaseIntent.PreviewManifestDigest);
        var store = new EfCoreElsaInstanceLifecycleStore(reloadedDb, source, new FixedTimeProvider(Now));
        var result = await new ElsaInstanceLifecycleWorker(
                store,
                new StaticResolver(SuccessfulResolution(workspace.Id, created.Instance.Id)),
                new FixedTimeProvider(Now))
            .ProcessAvailableAsync("preview-worker");

        var processed = Assert.Single(result.Results);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Queued, processed.Outcome);
        Assert.Equal(digest, observedConsent);
        Assert.Equal(digest, (await new EfCoreElsaInstanceLifecycleStore(reloadedDb, EmptyResolutionInputSource.Instance)
            .GetInstanceAsync(workspace.Id, created.Instance.Id))!.ReleaseIntent.PreviewManifestDigest);
    }

    [Fact]
    public async Task Create_slug_unique_reservation_maps_database_race_to_stable_slug_conflict()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Slug reservation workspace");
        var service = new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now));
        await service.CreateAsync(new ElsaInstanceCreateRequest(
            workspace.OrganizationId,
            workspace.Id,
            "First Elsa",
            "reserved-slug",
            CreateIntent(),
            "first-slug-reservation"));
        db.ChangeTracker.Clear();

        var conflict = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            service.CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId,
                workspace.Id,
                "Racing Elsa",
                "reserved-slug",
                CreateIntent(),
                "racing-slug-reservation")));

        Assert.Equal("Instance slug is already in use in this workspace.", conflict.Message);
        Assert.Equal(1, await db.ElsaInstances.CountAsync());
        Assert.Equal(1, await db.ElsaInstanceOperations.CountAsync());
    }

    [Fact]
    public async Task Accepted_operation_links_the_existing_revision_when_intent_hash_is_unchanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Revision link workspace");
        var service = new ElsaInstanceLifecycleService(CreateStore(db));
        var created = await service.CreateAsync(new ElsaInstanceCreateRequest(
            workspace.OrganizationId, workspace.Id, "Managed Elsa", "revision-link-elsa", CreateIntent(), "create-revision-link"));
        await CompleteOperationAsync(db, created.Operation.Id);

        db.ChangeTracker.Clear();
        var actorAccountId = Guid.NewGuid();
        var updated = await service.UpdateIntentAsync(new ElsaInstanceIntentUpdateRequest(
            workspace.Id, created.Instance.Id, CreateIntent(), created.Instance.Version, "update-revision-link",
            Reason: "approved customer change", ActorAccountId: actorAccountId));

        Assert.False(updated.Replayed);
        Assert.Equal(created.Instance.Version + 1, updated.Instance.Version);
        Assert.Equal(1, await db.ElsaInstanceIntentRevisions.CountAsync());
        Assert.Equal(created.Instance.DesiredStateRevisionId!.Value.Value,
            (await db.ElsaInstanceOperations.SingleAsync(x => x.Id == updated.Operation.Id)).DesiredStateRevisionId);
        Assert.Equal(2, await db.ElsaInstanceAuditEvents.CountAsync());

        var audit = await db.ElsaInstanceAuditEvents.SingleAsync(x => x.OperationId == updated.Operation.Id);
        Assert.Equal(actorAccountId, audit.ActorAccountId);
        Assert.StartsWith("reason.sha256.", audit.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("approved customer change", audit.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_operations_for_one_instance_have_one_winner_and_no_partial_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = CreateMigratedContext(connection);
        await setup.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(setup, "Concurrent lifecycle workspace");
        var createService = new ElsaInstanceLifecycleService(CreateStore(setup));
        var created = await createService.CreateAsync(new ElsaInstanceCreateRequest(
            workspace.OrganizationId, workspace.Id, "Managed Elsa", "concurrent-elsa", CreateIntent(), "create-concurrent"));
        await CompleteOperationAsync(setup, created.Operation.Id);
        setup.ChangeTracker.Clear();

        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        await using var firstDb = new CatalogDbContext(options);
        await using var secondDb = new CatalogDbContext(options);
        var firstStore = CreateStore(firstDb);
        var secondStore = CreateStore(secondDb);
        var firstExpected = await firstStore.GetInstanceAsync(workspace.Id, created.Instance.Id);
        var secondExpected = await secondStore.GetInstanceAsync(workspace.Id, created.Instance.Id);
        Assert.NotNull(firstExpected);
        Assert.NotNull(secondExpected);
        var firstTransition = ElsaInstanceStateMachine.Request(
            firstExpected!, ElsaInstanceOperationAction.Reconcile, expectedVersion: firstExpected.Version,
            idempotencyKey: "reconcile-race-1", requestHash: RequestHash("reconcile-race-1"));
        var secondTransition = ElsaInstanceStateMachine.Request(
            secondExpected!, ElsaInstanceOperationAction.Reconcile, expectedVersion: secondExpected.Version,
            idempotencyKey: "reconcile-race-2", requestHash: RequestHash("reconcile-race-2"));

        var results = await Task.WhenAll(
            CaptureAsync(() => firstStore.CommitAcceptedAsync(
                firstExpected, firstTransition.Instance, firstTransition.Operation,
                NewOutbox(firstTransition))),
            CaptureAsync(() => secondStore.CommitAcceptedAsync(
                secondExpected, secondTransition.Instance, secondTransition.Operation,
                NewOutbox(secondTransition))));

        Assert.Equal(1, results.Count(x => x.Error is null));
        var conflict = Assert.Single(results, x => x.Error is not null).Error;
        Assert.IsType<ElsaInstanceLifecycleConflictException>(conflict);
        await using var verify = new CatalogDbContext(options);
        Assert.Equal(2, await verify.ElsaInstanceOperations.CountAsync());
        Assert.Equal(2, await verify.ElsaInstanceLifecycleOutbox.CountAsync());
        Assert.Equal(2, await verify.ElsaInstanceAuditEvents.CountAsync());
        Assert.Equal(1, await verify.ElsaInstanceIntentRevisions.CountAsync());
        Assert.Equal(1, await verify.ElsaInstanceOperations.CountAsync(x => x.State == ElsaInstanceOperationState.Accepted));
    }

    [Fact]
    public async Task Commit_rechecks_expected_version_after_the_preflight_read()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = CreateMigratedContext(connection);
        await setup.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(setup, "Version lifecycle workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(setup)).CreateAsync(
            new ElsaInstanceCreateRequest(workspace.OrganizationId, workspace.Id, "Managed Elsa", "version-elsa", CreateIntent(), "create-version"));
        await CompleteOperationAsync(setup, created.Operation.Id);
        setup.ChangeTracker.Clear();

        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        await using var firstDb = new CatalogDbContext(options);
        await using var secondDb = new CatalogDbContext(options);
        var firstStore = CreateStore(firstDb);
        var secondStore = CreateStore(secondDb);
        var expected = await firstStore.GetInstanceAsync(workspace.Id, created.Instance.Id);
        var secondExpected = await secondStore.GetInstanceAsync(workspace.Id, created.Instance.Id);
        Assert.NotNull(expected);
        Assert.NotNull(secondExpected);

        var firstTransition = ElsaInstanceStateMachine.Request(
            expected!, ElsaInstanceOperationAction.Reconcile, expectedVersion: expected.Version,
            idempotencyKey: "version-reconcile-1", requestHash: RequestHash("version-reconcile-1"));
        var firstOutbox = NewOutbox(firstTransition);
        await firstStore.CommitAcceptedAsync(expected, firstTransition.Instance, firstTransition.Operation, firstOutbox);
        await CompleteOperationAsync(setup, firstTransition.Operation.Id);

        var secondTransition = ElsaInstanceStateMachine.Request(
            secondExpected!, ElsaInstanceOperationAction.Reconcile, expectedVersion: secondExpected.Version,
            idempotencyKey: "version-reconcile-2", requestHash: RequestHash("version-reconcile-2"));
        var secondOutbox = NewOutbox(secondTransition);
        var error = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            secondStore.CommitAcceptedAsync(secondExpected, secondTransition.Instance, secondTransition.Operation, secondOutbox));

        Assert.Equal("Instance version conflict.", error.Message);
    }

    [Fact]
    public async Task Failed_delete_acceptance_rolls_back_confirmation_consumption()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = CreateMigratedContext(connection);
        await setup.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(setup, "Atomic delete workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(setup), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "atomic-delete", CreateIntent(), "create-atomic-delete"));
        await CompleteOperationAsync(setup, created.Operation.Id);
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        await using var staleDb = new CatalogDbContext(options);
        var store = CreateStore(staleDb);
        var stale = await store.GetInstanceAsync(workspace.Id, created.Instance.Id);
        Assert.NotNull(stale);
        var transition = ElsaInstanceStateMachine.Request(
            stale!, ElsaInstanceOperationAction.Delete, expectedVersion: stale.Version,
            idempotencyKey: "delete-atomic-failure", requestHash: RequestHash("delete-atomic-failure"));
        var outbox = NewOutbox(transition);
        var actorAccountId = Guid.NewGuid();
        var confirmation = await new DeploymentWorkspaceStore(setup).CreateConfirmationAsync(
            workspace.Id,
            new CreateActionConfirmationRequest(
                ConfirmationActionType.DeleteManagedInstance,
                created.Instance.Id.ToString("D"),
                actorAccountId),
            outbox.CreatedAt);
        setup.ChangeTracker.Clear();

        await setup.ElsaInstances.Where(x => x.Id == created.Instance.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Version, x => x.Version + 1));

        await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            store.CommitAcceptedWithContextAsync(
                stale, transition.Instance, transition.Operation, outbox,
                new ElsaInstanceAcceptanceContext(
                    actorAccountId,
                    "requested deletion",
                    new ElsaInstanceDeleteConfirmationRequirement(confirmation.Id, actorAccountId))));

        await using var verify = new CatalogDbContext(options);
        Assert.Null((await verify.ActionConfirmations.SingleAsync(x => x.Id == confirmation.Id)).UsedAt);
        Assert.False(await verify.ElsaInstanceOperations.AnyAsync(x => x.Id == transition.Operation.Id));
        Assert.False(await verify.ElsaInstanceLifecycleOutbox.AnyAsync(x => x.OperationId == transition.Operation.Id));
    }

    [Fact]
    public async Task Delete_acceptance_without_confirmation_context_fails_closed_at_the_store_boundary()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Unconfirmed delete workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "unconfirmed-delete", CreateIntent(), "create-unconfirmed-delete"));
        await CompleteOperationAsync(db, created.Operation.Id);
        db.ChangeTracker.Clear();
        var store = CreateStore(db);
        var current = await store.GetInstanceAsync(workspace.Id, created.Instance.Id);
        Assert.NotNull(current);
        var transition = ElsaInstanceStateMachine.Request(
            current!, ElsaInstanceOperationAction.Delete,
            expectedVersion: current.Version,
            idempotencyKey: "delete-without-context",
            requestHash: RequestHash("delete-without-context"));

        await Assert.ThrowsAsync<ElsaInstanceDeleteConfirmationException>(() => store.CommitAcceptedAsync(
            current, transition.Instance, transition.Operation, NewOutbox(transition)));

        Assert.False(await db.ElsaInstanceOperations.AnyAsync(x => x.Id == transition.Operation.Id));
        Assert.False(await db.ElsaInstanceLifecycleOutbox.AnyAsync(x => x.OperationId == transition.Operation.Id));
    }

    [Fact]
    public async Task Recover_persists_reconciled_instance_observation_with_the_operation_resume()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Recovery lifecycle workspace");
        var service = new ElsaInstanceLifecycleService(CreateStore(db));
        var created = await service.CreateAsync(new ElsaInstanceCreateRequest(
            workspace.OrganizationId, workspace.Id, "Managed Elsa", "recovery-elsa", CreateIntent(), "create-recovery"));

        db.ChangeTracker.Clear();
        var instance = await db.ElsaInstances.SingleAsync(x => x.Id == created.Instance.Id);
        instance.ObservedLifecycle = ElsaObservedLifecycle.Unknown;
        instance.Health = ElsaInstanceHealth.Unknown;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == created.Operation.Id);
        operation.State = ElsaInstanceOperationState.Queued;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == created.Operation.Id);
        operation.State = ElsaInstanceOperationState.Running;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == created.Operation.Id);
        operation.State = ElsaInstanceOperationState.RecoveryRequired;
        operation.FailureCode = ElsaInstanceProviderReconciliationService.RetrySafeCode;
        operation.ReconciliationRetryEvidenceReference = "https://evidence.example/retry/recovery-elsa";
        operation.ReconciliationRetryEvidenceDigest = "sha256:" + new string('a', 64);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var current = await CreateStore(db)
            .GetInstanceAsync(workspace.Id, created.Instance.Id);
        Assert.NotNull(current);
        var recovered = await service.RecoverAsync(new ElsaInstanceLifecycleRequest(
            workspace.Id, created.Instance.Id, current!.Version, "recover-recovery"));

        Assert.False(recovered.Replayed);
        Assert.Equal(ElsaObservedLifecycle.Provisioning, recovered.Instance.ObservedLifecycle);
        Assert.Equal(ElsaInstanceOperationState.Queued, recovered.Operation.State);
        Assert.Equal(2, recovered.Operation.AttemptNumber);
        Assert.Equal(created.Outbox.Id, recovered.Outbox.Id);
        db.ChangeTracker.Clear();
        var persisted = await CreateStore(db)
            .GetInstanceAsync(workspace.Id, created.Instance.Id);
        Assert.Equal(ElsaObservedLifecycle.Provisioning, persisted!.ObservedLifecycle);
        Assert.Equal(3, persisted.Version);
        Assert.Equal(1, await db.ElsaInstanceLifecycleOutbox.CountAsync());
        Assert.Equal(2, await db.ElsaInstanceAuditEvents.CountAsync());

        var replayed = await service.RecoverAsync(new ElsaInstanceLifecycleRequest(
            workspace.Id, created.Instance.Id, current.Version, "recover-recovery"));
        Assert.True(replayed.Replayed);
        Assert.Equal(recovered.Operation.Id, replayed.Operation.Id);
        Assert.Equal(2, replayed.Operation.AttemptNumber);
        Assert.Equal(1, await db.ElsaInstanceLifecycleOutbox.CountAsync());
        Assert.Equal(2, await db.ElsaInstanceAuditEvents.CountAsync());

        // Simulate the worker claiming the winner before a concurrent loser
        // performs its authoritative lookup. The same recovery request must
        // replay the current durable state, never attempt Running -> Queued.
        db.ChangeTracker.Clear();
        var claimed = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == recovered.Operation.Id);
        claimed.State = ElsaInstanceOperationState.Running;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var replayedAfterClaim = await service.RecoverAsync(new ElsaInstanceLifecycleRequest(
            workspace.Id, created.Instance.Id, current.Version, "recover-recovery"));
        Assert.True(replayedAfterClaim.Replayed);
        Assert.Equal(ElsaInstanceOperationState.Running, replayedAfterClaim.Operation.State);
        Assert.Equal(recovered.Operation.AttemptNumber, replayedAfterClaim.Operation.AttemptNumber);
        Assert.Equal(recovered.Outbox.Id, replayedAfterClaim.Outbox.Id);
        Assert.Equal(1, await db.ElsaInstanceOperations.CountAsync());
        Assert.Equal(1, await db.ElsaInstanceLifecycleOutbox.CountAsync());
        Assert.Equal(2, await db.ElsaInstanceAuditEvents.CountAsync());

        db.ChangeTracker.Clear();
        var completed = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == recovered.Operation.Id);
        completed.State = ElsaInstanceOperationState.Succeeded;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var replayedAfterCompletion = await service.RecoverAsync(new ElsaInstanceLifecycleRequest(
            workspace.Id, created.Instance.Id, current.Version, "recover-recovery"));
        Assert.True(replayedAfterCompletion.Replayed);
        Assert.Equal(ElsaInstanceOperationState.Succeeded, replayedAfterCompletion.Operation.State);
        Assert.Equal(recovered.Operation.AttemptNumber, replayedAfterCompletion.Operation.AttemptNumber);
        Assert.Equal(recovered.Outbox.Id, replayedAfterCompletion.Outbox.Id);
        Assert.Equal(1, await db.ElsaInstanceOperations.CountAsync());
        Assert.Equal(1, await db.ElsaInstanceLifecycleOutbox.CountAsync());
        Assert.Equal(2, await db.ElsaInstanceAuditEvents.CountAsync());

        var mismatch = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            service.RecoverAsync(new ElsaInstanceLifecycleRequest(
                workspace.Id, created.Instance.Id, current.Version, "recover-recovery",
                Reason: "different recovery request")));
        Assert.Equal(ElsaInstanceLifecycleConflictReason.IdempotencyConflict, mismatch.Reason);
    }

    [Fact]
    public async Task Recovery_keys_remain_authoritative_across_multiple_recovery_attempts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Recovery ledger workspace");
        var service = new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now));
        var created = await service.CreateAsync(new ElsaInstanceCreateRequest(
            workspace.OrganizationId, workspace.Id, "Managed Elsa", "recovery-ledger-elsa",
            CreateIntent(), "create-recovery-ledger"));

        await MarkRecoveryRequiredAsync(db, created.Operation.Id, "a");
        var beforeA = await CreateStore(db).GetInstanceAsync(workspace.Id, created.Instance.Id);
        var recoveredA = await service.RecoverAsync(new ElsaInstanceLifecycleRequest(
            workspace.Id, created.Instance.Id, beforeA!.Version, "recovery-key-a"));
        Assert.Equal(2, recoveredA.Operation.AttemptNumber);

        await MarkRecoveryRequiredAsync(db, created.Operation.Id, "b");
        var beforeB = await CreateStore(db).GetInstanceAsync(workspace.Id, created.Instance.Id);
        var recoveredB = await service.RecoverAsync(new ElsaInstanceLifecycleRequest(
            workspace.Id, created.Instance.Id, beforeB!.Version, "recovery-key-b"));
        Assert.Equal(3, recoveredB.Operation.AttemptNumber);

        var replayA = await service.RecoverAsync(new ElsaInstanceLifecycleRequest(
            workspace.Id, created.Instance.Id, beforeA.Version, "recovery-key-a"));
        Assert.True(replayA.Replayed);
        Assert.Equal(recoveredB.Operation.Id, replayA.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.Queued, replayA.Operation.State);
        Assert.Equal(3, replayA.Operation.AttemptNumber);
        Assert.Equal("recovery-key-a", replayA.Operation.RecoveryIdempotencyKey);

        var changedA = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            service.RecoverAsync(new ElsaInstanceLifecycleRequest(
                workspace.Id, created.Instance.Id, beforeA.Version, "recovery-key-a",
                Reason: "different request")));
        Assert.Equal(ElsaInstanceLifecycleConflictReason.IdempotencyConflict, changedA.Reason);

        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.ElsaInstanceRecoveryRequests.CountAsync());
        Assert.Equal(1, await db.ElsaInstanceOperations.CountAsync());
        Assert.Equal(1, await db.ElsaInstanceLifecycleOutbox.CountAsync());
        Assert.Equal(3, (await db.ElsaInstanceOperations.SingleAsync()).AttemptNumber);
    }

    private static async Task MarkRecoveryRequiredAsync(CatalogDbContext db, Guid operationId, string suffix)
    {
        db.ChangeTracker.Clear();
        var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == operationId);
        if (operation.State == ElsaInstanceOperationState.Accepted)
        {
            operation.State = ElsaInstanceOperationState.Queued;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == operationId);
        }
        operation.State = ElsaInstanceOperationState.Running;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == operationId);
        operation.State = ElsaInstanceOperationState.RecoveryRequired;
        operation.FailureCode = ElsaInstanceProviderReconciliationService.RetrySafeCode;
        operation.ReconciliationRetryEvidenceReference = $"https://evidence.example/retry/recovery-ledger-{suffix}";
        operation.ReconciliationRetryEvidenceDigest = "sha256:" + new string(suffix[0], 64);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Concurrent_identical_recovery_requests_replay_one_authoritative_resume()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = CreateMigratedContext(connection);
        await setup.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(setup, "Concurrent recovery workspace");
        var accepted = await new ElsaInstanceLifecycleService(CreateStore(setup), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "concurrent-recovery-elsa",
                CreateIntent(), "create-concurrent-recovery"));

        setup.ChangeTracker.Clear();
        var persistedInstance = await setup.ElsaInstances.SingleAsync(x => x.Id == accepted.Instance.Id);
        persistedInstance.ObservedLifecycle = ElsaObservedLifecycle.Unknown;
        persistedInstance.Health = ElsaInstanceHealth.Unknown;
        var persistedOperation = await setup.ElsaInstanceOperations.SingleAsync(x => x.Id == accepted.Operation.Id);
        persistedOperation.State = ElsaInstanceOperationState.Queued;
        await setup.SaveChangesAsync();
        setup.ChangeTracker.Clear();
        persistedOperation = await setup.ElsaInstanceOperations.SingleAsync(x => x.Id == accepted.Operation.Id);
        persistedOperation.State = ElsaInstanceOperationState.Running;
        await setup.SaveChangesAsync();
        setup.ChangeTracker.Clear();
        persistedOperation = await setup.ElsaInstanceOperations.SingleAsync(x => x.Id == accepted.Operation.Id);
        persistedOperation.State = ElsaInstanceOperationState.RecoveryRequired;
        persistedOperation.FailureCode = ElsaInstanceProviderReconciliationService.RetrySafeCode;
        persistedOperation.ReconciliationRetryEvidenceReference = "https://evidence.example/retry/concurrent-recovery";
        persistedOperation.ReconciliationRetryEvidenceDigest = "sha256:" + new string('a', 64);
        await setup.SaveChangesAsync();
        setup.ChangeTracker.Clear();

        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        await using var firstDb = new CatalogDbContext(options);
        await using var secondDb = new CatalogDbContext(options);
        var firstStore = CreateStore(firstDb);
        var secondStore = CreateStore(secondDb);
        var firstExpected = await firstStore.GetInstanceAsync(workspace.Id, accepted.Instance.Id);
        var secondExpected = await secondStore.GetInstanceAsync(workspace.Id, accepted.Instance.Id);
        var firstActive = await firstStore.GetActiveOperationAsync(workspace.Id, accepted.Instance.Id);
        var secondActive = await secondStore.GetActiveOperationAsync(workspace.Id, accepted.Instance.Id);
        Assert.NotNull(firstExpected);
        Assert.NotNull(secondExpected);
        Assert.NotNull(firstActive);
        Assert.NotNull(secondActive);
        var recoveryHash = RequestHash("recover-concurrently");
        var firstTransition = ElsaInstanceStateMachine.Request(
            firstExpected!, ElsaInstanceOperationAction.Recover, firstActive, firstExpected.Version,
            "recover-concurrently", recoveryHash);
        var secondTransition = ElsaInstanceStateMachine.Request(
            secondExpected!, ElsaInstanceOperationAction.Recover, secondActive, secondExpected.Version,
            "recover-concurrently", recoveryHash);

        var results = await Task.WhenAll(
            firstStore.CommitAcceptedAsync(
                firstExpected, firstTransition.Instance, firstTransition.Operation, NewOutbox(firstTransition)),
            secondStore.CommitAcceptedAsync(
                secondExpected, secondTransition.Instance, secondTransition.Operation, NewOutbox(secondTransition)));

        Assert.Equal(2, results.Length);
        Assert.Single(results, x => !x.Replayed);
        Assert.Single(results, x => x.Replayed);
        Assert.All(results, result =>
        {
            Assert.Equal(accepted.Operation.Id, result.Operation.Id);
            Assert.Equal(ElsaInstanceOperationState.Queued, result.Operation.State);
            Assert.Equal(2, result.Operation.AttemptNumber);
            Assert.Equal(accepted.Outbox.Id, result.Outbox.Id);
        });
        await using var verify = new CatalogDbContext(options);
        Assert.Equal(1, await verify.ElsaInstanceOperations.CountAsync());
        Assert.Equal(1, await verify.ElsaInstanceLifecycleOutbox.CountAsync());
        Assert.Equal(2, await verify.ElsaInstanceAuditEvents.CountAsync());
    }

    [Fact]
    public async Task Sql_server_recovery_replay_policy_requires_the_exact_recovery_and_durable_envelope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Recovery replay policy workspace");
        var accepted = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "recovery-policy-elsa",
                CreateIntent(), "create-recovery-policy"));
        var expected = accepted.Instance;
        var recoveryHash = RequestHash("recover-policy");
        var requestedOperation = ElsaInstanceOperation.Hydrate(
            accepted.Operation.Id,
            accepted.Instance.Id,
            accepted.Operation.Action,
            accepted.Operation.IdempotencyScope,
            accepted.Operation.IdempotencyKey,
            accepted.Operation.RequestHash,
            accepted.Operation.ExpectedVersion,
            ElsaInstanceOperationState.Queued,
            2,
            accepted.Operation.AcceptedAt,
            $"instance/{accepted.Instance.Id:D}/Recover",
            "recover-policy",
            recoveryHash);
        var existing = new ElsaInstanceOperationEntity
        {
            Id = requestedOperation.Id,
            InstanceId = requestedOperation.InstanceId,
            OrganizationId = expected.OrganizationId,
            WorkspaceId = expected.WorkspaceId,
            Action = requestedOperation.Action,
            IdempotencyScope = requestedOperation.IdempotencyScope,
            IdempotencyKey = requestedOperation.IdempotencyKey,
            RequestHash = requestedOperation.RequestHash,
            State = requestedOperation.State,
            AttemptNumber = requestedOperation.AttemptNumber,
            RecoveryIdempotencyScope = requestedOperation.RecoveryIdempotencyScope,
            RecoveryIdempotencyKey = requestedOperation.RecoveryIdempotencyKey,
            RecoveryRequestHash = requestedOperation.RecoveryRequestHash
        };
        var recovery = new ElsaInstanceRecoveryRequestEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = expected.OrganizationId,
            WorkspaceId = expected.WorkspaceId,
            InstanceId = expected.Id,
            OperationId = existing.Id,
            AttemptNumber = requestedOperation.AttemptNumber,
            IdempotencyScope = requestedOperation.RecoveryIdempotencyScope!,
            IdempotencyKey = requestedOperation.RecoveryIdempotencyKey!,
            RequestHash = requestedOperation.RecoveryRequestHash!,
            AcceptedAt = Now,
            CreatedAt = Now
        };

        Assert.True(EfCoreElsaInstanceLifecycleStore.IsExactAuthoritativeRecoveryReplay(
            expected, expected, requestedOperation, existing, recovery));
        existing.State = ElsaInstanceOperationState.Running;
        Assert.True(EfCoreElsaInstanceLifecycleStore.IsExactAuthoritativeRecoveryReplay(
            expected, expected, requestedOperation, existing, recovery));
        recovery.RequestHash = RequestHash("different-recovery");
        Assert.False(EfCoreElsaInstanceLifecycleStore.IsExactAuthoritativeRecoveryReplay(
            expected, expected, requestedOperation, existing, recovery));
        recovery.RequestHash = requestedOperation.RecoveryRequestHash!;
        existing.Id = Guid.NewGuid();
        Assert.False(EfCoreElsaInstanceLifecycleStore.IsExactAuthoritativeRecoveryReplay(
            expected, expected, requestedOperation, existing, recovery));
        existing.Id = requestedOperation.Id;
        recovery.OperationId = Guid.NewGuid();
        Assert.False(EfCoreElsaInstanceLifecycleStore.IsExactAuthoritativeRecoveryReplay(
            expected, expected, requestedOperation, existing, recovery));
    }

    [Fact]
    public async Task Worker_claims_and_commits_plan_run_and_environment_reservation_atomically()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Worker workspace");
        var instance = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Worker Elsa", "worker-elsa", WorkerIntent(), "worker-create"));
        var target = await AddManagedEnvironmentAsync(db, workspace, instance.Instance.Id);
        db.ChangeTracker.Clear();

        var source = new StaticResolutionInputSource(instance.Instance, target);
        var store = new EfCoreElsaInstanceLifecycleStore(db, source, new FixedTimeProvider(Now));
        var check = SuccessfulResolution(workspace.Id, instance.Instance.Id);
        Assert.Empty(ResolvedElsaApplicationPlanValidator.Validate(check.Plan!));
        var result = await new ElsaInstanceLifecycleWorker(
                store,
                new StaticResolver(check),
                new FixedTimeProvider(Now))
            .ProcessAvailableAsync("worker-one");

        var completed = Assert.Single(result.Results);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Queued, completed.Outcome);
        Assert.Equal(ElsaInstanceOperationState.Queued, completed.Operation.State);
        Assert.NotNull(completed.Run);
        Assert.Equal(instance.Instance.Id, (await db.DeploymentRuns.SingleAsync()).ElsaInstanceId);
        Assert.Equal(target.EnvironmentId, completed.Run!.EnvironmentId);
        Assert.Equal(1, await db.ElsaInstanceResolvedPlans.CountAsync());
        Assert.Equal(2, (await CreateStore(db)
            .GetInstanceAsync(workspace.Id, instance.Instance.Id))!.Version);
        Assert.Equal(2, await db.ElsaInstanceAuditEvents.CountAsync());
        Assert.Equal(0, result.ProviderInvocations);
    }

    [Fact]
    public async Task Worker_commit_replay_returns_existing_run_without_mutating_immutable_plan()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Replay workspace");
        var accepted = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Worker Elsa", "replay-worker-elsa", WorkerIntent(), "replay-worker-create"));
        var target = await AddManagedEnvironmentAsync(db, workspace, accepted.Instance.Id);
        var store = new EfCoreElsaInstanceLifecycleStore(
            db,
            new StaticResolutionInputSource(accepted.Instance, target),
            new FixedTimeProvider(Now.AddSeconds(1)));
        var item = await store.TryClaimNextAsync("worker-one", Now)
            ?? throw new InvalidOperationException("Expected a claimed work item.");
        var resolved = SuccessfulResolution(workspace.Id, accepted.Instance.Id);
        var commit = CreateResolutionCommit(item, resolved, Now.AddSeconds(1));

        var first = await store.CommitResolvedAsync(commit);
        var replay = await store.CommitResolvedAsync(commit);

        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Queued, first.Outcome);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.AlreadyCompleted, replay.Outcome);
        Assert.Equal(first.Run!.Id, replay.Run!.Id);
        Assert.Equal(1, await db.DeploymentRuns.CountAsync());
        Assert.Equal(1, await db.ElsaInstanceResolvedPlans.CountAsync());
    }

    [Fact]
    public async Task Finalization_uses_store_clock_when_caller_supplies_a_backdated_timestamp()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Authoritative clock workspace");
        var accepted = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Worker Elsa", "authoritative-clock-elsa", WorkerIntent(), "authoritative-clock-create"));
        var target = await AddManagedEnvironmentAsync(db, workspace, accepted.Instance.Id);
        var store = new EfCoreElsaInstanceLifecycleStore(
            db,
            new StaticResolutionInputSource(accepted.Instance, target),
            new FixedTimeProvider(Now.AddMinutes(6)));
        var item = await store.TryClaimNextAsync("worker-one", Now)
            ?? throw new InvalidOperationException("Expected a claimed work item.");

        var error = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() => store.CommitResolvedAsync(
            CreateResolutionCommit(item, SuccessfulResolution(workspace.Id, accepted.Instance.Id), Now.AddMinutes(1))));

        Assert.Equal("Lifecycle work item is no longer owned by this worker.", error.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(ElsaInstanceOperationState.Accepted,
            (await db.ElsaInstanceOperations.SingleAsync(x => x.Id == accepted.Operation.Id)).State);
        Assert.Empty(await db.DeploymentRuns.ToListAsync());
    }

    [Fact]
    public async Task Worker_rejects_stale_lease_token_without_partial_finalization()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Stale lease workspace");
        var accepted = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Worker Elsa", "stale-lease-elsa", WorkerIntent(), "stale-create"));
        var target = await AddManagedEnvironmentAsync(db, workspace, accepted.Instance.Id);
        var source = new StaticResolutionInputSource(accepted.Instance, target);
        var store = new EfCoreElsaInstanceLifecycleStore(db, source);
        var item = await store.TryClaimNextAsync("worker-one", Now)
            ?? throw new InvalidOperationException("Expected a claimed work item.");
        var resolved = SuccessfulResolution(workspace.Id, accepted.Instance.Id);
        var commit = CreateResolutionCommit(item, resolved, Now.AddSeconds(1)) with
        {
            LeaseToken = new string('a', 64)
        };

        var error = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() => store.CommitResolvedAsync(commit));

        Assert.Equal("Lifecycle work item is no longer owned by this worker.", error.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(ElsaInstanceOperationState.Accepted,
            (await db.ElsaInstanceOperations.SingleAsync(x => x.Id == item.Operation.Id)).State);
        Assert.Empty(await db.DeploymentRuns.ToListAsync());
        Assert.Empty(await db.ElsaInstanceResolvedPlans.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_workers_only_claim_one_accepted_outbox()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = CreateMigratedContext(connection);
        await setup.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(setup, "Concurrent worker workspace");
        var accepted = await new ElsaInstanceLifecycleService(CreateStore(setup), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Worker Elsa", "concurrent-worker-elsa", WorkerIntent(), "concurrent-worker-create"));
        var target = await AddManagedEnvironmentAsync(setup, workspace, accepted.Instance.Id);
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        await using var firstDb = new CatalogDbContext(options);
        await using var secondDb = new CatalogDbContext(options);
        var first = new EfCoreElsaInstanceLifecycleStore(firstDb, new StaticResolutionInputSource(accepted.Instance, target));
        var second = new EfCoreElsaInstanceLifecycleStore(secondDb, new StaticResolutionInputSource(accepted.Instance, target));

        var claims = await Task.WhenAll(
            CaptureAsync(() => first.TryClaimNextAsync("worker-one", Now)),
            CaptureAsync(() => second.TryClaimNextAsync("worker-two", Now)));

        Assert.Equal(1, claims.Count(x => x.Error is null && x.Value is not null));
        Assert.DoesNotContain(claims, x => x.Error is not null);
        await using var verify = new CatalogDbContext(options);
        Assert.Equal(1, await verify.ElsaInstanceOperations.CountAsync(x => x.WorkerId != null));
        Assert.Equal(0, await verify.ElsaInstanceOperations.CountAsync(x => x.State != ElsaInstanceOperationState.Accepted));
    }

    [Fact]
    public async Task Expired_resolver_claim_is_reclaimed_with_rotated_lease_and_stale_worker_cannot_finalize()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Expired lease workspace");
        var accepted = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Worker Elsa", "expired-lease-elsa", WorkerIntent(), "expired-create"));
        var target = await AddManagedEnvironmentAsync(db, workspace, accepted.Instance.Id);
        var store = new EfCoreElsaInstanceLifecycleStore(
            db,
            new StaticResolutionInputSource(accepted.Instance, target),
            new FixedTimeProvider(Now.AddMinutes(7)));

        var first = await store.TryClaimNextAsync("worker-one", Now)
            ?? throw new InvalidOperationException("Expected the first worker claim.");
        var second = await store.TryClaimNextAsync("worker-two", Now.AddMinutes(6))
            ?? throw new InvalidOperationException("Expected the expired claim to be reclaimed.");

        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        Assert.Equal(first.LeaseVersion + 1, second.LeaseVersion);
        var resolved = SuccessfulResolution(workspace.Id, accepted.Instance.Id);
        var stale = CreateResolutionCommit(first, resolved, Now.AddMinutes(1));
        await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() => store.CommitResolvedAsync(stale));

        var current = CreateResolutionCommit(second, resolved, Now.AddMinutes(7), "worker-two");
        var result = await store.CommitResolvedAsync(current);

        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Queued, result.Outcome);
        Assert.Equal(1, await db.DeploymentRuns.CountAsync());
    }

    [Fact]
    public async Task A_lost_acknowledgement_after_a_successful_claim_commit_returns_no_work_and_does_not_double_claim()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = CreateMigratedContext(connection);
        await setup.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(setup, "Lost claim acknowledgement workspace");
        var accepted = await new ElsaInstanceLifecycleService(CreateStore(setup), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Worker Elsa", "lost-ack-elsa", WorkerIntent(), "lost-ack-create"));
        var target = await AddManagedEnvironmentAsync(setup, workspace, accepted.Instance.Id);
        var acknowledgement = new LostCommitAcknowledgementInterceptor();
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection,
                sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly),
                isTransient: exception => exception is LostCommitAcknowledgementException)
            .AddInterceptors(acknowledgement)
            .Options;
        await using var db = new CatalogDbContext(options);
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, new StaticResolutionInputSource(accepted.Instance, target), new FixedTimeProvider(Now));

        // The claim's commit below succeeds durably, but the acknowledgement interceptor simulates the
        // caller never learning that: it throws once, after the commit, as a transient failure the
        // execution strategy retries. The retried attempt must find no reclaimable work rather than
        // claim the same operation a second time.
        var item = await store.TryClaimNextAsync("worker-one", Now);

        Assert.Null(item);
        Assert.Equal(2, acknowledgement.Committed);
        await using var verify = new CatalogDbContext(options);
        var operation = await verify.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        Assert.Equal("worker-one", operation.WorkerId);
        Assert.Equal(ElsaInstanceOperationState.Accepted, operation.State);
        Assert.Equal(1, operation.LeaseVersion);
        Assert.Equal(1, await verify.ElsaInstanceOperations.CountAsync(x => x.WorkerId != null));

        // Once that lease expires, the operation is claimable again, with exactly one rotated claim.
        var reclaimed = await store.TryClaimNextAsync("worker-two", Now.AddMinutes(6));

        Assert.NotNull(reclaimed);
        Assert.Equal(accepted.Operation.Id, reclaimed!.Operation.Id);
        Assert.Equal(2, reclaimed.LeaseVersion);
        await using var verifyAfter = new CatalogDbContext(options);
        Assert.Equal(1, await verifyAfter.ElsaInstanceOperations.CountAsync(x => x.Id == accepted.Operation.Id));
        Assert.Equal("worker-two",
            (await verifyAfter.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id)).WorkerId);
    }

    [Fact]
    public async Task Pending_delete_without_owned_resources_finalizes_locally_after_prior_operation_completes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Waiting delete workspace");
        var service = new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now));
        var created = await service.CreateAsync(new ElsaInstanceCreateRequest(
            workspace.OrganizationId, workspace.Id, "Worker Elsa", "waiting-delete-elsa", WorkerIntent(), "waiting-delete-create"));
        var (_, environmentId) = await AddManagedEnvironmentAsync(db, workspace, created.Instance.Id);
        var deletion = await service.DeleteAsync(await CreateConfirmedDeleteRequestAsync(
            db, workspace.Id, created.Instance.Id, created.Instance.Version, "waiting-delete"));
        await CompleteOperationAsync(db, created.Operation.Id);

        var result = await new ElsaInstanceDeletionWorker(
                new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance,
                    new FixedTimeProvider(Now.AddMinutes(1))),
                new ThrowingCleanupPort(), new FixedTimeProvider(Now.AddMinutes(1)))
            .ProcessAvailableAsync("deletion-worker");

        Assert.Equal(0, result.ProviderInvocations);
        var storedInstance = await db.ElsaInstances.SingleAsync(x => x.Id == deletion.Instance.Id);
        var storedOperation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == deletion.Operation.Id);
        Assert.Equal(ElsaObservedLifecycle.Deleted, storedInstance.ObservedLifecycle);
        Assert.Equal(Now.AddMinutes(1), storedInstance.DeletedAt);
        Assert.Equal(ElsaInstanceOperationState.Succeeded, storedOperation.State);
        Assert.Equal("deletion.local.absent", storedOperation.DeletionDiagnosticCode);
        Assert.Equal(64, storedOperation.DeletionEvidenceFingerprint!.Length);
        Assert.Null((await db.DeploymentEnvironments.SingleAsync(x => x.Id == environmentId)).ElsaInstanceId);
        Assert.Equal(1, await db.ElsaInstanceAuditEvents.CountAsync(x =>
            x.InstanceId == deletion.Instance.Id && x.EventType == "lifecycle.deleted"));

        db.ChangeTracker.Clear();
        var tombstone = await CreateStore(db).GetInstanceAsync(workspace.Id, deletion.Instance.Id);
        var repeatedRequest = await CreateConfirmedDeleteRequestAsync(
            db, workspace.Id, deletion.Instance.Id, tombstone!.Version, "delete-again", Now.AddMinutes(2));
        var repeated = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now.AddMinutes(2)))
            .DeleteAsync(repeatedRequest);
        var afterReplay = await new ElsaInstanceDeletionWorker(
                new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance,
                    new FixedTimeProvider(Now.AddMinutes(2))), new ThrowingCleanupPort(),
                new FixedTimeProvider(Now.AddMinutes(2)))
            .ProcessAvailableAsync("deletion-worker-replay");
        Assert.Equal(ElsaInstanceOperationState.Succeeded, repeated.Operation.State);
        Assert.Empty(afterReplay.Results);
        Assert.Equal(1, await db.ElsaInstanceAuditEvents.CountAsync(x =>
            x.InstanceId == deletion.Instance.Id && x.EventType == "lifecycle.deleted"));
    }

    [Fact]
    public async Task In_progress_provider_cleanup_is_deferred_then_same_delete_completes_on_next_poll()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Deferred deletion workspace");
        await CompleteManagedRunAsync(db, accepted.Operation.Id, accepted.Instance.Id);
        var current = await CreateStore(db).GetInstanceAsync(workspace.Id, accepted.Instance.Id);
        var deletion = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now.AddMinutes(2)))
            .DeleteAsync(await CreateConfirmedDeleteRequestAsync(
                db, workspace.Id, accepted.Instance.Id, current!.Version, "deferred-delete", Now.AddMinutes(2)));
        var pending = new ElsaInstanceCleanupObservation(
            ElsaInstanceCleanupObservationKind.InProgress, deletion.Operation.Id,
            deletion.Operation.AttemptNumber, "deletion.provider-cleanup-pending");
        var confirmed = new ElsaInstanceCleanupObservation(
            ElsaInstanceCleanupObservationKind.ConfirmedAbsent, deletion.Operation.Id,
            deletion.Operation.AttemptNumber, "deletion.provider-confirmed-absent");

        var first = await new ElsaInstanceDeletionWorker(
                new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance,
                    new FixedTimeProvider(Now.AddMinutes(3))), new QueueCleanupPort(pending),
                new FixedTimeProvider(Now.AddMinutes(3)))
            .ProcessAvailableAsync("deletion-worker");

        Assert.Empty(first.Results);
        var deferredOperation = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == deletion.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.Accepted, deferredOperation.State);
        Assert.Equal("deletion.provider-cleanup-pending", deferredOperation.DeletionDiagnosticCode);
        Assert.Equal(Now.AddMinutes(4), deferredOperation.LeaseExpiresAt);

        var second = await new ElsaInstanceDeletionWorker(
                new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance,
                    new FixedTimeProvider(Now.AddMinutes(4))), new QueueCleanupPort(confirmed),
                new FixedTimeProvider(Now.AddMinutes(4)))
            .ProcessAvailableAsync("deletion-worker");

        Assert.Single(second.Results);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Deleted, second.Results[0].Outcome);
        Assert.Equal(ElsaInstanceOperationState.Succeeded,
            (await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == deletion.Operation.Id)).State);
        Assert.Equal(ElsaObservedLifecycle.Deleted,
            (await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == accepted.Instance.Id)).ObservedLifecycle);
    }

    [Fact]
    public async Task Stale_or_foreign_deletion_lease_cannot_defer_provider_cleanup()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Stale deferred deletion workspace");
        await CompleteManagedRunAsync(db, accepted.Operation.Id, accepted.Instance.Id);
        var current = await CreateStore(db).GetInstanceAsync(workspace.Id, accepted.Instance.Id);
        var deletion = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now.AddMinutes(2)))
            .DeleteAsync(await CreateConfirmedDeleteRequestAsync(
                db, workspace.Id, accepted.Instance.Id, current!.Version, "stale-deferred-delete", Now.AddMinutes(2)));
        var store = new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance,
            new FixedTimeProvider(Now.AddMinutes(3)));
        var item = await store.TryClaimNextDeletionAsync("worker-one", Now.AddMinutes(3));
        Assert.NotNull(item);

        Assert.False(await store.DeferDeletionAsync(item! with { LeaseToken = new string('c', 64) },
            "worker-one", Now.AddMinutes(3), "deletion.provider-cleanup-pending"));
        Assert.False(await store.DeferDeletionAsync(item, "worker-two", Now.AddMinutes(3),
            "deletion.provider-cleanup-pending"));
        Assert.False(await store.DeferDeletionAsync(item with
        {
            Outbox = item.Outbox with { RequestHash = new string('d', 64) }
        }, "worker-one", Now.AddMinutes(3), "deletion.provider-cleanup-pending"));
        Assert.False(await store.DeferDeletionAsync(item with
        {
            Outbox = item.Outbox with { Id = Guid.NewGuid() }
        }, "worker-one", Now.AddMinutes(3), "deletion.provider-cleanup-pending"));
        Assert.False(await store.DeferDeletionAsync(item with
        {
            Operation = ElsaInstanceOperation.Create(item.Operation.InstanceId,
                ElsaInstanceOperationAction.Delete, item.Operation.IdempotencyScope,
                item.Operation.IdempotencyKey, new string('e', 64), item.Operation.ExpectedVersion,
                item.Operation.Id, item.Operation.AcceptedAt)
        }, "worker-one", Now.AddMinutes(3), "deletion.provider-cleanup-pending"));
        Assert.True(await store.DeferDeletionAsync(item, "worker-one", Now.AddMinutes(3),
            "deletion.provider-cleanup-pending"));

        var reclaimed = await store.TryClaimNextDeletionAsync("worker-two", Now.AddMinutes(4));
        Assert.NotNull(reclaimed);
        var instanceEntity = await db.ElsaInstances.SingleAsync(x => x.Id == accepted.Instance.Id);
        instanceEntity.Version++;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.False(await store.DeferDeletionAsync(reclaimed!, "worker-two", Now.AddMinutes(4),
            "deletion.provider-cleanup-pending"));
        Assert.False(await store.DeferDeletionAsync(item, "worker-one", Now.AddMinutes(4),
            "deletion.provider-cleanup-pending"));
        var operation = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == deletion.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.Accepted, operation.State);
        Assert.Equal("worker-two", operation.WorkerId);
    }

    [Fact]
    public async Task Ambiguous_provider_cleanup_preserves_tombstone_safety_and_can_recover_without_retry_observation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Ambiguous deletion workspace");
        await CompleteManagedRunAsync(db, accepted.Operation.Id, accepted.Instance.Id);
        var current = await CreateStore(db).GetInstanceAsync(workspace.Id, accepted.Instance.Id);
        var deleteRequest = await CreateConfirmedDeleteRequestAsync(
            db, workspace.Id, accepted.Instance.Id, current!.Version, "ambiguous-delete", Now.AddMinutes(2));
        var deletion = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now.AddMinutes(2)))
            .DeleteAsync(deleteRequest);
        await Assert.ThrowsAsync<ElsaInstanceDeleteConfirmationException>(() =>
            CreateStore(db).CommitAcceptedAsync(current, deletion.Instance, deletion.Operation, deletion.Outbox));
        var port = new QueueCleanupPort(new ElsaInstanceCleanupObservation(
            ElsaInstanceCleanupObservationKind.Ambiguous, deletion.Operation.Id,
            deletion.Operation.AttemptNumber, "deletion.provider.ambiguous"));

        var result = await new ElsaInstanceDeletionWorker(
                new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance,
                    new FixedTimeProvider(Now.AddMinutes(3))), port, new FixedTimeProvider(Now.AddMinutes(3)))
            .ProcessAvailableAsync("deletion-worker");

        Assert.Equal(1, result.ProviderInvocations);
        var storedInstance = await db.ElsaInstances.SingleAsync(x => x.Id == accepted.Instance.Id);
        var storedOperation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == deletion.Operation.Id);
        Assert.NotEqual(ElsaObservedLifecycle.Deleted, storedInstance.ObservedLifecycle);
        Assert.Null(storedInstance.DeletedAt);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, storedOperation.State);
        Assert.Equal("deletion.provider.ambiguous", storedOperation.DeletionDiagnosticCode);

        var recovered = await new ElsaInstanceLifecycleService(
                CreateStore(db), new FixedTimeProvider(Now.AddMinutes(4)))
            .RecoverAsync(new(workspace.Id, accepted.Instance.Id, storedInstance.Version, "recover-ambiguous-delete"));

        Assert.Equal(deletion.Operation.Id, recovered.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.Queued, recovered.Operation.State);
        Assert.Equal(deletion.Operation.AttemptNumber + 1, recovered.Operation.AttemptNumber);
        Assert.NotEqual(ElsaObservedLifecycle.Deleted, recovered.Instance.ObservedLifecycle);
        var recovery = await db.ElsaInstanceRecoveryRequests.AsNoTracking().SingleAsync();
        Assert.Null(recovery.RecoveryObservationReference);
        Assert.Null(recovery.RecoveryObservationDigest);
    }

    [Fact]
    public async Task Unknown_instance_rejects_a_forged_local_deletion_proof_at_the_store_boundary()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Unknown deletion proof workspace");
        var service = new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now));
        var created = await service.CreateAsync(new ElsaInstanceCreateRequest(
            workspace.OrganizationId, workspace.Id, "Unknown Elsa", "unknown-delete-elsa", WorkerIntent(), "unknown-create"));
        await CompleteOperationAsync(db, created.Operation.Id);
        var entity = await db.ElsaInstances.SingleAsync(x => x.Id == created.Instance.Id);
        entity.ObservedLifecycle = ElsaObservedLifecycle.Unknown;
        entity.Health = ElsaInstanceHealth.Unknown;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var current = await CreateStore(db).GetInstanceAsync(workspace.Id, created.Instance.Id);
        var deletion = await service.DeleteAsync(await CreateConfirmedDeleteRequestAsync(
            db, workspace.Id, created.Instance.Id, current!.Version, "unknown-delete"));
        var store = new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance,
            new FixedTimeProvider(Now.AddMinutes(1)));
        var item = await store.TryClaimNextDeletionAsync("deletion-worker", Now.AddMinutes(1));
        Assert.NotNull(item);
        Assert.False(item!.CanFinalizeLocally);
        var observation = new ElsaInstanceCleanupObservation(ElsaInstanceCleanupObservationKind.ConfirmedAbsent,
            deletion.Operation.Id, deletion.Operation.AttemptNumber, "deletion.local.absent");
        var commit = new ElsaInstanceDeletionCommit(workspace.Id, created.Instance.Id, deletion.Operation.Id,
            item.Outbox.Id, item.Instance.Version, item.Operation.AttemptNumber, item.CorrelatedRunId,
            "deletion-worker", item.LeaseToken, item.LeaseVersion, observation.ComputeFingerprint(),
            ElsaInstanceDeletionProofKind.LocalNoOwnedResources, observation.DiagnosticCode, null, null,
            ElsaInstanceStateMachine.FinalizeDeletion(item.Instance, Now.AddMinutes(2)),
            item.Operation.TransitionTo(ElsaInstanceOperationState.Succeeded), Now.AddMinutes(2));

        await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() => store.CommitDeletionAsync(commit));
        Assert.Equal(ElsaObservedLifecycle.Unknown,
            (await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == created.Instance.Id)).ObservedLifecycle);
    }

    [Fact]
    public async Task Confirmed_absence_finalization_replays_without_duplicate_audit_or_version_increment()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Deletion replay workspace");
        await CompleteManagedRunAsync(db, accepted.Operation.Id, accepted.Instance.Id);
        var current = await CreateStore(db).GetInstanceAsync(workspace.Id, accepted.Instance.Id);
        var deleteRequest = await CreateConfirmedDeleteRequestAsync(
            db, workspace.Id, accepted.Instance.Id, current!.Version, "replay-delete", Now.AddMinutes(2));
        var deletion = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now.AddMinutes(2)))
            .DeleteAsync(deleteRequest);
        var store = new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance,
            new FixedTimeProvider(Now.AddMinutes(3)));
        var item = await store.TryClaimNextDeletionAsync("deletion-worker", Now.AddMinutes(3));
        Assert.NotNull(item);
        var deletedAt = Now.AddMinutes(4);
        var evidence = new ElsaInstanceCleanupEvidence("https://evidence.example/deletions/replay", Digest('e'));
        var observation = new ElsaInstanceCleanupObservation(ElsaInstanceCleanupObservationKind.ConfirmedAbsent,
            deletion.Operation.Id, deletion.Operation.AttemptNumber, "deletion.provider.absent", evidence);
        var commit = new ElsaInstanceDeletionCommit(workspace.Id, accepted.Instance.Id, deletion.Operation.Id,
            item!.Outbox.Id, item.Instance.Version, item.Operation.AttemptNumber, item.CorrelatedRunId,
            "deletion-worker", item.LeaseToken, item.LeaseVersion, observation.ComputeFingerprint(),
            ElsaInstanceDeletionProofKind.ProviderConfirmedAbsent, observation.DiagnosticCode, evidence.Reference, evidence.Digest,
            ElsaInstanceStateMachine.FinalizeDeletion(item.Instance, deletedAt),
            item.Operation.TransitionTo(ElsaInstanceOperationState.Succeeded), deletedAt);

        var first = await store.CommitDeletionAsync(commit);
        var replay = await store.CommitDeletionAsync(commit);

        Assert.Equal(ElsaInstanceDeletionOutcome.Deleted, first.Outcome);
        Assert.Equal(ElsaInstanceDeletionOutcome.AlreadyCompleted, replay.Outcome);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Instance.Version, replay.Instance.Version);
        var run = await db.DeploymentRuns.SingleAsync(x => x.Id == item.CorrelatedRunId!.Value);
        var environment = await db.DeploymentEnvironments.SingleAsync(x => x.Id == run.EnvironmentId);
        Assert.Null(environment.ElsaInstanceId);
        Assert.Null(environment.DesiredRevisionId);
        Assert.Null(environment.DeployedRevisionId);
        Assert.Equal(1, await db.ElsaInstanceAuditEvents.CountAsync(x =>
            x.InstanceId == accepted.Instance.Id && x.EventType == "lifecycle.deleted"));
    }

    [Fact]
    public async Task Expired_deletion_claim_rotates_lease_without_provider_or_duplicate_operation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Deletion lease workspace");
        var service = new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now));
        var created = await service.CreateAsync(new ElsaInstanceCreateRequest(
            workspace.OrganizationId, workspace.Id, "Lease Elsa", "lease-elsa", WorkerIntent(), "lease-create"));
        var deletion = await service.DeleteAsync(await CreateConfirmedDeleteRequestAsync(
            db, workspace.Id, created.Instance.Id, created.Instance.Version, "lease-delete"));
        await CompleteOperationAsync(db, created.Operation.Id);
        var store = new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance,
            new FixedTimeProvider(Now.AddMinutes(6)));

        var first = await store.TryClaimNextDeletionAsync("worker-one", Now);
        var blocked = await store.TryClaimNextDeletionAsync("worker-two", Now.AddMinutes(4));
        var reclaimed = await store.TryClaimNextDeletionAsync("worker-two", Now.AddMinutes(6));

        Assert.NotNull(first);
        Assert.Null(blocked);
        Assert.NotNull(reclaimed);
        Assert.Equal(first!.Operation.Id, reclaimed!.Operation.Id);
        Assert.Equal(deletion.Operation.Id, reclaimed.Operation.Id);
        Assert.Equal(first.LeaseVersion + 1, reclaimed.LeaseVersion);
        Assert.NotEqual(first.LeaseToken, reclaimed.LeaseToken);
        Assert.Equal(1, await db.ElsaInstanceOperations.CountAsync(x => x.Id == deletion.Operation.Id));

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstanceOperations SET LeaseExpiresAt = NULL WHERE Id = {reclaimed.Operation.Id}");
        db.ChangeTracker.Clear();
        var failure = new ElsaInstanceDeletionFailure(workspace.Id, created.Instance.Id, deletion.Operation.Id,
            reclaimed.Outbox.Id, reclaimed.Instance.Version, reclaimed.Operation.AttemptNumber,
            reclaimed.CorrelatedRunId, "worker-two", reclaimed.LeaseToken, reclaimed.LeaseVersion,
            new string('f', 64), "deletion.provider.unavailable", Now.AddMinutes(6));
        await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            store.RequireDeletionRecoveryAsync(failure));
    }

    [Fact]
    public async Task A_lost_acknowledgement_after_a_successful_deletion_claim_commit_returns_no_work_and_does_not_double_claim()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = CreateMigratedContext(connection);
        await setup.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(setup, "Lost deletion claim acknowledgement workspace");
        var setupService = new ElsaInstanceLifecycleService(CreateStore(setup), new FixedTimeProvider(Now));
        var created = await setupService.CreateAsync(new ElsaInstanceCreateRequest(
            workspace.OrganizationId, workspace.Id, "Lease Elsa", "lost-ack-delete-elsa", WorkerIntent(), "lost-ack-delete-create"));
        var deletion = await setupService.DeleteAsync(await CreateConfirmedDeleteRequestAsync(
            setup, workspace.Id, created.Instance.Id, created.Instance.Version, "lost-ack-delete"));
        await CompleteOperationAsync(setup, created.Operation.Id);
        var acknowledgement = new LostCommitAcknowledgementInterceptor();
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection,
                sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly),
                isTransient: exception => exception is LostCommitAcknowledgementException)
            .AddInterceptors(acknowledgement)
            .Options;
        await using var db = new CatalogDbContext(options);
        var store = new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));

        // As with TryClaimNextAsync: the commit below succeeds durably, but the acknowledgement is lost
        // and the execution strategy retries the whole unit. The retry must find no reclaimable work.
        var item = await store.TryClaimNextDeletionAsync("worker-one", Now);

        Assert.Null(item);
        Assert.Equal(2, acknowledgement.Committed);
        await using var verify = new CatalogDbContext(options);
        var operation = await verify.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == deletion.Operation.Id);
        Assert.Equal("worker-one", operation.WorkerId);
        Assert.Equal(1, operation.LeaseVersion);
        Assert.Equal(1, await verify.ElsaInstanceOperations.CountAsync(x => x.WorkerId != null));

        // Once that lease expires, the operation is claimable again, with exactly one rotated claim.
        var reclaimed = await store.TryClaimNextDeletionAsync("worker-two", Now.AddMinutes(6));

        Assert.NotNull(reclaimed);
        Assert.Equal(deletion.Operation.Id, reclaimed!.Operation.Id);
        Assert.Equal(2, reclaimed.LeaseVersion);
        await using var verifyAfter = new CatalogDbContext(options);
        Assert.Equal(1, await verifyAfter.ElsaInstanceOperations.CountAsync(x => x.Id == deletion.Operation.Id));
        Assert.Equal("worker-two",
            (await verifyAfter.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == deletion.Operation.Id)).WorkerId);
    }

    [Fact]
    public async Task Stale_managed_run_marks_run_operation_and_instance_recovery_required_atomically()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Stale managed run");
        var workspaceStore = new DeploymentWorkspaceStore(db);
        var claimed = await workspaceStore.ClaimNextQueuedRunAsync("deployment-worker", Now);
        Assert.NotNull(claimed);

        var marked = await workspaceStore.MarkStaleRunningRunsRecoveryRequiredAsync(
            Now.AddMinutes(10), TimeSpan.FromMinutes(5));

        Assert.Equal(1, marked);
        db.ChangeTracker.Clear();
        var run = await db.DeploymentRuns.Include(x => x.Environment).SingleAsync(x => x.Id == claimed!.Id);
        var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == accepted.Operation.Id);
        var instance = await db.ElsaInstances.SingleAsync(x => x.Id == accepted.Instance.Id);
        Assert.Equal(WorkspaceDeploymentRunStatus.RecoveryRequired, run.Status);
        Assert.Null(run.CompletedAt);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, operation.State);
        Assert.Null(operation.CompletedAt);
        Assert.Equal(ElsaObservedLifecycle.Unknown, instance.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Unknown, instance.Health);
        Assert.Equal(DeploymentStatus.Blocked, run.Environment!.DeploymentStatus);
        var audit = await db.ElsaInstanceAuditEvents.SingleAsync(x => x.EventType == "lifecycle.recovery-required");
        Assert.Equal("provider.reconciliation.required", audit.DiagnosticCode);
        Assert.DoesNotContain("deployment-worker", audit.Summary ?? "", StringComparison.Ordinal);
        Assert.Equal(workspace.Id, audit.WorkspaceId);
    }

    [Fact]
    public async Task Queued_managed_run_reconstructs_a_safe_provider_submission_after_restart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Provider restart replay");

        db.ChangeTracker.Clear();
        var pending = await new EfCoreElsaInstanceLifecycleStore(
            db,
            EmptyResolutionInputSource.Instance,
            new FixedTimeProvider(Now)).ListPendingProviderOperationsAsync(16);

        var item = Assert.Single(pending);
        Assert.Equal(workspace.Id, item.WorkspaceId);
        Assert.Equal(accepted.Operation.Id, item.OperationId);
        Assert.NotNull(item.Submission);
        Assert.Equal("5.0", item.Submission!.Plan.Release.ReleaseLine);
        Assert.Equal(accepted.Instance.Id, item.Submission.InstanceId);
        Assert.Equal(accepted.Operation.Id, item.Submission.OperationId);
        Assert.Equal("westeurope", item.Submission.Location);
    }

    [Fact]
    public async Task Queued_provider_submission_is_held_without_a_call_and_resumes_the_same_run_once()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Provider entitlement hold");
        var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == accepted.Operation.Id);
        var runId = operation.DeploymentRunId;
        Assert.NotNull(runId);

        var entitlement = await db.OrganizationEntitlementSnapshots.SingleAsync(x => x.OrganizationId == workspace.OrganizationId);
        entitlement.SubscriptionState = OrganizationSubscriptionState.Constrained;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));
        var denied = await store.AuthorizeProviderSubmissionAsync(
            workspace.Id, accepted.Instance.Id, accepted.Operation.Id, Now.AddMinutes(1));

        Assert.False(denied.Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.LifecycleConstrained, denied.Code);
        db.ChangeTracker.Clear();
        Assert.Equal(ElsaInstanceOperationState.EntitlementHeld,
            (await db.ElsaInstanceOperations.SingleAsync(x => x.Id == accepted.Operation.Id)).State);
        var heldAudit = Assert.Single(await db.ElsaInstanceAuditEvents
            .Where(x => x.EventType == "lifecycle.entitlement-held")
            .ToListAsync());
        Assert.Equal(ElsaInstanceCommercialOperation.LifecycleConstrained, heldAudit.DiagnosticCode);
        Assert.DoesNotContain(workspace.OrganizationId.ToString("D"), heldAudit.Summary ?? "", StringComparison.OrdinalIgnoreCase);
        var heldPending = Assert.Single(await store.ListPendingProviderOperationsAsync(16));
        Assert.NotNull(heldPending.Submission);
        Assert.Equal(accepted.Operation.Id, heldPending.Submission!.OperationId);

        entitlement = await db.OrganizationEntitlementSnapshots.SingleAsync(x => x.OrganizationId == workspace.OrganizationId);
        entitlement.SubscriptionState = OrganizationSubscriptionState.Active;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var resumed = await store.AuthorizeProviderSubmissionAsync(
            workspace.Id, accepted.Instance.Id, accepted.Operation.Id, Now.AddMinutes(2));

        Assert.True(resumed.Allowed);
        var pending = Assert.Single(await store.ListPendingProviderOperationsAsync(16));
        Assert.NotNull(pending.Submission);
        Assert.Equal(accepted.Operation.Id, pending.Submission!.OperationId);
        Assert.Equal(accepted.Instance.Id, pending.Submission.InstanceId);
        var currentOperation = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.Queued, currentOperation.State);
        Assert.Equal(runId, currentOperation.DeploymentRunId);

        await store.CommitProviderSubmissionAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-operation-accepted",
            Now.AddMinutes(3)));

        var afterSubmission = Assert.Single(await store.ListPendingProviderOperationsAsync(16));
        Assert.Null(afterSubmission.Submission);
        currentOperation = await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == accepted.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, currentOperation.State);
        Assert.Equal(runId, currentOperation.DeploymentRunId);
    }

    [Fact]
    public async Task Accepted_provider_handoff_does_not_replay_submission_on_every_poll()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Provider accepted handoff");
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));

        var assignmentId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee").ToString("D");
        await store.CommitProviderSubmissionAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-operation-accepted",
            Now,
            assignmentId));

        db.ChangeTracker.Clear();
        var item = Assert.Single(await store.ListPendingProviderOperationsAsync(16));
        Assert.Equal(accepted.Operation.Id, item.OperationId);
        Assert.Null(item.Submission);
        Assert.Equal(assignmentId,
            (await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == accepted.Instance.Id)).PlacementAssignmentId);
    }

    [Fact]
    public async Task Successful_replay_upgrades_uncertain_handoff_and_stops_future_submission_replays()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Provider uncertain replay");
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
        var pendingBeforeReplay = Assert.Single(await store.ListPendingProviderOperationsAsync(16));
        Assert.NotNull(pendingBeforeReplay.Submission);

        await store.CommitProviderSubmissionAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-operation-replayed",
            Now.AddSeconds(1)));

        db.ChangeTracker.Clear();
        var pendingAfterReplay = Assert.Single(await store.ListPendingProviderOperationsAsync(16));
        Assert.Null(pendingAfterReplay.Submission);
        var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == accepted.Operation.Id);
        var run = await db.DeploymentRuns.SingleAsync(x => x.ElsaInstanceId == accepted.Instance.Id);
        Assert.Null(operation.FailureCode);
        Assert.Equal("provider.submission.accepted", run.RecoveryReason);
    }

    [Fact]
    public async Task Unknown_reconciliation_preserves_uncertain_submission_for_restart_reconstruction()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Provider uncertain reconciliation");
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));

        // This is the durable marker written when the provider may have accepted
        // the request but the worker lost the response before recording success.
        await store.CommitProviderSubmissionAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-submission-uncertain",
            Now));

        var reconciled = await new ElsaInstanceProviderReconciliationService(
                store,
                new QueueProviderPort(new ElsaInstanceProviderObservation(
                    ElsaInstanceProviderObservationKind.Unknown,
                    ElsaObservedLifecycle.Unknown,
                    ElsaInstanceProviderHealthGate.Unknown,
                    "provider-observation-unknown")),
                new FixedTimeProvider(Now.AddMinutes(1)))
            .ReconcileAsync(workspace.Id, accepted.Operation.Id);

        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.RecoveryRequired, reconciled.Outcome);
        Assert.False(reconciled.RetrySafe);

        db.ChangeTracker.Clear();
        var pending = Assert.Single(await new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now.AddMinutes(1)))
            .ListPendingProviderOperationsAsync(16));
        Assert.NotNull(pending.Submission);
        Assert.Equal(accepted.Operation.Id, pending.Submission!.OperationId);
        Assert.Equal("provider.submission.uncertain",
            (await db.ElsaInstanceOperations.SingleAsync(x => x.Id == accepted.Operation.Id)).FailureCode);
        Assert.Equal("provider.submission.uncertain",
            (await db.DeploymentRuns.SingleAsync(x => x.ElsaInstanceId == accepted.Instance.Id)).RecoveryReason);
    }

    [Fact]
    public async Task Invalid_deleting_provider_submission_is_not_reconstructed_for_replay()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Provider deleting replay");
        var operationStore = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now));

        await operationStore.CommitProviderSubmissionAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-submission-uncertain",
            Now));

        var instance = await db.ElsaInstances.SingleAsync(x => x.Id == accepted.Instance.Id);
        instance.DesiredLifecycle = ElsaDesiredLifecycle.Deleting;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var pending = Assert.Single(await operationStore.ListPendingProviderOperationsAsync(16));
        Assert.Null(pending.Submission);
    }

    [Fact]
    public async Task Non_managed_actor_create_does_not_create_a_managed_deployment_shell()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Non-managed shell workspace");
        var intent = CreateIntent() with
        {
            Placement = new ElsaPlacementIntent(
                "self-hosted", "westeurope", "dedicated", "standard-small", "public", "self-hosted")
        };

        var accepted = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId,
                workspace.Id,
                "Customer-hosted Elsa",
                "customer-hosted-elsa",
                intent,
                "create-customer-hosted",
                ActorAccountId: Guid.NewGuid()));

        Assert.NotNull(accepted.Instance);
        Assert.Empty(await db.DeploymentApplications.Where(x => x.WorkspaceId == workspace.Id).ToListAsync());
        Assert.Empty(await db.DeploymentEnvironments.Where(x => x.WorkspaceId == workspace.Id).ToListAsync());
    }

    [Fact]
    public async Task Recreating_a_deleted_slug_creates_a_distinct_managed_application_shell()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Managed recreate workspace");
        var actor = Guid.NewGuid();
        var service = new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now));
        var first = await service.CreateAsync(new ElsaInstanceCreateRequest(
            workspace.OrganizationId,
            workspace.Id,
            "Managed runtime",
            "reusable-runtime",
            WorkerIntent(),
            "create-reusable-first",
            ActorAccountId: actor));

        var firstEntity = await db.ElsaInstances.SingleAsync(x => x.Id == first.Instance.Id);
        firstEntity.DeletedAt = Now.AddMinutes(1);
        firstEntity.DesiredLifecycle = ElsaDesiredLifecycle.Deleting;
        firstEntity.ObservedLifecycle = ElsaObservedLifecycle.Deleted;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var second = await service.CreateAsync(new ElsaInstanceCreateRequest(
            workspace.OrganizationId,
            workspace.Id,
            "Managed runtime replacement",
            "reusable-runtime",
            WorkerIntent(),
            "create-reusable-second",
            ActorAccountId: actor));

        Assert.NotEqual(first.Instance.Id, second.Instance.Id);
        Assert.Equal(2, await db.DeploymentApplications.CountAsync(x => x.WorkspaceId == workspace.Id));
        Assert.Equal(2, await db.DeploymentEnvironments.CountAsync(x => x.WorkspaceId == workspace.Id && x.ElsaInstanceId != null));
    }

    [Fact]
    public async Task Recovery_resume_requeues_the_managed_deployment_run()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Recovery resume");
        var workspaceStore = new DeploymentWorkspaceStore(db);
        var claimed = await workspaceStore.ClaimNextQueuedRunAsync("deployment-worker", Now);
        Assert.NotNull(claimed);
        Assert.Equal(1, await workspaceStore.MarkStaleRunningRunsRecoveryRequiredAsync(
            Now.AddMinutes(10), TimeSpan.FromMinutes(5)));
        db.ChangeTracker.Clear();
        var lifecycleStore = CreateStore(db);
        var reconciliation = new ElsaInstanceProviderReconciliationService(
            lifecycleStore,
            new QueueProviderPort(new ElsaInstanceProviderObservation(
                ElsaInstanceProviderObservationKind.Ambiguous,
                ElsaObservedLifecycle.Unknown,
                ElsaInstanceProviderHealthGate.Unknown,
                "retry-safe-observation",
                new ElsaInstanceProviderRetryEvidence(
                    "https://evidence.example/retry/recovery-resume",
                    "sha256:" + new string('a', 64)))),
            new FixedTimeProvider(Now.AddMinutes(11)));
        var reconciled = await reconciliation.ReconcileAsync(workspace.Id, accepted.Operation.Id);
        Assert.True(reconciled.RetrySafe);

        var current = await lifecycleStore.GetInstanceAsync(workspace.Id, accepted.Instance.Id);
        Assert.NotNull(current);
        var recovered = await new ElsaInstanceLifecycleService(lifecycleStore, new FixedTimeProvider(Now.AddMinutes(12)))
            .RecoverAsync(new(workspace.Id, accepted.Instance.Id, current!.Version, "resume-recovery"));

        Assert.Equal(ElsaInstanceOperationState.Queued, recovered.Operation.State);
        db.ChangeTracker.Clear();
        var run = await db.DeploymentRuns.SingleAsync(x => x.Id == claimed!.Id);
        Assert.Equal(WorkspaceDeploymentRunStatus.Queued, run.Status);
        Assert.Null(run.WorkerId);
        Assert.Null(run.WorkerHeartbeatAt);
        Assert.Equal(DeploymentStatus.Running,
            (await db.DeploymentEnvironments.SingleAsync(x => x.Id == run.EnvironmentId)).DeploymentStatus);
        Assert.Contains(await db.DeploymentRunHistoryEvents.Where(x => x.RunId == run.Id).ToListAsync(),
            x => x.Status == WorkspaceDeploymentRunStatus.Queued &&
                 x.Message == "Deployment run requeued after provider reconciliation.");
        db.ChangeTracker.Clear();
        Assert.NotNull(await workspaceStore.ClaimNextQueuedRunAsync("recovery-worker", Now.AddMinutes(13)));
    }

    [Fact]
    public async Task Provider_reconciliation_persists_uncertainty_then_converges_and_replays()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var (_, accepted) = await QueueManagedLifecycleRunAsync(db, "Reconciliation persistence");
        var workspaceStore = new DeploymentWorkspaceStore(db);
        _ = await workspaceStore.ClaimNextQueuedRunAsync("deployment-worker", Now);
        Assert.Equal(1, await workspaceStore.MarkStaleRunningRunsRecoveryRequiredAsync(
            Now.AddMinutes(10), TimeSpan.FromMinutes(5)));
        db.ChangeTracker.Clear();
        var port = new QueueProviderPort(
            new(ElsaInstanceProviderObservationKind.Ambiguous, ElsaObservedLifecycle.Unknown,
                ElsaInstanceProviderHealthGate.Unknown, "provider-observation-1",
                new ElsaInstanceProviderRetryEvidence(
                    "https://evidence.example/retry/provider-observation-1",
                    "sha256:" + new string('b', 64))),
            new(ElsaInstanceProviderObservationKind.Confirmed, ElsaObservedLifecycle.Ready,
                ElsaInstanceProviderHealthGate.Passed, "provider-observation-2",
                retryEvidence: null,
                currentDeploymentReference: new ElsaCurrentDeploymentReference(
                    "deployment-reconciled", endpointUri: "https://managed.example.test")));
        var lifecycleStore = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now.AddMinutes(11)));
        var service = new ElsaInstanceProviderReconciliationService(
            lifecycleStore, port, new FixedTimeProvider(Now.AddMinutes(11)));
        var originalTarget = await lifecycleStore.GetTargetAsync(
            accepted.Instance.WorkspaceId, accepted.Operation.Id)
            ?? throw new InvalidOperationException("Expected a reconciliation target.");

        var uncertain = await service.ReconcileAsync(accepted.Instance.WorkspaceId, accepted.Operation.Id);
        var converged = await service.ReconcileAsync(accepted.Instance.WorkspaceId, accepted.Operation.Id);
        var replay = await service.ReconcileAsync(accepted.Instance.WorkspaceId, accepted.Operation.Id);

        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.RecoveryRequired, uncertain.Outcome);
        Assert.Equal(ElsaObservedLifecycle.Unknown, uncertain.Projection.ObservedLifecycle);
        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.Converged, converged.Outcome);
        Assert.Equal(ElsaObservedLifecycle.Ready, converged.Projection.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Healthy, converged.Projection.Health);
        Assert.True(replay.Replayed);
        Assert.Equal(2, port.Calls);
        db.ChangeTracker.Clear();
        var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == accepted.Operation.Id);
        var run = await db.DeploymentRuns.SingleAsync(x => x.Id == operation.DeploymentRunId);
        Assert.Equal(ElsaInstanceOperationState.Succeeded, operation.State);
        Assert.Equal(WorkspaceDeploymentRunStatus.Succeeded, run.Status);
        Assert.Equal(Now.AddMinutes(11), operation.CompletedAt);
        var persistedInstanceVersion = await db.ElsaInstances
            .Where(x => x.Id == accepted.Instance.Id)
            .Select(x => x.Version)
            .SingleAsync();
        Assert.Equal(persistedInstanceVersion, converged.Projection.InstanceVersion);
        Assert.Equal(persistedInstanceVersion, operation.ReconciledInstanceVersion);
        var identityBinding = await db.ElsaInstanceIdentityBindings.SingleAsync(x => x.InstanceId == accepted.Instance.Id);
        Assert.Equal(ElsaInstanceIdentityBinding.AudienceFor(accepted.Instance.Id), identityBinding.Audience);
        Assert.Equal("https://managed.example.test/managed-elsa/handoff/callback",
            identityBinding.CanonicalCallbackUri);
        Assert.Equal(2, await db.ElsaInstanceAuditEvents.CountAsync(x =>
            x.OperationId == accepted.Operation.Id && x.EventType == "lifecycle.reconciled"));
        Assert.Equal("https://evidence.example/retry/provider-observation-1",
            operation.ReconciliationRetryEvidenceReference);
        Assert.Equal("sha256:" + new string('b', 64), operation.ReconciliationRetryEvidenceDigest);
        Assert.False(replay.RetrySafe);

        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycleStore.CommitAsync(new(
            accepted.Instance.WorkspaceId,
            accepted.Instance.Id,
            accepted.Operation.Id,
            originalTarget.Instance.Version,
            originalTarget.Operation.AttemptNumber,
            originalTarget.ReconciliationVersion,
            new string('A', 64),
            originalTarget.Instance,
            originalTarget.Operation,
            ElsaInstanceProviderReconciliationService.ConvergedCode,
            false,
            null,
            null,
            Now.AddMinutes(12))));

        var concurrentReplay = await lifecycleStore.CommitAsync(new(
            accepted.Instance.WorkspaceId,
            accepted.Instance.Id,
            accepted.Operation.Id,
            originalTarget.Instance.Version,
            originalTarget.Operation.AttemptNumber,
            originalTarget.ReconciliationVersion,
            operation.ReconciliationEvidenceFingerprint!,
            originalTarget.Instance,
            originalTarget.Operation,
            ElsaInstanceProviderReconciliationService.ConvergedCode,
            false,
            null,
            null,
            Now.AddMinutes(12)));
        Assert.True(concurrentReplay.Replayed);
        Assert.False(concurrentReplay.RetrySafe);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstances SET ObservedLifecycle = 'Failed', Health = 'Unreachable', Version = Version + 1 WHERE Id = {accepted.Instance.Id}");
        db.ChangeTracker.Clear();
        var historicalReplay = await service.ReconcileAsync(accepted.Instance.WorkspaceId, accepted.Operation.Id);
        Assert.Equal(ElsaObservedLifecycle.Ready, historicalReplay.Projection.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Healthy, historicalReplay.Projection.Health);
        Assert.Equal(converged.Projection.InstanceVersion, historicalReplay.Projection.InstanceVersion);

        await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() => lifecycleStore.CommitAsync(new(
            accepted.Instance.WorkspaceId,
            accepted.Instance.Id,
            accepted.Operation.Id,
            originalTarget.Instance.Version,
            originalTarget.Operation.AttemptNumber,
            originalTarget.ReconciliationVersion,
            new string('c', 64),
            originalTarget.Instance,
            originalTarget.Operation,
            ElsaInstanceProviderReconciliationService.AmbiguousCode,
            false,
            null,
            null,
            Now.AddMinutes(12))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Azure_provider_reconciliation_persists_safe_origin_and_identity_in_EF_projection(bool declaresManagedHandoff)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var workspace = await CreateWorkspaceAsync(db, "Azure provider EF integration workspace");
        var accepted = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId,
                workspace.Id,
                "Managed Elsa",
                "azure-provider-ef",
                WorkerIntent(),
                "azure-provider-ef-create"));
        var targetIds = await AddManagedEnvironmentAsync(db, workspace, accepted.Instance.Id);
        db.ChangeTracker.Clear();

        var lifecycleStore = new EfCoreElsaInstanceLifecycleStore(
            db,
            new StaticResolutionInputSource(accepted.Instance, targetIds),
            new FixedTimeProvider(Now));
        var item = await lifecycleStore.TryClaimNextAsync("resolver-worker", Now)
            ?? throw new InvalidOperationException("Expected a claimed lifecycle operation.");
        var resolved = AzureProviderResolution(workspace.Id, accepted.Instance.Id, declaresManagedHandoff);
        var translated = AzureWorkloadPlanTranslator.Translate(
            resolved.Plan,
            new("azure-provider", "westeurope"));
        Assert.True(translated.IsAccepted,
            string.Join("; ", translated.Findings.Select(x => $"{x.Code}:{x.Scope}")));
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Queued,
            (await lifecycleStore.CommitResolvedAsync(
                CreateResolutionCommit(item, resolved, Now, "resolver-worker"))).Outcome);

        db.ChangeTracker.Clear();
        var run = await db.DeploymentRuns.SingleAsync(x => x.ElsaInstanceId == accepted.Instance.Id);
        var deploymentTarget = new ElsaInstanceLifecycleDeploymentTarget(
            run.ApplicationId,
            run.EnvironmentId,
            run.EngineId,
            run.SourceRevisionId,
            run.ConfirmationId,
            run.ActorAccountId);
        var providerScopeFingerprint = new string('a', 64);
        var operationStore = new AzureProviderOperationStore(db);
        var provider = new AzureElsaInstanceProvider(
            new AzureProviderOperationService(operationStore, new FixedTimeProvider(Now)),
            operationStore,
            operationStore,
            options:
            new AzureElsaInstanceProviderOptions
            {
                Enabled = true,
                TemplateFingerprint = new string('b', 64),
                ProviderScopeFingerprint = providerScopeFingerprint,
                SubscriptionId = "11111111-1111-1111-1111-111111111111",
                ResourceGroupNamePrefix = "rg-elsa"
            });
        var submission = await provider.SubmitAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            ElsaDesiredLifecycle.Running,
            resolved.Plan!,
            deploymentTarget,
            "westeurope",
            accepted.Instance.OrganizationId,
            ElsaInstanceOperationAction.Reconcile,
            accepted.Operation.Id.ToString("D")));
        Assert.False(submission.Replayed);

        var providerOperation = Assert.Single(await operationStore.ListRunnableAsync(Now, 16));
        Assert.Equal(declaresManagedHandoff, providerOperation.ManagedHandoff);
        await lifecycleStore.CommitProviderSubmissionAsync(new(
            workspace.Id,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            submission.CorrelationId,
            Now));

        var assignment = Assert.IsType<AzureProviderResourceAssignment>(await
            ((IAzureProviderResourceAssignmentStore)operationStore).GetAsync(
                workspace.Id,
                providerOperation.ProviderAssignmentId!.Value));
        var subscriptionId = assignment.SubscriptionId;
        var resourceGroupName = assignment.ResourceGroupName;
        var foundationDeploymentId =
            $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Resources/deployments/foundation";
        var workloadDeploymentId =
            $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Resources/deployments/workload";
        var workloadResourceId =
            $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.App/containerApps/{providerOperation.TargetKey}";
        var claimed = await operationStore.ClaimAsync(
            workspace.Id,
            providerOperation.Id,
            "azure-worker",
            "azure-lease",
            TimeSpan.FromMinutes(5),
            Now);
        Assert.NotNull(claimed);
        var checkpoint = Assert.IsType<AzureProviderOperation>(await operationStore.CheckpointAsync(
            workspace.Id,
            providerOperation.Id,
            "azure-lease",
            new(
                AzureProviderOperationPhase.HealthVerified,
                "health.verified",
                "Azure workload health verified.",
                new(
                    ResourceGroupName: resourceGroupName,
                    FoundationDeploymentId: foundationDeploymentId,
                    WorkloadDeploymentId: workloadDeploymentId,
                    WorkloadResourceId: workloadResourceId),
                "https://runtime.example.test",
                AzureProviderHealth.Healthy,
                []),
            Now,
            claimed.Version));
        var finalized = await operationStore.FinalizeAsync(
            workspace.Id,
            providerOperation.Id,
            "azure-lease",
            AzureProviderOperationStatus.Succeeded,
            "operation.succeeded",
            Now,
            checkpoint.Version);
        Assert.Equal(AzureProviderOperationStatus.Succeeded, finalized?.Status);
        Assert.Equal(AzureProviderHealth.Healthy, finalized?.Health);
        Assert.Equal("https://runtime.example.test", finalized?.Endpoint);

        db.ChangeTracker.Clear();
        var reconciled = await new ElsaInstanceProviderReconciliationService(
                lifecycleStore,
                provider,
                new FixedTimeProvider(Now.AddMinutes(1)))
            .ReconcileAsync(workspace.Id, accepted.Operation.Id);

        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.Converged, reconciled.Outcome);
        Assert.Equal(ElsaObservedLifecycle.Ready, reconciled.Projection.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Healthy, reconciled.Projection.Health);

        db.ChangeTracker.Clear();
        var persisted = await db.ElsaInstances
            .Include(x => x.IdentityBinding)
            .SingleAsync(x => x.Id == accepted.Instance.Id);
        Assert.Equal(ElsaObservedLifecycle.Ready, persisted.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Healthy, persisted.Health);
        Assert.Equal("https://runtime.example.test", persisted.CurrentDeploymentEndpointUri);
        Assert.Equal(ElsaInstanceIdentityBinding.AudienceFor(accepted.Instance.Id), persisted.IdentityBinding?.Audience);
        Assert.Equal(
            "https://runtime.example.test/managed-elsa/handoff/callback",
            persisted.IdentityBinding?.CanonicalCallbackUri);

        var customerProjection = Assert.Single(
            (await new EfCoreManagedElsaInstanceApiStore(db).ListInstancesAsync(workspace.Id, 1, 10)).Items);
        Assert.Equal(ElsaObservedLifecycle.Ready, customerProjection.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Healthy, customerProjection.Health);
        Assert.Equal("https://runtime.example.test", customerProjection.CurrentDeploymentReference?.EndpointUri);
        Assert.Equal(ElsaInstanceIdentityBinding.AudienceFor(accepted.Instance.Id),
            customerProjection.IdentityBinding?.Audience);
        Assert.Equal("https://runtime.example.test/managed-elsa/handoff/callback",
            customerProjection.IdentityBinding?.CanonicalCallbackUri);
        // The runtime serves the handoff only when the release declared it and the provider configured it;
        // that fact, not the binding alone, decides whether Control offers to open the instance.
        Assert.Equal(declaresManagedHandoff, persisted.CurrentDeploymentManagedHandoff);
        Assert.Equal(declaresManagedHandoff, customerProjection.CurrentDeploymentReference?.ManagedHandoff);
        Assert.Equal(declaresManagedHandoff, await new EfCoreManagedElsaInstanceIdentityStore(db)
            .FindOpenableAsync(accepted.Instance.OrganizationId, accepted.Instance.Id) is not null);
        var customerJson = JsonSerializer.Serialize(customerProjection);
        Assert.DoesNotContain(foundationDeploymentId, customerJson, StringComparison.Ordinal);
        Assert.DoesNotContain(workloadDeploymentId, customerJson, StringComparison.Ordinal);
        Assert.DoesNotContain(workloadResourceId, customerJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_first_persisted_instance_is_quarantined_and_later_valid_work_continues()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Malformed work workspace");
        var first = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Malformed Elsa", "malformed-worker-elsa", WorkerIntent(), "malformed-create"));
        var second = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now.AddMinutes(1)))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Valid Elsa", "valid-worker-elsa", WorkerIntent(), "valid-create"));
        var target = await AddManagedEnvironmentAsync(db, workspace, second.Instance.Id);

        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ElsaInstances SET FeatureOverridesJson = 'not-json' WHERE Id = {first.Instance.Id}");
        db.ChangeTracker.Clear();
        var store = new EfCoreElsaInstanceLifecycleStore(
            db,
            new StaticResolutionInputSource(second.Instance, target),
            new FixedTimeProvider(Now.AddMinutes(2)));
        var result = await new ElsaInstanceLifecycleWorker(
                store,
                new StaticResolver(SuccessfulResolution(workspace.Id, second.Instance.Id)),
                new FixedTimeProvider(Now.AddMinutes(2)))
            .ProcessAvailableAsync("worker-one");

        Assert.Single(result.Results);
        Assert.Equal(second.Operation.Id, result.Results[0].Operation.Id);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Queued, result.Results[0].Outcome);
        Assert.Equal(ElsaInstanceOperationState.Failed,
            (await db.ElsaInstanceOperations.SingleAsync(x => x.Id == first.Operation.Id)).State);
        Assert.Equal("outbox.invalid",
            (await db.ElsaInstanceOperations.SingleAsync(x => x.Id == first.Operation.Id)).FailureCode);
        Assert.Equal(ElsaInstanceOperationState.Queued,
            (await db.ElsaInstanceOperations.SingleAsync(x => x.Id == second.Operation.Id)).State);
    }

    [Fact]
    public async Task Untrusted_first_envelope_is_skipped_and_later_valid_work_continues()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Untrusted work workspace");
        var first = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Untrusted Elsa", "untrusted-worker-elsa", WorkerIntent(), "untrusted-create"));
        var second = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now.AddMinutes(1)))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Valid Elsa", "valid-after-untrusted-elsa", WorkerIntent(), "valid-after-untrusted-create"));
        var target = await AddManagedEnvironmentAsync(db, workspace, second.Instance.Id);

        // Bypass the application boundary to model a row whose operation cannot
        // be safely reconstructed. It must not be quarantined or starve later work.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstanceOperations SET IdempotencyKey = '' WHERE Id = {first.Operation.Id}");
        db.ChangeTracker.Clear();
        var store = new EfCoreElsaInstanceLifecycleStore(
            db,
            new StaticResolutionInputSource(second.Instance, target),
            new FixedTimeProvider(Now.AddMinutes(2)));
        var result = await new ElsaInstanceLifecycleWorker(
                store,
                new StaticResolver(SuccessfulResolution(workspace.Id, second.Instance.Id)),
                new FixedTimeProvider(Now.AddMinutes(2)))
            .ProcessAvailableAsync("worker-one");

        Assert.Single(result.Results);
        Assert.Equal(second.Operation.Id, result.Results[0].Operation.Id);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Queued, result.Results[0].Outcome);
        Assert.Equal(ElsaInstanceOperationState.Accepted,
            (await db.ElsaInstanceOperations.SingleAsync(x => x.Id == first.Operation.Id)).State);
        Assert.Equal(ElsaInstanceOperationState.Queued,
            (await db.ElsaInstanceOperations.SingleAsync(x => x.Id == second.Operation.Id)).State);
        var quarantined = await db.ElsaInstanceLifecycleOutbox
            .SingleAsync(x => x.OperationId == first.Operation.Id);
        Assert.Equal("outbox.invalid", quarantined.QuarantineCode);
        Assert.Equal(Now.AddMinutes(2), quarantined.QuarantinedAt);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task Corrupt_lease_version_is_quarantined_without_blocking_later_work(int leaseVersion)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, $"Corrupt lease workspace {leaseVersion}");
        var first = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Corrupt Elsa", $"corrupt-lease-{leaseVersion}", WorkerIntent(), $"corrupt-lease-create-{leaseVersion}"));
        var second = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now.AddMinutes(1)))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Valid Elsa", $"valid-after-lease-{leaseVersion}", WorkerIntent(), $"valid-after-lease-create-{leaseVersion}"));
        var target = await AddManagedEnvironmentAsync(db, workspace, second.Instance.Id);

        // Disable only the SQLite compatibility trigger to model a legacy row
        // that predates the database range guard.
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS TR_ElsaInstanceOperations_LeaseVersion_Range_Insert;");
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS TR_ElsaInstanceOperations_LeaseVersion_Range_Update;");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstanceOperations SET LeaseVersion = {leaseVersion} WHERE Id = {first.Operation.Id}");
        db.ChangeTracker.Clear();
        var store = new EfCoreElsaInstanceLifecycleStore(
            db,
            new StaticResolutionInputSource(second.Instance, target),
            new FixedTimeProvider(Now.AddMinutes(2)));
        var result = await new ElsaInstanceLifecycleWorker(
                store,
                new StaticResolver(SuccessfulResolution(workspace.Id, second.Instance.Id)),
                new FixedTimeProvider(Now.AddMinutes(2)))
            .ProcessAvailableAsync("worker-one");

        Assert.Single(result.Results);
        Assert.Equal(second.Operation.Id, result.Results[0].Operation.Id);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Queued, result.Results[0].Outcome);
        Assert.Equal(ElsaInstanceOperationState.Accepted,
            (await db.ElsaInstanceOperations.SingleAsync(x => x.Id == first.Operation.Id)).State);
        var quarantined = await db.ElsaInstanceLifecycleOutbox
            .SingleAsync(x => x.OperationId == first.Operation.Id);
        Assert.Equal("outbox.invalid", quarantined.QuarantineCode);
        Assert.Equal(Now.AddMinutes(2), quarantined.QuarantinedAt);
    }

    private static async Task<Workspace> CreateWorkspaceAsync(CatalogDbContext db, string name)
    {
        var workspace = new Workspace { Name = name };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = workspace.OrganizationId,
            ManagedHostingEnabled = true,
            SubscriptionState = OrganizationSubscriptionState.Active,
            MaxInstances = int.MaxValue,
            SyncedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();
        return workspace;
    }

    private static EfCoreElsaInstanceLifecycleStore CreateStore(CatalogDbContext db) =>
        new(db, EmptyResolutionInputSource.Instance);

    private static async Task<(Workspace Workspace, ElsaInstanceLifecycleAcceptance Accepted)>
        QueueManagedLifecycleRunAsync(CatalogDbContext db, string workspaceName)
    {
        var workspace = await CreateWorkspaceAsync(db, workspaceName);
        var accepted = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", $"managed-{Guid.NewGuid():N}",
                WorkerIntent(), $"create-{Guid.NewGuid():N}"));
        var targetIds = await AddManagedEnvironmentAsync(db, workspace, accepted.Instance.Id);
        db.ChangeTracker.Clear();
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, new StaticResolutionInputSource(accepted.Instance, targetIds), new FixedTimeProvider(Now));
        var result = await new ElsaInstanceLifecycleWorker(
            store, new StaticResolver(SuccessfulResolution(workspace.Id, accepted.Instance.Id)),
            new FixedTimeProvider(Now)).ProcessAvailableAsync("resolver-worker");
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Queued, Assert.Single(result.Results).Outcome);
        return (workspace, accepted);
    }

    private static async Task CompleteOperationAsync(CatalogDbContext db, Guid operationId)
    {
        db.ChangeTracker.Clear();
        var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == operationId);
        operation.State = ElsaInstanceOperationState.Succeeded;
        await db.SaveChangesAsync();
    }

    private static async Task CompleteManagedRunAsync(CatalogDbContext db, Guid operationId, Guid instanceId)
    {
        db.ChangeTracker.Clear();
        var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == operationId);
        operation.State = ElsaInstanceOperationState.Running;
        await db.SaveChangesAsync();
        operation.State = ElsaInstanceOperationState.Succeeded;
        operation.CompletedAt = Now.AddMinutes(1);
        var run = await db.DeploymentRuns.SingleAsync(x => x.ElsaInstanceId == instanceId);
        run.Status = WorkspaceDeploymentRunStatus.Succeeded;
        run.CompletedAt = Now.AddMinutes(1);
        var instance = await db.ElsaInstances.SingleAsync(x => x.Id == instanceId);
        instance.CurrentDeploymentId = "deployment-safe";
        instance.PlacementAssignmentId = "placement-safe";
        instance.ObservedLifecycle = ElsaObservedLifecycle.Ready;
        instance.Health = ElsaInstanceHealth.Healthy;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static ElsaInstanceLifecycleOutboxMessage NewOutbox(
        ElsaInstanceTransitionResult transition) => new(
        Guid.NewGuid(),
        transition.Instance.WorkspaceId,
        transition.Instance.Id,
        transition.Operation.Id,
        transition.Operation.Action,
        transition.Operation.RequestHash,
        DateTimeOffset.UtcNow);

    private static string RequestHash(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static string Digest(char value) => "sha256:" + new string(value, 64);

    private static async Task<Captured<T>> CaptureAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return new Captured<T>(await action(), null);
        }
        catch (Exception error)
        {
            return new Captured<T>(default, error);
        }
    }

    private static ElsaInstanceIntent CreateIntent() => new(
        new ElsaReleaseIntent("server-studio", "3.10", "3.10.4"),
        new ElsaApplicationIntent(
            "combined",
            "starter",
            new Dictionary<string, ElsaFeatureOverride> { ["replicas"] = ElsaFeatureOverride.FromNumber(3) },
            "approved"),
            new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed"));

    private static ElsaInstanceIntent WorkerIntent() => new(
        new ElsaReleaseIntent("future-runtime", "5.0", "5.0.0-preview.1", "preview"),
        new ElsaApplicationIntent("server-studio"),
        new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed"));

    private static async Task<(Guid ApplicationId, Guid EnvironmentId)> AddManagedEnvironmentAsync(
        CatalogDbContext db,
        Workspace workspace,
        Guid instanceId)
    {
        var applicationId = Guid.NewGuid();
        var environmentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.DeploymentApplications.Add(new DeploymentApplicationEntity
        {
            Id = applicationId,
            WorkspaceId = workspace.Id,
            Name = $"Application-{instanceId:N}",
            CreatedAt = now,
            UpdatedAt = now
        });
        db.DeploymentEnvironments.Add(new DeploymentEnvironmentEntity
        {
            Id = environmentId,
            WorkspaceId = workspace.Id,
            ApplicationId = applicationId,
            ElsaInstanceId = instanceId,
            Name = "Production",
            Tier = EnvironmentTier.Production,
            DeploymentStatus = DeploymentStatus.Blocked,
            DriftStatus = DriftStatus.Unknown,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return (applicationId, environmentId);
    }

    private static ElsaInstanceLifecycleResolutionInput ResolutionInput(
        ElsaInstance instance,
        (Guid ApplicationId, Guid EnvironmentId) target) => new(
        new ElsaInstancePlanResolutionRequest(
            instance.Intent,
            new(new("future-runtime", null, null, null), [], [], [], null),
            AdmittedManifest(),
            "plan_worker_01",
            $"https://control.example.test/api/workspaces/{instance.WorkspaceId:D}/instances/{instance.Id:D}/resolved-plans/plan_worker_01",
            instance.WorkspaceId),
        new(target.ApplicationId, target.EnvironmentId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

    private static async Task<ElsaInstanceLifecycleRequest> CreateConfirmedDeleteRequestAsync(
        CatalogDbContext db,
        Guid workspaceId,
        Guid instanceId,
        int expectedVersion,
        string idempotencyKey,
        DateTimeOffset? confirmedAt = null)
    {
        var actorAccountId = Guid.NewGuid();
        var confirmation = await new DeploymentWorkspaceStore(db).CreateConfirmationAsync(
            workspaceId,
            new CreateActionConfirmationRequest(
                ConfirmationActionType.DeleteManagedInstance,
                instanceId.ToString("D"),
                actorAccountId,
                TimeSpan.FromDays(1)),
            confirmedAt ?? Now);
        return new ElsaInstanceLifecycleRequest(
            workspaceId,
            instanceId,
            expectedVersion,
            idempotencyKey,
            DeleteConfirmationId: confirmation.Id,
            ActorAccountId: actorAccountId);
    }

    private static ReleaseManifestAdmissionResult AdmittedManifest() =>
        new(
            true,
            "https://example.test/manifests/5.0.0-preview.1.json",
            "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            new(
                "1",
                new("future-runtime", "commercial", "5.0", "5.0.0-preview.1", "preview", "preview",
                    new("https://example.test/repo", "0123456789abcdef0123456789abcdef01234567", "build", "1")),
                [new(
                    "server-studio",
                    ["elsa.server"],
                    [new("paid", "registry.example.test/elsa/server@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")],
                    new Dictionary<string, string> { ["server"] = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
                    new Dictionary<string, string>(),
                    new("1", []),
                    new(null, null, [], null))]),
            new("https://example.test/signatures/5.0.0-preview.1.sig", "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"),
            "paid",
            "server-studio",
            []);

    private static ElsaInstancePlanResolutionResult AzureProviderResolution(
        Guid workspaceId,
        Guid instanceId,
        bool declaresManagedHandoff = false)
    {
        var baseline = SuccessfulResolution(workspaceId, instanceId);
        var baselinePlan = baseline.Plan!;
        const string imageDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string manifestDigest = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        const string signatureDigest = "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        var manifestReference = baselinePlan.Release.ReleaseManifestReference;
        var component = baselinePlan.Topology.Components.Single();
        var plan = baselinePlan with
        {
            Release = baselinePlan.Release with
            {
                ComponentDeclarations = new(
                    "1",
                    imageDigest,
                    [
                        new(AzureWorkloadPlanTranslator.SqlWorkflowPackageId, "5.0.0-preview.1"),
                        new(AzureWorkloadPlanTranslator.SqlQuartzPackageId, "5.0.0-preview.1")
                    ])
            },
            Topology = baselinePlan.Topology with
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
                        },
                        Capabilities = declaresManagedHandoff
                            ? [.. component.Capabilities, ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaHandoffV1]
                            : component.Capabilities
                    }
                ]
            },
            Network = baselinePlan.Network with
            {
                Ingress = "public",
                Egress = "unrestricted",
                RequiresPrivateConnectivity = false,
                Endpoints = baselinePlan.Network.Endpoints
                    .Select(endpoint => endpoint with
                    {
                        Visibility = "public",
                        Protocol = "https",
                        RequiresTls = true
                    })
                    .ToArray()
            },
            Capacity = baselinePlan.Capacity with { Components = GovernedTestCapacity.StandardSmall(component.Id) },
            Isolation = AzureWorkloadPlanTranslator.SupportedIsolation,
            Configuration = new(
            [
                new("database:connectionstring", "string", true, true, false, null, null,
                    "secret://vault/database-connection", null),
                new("identity:signingkey", "string", true, true, false, null, null,
                    "secret://vault/identity-signing-key", null),
                new("admin:password", "string", true, true, false, null, null,
                    "secret://vault/admin-password", null)
            ]),
            Evidence =
            [
                new(ReleaseManifestEvidenceKinds.Manifest, manifestReference, manifestDigest,
                    ReleaseManifestEvidenceContract.DescriptionFor(ReleaseManifestEvidenceKinds.Manifest)),
                new(ReleaseManifestEvidenceKinds.Signature,
                    "https://example.test/signatures/5.0.0-preview.1.sig", signatureDigest,
                    ReleaseManifestEvidenceContract.DescriptionFor(ReleaseManifestEvidenceKinds.Signature))
            ]
        };
        var reference = new ElsaResolvedPlanReference(
            "plan_worker_01",
            1,
            ResolvedElsaApplicationPlanSerialization.ComputeContentHash(plan),
            $"https://control.example.test/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/resolved-plans/plan_worker_01");
        return new(
            true,
            plan,
            reference,
            new(reference, baselinePlan.Release.DistributionId, baselinePlan.Release.ReleaseLine,
                baselinePlan.Release.Version, manifestDigest,
                [new(component.Id, imageDigest)]),
            []);
    }

    private static ElsaInstanceLifecycleResolutionCommit CreateResolutionCommit(
        ElsaInstanceLifecycleWorkItem item,
        ElsaInstancePlanResolutionResult resolved,
        DateTimeOffset committedAt,
        string workerId = "worker-one")
    {
        var queued = item.Operation.TransitionTo(ElsaInstanceOperationState.Queued);
        var resolvedInstance = item.Instance.AttachResolvedPlan(resolved.Reference!, resolved.CurrentResolvedRelease!);
        return new ElsaInstanceLifecycleResolutionCommit(
            item.Outbox.WorkspaceId,
            item.Outbox.InstanceId,
            item.Operation.Id,
            item.Outbox.Id,
            item.Outbox.RequestHash,
            workerId,
            queued,
            resolvedInstance,
            new ElsaInstanceLifecycleResolvedPlan(
                resolved.Reference!,
                ResolvedElsaApplicationPlanSerialization.Serialize(resolved.Plan!)),
            item.Resolution.DeploymentTarget,
            committedAt,
            item.LeaseToken,
            item.LeaseVersion);
    }

    private static ElsaInstancePlanResolutionResult SuccessfulResolution(Guid workspaceId, Guid instanceId)
    {
        var provisionalReference = new ElsaResolvedPlanReference(
            "plan_worker_01",
            1,
            "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            $"https://control.example.test/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/resolved-plans/plan_worker_01");
        var plan = new ResolvedElsaApplicationPlan(
            "1",
            new("future-runtime", "5.0", "5.0.0-preview.1", "https://example.test/repo", "0123456789abcdef0123456789abcdef01234567", "https://example.test/manifests/5.0.0-preview.1.json", "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"),
            new("server-studio", [new("server", ["server"], new("paid", "registry.example.test/elsa", "registry.example.test/elsa/server@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), ["elsa.server"], [], [])]),
            [],
            new([]),
            new([], []),
            new("public", "public", false, [], []),
            "dedicated",
            new("preview", "preview", "stable", "automatic-within-minor", "explicit-approval", "explicit-migration"),
            [],
            [new(ReleaseManifestEvidenceKinds.Manifest, "https://example.test/manifests/5.0.0-preview.1.json", "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", ReleaseManifestEvidenceContract.DescriptionFor(ReleaseManifestEvidenceKinds.Manifest))]);
        var reference = new ElsaResolvedPlanReference(
            provisionalReference.PlanId,
            provisionalReference.SchemaVersion,
            ResolvedElsaApplicationPlanSerialization.ComputeContentHash(plan),
            provisionalReference.PlanUri);
        return new(
            true,
            plan,
            reference,
            new(reference, "future-runtime", "5.0", "5.0.0-preview.1", "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", [new("server", "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]),
            []);
    }

    private sealed class StaticResolutionInputSource(
        ElsaInstance instance,
        (Guid ApplicationId, Guid EnvironmentId) target) : IElsaInstanceLifecycleResolutionInputSource
    {
        public Task<ElsaInstanceLifecycleResolutionInput?> GetAsync(
            ElsaInstance requestedInstance,
            ElsaInstanceOperation operation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ElsaInstanceLifecycleResolutionInput?>(ResolutionInput(instance, target));
    }

    private sealed class DurableConsentResolutionInputSource(
        (Guid ApplicationId, Guid EnvironmentId) target,
        Action<ElsaInstance> observe) : IElsaInstanceLifecycleResolutionInputSource
    {
        public Task<ElsaInstanceLifecycleResolutionInput?> GetAsync(
            ElsaInstance requestedInstance,
            ElsaInstanceOperation operation,
            CancellationToken cancellationToken = default)
        {
            observe(requestedInstance);
            return Task.FromResult<ElsaInstanceLifecycleResolutionInput?>(ResolutionInput(requestedInstance, target));
        }
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

    private sealed class StaticResolver(ElsaInstancePlanResolutionResult result) : IElsaInstancePlanResolver
    {
        public Task<ElsaInstancePlanResolutionResult> ResolveAsync(
            ElsaInstancePlanResolutionRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class QueueProviderPort(params ElsaInstanceProviderObservation[] observations)
        : IElsaInstanceProviderReconciliationPort
    {
        private readonly Queue<ElsaInstanceProviderObservation> _observations = new(observations);

        public int Calls { get; private set; }

        public Task<ElsaInstanceProviderObservation> ObserveAsync(
            ElsaInstanceProviderReconciliationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_observations.Dequeue().Correlate(request));
        }
    }

    private sealed class ThrowingCleanupPort : IElsaInstanceProviderCleanupPort
    {
        public Task<ElsaInstanceCleanupObservation> CleanupAsync(
            ElsaInstanceCleanupRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Provider cleanup must not run for a local tombstone.");
    }

    private sealed class QueueCleanupPort(params ElsaInstanceCleanupObservation[] observations)
        : IElsaInstanceProviderCleanupPort
    {
        private readonly Queue<ElsaInstanceCleanupObservation> _observations = new(observations);

        public Task<ElsaInstanceCleanupObservation> CleanupAsync(
            ElsaInstanceCleanupRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_observations.Dequeue());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// Simulates a claim commit that succeeds durably but whose acknowledgement to the caller is lost:
    /// it throws once, right after the transaction commits, an exception the test's execution strategy
    /// classifies as transient so the whole unit is retried.
    /// </summary>
    private sealed class LostCommitAcknowledgementInterceptor(int failures = 1) : DbTransactionInterceptor
    {
        private int _failed;

        public int Committed { get; private set; }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Committed++;
            if (_failed < failures)
            {
                _failed++;
                throw new LostCommitAcknowledgementException();
            }

            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }

    private sealed class LostCommitAcknowledgementException : Exception;

    private static CatalogDbContext CreateMigratedContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        return new CatalogDbContext(options);
    }

    private sealed record Captured<T>(T? Value, Exception? Error);
}
