using System.Text.Json;
using ElsaControl.Deployment.Core.ExternalConnections;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ExternalEngineConnectionPersistenceTests : IAsyncLifetime
{
    private const string SqliteMigration = "20260917090635_AddExternalEngineConnections";
    private const string SqlServerMigration = "20260917090821_AddExternalEngineConnections";
    private static readonly Guid OrganizationId = Guid.Parse("10000000-0000-0000-0000-000000000481");
    private static readonly Guid WorkspaceId = Guid.Parse("20000000-0000-0000-0000-000000000481");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T09:00:00Z");
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"elsa-control-connection-{Guid.NewGuid():N}.db");

    public async Task InitializeAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        var organization = new Organization
        {
            Id = OrganizationId,
            Name = "Connection test organization",
            CreatedAt = Now,
            UpdatedAt = Now
        };
        db.Organizations.Add(organization);
        db.Workspaces.Add(new Workspace
        {
            Id = WorkspaceId,
            OrganizationId = OrganizationId,
            Organization = organization,
            Name = "Connection test workspace",
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Create_is_scoped_idempotent_and_conflicting_reuse_fails()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineConnectionStore(db);
        var proposed = Connection(Guid.NewGuid(), "Customer engine");

        var created = await store.TryCreateAsync(proposed, "create-engine", "digest-a");
        var replayed = await store.TryCreateAsync(Connection(Guid.NewGuid(), "Customer engine"), "create-engine", "digest-a");
        var conflict = await store.TryCreateAsync(Connection(Guid.NewGuid(), "Other engine"), "create-engine", "digest-b");

        Assert.True(created.Succeeded);
        Assert.False(created.Replayed);
        Assert.True(replayed.Succeeded);
        Assert.True(replayed.Replayed);
        Assert.Equal(proposed.Id, replayed.Connection!.Id);
        Assert.Equal(ExternalEngineConnectionCreateFailure.Conflict, conflict.Failure);
        Assert.Single(await store.ListAsync(OrganizationId, WorkspaceId));
        Assert.Empty(await store.ListAsync(OrganizationId, Guid.NewGuid()));
        Assert.Null(await store.FindAsync(Guid.NewGuid(), WorkspaceId, proposed.Id));
    }

    [Fact]
    public async Task Concurrent_create_replay_has_one_durable_connection()
    {
        var first = CreateFromNewContextAsync(Connection(Guid.NewGuid(), "Concurrent engine"));
        var second = CreateFromNewContextAsync(Connection(Guid.NewGuid(), "Concurrent engine"));

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(results[0].Connection!.Id, results[1].Connection!.Id);
        await using var db = CreateDbContext();
        Assert.Single(await db.ExternalEngineConnections.ToListAsync());
    }

    [Fact]
    public async Task Disconnect_retains_a_value_free_tombstone_and_append_only_audit()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineConnectionStore(db);
        var value = Connection(Guid.NewGuid(), "Customer engine");
        var current = (await store.TryCreateAsync(value, "disconnect-engine", "digest")).Connection!;
        current = (await store.TrySetPairingChallengeAsync(current, Guid.NewGuid(), Now.AddMinutes(1)))!;
        current = (await store.TrySetActiveIdentityAsync(current, Guid.NewGuid(), Now.AddMinutes(2)))!;

        var disconnected = await store.TryDisconnectAsync(current, Now.AddMinutes(3));

        Assert.NotNull(disconnected);
        Assert.Equal(ExternalEngineConnectionStatus.Revoked, disconnected.Status);
        Assert.NotNull(disconnected.RevokedAt);
        Assert.NotNull(disconnected.ActiveIdentityId);
        var persisted = await store.FindAsync(OrganizationId, WorkspaceId, value.Id);
        Assert.Equal(ExternalEngineConnectionStatus.Revoked, persisted!.Status);
        var audits = await db.ExternalEngineConnectionAuditEvents.OrderBy(x => x.OccurredAt).ToArrayAsync();
        Assert.Equal(["Created", "PairingIssued", "IdentityEnrolled", "Disconnected"], audits.Select(x => x.Action));
        Assert.Equal(
            ["Action", "ConnectionId", "Id", "OccurredAt", "OrganizationId", "WorkspaceId"],
            db.Model.FindEntityType(typeof(ExternalEngineConnectionAuditEventEntity))!
                .GetProperties().Select(x => x.Name).Order(StringComparer.Ordinal));
        var serialized = JsonSerializer.Serialize(audits);
        Assert.DoesNotContain("challenge", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publicKey", serialized, StringComparison.OrdinalIgnoreCase);

        db.ExternalEngineConnectionAuditEvents.Remove(audits[0]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void Provider_migrations_are_synchronized()
    {
        using var sqlite = CreateDbContext();
        using var sqlServer = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,1;Database=ElsaControlMigrationScriptTests;User ID=unused;Password=unused;Encrypt=False",
                options => options.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);

        Assert.Contains(SqliteMigration, sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqlServerMigration, sqlServer.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.False(sqlite.Database.HasPendingModelChanges());
        Assert.False(sqlServer.Database.HasPendingModelChanges());
        var script = sqlServer.GetService<IMigrator>().GenerateScript(
            toMigration: SqlServerMigration,
            options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("ExternalEngineConnections", script, StringComparison.Ordinal);
        Assert.Contains("CustomerOperated", script, StringComparison.Ordinal);
        Assert.Contains("ExternalEngineConnectionAuditEvents", script, StringComparison.Ordinal);
        Assert.Contains("TR_ExternalEngineConnectionAuditEvents_AppendOnly", script, StringComparison.Ordinal);
    }

    private CatalogDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(
                $"Data Source={_databasePath};Pooling=False;Default Timeout=30",
                options => options.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);

    private async Task<ExternalEngineConnectionCreateResult> CreateFromNewContextAsync(ExternalEngineConnection connection)
    {
        await using var db = CreateDbContext();
        return await new EfCoreExternalEngineConnectionStore(db)
            .TryCreateAsync(connection, "concurrent-engine", "same-digest");
    }

    private static ExternalEngineConnection Connection(Guid id, string displayName) =>
        new(
            id,
            OrganizationId,
            WorkspaceId,
            displayName,
            ExternalEngineConnectionStatus.Pending,
            ExternalEngineRuntimeHealth.Unknown,
            ExternalEngineConnectorReachability.Unknown,
            null,
            null,
            null,
            null,
            null,
            ExternalEngineReleaseEvidenceLevel.None,
            null,
            [],
            null,
            null,
            null,
            Now,
            Now,
            null,
            1);
}
