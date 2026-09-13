using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class SqlServerMigrationScriptTests
{
    [Fact]
    public void Full_idempotent_script_uses_dynamic_binding_for_historical_backfills()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                @"Server=(localdb)\MSSQLLocalDB;Initial Catalog=ElsaControlMigrationScriptTests;Integrated Security=True;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options;
        using var db = new CatalogDbContext(options);

        var script = db.GetService<IMigrator>().GenerateScript(
            options: MigrationsSqlGenerationOptions.Idempotent);

        Assert.Contains("EXEC(N'UPDATE [SyncRuns]", script, StringComparison.Ordinal);
        Assert.Contains("EXEC(N'UPDATE Packages", script, StringComparison.Ordinal);
        Assert.Contains("EXEC(N'INSERT INTO DeploymentTierDefinitions", script, StringComparison.Ordinal);
        Assert.Contains("EXEC(N'UPDATE StructuredDesiredStateRecords", script, StringComparison.Ordinal);
        Assert.Contains("EXEC(N'INSERT INTO Organizations", script, StringComparison.Ordinal);
        Assert.Contains("EXEC(N'INSERT INTO WorkspacePermissionAuditRecords", script, StringComparison.Ordinal);
        Assert.Contains("TR_ElsaInstanceOperations_NoDelete", script, StringComparison.Ordinal);
        Assert.Contains("EXEC(N'CREATE TRIGGER dbo.TR_AzureProviderRecoveryObservations_AppendOnly", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [AzureProviderAssignmentRebinds]", script, StringComparison.Ordinal);
        Assert.Contains("EXEC(N'CREATE TRIGGER dbo.TR_AzureProviderAssignmentRebinds_AppendOnly", script, StringComparison.Ordinal);
        var normalizedScript = script.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        Assert.DoesNotContain("BEGIN\n    UPDATE [SyncRuns]\n    SET [CompletedAtTicks]", normalizedScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Elsa_instance_migration_uses_sql_server_supported_filtered_index_predicate()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                @"Server=(localdb)\MSSQLLocalDB;Initial Catalog=ElsaControlMigrationScriptTests;Integrated Security=True;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options;
        using var db = new CatalogDbContext(options);

        var script = db.GetService<IMigrator>().GenerateScript(
            fromMigration: "20260830171226_AddElsaInstanceDeletionEvidence",
            toMigration: "20260830191058_OperateElsaInstanceMigrations");

        const string activeIndexMarker = "CREATE UNIQUE INDEX [IX_ElsaInstanceMigrations_InstanceId]";
        var activeIndexStart = script.IndexOf(activeIndexMarker, StringComparison.Ordinal);
        Assert.True(activeIndexStart >= 0, "The generated script did not contain the active migration index.");

        var activeIndexEnd = script.IndexOf(';', activeIndexStart);
        Assert.True(activeIndexEnd > activeIndexStart, "The generated active migration index statement was incomplete.");

        var activeIndexSql = script[activeIndexStart..activeIndexEnd];
        Assert.Matches(
            @"WHERE\s+\[?Phase\]?\s*<>\s*N?'RolledBack'\s+AND\s+\[?Phase\]?\s*<>\s*N?'Released'\s+AND\s+\[?Phase\]?\s*<>\s*N?'Failed'",
            activeIndexSql);
        Assert.DoesNotMatch(@"\bNOT\s+IN\b", activeIndexSql);
    }

    [Fact]
    public void Azure_provider_assignment_migration_persists_assignment_identity_and_references()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                @"Server=(localdb)\MSSQLLocalDB;Initial Catalog=ElsaControlMigrationScriptTests;Integrated Security=True;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options;
        using var db = new CatalogDbContext(options);

        var script = db.GetService<IMigrator>().GenerateScript(
            fromMigration: "20260904055356_AddBillingLifecycleNoticesAndCleanup",
            toMigration: "20260904104524_AddAzureProviderResourceAssignments");

        Assert.Contains("ALTER TABLE [AzureProviderOperations] ADD [ProviderAssignmentId] uniqueidentifier NULL", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [AzureProviderResourceAssignments]", script, StringComparison.Ordinal);
        Assert.Contains("[ProviderScopeFingerprint] nvarchar(64) NOT NULL", script, StringComparison.Ordinal);
        Assert.Contains("[WorkloadIdentityResourceId] nvarchar(1024) NULL", script, StringComparison.Ordinal);
        Assert.Contains("[AcrPullRoleAssignmentId] nvarchar(1024) NULL", script, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT [FK_AzureProviderResourceAssignments_Workspaces_WorkspaceId]", script, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT [FK_AzureProviderOperations_AzureProviderResourceAssignments_ProviderAssignmentId]", script, StringComparison.Ordinal);
        Assert.Contains("ON DELETE NO ACTION", script, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX [IX_AzureProviderResourceAssignments_State_UpdatedAt_Id]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX [IX_AzureProviderResourceAssignments_WorkspaceId_InstanceId_ProviderScopeFingerprint]", script, StringComparison.Ordinal);
    }
}
