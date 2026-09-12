using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ManagedElsaHandoffPersistenceTests
{
    [Fact]
    public async Task Migration_creates_durable_handoff_tables()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        await using var db = CreateContext(connection);

        await db.Database.MigrateAsync();

        var tables = await db.Database.SqlQuery<string>(
                $"SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync();
        Assert.Contains("ManagedElsaHandoffReplayConsumptions", tables);
        Assert.Contains("ManagedElsaHandoffAuditEvents", tables);
    }

    [Fact]
    public async Task Replay_consumption_is_durable_and_single_use()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        await using (var setup = CreateContext(connection))
            await setup.Database.MigrateAsync();

        var jti = "handoff-jti-1";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await using (var firstDb = CreateContext(connection))
        {
            var first = new EfCoreManagedElsaHandoffStore(firstDb);
            Assert.True(await first.TryConsumeAsync(jti, expiresAt));
        }

        await using (var secondDb = CreateContext(connection))
        {
            var second = new EfCoreManagedElsaHandoffStore(secondDb);
            Assert.False(await second.TryConsumeAsync(jti, expiresAt));
            Assert.Equal(1, await secondDb.ManagedElsaHandoffReplays.CountAsync());
        }
    }

    [Fact]
    public async Task Replay_consumption_clamps_subsecond_expiry_boundary()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.MigrateAsync();
        var expiresAt = new DateTimeOffset(2026, 8, 30, 21, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(expiresAt.AddMilliseconds(250));

        Assert.True(await new EfCoreManagedElsaHandoffStore(db, clock)
            .TryConsumeAsync("handoff-expiry-boundary", expiresAt));

        var saved = await db.ManagedElsaHandoffReplays.SingleAsync();
        Assert.Equal(expiresAt.UtcTicks, saved.ExpiresAt.UtcTicks);
        Assert.Equal(expiresAt.AddTicks(-1).UtcTicks, saved.ConsumedAt.UtcTicks);
    }

    [Fact]
    public async Task Replay_consumption_purges_only_rows_expired_beyond_retention()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.MigrateAsync();
        var now = new DateTimeOffset(2026, 8, 30, 21, 0, 0, TimeSpan.Zero);
        db.ManagedElsaHandoffReplays.AddRange(
            new ManagedElsaHandoffReplayEntity
            {
                Jti = "expired-old",
                ExpiresAt = now.AddHours(-25),
                ConsumedAt = now.AddHours(-26)
            },
            new ManagedElsaHandoffReplayEntity
            {
                Jti = "expired-recent",
                ExpiresAt = now.AddHours(-1),
                ConsumedAt = now.AddHours(-2)
            });
        await db.SaveChangesAsync();

        Assert.True(await new EfCoreManagedElsaHandoffStore(db, new FixedTimeProvider(now))
            .TryConsumeAsync("current", now.AddMinutes(1)));

        var retained = await db.ManagedElsaHandoffReplays.Select(x => x.Jti).Order().ToListAsync();
        Assert.Equal(["current", "expired-recent"], retained);
    }

    [Fact]
    public void Sqlite_non_unique_constraints_are_not_classified_as_replays()
    {
        var exception = new DbUpdateException(
            "constraint",
            new SqliteException("not null", 19, 1299));

        Assert.False(EfCoreDatabaseExceptionPolicy.IsUniqueViolation(exception));
    }

    [Fact]
    public async Task Concurrent_replay_consumers_have_one_winner()
    {
        var databaseName = "handoff-atomic-" + Guid.NewGuid().ToString("N");
        await using var setupConnection = NewConnection(databaseName);
        await setupConnection.OpenAsync();
        await using (var setup = CreateContext(setupConnection))
            await setup.Database.MigrateAsync();

        var connections = Enumerable.Range(0, 4).Select(_ => NewConnection(databaseName)).ToArray();
        try
        {
            foreach (var connection in connections)
                await connection.OpenAsync();

            var outcomes = await Task.WhenAll(connections.Select(async connection =>
            {
                await using var db = CreateContext(connection);
                var store = new EfCoreManagedElsaHandoffStore(db);
                return await store.TryConsumeAsync("handoff-race", DateTimeOffset.UtcNow.AddMinutes(1));
            }));

            Assert.Single(outcomes, winner => winner);
            Assert.Equal(1, outcomes.Count(winner => winner));
        }
        finally
        {
            foreach (var connection in connections)
                await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Audit_sink_persists_bounded_safe_metadata()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var audit = new ManagedElsaHandoffAuditRecord(
            "redeem.succeeded",
            "handoff-jti-2",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "urn:elsa:instance:00000000-0000-0000-0000-000000000001",
            3,
            "trace-2",
            now);

        await new EfCoreManagedElsaHandoffStore(db).RecordAsync(audit);

        var saved = await db.ManagedElsaHandoffAuditEvents.SingleAsync();
        Assert.Equal(audit.Action, saved.Action);
        Assert.Equal(audit.Jti, saved.Jti);
        Assert.Equal(audit.BindingVersion, saved.BindingVersion);
        Assert.Equal(audit.CorrelationId, saved.CorrelationId);
        Assert.Equal(now.UtcTicks, saved.OccurredAt.UtcTicks);
        Assert.DoesNotContain(db.Model.FindEntityType(typeof(ManagedElsaHandoffAuditEventEntity))!.GetProperties(),
            property => property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Verifier", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Handoff_audit_rows_are_append_only_and_reject_unsafe_correlation()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.MigrateAsync();
        var store = new EfCoreManagedElsaHandoffStore(db);
        await store.RecordAsync(new ManagedElsaHandoffAuditRecord(
            "redeem.invalid", "", null, null, null, null, null, "trace-3", DateTimeOffset.UtcNow));

        var saved = await db.ManagedElsaHandoffAuditEvents.SingleAsync();
        saved.Action = "redeem.succeeded";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordAsync(new ManagedElsaHandoffAuditRecord(
            "redeem.invalid", "", null, null, null, null, null, "trace with spaces", DateTimeOffset.UtcNow)).AsTask());

        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE ManagedElsaHandoffAuditEvents SET Action = 'redeem.succeeded'"));
    }

    [Fact]
    public void SqlServer_migration_script_guards_handoff_rows()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\MSSQLLocalDB;Initial Catalog=ElsaControlHandoffMigrationTests;Integrated Security=True;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options;
        using var db = new CatalogDbContext(options);

        var script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);

        Assert.Contains("TR_ManagedElsaHandoffReplayConsumptions_AppendOnly", script, StringComparison.Ordinal);
        Assert.Contains("TR_ManagedElsaHandoffAuditEvents_AppendOnly", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Identity_store_creates_finds_and_rotates_the_current_binding()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        await using (var setup = CreateContext(connection))
        {
            await setup.Database.MigrateAsync();
            await SeedInstanceAsync(setup);
        }

        Guid organizationId;
        Guid workspaceId;
        Guid instanceId;
        await using (var read = CreateContext(connection))
        {
            var instance = await read.ElsaInstances.SingleAsync();
            organizationId = instance.OrganizationId;
            workspaceId = instance.WorkspaceId;
            instanceId = instance.Id;
        }

        var changedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await using (var bindDb = CreateContext(connection))
        {
            var store = new EfCoreManagedElsaInstanceIdentityStore(bindDb);
            var created = await store.BindAsync(organizationId, workspaceId, instanceId,
                "https://managed.example.test", null, changedAt);
            Assert.Equal(ManagedElsaInstanceIdentityBindingWriteOutcome.Created, created.Outcome);
            Assert.Equal(1, created.Identity?.BindingVersion);

            var found = await store.FindAsync(organizationId, instanceId);
            Assert.Equal(created.Identity, found);
        }

        await using (var rotateDb = CreateContext(connection))
        {
            var store = new EfCoreManagedElsaInstanceIdentityStore(rotateDb);
            var stale = await store.BindAsync(organizationId, workspaceId, instanceId,
                "https://rotated.example.test", 9, changedAt.AddMinutes(1));
            Assert.Equal(ManagedElsaInstanceIdentityBindingWriteOutcome.Conflict, stale.Outcome);

            var invalid = await store.BindAsync(organizationId, workspaceId, instanceId,
                "https://rotated.example.test", 0, changedAt.AddMinutes(1));
            Assert.Equal(ManagedElsaInstanceIdentityBindingWriteOutcome.Conflict, invalid.Outcome);

            var instance = await rotateDb.ElsaInstances.SingleAsync();
            instance.CurrentDeploymentEndpointUri = "https://rotated.example.test";
            await rotateDb.SaveChangesAsync();

            var rotated = await store.BindAsync(organizationId, workspaceId, instanceId,
                "https://rotated.example.test", 1, changedAt.AddMinutes(1));
            Assert.Equal(ManagedElsaInstanceIdentityBindingWriteOutcome.Rotated, rotated.Outcome);
            Assert.Equal(2, rotated.Identity?.BindingVersion);
            Assert.Equal("https://rotated.example.test/managed-elsa/handoff/callback",
                rotated.Identity?.CallbackUri.AbsoluteUri);
        }

        await using (var endpointRemoved = CreateContext(connection))
        {
            var instance = await endpointRemoved.ElsaInstances.SingleAsync();
            instance.CurrentDeploymentEndpointUri = null;
            await endpointRemoved.SaveChangesAsync();
            Assert.Null(await new EfCoreManagedElsaInstanceIdentityStore(endpointRemoved)
                .FindAsync(organizationId, instanceId));
        }
    }

    [Fact]
    public async Task Identity_store_fails_closed_for_missing_deleted_and_malformed_bindings()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        Guid organizationId;
        Guid instanceId;
        await using (var setup = CreateContext(connection))
        {
            await setup.Database.MigrateAsync();
            var instance = await SeedInstanceAsync(setup);
            organizationId = instance.OrganizationId;
            instanceId = instance.Id;
        }

        await using (var read = CreateContext(connection))
        {
            var store = new EfCoreManagedElsaInstanceIdentityStore(read);
            Assert.Null(await store.FindAsync(organizationId, Guid.NewGuid()));
        }

        await using (var bind = CreateContext(connection))
        {
            var instance = await bind.ElsaInstances.SingleAsync();
            bind.ElsaInstanceIdentityBindings.Add(new ElsaInstanceIdentityBindingEntity
            {
                InstanceId = instance.Id,
                Audience = ElsaInstanceIdentityBinding.AudienceFor(instance.Id),
                CanonicalCallbackUri = ElsaInstanceIdentityBinding.CanonicalizeCallbackUri("https://managed.example.test"),
                VerifiedEndpointOrigin = "https://managed.example.test",
                BindingVersion = 1,
                ChangedAt = DateTimeOffset.UtcNow
            });
            await bind.SaveChangesAsync();

            await bind.Database.ExecuteSqlInterpolatedAsync($"UPDATE ElsaInstanceIdentityBindings SET CanonicalCallbackUri = 'https://managed.example.test/wrong' WHERE InstanceId = {instance.Id}");
        }

        await using (var malformed = CreateContext(connection))
        {
            var store = new EfCoreManagedElsaInstanceIdentityStore(malformed);
            Assert.Null(await store.FindAsync(organizationId, instanceId));
        }

        await using (var deleted = CreateContext(connection))
        {
            var instance = await deleted.ElsaInstances.SingleAsync();
            instance.DesiredLifecycle = ElsaDesiredLifecycle.Deleting;
            instance.ObservedLifecycle = ElsaObservedLifecycle.Deleted;
            instance.DeletedAt = DateTimeOffset.UtcNow;
            await deleted.SaveChangesAsync();
        }

        await using (var unavailable = CreateContext(connection))
        {
            var store = new EfCoreManagedElsaInstanceIdentityStore(unavailable);
            Assert.Null(await store.FindAsync(organizationId, instanceId));
        }
    }

    [Fact]
    public async Task Openable_identity_requires_the_current_deployment_to_carry_the_managed_handoff()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.MigrateAsync();
        var instance = await SeedInstanceAsync(db);
        instance.ObservedLifecycle = ElsaObservedLifecycle.Ready;
        instance.Health = ElsaInstanceHealth.Healthy;
        await db.SaveChangesAsync();
        var identities = new EfCoreManagedElsaInstanceIdentityStore(db);
        var catalog = new EfCoreManagedElsaInstanceCatalog(db);
        Assert.True((await identities.BindAsync(instance.OrganizationId, instance.WorkspaceId, instance.Id,
            "https://managed.example.test", expectedBindingVersion: null, DateTimeOffset.UtcNow)).Succeeded);

        // Healthy and bound, but deployed without the runtime handoff: listing, issuing and redeeming all refuse it.
        Assert.NotNull(await identities.FindAsync(instance.OrganizationId, instance.Id));
        Assert.Null(await identities.FindOpenableAsync(instance.OrganizationId, instance.Id));
        Assert.Empty(await identities.FindOpenableManyAsync(instance.OrganizationId, [instance.Id]));
        Assert.Null(Assert.Single(await catalog.ListAsync(instance.WorkspaceId)).CallbackUri);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstances SET CurrentDeploymentManagedHandoff = {true} WHERE Id = {instance.Id}");

        Assert.NotNull(await identities.FindOpenableAsync(instance.OrganizationId, instance.Id));
        Assert.Single(await identities.FindOpenableManyAsync(instance.OrganizationId, [instance.Id]));
        Assert.Equal("https://managed.example.test/managed-elsa/handoff/callback",
            Assert.Single(await catalog.ListAsync(instance.WorkspaceId)).CallbackUri?.AbsoluteUri);
    }

    [Fact]
    public async Task A_managed_handoff_cannot_be_persisted_without_a_current_deployment_endpoint()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.MigrateAsync();
        var instance = await SeedInstanceAsync(db);

        instance.CurrentDeploymentManagedHandoff = true;
        instance.CurrentDeploymentEndpointUri = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Identity_store_does_not_create_binding_while_instance_is_deleting()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.MigrateAsync();
        var instance = await SeedInstanceAsync(db);
        instance.ObservedLifecycle = ElsaObservedLifecycle.Deleting;
        await db.SaveChangesAsync();

        var identity = await new EfCoreManagedElsaInstanceIdentityStore(db)
            .EnsureAsync(instance.OrganizationId, instance.Id);

        Assert.Null(identity);
        Assert.Empty(await db.ElsaInstanceIdentityBindings.ToListAsync());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static async Task<ElsaInstanceEntity> SeedInstanceAsync(CatalogDbContext db)
    {
        var workspace = new Workspace { Name = "Handoff workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        var instance = new ElsaInstanceEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = workspace.OrganizationId,
            WorkspaceId = workspace.Id,
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
            CurrentDeploymentId = "deployment-managed",
            CurrentDeploymentEndpointUri = "https://managed.example.test",
            DesiredLifecycle = ElsaDesiredLifecycle.Running,
            ObservedLifecycle = ElsaObservedLifecycle.Pending,
            Health = ElsaInstanceHealth.Unknown,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();
        return instance;
    }

    private static SqliteConnection NewConnection(string? databaseName = null) =>
        new($"Data Source=file:{databaseName ?? "handoff-tests-" + Guid.NewGuid().ToString("N")};Mode=Memory;Cache=Shared;Default Timeout=30");

    private static CatalogDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);
}
