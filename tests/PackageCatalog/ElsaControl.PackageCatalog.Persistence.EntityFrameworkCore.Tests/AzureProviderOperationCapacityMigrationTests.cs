using System.Text.Json;
using ElsaControl.Deployment.Azure;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class AzureProviderOperationCapacityMigrationTests
{
    private const string PreviousSqliteMigration = "20260912020258_AddSyncRunMode";
    private const string PreviousSqlServerMigration = "20260912020301_AddSyncRunMode";
    private const string SqlServerMigration = "20260912132436_AddAzureProviderOperationCapacity";
    private static readonly string[] CapacityColumns =
        ["CapacityMinReplicas", "CapacityMaxReplicas", "CapacityCpuMillicores", "CapacityMemoryMiB"];

    [Fact]
    public async Task Sqlite_operation_retained_before_the_migration_stays_restorable_without_capacity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);
        var workspaceId = Guid.NewGuid();

        await db.Database.MigrateAsync(PreviousSqliteMigration);
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Retained operation workspace" });
        await db.SaveChangesAsync();
        var operationId = await InsertRetainedOperationAsync(db, workspaceId);
        await db.Database.MigrateAsync();

        var retained = await new AzureProviderOperationStore(db).GetAsync(workspaceId, operationId);
        var restored = new PersistedAzureProviderPlanSource().Resolve(retained!);

        Assert.Null(retained!.Capacity);
        Assert.False(retained.PersistedMetadataInvalid);
        Assert.NotNull(restored);
        Assert.Null(restored.Capacity);
    }

    [Fact]
    public void SqlServer_adds_nullable_capacity_columns_without_rewriting_retained_operations()
    {
        using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                @"Server=(localdb)\MSSQLLocalDB;Initial Catalog=ElsaControlMigrationScriptTests;Integrated Security=True;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);

        var script = db.GetService<IMigrator>().GenerateScript(fromMigration: PreviousSqlServerMigration, toMigration: SqlServerMigration);

        Assert.All(CapacityColumns, column =>
            Assert.Contains($"ALTER TABLE [AzureProviderOperations] ADD [{column}] int NULL;", script, StringComparison.Ordinal));
        Assert.DoesNotContain("UPDATE [AzureProviderOperations]", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes the row shape an operation had before capacity existed, with the request hash and
    /// identity that shape was stored with.
    /// </summary>
    private static async Task<Guid> InsertRetainedOperationAsync(CatalogDbContext db, Guid workspaceId)
    {
        var request = AzureProviderOperationValidation.Normalize(AzureProviderOperationCapacityPersistenceTests.RestorableRequest(workspaceId));
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var hash = AzureProviderOperationValidation.ComputeRequestHash(request);
        var identity = AzureProviderOperationValidation.ComputeOperationIdentity(request);
        var secretReferences = JsonSerializer.Serialize(request.SecretReferences);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AzureProviderOperations" (
                "Id", "WorkspaceId", "TargetKey", "Action", "IdempotencyKey", "RequestHash", "OperationIdentity",
                "PlanFingerprint", "TemplateFingerprint", "ProviderScopeFingerprint", "SqlWorkflowPackageVersion", "SqlQuartzPackageVersion",
                "ElsaVersion", "ReleaseLine", "Topology", "Isolation", "Location", "ImageRepository", "ImageDigest",
                "ReleaseManifestDigest", "ReleaseManifestSignatureDigest", "ReleaseManifestReference", "ReleaseManifestSignatureReference",
                "SecretReferencesJson", "Status", "Phase", "CheckpointSequence", "AttemptNumber", "Version", "Health", "DiagnosticsJson",
                "CreatedAt", "UpdatedAt")
            VALUES (
                {id}, {workspaceId}, {request.TargetKey}, {"Reconcile"}, {request.IdempotencyKey}, {hash}, {identity},
                {request.PlanFingerprint}, {request.TemplateFingerprint}, {request.ProviderScopeFingerprint}, {request.SqlWorkflowPackageVersion}, {request.SqlQuartzPackageVersion},
                {request.ElsaVersion}, {request.ReleaseLine}, {request.Topology}, {request.Isolation}, {request.Location}, {request.ImageRepository}, {request.ImageDigest},
                {request.ReleaseManifestDigest}, {request.ReleaseManifestSignatureDigest}, {request.ReleaseManifestReference}, {request.ReleaseManifestSignatureReference},
                {secretReferences}, {"Succeeded"}, {"TrafficPromoted"}, {6}, {1}, {9}, {"Healthy"}, {"[]"},
                {now}, {now})
            """);
        return id;
    }
}
