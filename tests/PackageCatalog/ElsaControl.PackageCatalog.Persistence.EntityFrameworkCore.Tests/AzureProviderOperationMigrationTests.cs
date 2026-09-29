using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class AzureProviderOperationMigrationTests
{
    private const string PreviousSqliteMigration = "20260917224000_CapStripeHostedInstances";
    private const string SqliteMigration = "20260928023000_AddAzureProviderOperationStatusChangedAt";

    [Fact]
    public async Task Sqlite_migration_creates_durable_operation_tables_and_indexes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        await using var db = new CatalogDbContext(options);

        await db.Database.MigrateAsync();

        var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToListAsync();
        var indexes = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'index'").ToListAsync();
        Assert.Contains("AzureProviderOperations", tables);
        Assert.Contains("AzureProviderOperationTransitions", tables);
        Assert.Contains("GovernedReleaseCatalogPackageDeclarations", tables);
        Assert.Contains("IX_AzureProviderOperations_WorkspaceId_TargetKey_OperationIdentity", indexes);
        Assert.Contains("IX_AzureProviderOperations_WorkspaceId_TargetKey", indexes);
        Assert.Contains("IX_AzureProviderOperations_Status_LeaseExpiresAt_UpdatedAt_Id", indexes);
        Assert.Contains("IX_GovernedReleaseCatalogPackageDeclarations_ReleaseId_PackageId", indexes);
        Assert.DoesNotContain("IX_AzureProviderOperations_WorkspaceId_Status_LeaseExpiresAt_UpdatedAt", indexes);
        var columns = await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM pragma_table_info('AzureProviderOperations')").ToListAsync();
        Assert.Contains("WorkloadIdentityResourceId", columns);
        Assert.Contains("WorkloadIdentityClientId", columns);
        Assert.Contains("WorkloadIdentityPrincipalId", columns);
        Assert.Contains("KeyVaultResourceId", columns);
        Assert.Contains("KeyVaultUri", columns);
        Assert.Contains("SqlServerResourceId", columns);
        Assert.Contains("SqlServerFqdn", columns);
        Assert.Contains("ContainerAppsEnvironmentResourceId", columns);
        Assert.Contains("RegistryResourceId", columns);
        Assert.Contains("AcrPullDeploymentId", columns);
        Assert.Contains("AcrPullRoleAssignmentId", columns);
        Assert.Contains("ProviderAssignmentId", columns);
        Assert.Contains("ProviderScopeFingerprint", columns);
        Assert.Contains("SqlWorkflowPackageVersion", columns);
        Assert.Contains("SqlQuartzPackageVersion", columns);
        Assert.Contains("StatusChangedAt", columns);
        Assert.Contains("ManagedHandoffStudioGrants", columns);
        Assert.Contains("AttemptedStepStartedAt", columns);
        Assert.Contains("LastArmObservedAt", columns);
        Assert.Contains("AutoResumeCount", columns);
        Assert.Contains("ArmObservationBackoffSeconds", columns);
        Assert.Contains("LastObservationReasonCode", columns);
        var instanceColumns = await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM pragma_table_info('ElsaInstances')").ToListAsync();
        Assert.Contains("CurrentDeploymentStudioGrants", instanceColumns);
        Assert.Contains("CurrentDeploymentStudioGrantsDeploymentId", instanceColumns);
        Assert.Contains("AzureProviderResourceAssignments", tables);
        Assert.Contains("AzureProviderAssignmentRebinds", tables);
        Assert.Contains("AzureProviderRecoveryObservations", tables);
        var triggers = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'trigger'").ToListAsync();
        Assert.Contains("TR_AzureProviderRecoveryObservations_AppendOnly_Update", triggers);
        Assert.Contains("TR_AzureProviderRecoveryObservations_AppendOnly_Delete", triggers);
        Assert.Contains("TR_AzureProviderAssignmentRebinds_AppendOnly_Update", triggers);
        Assert.Contains("TR_AzureProviderAssignmentRebinds_AppendOnly_Delete", triggers);
        Assert.Contains("IX_AzureProviderOperations_ProviderAssignmentId", indexes);
        Assert.Contains("IX_AzureProviderResourceAssignments_State_UpdatedAt_Id", indexes);
        Assert.Contains("IX_AzureProviderResourceAssignments_WorkspaceId_InstanceId_ProviderScopeFingerprint", indexes);
        var assignmentColumns = await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM pragma_table_info('AzureProviderResourceAssignments')").ToListAsync();
        var expectedAssignmentColumns = new[]
        {
            "Id", "WorkspaceId", "OrganizationId", "InstanceId", "ProviderScopeFingerprint", "NamingVersion",
            "SubscriptionId", "ResourceGroupName", "WorkloadName", "OwnershipKey", "Location", "State", "Version",
            "LastOperationId", "FoundationDeploymentId", "WorkloadDeploymentId", "WorkloadResourceId",
            "WorkloadRevisionName", "StableTrafficRevisionName", "WorkloadIdentityResourceId", "WorkloadIdentityClientId",
            "WorkloadIdentityPrincipalId", "KeyVaultResourceId", "KeyVaultUri", "SqlServerResourceId", "SqlServerFqdn",
            "ContainerAppsEnvironmentResourceId", "RegistryResourceId", "AcrPullDeploymentId", "AcrPullRoleAssignmentId",
            "CreatedAt", "UpdatedAt", "DeletedAt"
        };
        Assert.All(expectedAssignmentColumns, column => Assert.Contains(column, assignmentColumns));
        var assignmentForeignKeys = await db.Database.SqlQueryRaw<string>(
            "SELECT \"table\" || ':' || \"on_delete\" AS Value FROM pragma_foreign_key_list('AzureProviderResourceAssignments')")
            .ToListAsync();
        Assert.Contains("Workspaces:RESTRICT", assignmentForeignKeys);
        var operationForeignKeys = await db.Database.SqlQueryRaw<string>(
            "SELECT \"table\" || ':' || \"on_delete\" AS Value FROM pragma_foreign_key_list('AzureProviderOperations')")
            .ToListAsync();
        Assert.Contains("AzureProviderResourceAssignments:RESTRICT", operationForeignKeys);
        var releaseColumns = await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM pragma_table_info('GovernedReleaseCatalog')").ToListAsync();
        Assert.Contains("ComponentDeclarationsFormat", releaseColumns);
        Assert.Contains("ComponentDeclarationsDigest", releaseColumns);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var observationColumns = await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM pragma_table_info('AzureProviderRecoveryObservations')").ToListAsync();
        Assert.Contains("NaturalKey", observationColumns);
        Assert.Contains("RecordDigest", observationColumns);
        Assert.Contains("ObservedLifecycleAttemptNumber", observationColumns);
        Assert.Contains("ObservedInstanceVersion", observationColumns);
    }

    [Fact]
    public async Task Sqlite_backfills_StatusChangedAt_from_UpdatedAt_for_preexisting_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);

        await db.Database.MigrateAsync(PreviousSqliteMigration);
        var workspaceId = Guid.NewGuid();
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Pre-existing status-changed workspace" });
        await db.SaveChangesAsync();

        var operationId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 9, 28, 1, 0, 0, TimeSpan.Zero).UtcTicks;
        var updatedAt = new DateTimeOffset(2026, 9, 28, 1, 23, 0, TimeSpan.Zero).UtcTicks;
        Assert.NotEqual(createdAt, updatedAt);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AzureProviderOperations" (
                "Id", "WorkspaceId", "TargetKey", "Action", "IdempotencyKey", "RequestHash", "OperationIdentity",
                "PlanFingerprint", "TemplateFingerprint", "ElsaVersion", "ReleaseLine", "Topology", "Isolation",
                "Location", "ImageRepository", "ImageDigest", "SecretReferencesJson", "Status", "Phase",
                "CheckpointSequence", "AttemptNumber", "Version", "Health", "DiagnosticsJson", "ManagedHandoff",
                "CreatedAt", "UpdatedAt")
            VALUES (
                {operationId}, {workspaceId}, {"preexisting-target"}, {"Reconcile"}, {"preexisting-idempotency"},
                {"request-hash"}, {"operation-identity"}, {"plan-fingerprint"}, {"template-fingerprint"},
                {"3.8.1"}, {"3.8"}, {"topology"}, {"isolated"}, {"westeurope"}, {"provider/image"},
                {"sha256:provider-image"}, {"{}"}, {"Succeeded"}, {"HealthVerified"}, {6}, {1}, {9},
                {"Healthy"}, {"[]"}, {0}, {createdAt}, {updatedAt})
            """);

        await db.Database.MigrateAsync(SqliteMigration);

        var statusChangedAt = await db.Database.SqlQueryRaw<long>(
            """SELECT "StatusChangedAt" AS Value FROM "AzureProviderOperations" """).SingleAsync();
        Assert.Equal(updatedAt, statusChangedAt);
        Assert.NotEqual(createdAt, statusChangedAt);
    }

    [Fact]
    public async Task Sqlite_down_migration_drops_StatusChangedAt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);

        await db.Database.MigrateAsync();
        Assert.Contains("StatusChangedAt", await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM pragma_table_info('AzureProviderOperations')").ToListAsync());

        await db.Database.MigrateAsync(PreviousSqliteMigration);

        Assert.DoesNotContain("StatusChangedAt", await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM pragma_table_info('AzureProviderOperations')").ToListAsync());
    }
}
