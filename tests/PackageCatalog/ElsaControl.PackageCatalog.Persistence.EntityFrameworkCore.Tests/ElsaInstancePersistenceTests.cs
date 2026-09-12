using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ElsaInstancePersistenceTests
{
    private const string PreviousMigration = "20260716031322_AddWorkspacePermissionLifecycle";

    [Fact]
    public async Task Additive_migration_preserves_existing_environment_as_unbound()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        Guid environmentId;
        await using (var db = CreateMigratedContext(connection))
        {
            await db.Database.MigrateAsync(PreviousMigration);
            var workspace = new Workspace { Name = "Legacy workspace" };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();
            var applicationId = Guid.NewGuid();
            environmentId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow.UtcTicks;
            // The current EF model contains ElsaInstanceId, so it cannot query the
            // pre-change schema. Seed through the old column set after migrating
            // only to the historical boundary, then apply the additive migration.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO DeploymentApplications
                    (Id, WorkspaceId, Name, Description, CreatedAt, UpdatedAt, CreatedByAccountId, UpdatedByAccountId)
                VALUES ({applicationId}, {workspace.Id}, {"Legacy application"}, {null}, {now}, {now}, {null}, {null});
                INSERT INTO DeploymentEnvironments
                    (Id, WorkspaceId, ApplicationId, Name, Tier, TierRequiresReview, DesiredRevisionId,
                     DeployedRevisionId, DeploymentStatus, DriftStatus, CreatedAt, UpdatedAt)
                VALUES ({environmentId}, {workspace.Id}, {applicationId}, {"Legacy environment"}, {"Production"}, {false},
                        {null}, {null}, {"Blocked"}, {"Unknown"}, {now}, {now});
                """);

            await db.Database.MigrateAsync();
        }

        await using (var db = CreateMigratedContext(connection))
        {
            var unbound = await db.Database.SqlQuery<Guid?>(
                    $"SELECT ElsaInstanceId AS Value FROM DeploymentEnvironments WHERE Id = {environmentId}")
                .SingleAsync();
            Assert.Null(unbound);

            var tables = await ReadScalarStringsAsync(db, "SELECT name FROM sqlite_master WHERE type = 'table'");
            Assert.Contains("ElsaInstances", tables);
            Assert.Contains("ElsaInstanceOperations", tables);
            Assert.Contains("ElsaInstanceIntentRevisions", tables);
            Assert.Contains("ElsaInstanceLifecycleOutbox", tables);
            Assert.Contains("ElsaInstanceAuditEvents", tables);
            Assert.Contains("ElsaInstanceIdentityBindings", tables);
            Assert.Contains("ElsaInstanceMigrations", tables);
        }
    }

    [Fact]
    public async Task Intent_revisions_and_lifecycle_outbox_are_safe_and_append_only()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Intent revision workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();

        var operation = NewOperation(workspace, instance, ElsaInstanceOperationState.Succeeded, "intent-revision-operation");
        var revision = NewIntentRevision(workspace, instance);
        var outbox = new ElsaInstanceLifecycleOutboxEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = workspace.OrganizationId,
            WorkspaceId = workspace.Id,
            InstanceId = instance.Id,
            OperationId = operation.Id,
            Action = operation.Action,
            RequestHash = operation.RequestHash,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ElsaInstanceOperations.Add(operation);
        db.ElsaInstanceIntentRevisions.Add(revision);
        db.ElsaInstanceLifecycleOutbox.Add(outbox);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var savedRevision = await db.ElsaInstanceIntentRevisions.SingleAsync();
        var savedOutbox = await db.ElsaInstanceLifecycleOutbox.SingleAsync();
        Assert.Equal(1, savedRevision.RevisionNumber);
        Assert.Equal(64, savedRevision.ContentHash.Length);
        Assert.Equal(operation.Id, savedOutbox.OperationId);
        Assert.Equal(operation.RequestHash, savedOutbox.RequestHash);
        Assert.DoesNotContain("Payload", db.Model.FindEntityType(typeof(ElsaInstanceIntentRevisionEntity))!.GetProperties().Select(x => x.Name));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            savedRevision.TopologyId = "changed";
            await db.SaveChangesAsync();
        });
        db.ChangeTracker.Clear();
        savedOutbox = await db.ElsaInstanceLifecycleOutbox.SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            savedOutbox.RequestHash = new string('b', 64);
            await db.SaveChangesAsync();
        });

        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstanceIntentRevisions SET TopologyId = {"changed"} WHERE Id = {savedRevision.Id}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM ElsaInstanceLifecycleOutbox WHERE Id = {savedOutbox.Id}"));
    }

    [Fact]
    public async Task Environment_binding_is_explicit_and_globally_unique()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Managed workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();

        var application = new DeploymentApplicationEntity
        {
            WorkspaceId = workspace.Id,
            Name = "Managed app",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.DeploymentApplications.Add(application);
        db.DeploymentEnvironments.AddRange(
            NewEnvironment(workspace.Id, application.Id, instance.Id, "Managed one"),
            NewEnvironment(workspace.Id, application.Id, instance.Id, "Managed two"));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void Model_contains_provider_correct_reservation_filters()
    {
        using var db = CreateEnsureCreatedContext();
        var operation = db.Model.FindEntityType(typeof(ElsaInstanceOperationEntity))!;
        var operationFilters = operation.GetIndexes().Select(x => x.GetFilter()).Where(x => x is not null).ToArray();
        Assert.Contains(operationFilters, x => x!.Contains("Accepted", StringComparison.Ordinal) && x.Contains("RecoveryRequired", StringComparison.Ordinal));
        Assert.Contains(operationFilters, x => x!.Contains("WaitingForPriorOperation", StringComparison.Ordinal));

        var run = db.Model.FindEntityType(typeof(DeploymentRunEntity))!;
        Assert.Contains(run.GetIndexes().Select(x => x.GetFilter()), x => x == "Status IN ('Queued', 'Running', 'RecoveryRequired')");

        var migration = db.Model.FindEntityType(typeof(ElsaInstanceMigrationEntity))!;
        var activeMigrationIndex = Assert.Single(migration.GetIndexes(), index =>
            index.IsUnique && index.Properties.Count == 1 && index.Properties[0].Name == nameof(ElsaInstanceMigrationEntity.InstanceId));
        Assert.Equal("Phase <> 'RolledBack' AND Phase <> 'Released' AND Phase <> 'Failed'", activeMigrationIndex.GetFilter());

        var identityBinding = db.Model.FindEntityType(typeof(ElsaInstanceIdentityBindingEntity))!;
        var callbackIndex = Assert.Single(identityBinding.GetIndexes(), index =>
            index.Properties.Count == 1 && index.Properties[0].Name == nameof(ElsaInstanceIdentityBindingEntity.CanonicalCallbackUri));
        Assert.True(callbackIndex.IsUnique);
        Assert.Null(callbackIndex.GetFilter());
    }

    [Fact]
    public async Task Active_and_waiting_operation_reservations_are_independently_unique()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Operation workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();

        db.ElsaInstanceOperations.AddRange(
            NewOperation(workspace, instance, ElsaInstanceOperationState.Queued, "active-1"),
            NewOperation(workspace, instance, ElsaInstanceOperationState.Running, "active-2"));
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.ElsaInstanceOperations.AddRange(
            NewOperation(workspace, instance, ElsaInstanceOperationState.WaitingForPriorOperation, "waiting-1"),
            NewOperation(workspace, instance, ElsaInstanceOperationState.WaitingForPriorOperation, "waiting-2"));
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Idempotency_key_collision_is_rejected_even_for_completed_operations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Idempotency workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var first = NewOperation(workspace, null, ElsaInstanceOperationState.Succeeded, "same-key");
        var second = NewOperation(workspace, null, ElsaInstanceOperationState.Failed, "different-id");
        second.IdempotencyScope = first.IdempotencyScope;
        second.IdempotencyKey = first.IdempotencyKey;
        db.ElsaInstanceOperations.AddRange(first, second);

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Empty_instance_id_reports_instance_id_validation_error()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Instance ID validation workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        var operation = NewOperation(workspace, null, ElsaInstanceOperationState.Succeeded, "empty-instance-id");
        operation.InstanceId = Guid.Empty;
        db.ElsaInstanceOperations.Add(operation);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal("An instance operation requires a non-empty instance ID.", exception.Message);
    }

    [Fact]
    public async Task Cross_workspace_application_environment_and_instance_links_fail_closed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var first = new Workspace { Name = "First workspace" };
        var second = new Workspace { Name = "Second workspace" };
        db.Workspaces.AddRange(first, second);
        await db.SaveChangesAsync();
        var application = new DeploymentApplicationEntity
        {
            WorkspaceId = first.Id,
            Name = "First app",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.DeploymentApplications.Add(application);
        await db.SaveChangesAsync();
        db.DeploymentEnvironments.Add(NewEnvironment(second.Id, application.Id, null, "Cross-owned environment"));
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var instance = NewInstance(first.OrganizationId, first.Id);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();
        db.ElsaInstanceOperations.Add(NewOperation(second, instance, ElsaInstanceOperationState.Succeeded, "cross-operation"));
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.ElsaInstanceAuditEvents.Add(new ElsaInstanceAuditEventEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = second.OrganizationId,
            WorkspaceId = second.Id,
            InstanceId = instance.Id,
            Sequence = 1,
            EventType = "instance.updated",
            OccurredAt = DateTimeOffset.UtcNow
        });
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Identity_binding_audience_and_callback_are_unique()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Binding workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var first = NewInstance(workspace.OrganizationId, workspace.Id);
        var second = NewInstance(workspace.OrganizationId, workspace.Id);
        db.ElsaInstances.AddRange(first, second);
        await db.SaveChangesAsync();
        var firstAudience = $"urn:elsa:instance:{first.Id:D}".ToLowerInvariant();
        var secondAudience = $"urn:elsa:instance:{second.Id:D}".ToLowerInvariant();
        db.ElsaInstanceIdentityBindings.AddRange(
            NewBinding(first.Id, firstAudience, "https://control.example/managed-elsa/handoff/callback"),
            NewBinding(second.Id, secondAudience, "https://control.example/managed-elsa/handoff/callback"));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Identity_binding_callback_must_match_the_verified_endpoint_origin()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Callback workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();

        db.ElsaInstanceIdentityBindings.Add(NewBinding(
            instance.Id,
            $"urn:elsa:instance:{instance.Id:D}",
            "https://other.example/managed-elsa/handoff/callback"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.ElsaInstanceIdentityBindings.Add(new ElsaInstanceIdentityBindingEntity
        {
            InstanceId = instance.Id,
            Audience = $"urn:elsa:instance:{instance.Id:D}",
            CanonicalCallbackUri = "https://control.example/managed-elsa/handoff/callback",
            VerifiedEndpointOrigin = "https://control.example/runtime",
            BindingVersion = 1,
            ChangedAt = DateTimeOffset.UtcNow
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.ElsaInstanceIdentityBindings.Add(new ElsaInstanceIdentityBindingEntity
        {
            InstanceId = instance.Id,
            Audience = $"urn:elsa:instance:{instance.Id:D}",
            CanonicalCallbackUri = "http://localhost/managed-elsa/handoff/callback",
            VerifiedEndpointOrigin = "http://localhost",
            BindingVersion = 1,
            ChangedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Managed_instance_deployment_endpoint_must_be_an_https_origin()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Managed endpoint workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        instance.CurrentDeploymentId = "deployment-managed";
        instance.CurrentDeploymentEndpointUri = "https://runtime.example.test/health";
        db.ElsaInstances.Add(instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal("Managed deployment endpoint origin is invalid.", exception.Message);
    }

    [Fact]
    public async Task Managed_instance_catalog_omits_stale_binding_values()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Managed catalog workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        instance.CurrentDeploymentId = "deployment-current";
        instance.CurrentDeploymentEndpointUri = "https://control.example";
        instance.CurrentDeploymentManagedHandoff = true;
        db.ElsaInstances.Add(instance);
        db.ElsaInstanceIdentityBindings.Add(NewBinding(
            instance.Id,
            ElsaInstanceIdentityBinding.AudienceFor(instance.Id),
            "https://control.example/managed-elsa/handoff/callback"));
        await db.SaveChangesAsync();

        var catalog = new EfCoreManagedElsaInstanceCatalog(db);
        var current = Assert.Single(await catalog.ListAsync(workspace.Id));
        Assert.Equal(ElsaInstanceIdentityBinding.AudienceFor(instance.Id), current.Audience);
        Assert.Equal("https://control.example/managed-elsa/handoff/callback", current.CallbackUri?.OriginalString);
        Assert.Equal(1, current.BindingVersion);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstances SET CurrentDeploymentEndpointUri = {"https://control.example/runtime"} WHERE Id = {instance.Id}");
        db.ChangeTracker.Clear();

        var malformed = Assert.Single(await catalog.ListAsync(workspace.Id));
        Assert.Null(malformed.Audience);
        Assert.Null(malformed.CallbackUri);
        Assert.Null(malformed.BindingVersion);

        var legacyEntity = await db.ElsaInstances
            .Include(x => x.IdentityBinding)
            .SingleAsync(x => x.Id == instance.Id);
        var legacyInstance = EfCoreElsaInstanceLifecycleStore.MapInstance(legacyEntity);
        Assert.Equal("deployment-current", legacyInstance.CurrentDeploymentReference?.DeploymentId);
        Assert.Null(legacyInstance.CurrentDeploymentReference?.EndpointUri);
        Assert.Null(legacyInstance.IdentityBinding);

        Assert.False(legacyInstance.CurrentDeploymentReference?.ManagedHandoff);

        legacyEntity.Name = "Managed instance after legacy endpoint read";
        await db.SaveChangesAsync();
        Assert.Null(legacyEntity.CurrentDeploymentEndpointUri);
        // The handoff was bound to the dropped legacy origin, so it goes with it.
        Assert.False(legacyEntity.CurrentDeploymentManagedHandoff);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstances SET CurrentDeploymentEndpointUri = {"https://different.example"} WHERE Id = {instance.Id}");
        db.ChangeTracker.Clear();

        var stale = Assert.Single(await catalog.ListAsync(workspace.Id));
        Assert.Null(stale.Audience);
        Assert.Null(stale.CallbackUri);
        Assert.Null(stale.BindingVersion);

        var deleted = await db.ElsaInstances.SingleAsync(x => x.Id == instance.Id);
        deleted.DesiredLifecycle = ElsaDesiredLifecycle.Deleting;
        deleted.ObservedLifecycle = ElsaObservedLifecycle.Deleted;
        deleted.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        Assert.Empty(await catalog.ListAsync(workspace.Id));
    }

    [Fact]
    public async Task Rolling_back_preview_consent_preserves_instance_delete_guard()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var workspace = new Workspace { Name = "Preview consent rollback workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();

        await db.Database.MigrateAsync("20260904105437_AddAzureProviderResourceAssignments");
        db.ChangeTracker.Clear();
        var triggerNames = await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type = 'trigger' AND name IN ('TR_ElsaInstances_NoDelete', 'TR_DeploymentEnvironments_ManagedInstanceBinding_Update')")
            .ToListAsync();
        Assert.Contains("TR_ElsaInstances_NoDelete", triggerNames);
        Assert.Contains("TR_DeploymentEnvironments_ManagedInstanceBinding_Update", triggerNames);
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM ElsaInstances WHERE Id = {instance.Id}"));

        await db.Database.MigrateAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM ElsaInstances WHERE Id = {instance.Id}"));
    }

    [Fact]
    public async Task Instance_tenant_audience_must_be_derived_from_instance_id()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Tenant audience workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        instance.ElsaTenantAudience = "urn:elsa:tenant:caller-controlled";
        db.ElsaInstances.Add(instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Instance_version_is_optimistic_concurrency_token()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = CreateMigratedContext(connection);
        await setup.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Concurrency workspace" };
        setup.Workspaces.Add(workspace);
        await setup.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        setup.ElsaInstances.Add(instance);
        await setup.SaveChangesAsync();
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        await using var first = new CatalogDbContext(options);
        await using var second = new CatalogDbContext(options);
        var firstCopy = await first.ElsaInstances.SingleAsync(x => x.Id == instance.Id);
        var secondCopy = await second.ElsaInstances.SingleAsync(x => x.Id == instance.Id);
        firstCopy.Name = "First writer";
        secondCopy.Name = "Second writer";
        await first.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        Assert.Equal(2, firstCopy.Version);
    }

    [Fact]
    public async Task SaveChanges_accept_all_changes_false_still_runs_instance_validation_and_concurrency()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = CreateMigratedContext(connection);
        await setup.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Save overload workspace" };
        setup.Workspaces.Add(workspace);
        await setup.SaveChangesAsync();

        var invalid = NewInstance(workspace.OrganizationId, workspace.Id);
        invalid.FeatureOverridesJson = "{\"flag\":{\"kind\":\"Boolean\",\"value\":\"not-a-boolean\"}}";
        setup.ElsaInstances.Add(invalid);
        Assert.Throws<InvalidOperationException>(() => setup.SaveChanges(acceptAllChangesOnSuccess: false));
        setup.ChangeTracker.Clear();

        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        setup.ElsaInstances.Add(instance);
        await setup.SaveChangesAsync();
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        await using var first = new CatalogDbContext(options);
        await using var second = new CatalogDbContext(options);
        var firstCopy = await first.ElsaInstances.SingleAsync(x => x.Id == instance.Id);
        var secondCopy = await second.ElsaInstances.SingleAsync(x => x.Id == instance.Id);
        firstCopy.Name = "First overload writer";
        secondCopy.Name = "Second overload writer";

        first.SaveChanges(acceptAllChangesOnSuccess: false);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            second.SaveChangesAsync(acceptAllChangesOnSuccess: false));
    }

    [Fact]
    public async Task Feature_overrides_require_known_kinds_and_matching_typed_values()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Typed override workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        foreach (var json in new[]
                 {
                     "{\"flag\":{\"kind\":\"Unknown\",\"value\":\"safe\"}}",
                     "{\"flag\":{\"kind\":\"Boolean\",\"value\":\"not-a-boolean\"}}",
                     "{\"limit\":{\"kind\":\"Number\",\"value\":\"not-a-number\"}}",
                     "{\"tier\":{\"kind\":\"Catalog\",\"value\":\"free text\"}}"
                 })
        {
            var invalid = NewInstance(workspace.OrganizationId, workspace.Id);
            invalid.FeatureOverridesJson = json;
            db.ElsaInstances.Add(invalid);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
        }
    }

    [Fact]
    public async Task Identity_binding_version_rejects_concurrent_rotation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = CreateMigratedContext(connection);
        await setup.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Binding concurrency workspace" };
        setup.Workspaces.Add(workspace);
        await setup.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        setup.ElsaInstances.Add(instance);
        setup.ElsaInstanceIdentityBindings.Add(NewBinding(
            instance.Id,
            $"urn:elsa:instance:{instance.Id:D}",
            "https://control.example/managed-elsa/handoff/callback"));
        await setup.SaveChangesAsync();

        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        await using var first = new CatalogDbContext(options);
        await using var second = new CatalogDbContext(options);
        var firstCopy = await first.ElsaInstanceIdentityBindings.SingleAsync(x => x.InstanceId == instance.Id);
        var secondCopy = await second.ElsaInstanceIdentityBindings.SingleAsync(x => x.InstanceId == instance.Id);
        firstCopy.BindingVersion = 2;
        firstCopy.ChangedAt = firstCopy.ChangedAt.AddSeconds(1);
        secondCopy.BindingVersion = 2;
        secondCopy.ChangedAt = secondCopy.ChangedAt.AddSeconds(2);
        await first.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Instance_slug_is_immutable_after_creation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Immutable slug workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();

        instance.Slug = "replacement-slug";

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Active_and_recovery_required_deployment_runs_share_one_reservation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Run workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var application = new DeploymentApplicationEntity
        {
            WorkspaceId = workspace.Id,
            Name = "Run app",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.DeploymentApplications.Add(application);
        await db.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();
        var environment = NewEnvironment(workspace.Id, application.Id, instance.Id, "Run environment");
        db.DeploymentEnvironments.Add(environment);
        await db.SaveChangesAsync();
        db.DeploymentRuns.Add(NewRun(workspace.Id, application.Id, environment.Id, WorkspaceDeploymentRunStatus.Queued, instance.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.DeploymentRuns.Add(NewRun(workspace.Id, application.Id, environment.Id, WorkspaceDeploymentRunStatus.RecoveryRequired, instance.Id));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Existing_managed_run_binding_cannot_be_cleared_or_changed_through_ef()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var workspace = new Workspace { Name = "Run binding workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var application = new DeploymentApplicationEntity
        {
            WorkspaceId = workspace.Id,
            Name = "Run binding app",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var first = NewInstance(workspace.OrganizationId, workspace.Id);
        var second = NewInstance(workspace.OrganizationId, workspace.Id);
        db.DeploymentApplications.Add(application);
        db.ElsaInstances.AddRange(first, second);
        await db.SaveChangesAsync();
        var environment = NewEnvironment(workspace.Id, application.Id, first.Id, "Run binding environment");
        db.DeploymentEnvironments.Add(environment);
        await db.SaveChangesAsync();
        var run = NewRun(workspace.Id, application.Id, environment.Id, WorkspaceDeploymentRunStatus.Queued, first.Id);
        db.DeploymentRuns.Add(run);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var loaded = await db.DeploymentRuns.SingleAsync(x => x.Id == run.Id);
        loaded.ElsaInstanceId = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        db.ChangeTracker.Clear();
        loaded = await db.DeploymentRuns.SingleAsync(x => x.Id == run.Id);
        loaded.ElsaInstanceId = second.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        db.ChangeTracker.Clear();
        db.DeploymentRuns.Add(NewRun(workspace.Id, application.Id, environment.Id, WorkspaceDeploymentRunStatus.RecoveryRequired, first.Id));
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task HasActiveRun_includes_recovery_required_managed_runs()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Recovery run workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var application = new DeploymentApplicationEntity
        {
            WorkspaceId = workspace.Id,
            Name = "Recovery app",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.DeploymentApplications.Add(application);
        await db.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();
        var environment = NewEnvironment(workspace.Id, application.Id, instance.Id, "Recovery environment");
        db.DeploymentEnvironments.Add(environment);
        await db.SaveChangesAsync();
        db.DeploymentRuns.Add(NewRun(workspace.Id, application.Id, environment.Id, WorkspaceDeploymentRunStatus.RecoveryRequired, instance.Id));
        await db.SaveChangesAsync();

        Assert.True(await new DeploymentWorkspaceStore(db).HasActiveRunAsync(workspace.Id, environment.Id));
    }

    [Fact]
    public async Task Instance_operation_envelope_is_immutable_after_creation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Operation immutability workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var first = NewInstance(workspace.OrganizationId, workspace.Id);
        var second = NewInstance(workspace.OrganizationId, workspace.Id);
        db.ElsaInstances.AddRange(first, second);
        await db.SaveChangesAsync();
        var operation = NewOperation(workspace, first, ElsaInstanceOperationState.Succeeded, "immutable-operation");
        db.ElsaInstanceOperations.Add(operation);
        await db.SaveChangesAsync();

        operation.InstanceId = second.Id;

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Instance_operation_state_transitions_are_validated_on_update()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Operation state workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();
        var operation = NewOperation(workspace, instance, ElsaInstanceOperationState.Succeeded, "state-operation");
        db.ElsaInstanceOperations.Add(operation);
        await db.SaveChangesAsync();

        operation.State = ElsaInstanceOperationState.Running;

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Instance_migration_identity_and_release_references_are_immutable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Migration immutability workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();
        var migration = NewMigration(workspace, instance);
        db.ElsaInstanceOperations.Add(NewMigrationOperation(workspace, instance, migration));
        db.ElsaInstanceMigrations.Add(migration);
        await db.SaveChangesAsync();

        migration.SourcePlanId = "replacement-source-plan";
        migration.SourcePlanUri = PlanUri(workspace.Id, instance.Id, migration.SourcePlanId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Instance_migration_updates_use_phase_validation_and_optimistic_concurrency()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = CreateMigratedContext(connection);
        await setup.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Migration concurrency workspace" };
        setup.Workspaces.Add(workspace);
        await setup.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        setup.ElsaInstances.Add(instance);
        await setup.SaveChangesAsync();
        var migration = NewMigration(workspace, instance, "Preparing");
        setup.ElsaInstanceOperations.Add(NewMigrationOperation(workspace, instance, migration));
        setup.ElsaInstanceMigrations.Add(migration);
        await setup.SaveChangesAsync();

        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        await using var first = new CatalogDbContext(options);
        await using var second = new CatalogDbContext(options);
        var firstCopy = await first.ElsaInstanceMigrations.SingleAsync(x => x.MigrationId == migration.MigrationId);
        var secondCopy = await second.ElsaInstanceMigrations.SingleAsync(x => x.MigrationId == migration.MigrationId);
        firstCopy.Phase = "Cutover";
        firstCopy.UpdatedAt = firstCopy.UpdatedAt.AddSeconds(1);
        secondCopy.Phase = "Cutover";
        secondCopy.UpdatedAt = secondCopy.UpdatedAt.AddSeconds(2);

        await first.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Database_guards_managed_run_bindings_operation_scope_and_durable_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Database guard workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var application = new DeploymentApplicationEntity
        {
            WorkspaceId = workspace.Id,
            Name = "Guard app",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var first = NewInstance(workspace.OrganizationId, workspace.Id);
        var second = NewInstance(workspace.OrganizationId, workspace.Id);
        db.DeploymentApplications.Add(application);
        db.ElsaInstances.AddRange(first, second);
        await db.SaveChangesAsync();
        var environment = NewEnvironment(workspace.Id, application.Id, first.Id, "Guard environment");
        db.DeploymentEnvironments.Add(environment);
        await db.SaveChangesAsync();
        var run = NewRun(workspace.Id, application.Id, environment.Id, WorkspaceDeploymentRunStatus.Queued, first.Id);
        var create = NewOperation(workspace, null, ElsaInstanceOperationState.Succeeded, "guard-create");
        var migration = new ElsaInstanceMigrationEntity
        {
            MigrationId = Guid.NewGuid(),
            InstanceId = first.Id,
            Phase = "Preparing",
            SourceAccessMode = "Running",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        SetMigrationReferences(migration, workspace.Id, first.Id);
        db.DeploymentRuns.Add(run);
        db.ElsaInstanceOperations.Add(create);
        db.ElsaInstanceOperations.Add(NewMigrationOperation(workspace, first, migration));
        db.ElsaInstanceMigrations.Add(migration);
        await db.SaveChangesAsync();

        var efNullBindingRun = NewRun(workspace.Id, application.Id, environment.Id, WorkspaceDeploymentRunStatus.Failed);
        db.DeploymentRuns.Add(efNullBindingRun);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.ElsaInstanceOperations.Remove(create);
        var deleteException = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal("Elsa instance operations are durable and cannot be deleted.", deleteException.Message);
        db.ChangeTracker.Clear();

        var deleteAction = "Delete";

        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE DeploymentRuns SET ElsaInstanceId = {null} WHERE Id = {run.Id}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE DeploymentRuns SET ElsaInstanceId = {second.Id} WHERE Id = {run.Id}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO DeploymentRuns (Id, WorkspaceId, ElsaInstanceId, ApplicationId, EnvironmentId, EngineId,
                SourceRevisionId, Status, ValidationOutcome, ConfirmationId, ActorAccountId, QueuedAt, CreatedAt, AttemptNumber)
            SELECT {Guid.NewGuid()}, WorkspaceId, ElsaInstanceId, ApplicationId, EnvironmentId, EngineId,
                SourceRevisionId, 'RecoveryRequired', ValidationOutcome, ConfirmationId, ActorAccountId, QueuedAt, CreatedAt, AttemptNumber
            FROM DeploymentRuns WHERE Id = {run.Id}
            """));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO DeploymentRuns (Id, WorkspaceId, ElsaInstanceId, ApplicationId, EnvironmentId, EngineId,
                SourceRevisionId, Status, ValidationOutcome, ConfirmationId, ActorAccountId, QueuedAt, CreatedAt, AttemptNumber)
            SELECT {Guid.NewGuid()}, WorkspaceId, NULL, ApplicationId, EnvironmentId, EngineId,
                SourceRevisionId, 'Failed', ValidationOutcome, ConfirmationId, ActorAccountId, QueuedAt, CreatedAt, AttemptNumber
            FROM DeploymentRuns WHERE Id = {run.Id}
            """));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE DeploymentEnvironments SET ElsaInstanceId = {second.Id} WHERE Id = {environment.Id}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstanceOperations SET Action = {deleteAction} WHERE Id = {create.Id}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM ElsaInstanceOperations WHERE Id = {create.Id}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM ElsaInstanceMigrations WHERE MigrationId = {migration.MigrationId}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM ElsaInstances WHERE Id = {second.Id}"));

        var unboundEnvironment = NewEnvironment(workspace.Id, application.Id, null, "Unbound guard environment");
        db.DeploymentEnvironments.Add(unboundEnvironment);
        await db.SaveChangesAsync();
        var unboundRun = NewRun(workspace.Id, application.Id, unboundEnvironment.Id, WorkspaceDeploymentRunStatus.Failed);
        db.DeploymentRuns.Add(unboundRun);
        await db.SaveChangesAsync();

        unboundRun.EnvironmentId = environment.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var managedEnvironmentId = environment.Id;
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE DeploymentRuns SET EnvironmentId = {managedEnvironmentId} WHERE Id = {unboundRun.Id}"));
    }

    [Fact]
    public async Task Audit_is_sanitized_and_append_only()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Audit workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();

        db.ElsaInstanceAuditEvents.Add(new ElsaInstanceAuditEventEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = workspace.OrganizationId,
            WorkspaceId = workspace.Id,
            InstanceId = instance.Id,
            Sequence = 1,
            EventType = "OperationFailed",
            DiagnosticCode = "instance.failed",
            Summary = "secret token should never persist",
            OperatorSubject = "operator-secret-subject",
            OccurredAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var stored = await db.ElsaInstanceAuditEvents.SingleAsync();
        Assert.Equal("instance.failed", stored.Summary);
        Assert.StartsWith("sha256:", stored.OperatorSubject);
        Assert.DoesNotContain("operator-secret-subject", stored.OperatorSubject, StringComparison.Ordinal);

        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE ElsaInstanceAuditEvents SET Summary = 'tampered' WHERE Sequence = 1"));
        db.ChangeTracker.Clear();
        var tombstoneTarget = await db.ElsaInstances.SingleAsync(x => x.Id == instance.Id);
        db.ElsaInstances.Remove(tombstoneTarget);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Save_validation_requires_complete_plan_and_thirty_day_retention()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Invariant workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        var incompletePlan = NewInstance(workspace.OrganizationId, workspace.Id);
        incompletePlan.ResolvedPlanId = "plan-1";
        db.ElsaInstances.Add(incompletePlan);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var incompleteCurrent = NewInstance(workspace.OrganizationId, workspace.Id);
        incompleteCurrent.ResolvedPlanId = "plan-2";
        incompleteCurrent.ResolvedPlanSchemaVersion = 1;
        incompleteCurrent.ResolvedPlanContentHash = "sha256:" + new string('b', 64);
        incompleteCurrent.ResolvedPlanUri = PlanUri(workspace.Id, incompleteCurrent.Id, "plan-2");
        incompleteCurrent.CurrentReleaseLine = "3.10";
        db.ElsaInstances.Add(incompleteCurrent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();
        var cutover = DateTimeOffset.UtcNow;
        var shortRetention = new ElsaInstanceMigrationEntity
        {
            MigrationId = Guid.NewGuid(),
            InstanceId = instance.Id,
            Phase = "Cutover",
            SourceAccessMode = "Stopped",
            CutoverAt = cutover,
            SourceRetainUntil = cutover.AddDays(29),
            CreatedAt = cutover,
            UpdatedAt = cutover
        };
        SetMigrationReferences(shortRetention, workspace.Id, instance.Id);
        db.ElsaInstanceMigrations.Add(shortRetention);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var noRetention = new ElsaInstanceMigrationEntity
        {
            MigrationId = Guid.NewGuid(),
            InstanceId = instance.Id,
            Phase = "Cutover",
            SourceAccessMode = "Stopped",
            CutoverAt = cutover,
            CreatedAt = cutover,
            UpdatedAt = cutover
        };
        SetMigrationReferences(noRetention, workspace.Id, instance.Id);
        db.ElsaInstanceMigrations.Add(noRetention);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var partialTuple = new ElsaInstanceMigrationEntity
        {
            MigrationId = Guid.NewGuid(),
            InstanceId = instance.Id,
            SourcePlanId = "plan-source",
            Phase = "Preparing",
            SourceAccessMode = "Running",
            CreatedAt = cutover,
            UpdatedAt = cutover
        };
        db.ElsaInstanceMigrations.Add(partialTuple);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var validMigration = new ElsaInstanceMigrationEntity
        {
            MigrationId = Guid.NewGuid(),
            InstanceId = instance.Id,
            SourcePlanId = "source-plan",
            SourcePlanUri = PlanUri(workspace.Id, instance.Id, "source-plan"),
            SourceReleaseLine = "3.10",
            SourceVersion = "3.10.1",
            SourceManifestDigest = "sha256:" + new string('c', 64),
            SourceDeploymentId = "source-deployment",
            TargetPlanId = "target-plan",
            TargetPlanUri = PlanUri(workspace.Id, instance.Id, "target-plan"),
            TargetReleaseLine = "4.0",
            TargetVersion = "4.0.1",
            TargetManifestDigest = "sha256:" + new string('d', 64),
            TargetDeploymentId = "target-deployment",
            OperationId = Guid.NewGuid(),
            StartRequestHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("valid-migration"))),
            LastRequestHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("valid-migration"))),
            Phase = "Cutover",
            SourceAccessMode = "Stopped",
            CutoverAt = cutover,
            SourceRetainUntil = cutover.AddDays(30),
            CreatedAt = cutover,
            UpdatedAt = cutover
        };
        db.ElsaInstanceOperations.Add(NewMigrationOperation(workspace, instance, validMigration));
        db.ElsaInstanceMigrations.Add(validMigration);
        await db.SaveChangesAsync();

        var earlyReleaseInstance = NewInstance(workspace.OrganizationId, workspace.Id);
        db.ElsaInstances.Add(earlyReleaseInstance);
        await db.SaveChangesAsync();
        var earlyRelease = new ElsaInstanceMigrationEntity
        {
            MigrationId = Guid.NewGuid(),
            InstanceId = earlyReleaseInstance.Id,
            Phase = "RetiringSource",
            SourceAccessMode = "Stopped",
            CutoverAt = cutover,
            SourceRetainUntil = cutover.AddDays(30),
            SourceReleasedAt = cutover.AddDays(1),
            CreatedAt = cutover,
            UpdatedAt = cutover
        };
        SetMigrationReferences(earlyRelease, workspace.Id, earlyReleaseInstance.Id);
        db.ElsaInstanceMigrations.Add(earlyRelease);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        earlyRelease.EarlyReleaseApprovedByAccountId = Guid.NewGuid();
        earlyRelease.EarlyReleaseApprovedAt = cutover.AddHours(1);
        db.ElsaInstanceOperations.Add(NewMigrationOperation(workspace, earlyReleaseInstance, earlyRelease));
        db.ElsaInstanceMigrations.Add(earlyRelease);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Persistence_rejects_hostile_json_uri_digest_and_bearer_values()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Boundary workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        var secretJson = NewInstance(workspace.OrganizationId, workspace.Id);
        secretJson.FeatureOverridesJson = "{\"api\":{\"kind\":\"catalog\",\"value\":\"secret-token\"}}";
        db.ElsaInstances.Add(secretJson);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var oversizedJson = NewInstance(workspace.OrganizationId, workspace.Id);
        oversizedJson.FeatureOverridesJson = new string('x', 32_769);
        db.ElsaInstances.Add(oversizedJson);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var invalidPlan = NewInstance(workspace.OrganizationId, workspace.Id);
        invalidPlan.ResolvedPlanId = "plan-1";
        invalidPlan.ResolvedPlanSchemaVersion = 1;
        invalidPlan.ResolvedPlanContentHash = "sha256:" + new string('a', 64);
        invalidPlan.ResolvedPlanUri = "https://control.example/api/plans/1?token=leaked";
        db.ElsaInstances.Add(invalidPlan);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var trailingPlan = NewInstance(workspace.OrganizationId, workspace.Id);
        trailingPlan.ResolvedPlanId = "plan-2";
        trailingPlan.ResolvedPlanSchemaVersion = 1;
        trailingPlan.ResolvedPlanContentHash = "sha256:" + new string('a', 64);
        trailingPlan.ResolvedPlanUri = PlanUri(workspace.Id, trailingPlan.Id, "plan-2") + "/";
        db.ElsaInstances.Add(trailingPlan);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var operation = NewOperation(workspace, null, ElsaInstanceOperationState.Succeeded, "unsafe-lease");
        operation.LeaseTokenHash = "bearer-token";
        operation.FailureSummary = "provider secret and stack trace";
        db.ElsaInstanceOperations.Add(operation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var safeOperation = NewOperation(workspace, null, ElsaInstanceOperationState.Failed, "safe-failure");
        safeOperation.FailureCode = "operation.failed";
        safeOperation.FailureSummary = "provider token must collapse to the stable code";
        db.ElsaInstanceOperations.Add(safeOperation);
        await db.SaveChangesAsync();
        var stored = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == safeOperation.Id);
        Assert.Equal("operation.failed", stored.FailureSummary);
    }

    private static CatalogDbContext CreateMigratedContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        return new CatalogDbContext(options);
    }

    private static CatalogDbContext CreateEnsureCreatedContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite("Data Source=:memory:")
            .Options;
        return new CatalogDbContext(options);
    }

    internal static ElsaInstanceEntity NewInstance(Guid organizationId, Guid workspaceId)
    {
        var now = DateTimeOffset.UtcNow;
        return new ElsaInstanceEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            WorkspaceId = workspaceId,
            Name = "Managed Elsa",
            Slug = "managed-elsa-" + Guid.NewGuid().ToString("N")[..8],
            DistributionId = "valence-runtime",
            ReleaseLine = "3.10",
            Channel = "stable",
            PatchUpdates = "automatic-within-minor",
            MinorUpdates = "explicit-approval",
            MajorMigrations = "explicit-migration",
            TopologyId = "combined",
            FeatureOverridesJson = "{}",
            TargetMode = "managed",
            RegionCode = "westeurope",
            IsolationProfile = "dedicated",
            CapacityProfile = "standard-small",
            NetworkOutcome = "public",
            DomainOutcome = "managed",
            DesiredLifecycle = ElsaDesiredLifecycle.Running,
            ObservedLifecycle = ElsaObservedLifecycle.Pending,
            Health = ElsaInstanceHealth.Unknown,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ElsaInstanceIntentRevisionEntity NewIntentRevision(Workspace workspace, ElsaInstanceEntity instance)
    {
        var now = DateTimeOffset.UtcNow;
        return new ElsaInstanceIntentRevisionEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = workspace.OrganizationId,
            WorkspaceId = workspace.Id,
            InstanceId = instance.Id,
            RevisionNumber = 1,
            ContentHash = new string('a', 64),
            DistributionId = instance.DistributionId,
            ReleaseLine = instance.ReleaseLine,
            Channel = instance.Channel,
            PatchUpdates = instance.PatchUpdates,
            MinorUpdates = instance.MinorUpdates,
            MajorMigrations = instance.MajorMigrations,
            TopologyId = instance.TopologyId,
            FeatureOverridesJson = instance.FeatureOverridesJson,
            TargetMode = instance.TargetMode,
            RegionCode = instance.RegionCode,
            IsolationProfile = instance.IsolationProfile,
            CapacityProfile = instance.CapacityProfile,
            NetworkOutcome = instance.NetworkOutcome,
            DomainOutcome = instance.DomainOutcome,
            DesiredLifecycle = instance.DesiredLifecycle,
            AuthoredAt = now,
            CreatedAt = now
        };
    }

    private static DeploymentEnvironmentEntity NewEnvironment(Guid workspaceId, Guid applicationId, Guid? instanceId, string name) => new()
    {
        WorkspaceId = workspaceId,
        ApplicationId = applicationId,
        ElsaInstanceId = instanceId,
        Name = name,
        Tier = EnvironmentTier.Production,
        DeploymentStatus = DeploymentStatus.Blocked,
        DriftStatus = DriftStatus.Unknown,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static ElsaInstanceOperationEntity NewOperation(
        Workspace workspace,
        ElsaInstanceEntity? instance,
        ElsaInstanceOperationState state,
        string key)
    {
        var now = DateTimeOffset.UtcNow;
        return new ElsaInstanceOperationEntity
        {
            Id = Guid.NewGuid(),
            InstanceId = instance?.Id,
            OrganizationId = workspace.OrganizationId,
            WorkspaceId = workspace.Id,
            Action = instance is null
                ? ElsaInstanceOperationAction.Create
                : state == ElsaInstanceOperationState.WaitingForPriorOperation
                ? ElsaInstanceOperationAction.Delete
                : ElsaInstanceOperationAction.Reconcile,
            IdempotencyScope = $"instance/{instance?.Id.ToString("N") ?? "workspace"}",
            IdempotencyKey = key,
            RequestHash = new string('a', 64),
            ExpectedVersion = 1,
            State = state,
            AttemptNumber = 1,
            AcceptedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ElsaInstanceIdentityBindingEntity NewBinding(Guid instanceId, string audience, string callback) => new()
    {
        InstanceId = instanceId,
        Audience = audience,
        CanonicalCallbackUri = callback,
        VerifiedEndpointOrigin = "https://control.example",
        BindingVersion = 1,
        ChangedAt = DateTimeOffset.UtcNow
    };

    private static DeploymentRunEntity NewRun(Guid workspaceId, Guid applicationId, Guid environmentId, WorkspaceDeploymentRunStatus status, Guid? elsaInstanceId = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new DeploymentRunEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ElsaInstanceId = elsaInstanceId,
            ApplicationId = applicationId,
            EnvironmentId = environmentId,
            EngineId = Guid.NewGuid(),
            SourceRevisionId = Guid.NewGuid(),
            Status = status,
            ValidationOutcome = DeploymentValidationOutcome.Passed,
            ConfirmationId = Guid.NewGuid(),
            ActorAccountId = Guid.NewGuid(),
            QueuedAt = now,
            CreatedAt = now,
            AttemptNumber = 1
        };
    }

    private static string PlanUri(Guid workspaceId, Guid instanceId, string planId) =>
        $"https://control.example/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/resolved-plans/{planId}";

    private static ElsaInstanceMigrationEntity NewMigration(Workspace workspace, ElsaInstanceEntity instance, string phase = "Cutover")
    {
        var now = DateTimeOffset.UtcNow;
        var migration = new ElsaInstanceMigrationEntity
        {
            MigrationId = Guid.NewGuid(),
            InstanceId = instance.Id,
            OrganizationId = workspace.OrganizationId,
            WorkspaceId = workspace.Id,
            Phase = phase,
            SourceAccessMode = "Stopped",
            CutoverAt = now,
            SourceRetainUntil = now.AddDays(30),
            CreatedAt = now,
            UpdatedAt = now
        };
        SetMigrationReferences(migration, workspace.Id, instance.Id);
        return migration;
    }

    private static ElsaInstanceOperationEntity NewMigrationOperation(
        Workspace workspace, ElsaInstanceEntity instance, ElsaInstanceMigrationEntity migration) => new()
        {
            Id = migration.OperationId,
            InstanceId = instance.Id,
            OrganizationId = workspace.OrganizationId,
            WorkspaceId = workspace.Id,
            Action = ElsaInstanceOperationAction.MajorMigration,
            IdempotencyScope = $"instance/{instance.Id:D}/MajorMigration",
            IdempotencyKey = $"migration-{migration.MigrationId:N}",
            RequestHash = migration.StartRequestHash,
            ExpectedVersion = instance.Version,
            State = ElsaInstanceOperationState.Running,
            AttemptNumber = 1,
            AcceptedAt = migration.CreatedAt,
            StartedAt = migration.CreatedAt,
            CreatedAt = migration.CreatedAt,
            UpdatedAt = migration.UpdatedAt
        };

    private static void SetMigrationReferences(ElsaInstanceMigrationEntity migration, Guid workspaceId, Guid instanceId)
    {
        migration.WorkspaceId = workspaceId;
        migration.OperationId = migration.OperationId == Guid.Empty ? Guid.NewGuid() : migration.OperationId;
        migration.StartRequestHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(migration.MigrationId.ToString("D"))));
        migration.LastRequestHash = migration.StartRequestHash;
        migration.SourcePlanId = "source-plan";
        migration.SourcePlanUri = PlanUri(workspaceId, instanceId, migration.SourcePlanId);
        migration.SourceReleaseLine = "3.10";
        migration.SourceVersion = "3.10.1";
        migration.SourceManifestDigest = "sha256:" + new string('c', 64);
        migration.SourceDeploymentId = "source-deployment";
        migration.TargetPlanId = "target-plan";
        migration.TargetPlanUri = PlanUri(workspaceId, instanceId, migration.TargetPlanId);
        migration.TargetReleaseLine = "4.0";
        migration.TargetVersion = "4.0.1";
        migration.TargetManifestDigest = "sha256:" + new string('d', 64);
        migration.TargetDeploymentId = "target-deployment";
    }

    private static async Task<string[]> ReadScalarStringsAsync(CatalogDbContext db, string sql)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values.ToArray();
    }

    [Fact]
    public async Task Migration_service_persists_replays_and_audits_exact_source_target()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Migration service workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        SetCurrentRelease(instance, workspace.Id);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();
        await SeedTargetPlanAsync(db, workspace, instance);
        var service = new ElsaInstanceMigrationService(new EfCoreElsaInstanceMigrationStore(db),
            new AllowMigrationAuthorizer(), new FixedTimeProvider(new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero)));
        var request = new ElsaInstanceMigrationStartRequest(workspace.OrganizationId, workspace.Id, instance.Id, instance.Version,
            MigrationReference(instance.ResolvedPlanId!, instance.ResolvedPlanUri!, "3.10", "3.10.4",
                instance.CurrentReleaseManifestDigest!, instance.CurrentDeploymentId!),
            MigrationReference("target-plan", PlanUri(workspace.Id, instance.Id, "target-plan"), "4.0", "4.0.1",
                "sha256:" + new string('d', 64), "target-deployment"),
            "migration-request-1", Guid.NewGuid());

        var started = await service.StartAsync(request);
        var replay = await service.StartAsync(request);

        Assert.Equal(ElsaInstanceMigrationWriteOutcome.Applied, started.Outcome);
        Assert.Equal(ElsaInstanceMigrationWriteOutcome.Replayed, replay.Outcome);
        Assert.Equal(started.Migration!.Id, replay.Migration!.Id);
        db.ChangeTracker.Clear();
        var stored = await db.ElsaInstanceMigrations.SingleAsync();
        Assert.Equal("3.10.4", stored.SourceVersion);
        Assert.Equal("4.0.1", stored.TargetVersion);
        var audit = await db.ElsaInstanceAuditEvents.SingleAsync();
        Assert.Equal("MajorMigrationStarted", audit.EventType);
        Assert.Equal(64, audit.RequestKeyHash!.Length);
        Assert.Equal(started.Migration.Id, audit.MigrationId);
        Assert.Equal(started.Migration.OperationId, audit.OperationId);
        Assert.Equal("migration.event", audit.Summary);
    }

    [Fact]
    public async Task Migration_start_fails_closed_for_stale_source_and_deletion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Migration guard workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        SetCurrentRelease(instance, workspace.Id);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();
        var store = new EfCoreElsaInstanceMigrationStore(db);
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var stale = ElsaInstanceMigration.Plan(Guid.NewGuid(), workspace.OrganizationId, workspace.Id, instance.Id,
            MigrationReference("stale-plan", PlanUri(workspace.Id, instance.Id, "stale-plan"), "3.10", "3.10.4",
                instance.CurrentReleaseManifestDigest!, instance.CurrentDeploymentId!),
            MigrationReference("target-plan", PlanUri(workspace.Id, instance.Id, "target-plan"), "4.0", "4.0.1",
                "sha256:" + new string('d', 64), "target-deployment"),
            ElsaInstanceMigration.HashRequestKey("stale"), now);
        var audit = new ElsaInstanceMigrationAudit(stale.Id, stale.OperationId, "MajorMigrationStarted", null,
            "Planned", Guid.NewGuid(), stale.StartRequestHash, now);

        var staleResult = await store.CreateAsync(new(stale, instance.Version, "stale"), audit);
        Assert.Equal("migration.source.stale", staleResult.DiagnosticCode);

        instance = await db.ElsaInstances.SingleAsync(x => x.Id == instance.Id);
        instance.DesiredLifecycle = ElsaDesiredLifecycle.Deleting;
        instance.UpdatedAt = instance.UpdatedAt.AddMinutes(1);
        await db.SaveChangesAsync();
        var exact = stale with { };
        exact = ElsaInstanceMigration.Plan(Guid.NewGuid(), workspace.OrganizationId, workspace.Id, instance.Id,
            MigrationReference(instance.ResolvedPlanId!, instance.ResolvedPlanUri!, "3.10", "3.10.4",
                instance.CurrentReleaseManifestDigest!, instance.CurrentDeploymentId!), stale.Target,
            ElsaInstanceMigration.HashRequestKey("delete-conflict"), now);
        var busy = await store.CreateAsync(new(exact, instance.Version, "delete-conflict"),
            audit with { MigrationId = exact.Id, OperationId = exact.OperationId, RequestHash = exact.StartRequestHash });
        Assert.Equal("migration.instance.busy", busy.DiagnosticCode);
    }

    [Fact]
    public async Task Due_migration_source_release_requires_fenced_confirmation_and_persists_evidence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = new Workspace { Name = "Migration release workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        var migration = NewMigration(workspace, instance, "RetainingSource");
        migration.CutoverAt = now.AddDays(-31);
        migration.SourceRetainUntil = now.AddDays(-1);
        migration.CreatedAt = now.AddDays(-32);
        migration.UpdatedAt = now.AddDays(-31);
        db.ElsaInstanceOperations.Add(NewMigrationOperation(workspace, instance, migration));
        db.ElsaInstanceMigrations.Add(migration);
        await db.SaveChangesAsync();
        var store = new EfCoreElsaInstanceMigrationStore(db);

        var claim = await store.TryClaimDueAsync(now, TimeSpan.FromMinutes(5));
        Assert.NotNull(claim);
        var completed = await store.CompleteAsync(claim!, new(ElsaInstanceSourceReleaseOutcome.Confirmed,
            "migration.source-release.confirmed", "provider-operation-1",
            "https://evidence.example/migrations/release-1", "sha256:" + new string('e', 64)), now.AddSeconds(1));

        Assert.Equal(ElsaInstanceMigrationWriteOutcome.Applied, completed.Outcome);
        Assert.Equal(ElsaInstanceMigrationPhase.Released, completed.Migration!.Phase);
        db.ChangeTracker.Clear();
        var persisted = await db.ElsaInstanceMigrations.SingleAsync(x => x.MigrationId == migration.MigrationId);
        Assert.Equal("provider-operation-1", persisted.SourceReleaseProviderCorrelationId);
        Assert.Equal("sha256:" + new string('e', 64), persisted.SourceReleaseEvidenceDigest);
    }

    [Fact]
    public async Task Migration_upgrade_preserves_a_legacy_retained_source_under_an_active_reservation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync("20260830171154_AddElsaInstanceDeletionEvidence");
        var workspace = new Workspace { Name = "Legacy retained migration workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var instance = NewInstance(workspace.OrganizationId, workspace.Id);
        // The current EF model includes fields added after this historical
        // boundary, so seed through the exact legacy column set instead of
        // asking the current model to write a column that does not exist yet.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ElsaInstances
                (Id, OrganizationId, WorkspaceId, Name, Slug, DistributionId,
                 ReleaseLine, Channel, PatchUpdates, MinorUpdates, MajorMigrations,
                 TopologyId, FeatureOverridesJson, TargetMode, RegionCode,
                 IsolationProfile, CapacityProfile, NetworkOutcome, DomainOutcome,
                 DesiredLifecycle, ObservedLifecycle, Health, Version, CreatedAt, UpdatedAt)
            VALUES
                ({instance.Id}, {instance.OrganizationId}, {instance.WorkspaceId}, {instance.Name}, {instance.Slug}, {instance.DistributionId},
                 {instance.ReleaseLine}, {instance.Channel}, {instance.PatchUpdates}, {instance.MinorUpdates}, {instance.MajorMigrations},
                 {instance.TopologyId}, {instance.FeatureOverridesJson}, {instance.TargetMode}, {instance.RegionCode},
                 {instance.IsolationProfile}, {instance.CapacityProfile}, {instance.NetworkOutcome}, {instance.DomainOutcome},
                 {instance.DesiredLifecycle.ToString()}, {instance.ObservedLifecycle.ToString()}, {instance.Health.ToString()},
                 {instance.Version}, {instance.CreatedAt.UtcTicks}, {instance.UpdatedAt.UtcTicks})
            """);
        var migrationId = Guid.NewGuid();
        var cutover = DateTimeOffset.UtcNow.AddDays(-31);
        var sourcePlanId = "source-plan";
        var sourceDigest = "sha256:" + new string('a', 64);
        var targetDigest = "sha256:" + new string('b', 64);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ElsaInstanceMigrations
                (MigrationId, InstanceId, OrganizationId, WorkspaceId,
                 SourcePlanId, SourcePlanUri, SourceReleaseLine, SourceVersion, SourceManifestDigest, SourceDeploymentId,
                 TargetPlanId, TargetPlanUri, TargetReleaseLine, TargetVersion, TargetManifestDigest, TargetDeploymentId,
                 Phase, SourceAccessMode, CutoverAt, SourceRetainUntil, CreatedAt, UpdatedAt)
            VALUES
                ({migrationId}, {instance.Id}, {workspace.OrganizationId}, {workspace.Id},
                 {sourcePlanId}, {PlanUri(workspace.Id, instance.Id, sourcePlanId)}, {"3.10"}, {"3.10.4"}, {sourceDigest}, {"source-deployment"},
                 {"target-plan"}, {PlanUri(workspace.Id, instance.Id, "target-plan")}, {"5.0"}, {"5.0.0"}, {targetDigest}, {"target-deployment"},
                 {"RetainingSource"}, {"Stopped"}, {cutover.UtcTicks}, {cutover.AddDays(30).UtcTicks}, {cutover.AddDays(-1).UtcTicks}, {cutover.UtcTicks})
            """);

        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();

        var retained = await db.ElsaInstanceMigrations.SingleAsync(x => x.MigrationId == migrationId);
        var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == migrationId);
        Assert.Equal("RetainingSource", retained.Phase);
        Assert.Equal(64, retained.StartRequestHash.Length);
        Assert.Equal(ElsaInstanceOperationState.Running, operation.State);
    }

    private static void SetCurrentRelease(ElsaInstanceEntity instance, Guid workspaceId)
    {
        instance.ObservedLifecycle = ElsaObservedLifecycle.Ready;
        instance.Health = ElsaInstanceHealth.Healthy;
        instance.ResolvedPlanId = "source-plan";
        instance.ResolvedPlanSchemaVersion = 1;
        instance.ResolvedPlanContentHash = "sha256:" + new string('a', 64);
        instance.ResolvedPlanUri = PlanUri(workspaceId, instance.Id, instance.ResolvedPlanId);
        instance.CurrentReleaseDistributionId = "valence-runtime";
        instance.CurrentReleaseLine = "3.10";
        instance.CurrentReleaseVersion = "3.10.4";
        instance.CurrentReleaseManifestDigest = "sha256:" + new string('c', 64);
        instance.CurrentReleaseComponentDigestsJson =
            "[{\"componentId\":\"runtime\",\"digest\":\"sha256:" + new string('e', 64) + "\"}]";
        instance.CurrentDeploymentId = "source-deployment";
    }

    private static ElsaInstanceMigrationReleaseReference MigrationReference(
        string planId, string planUri, string line, string version, string digest, string deployment) =>
        new(planId, planUri, line, version, digest, deployment);

    private static Task SeedTargetPlanAsync(CatalogDbContext db, Workspace workspace, ElsaInstanceEntity instance)
    {
        var id = Guid.NewGuid();
        var planId = "target-plan";
        var planUri = PlanUri(workspace.Id, instance.Id, planId);
        var hash = "sha256:" + new string('f', 64);
        var manifestDigest = "sha256:" + new string('d', 64);
        var serialized =
            $"{{\"schemaVersion\":\"1\",\"release\":{{\"distributionId\":\"valence-runtime\",\"releaseLine\":\"4.0\",\"version\":\"4.0.1\",\"sourceRepository\":\"https://source.example/runtime\",\"sourceCommit\":\"0123456789abcdef\",\"releaseManifestReference\":\"https://artifacts.example/releases/4.0.1\",\"releaseManifestDigest\":\"{manifestDigest}\"}}}}";
        var createdAt = DateTimeOffset.UtcNow.UtcTicks;
        return db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO ElsaInstanceResolvedPlans
                (Id, OrganizationId, WorkspaceId, InstanceId, PlanId, SchemaVersion, ContentHash, PlanUri, SerializedPlan, CreatedAt)
            VALUES ({{id}}, {{workspace.OrganizationId}}, {{workspace.Id}}, {{instance.Id}}, {{planId}}, 1,
                {{hash}}, {{planUri}}, {{serialized}}, {{createdAt}})
            """);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AllowMigrationAuthorizer : IElsaInstanceMigrationAuthorizer
    {
        public Task RequireExecutionAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RequireEarlyReleaseAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
