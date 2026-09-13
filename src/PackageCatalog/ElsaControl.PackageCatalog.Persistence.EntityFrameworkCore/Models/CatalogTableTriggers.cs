using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;

/// <summary>
/// Declares the guard triggers the catalog migrations install. EF Core never creates or drops
/// these triggers; the declaration only makes EF batch writes in a trigger-safe way. Without it,
/// EF emits <c>OUTPUT</c> without <c>INTO</c> on SQL Server, which SQL Server rejects with error 334
/// for any table that has enabled triggers. Keep these lists equal to what the migrations create;
/// <c>CatalogTableTriggerDeclarationTests</c> enforces that for both providers.
/// </summary>
internal static class CatalogTableTriggers
{
    private static readonly IReadOnlyDictionary<string, string[]> SqlServer = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["AzureProviderAssignmentRebinds"] = ["TR_AzureProviderAssignmentRebinds_AppendOnly"],
        ["AzureProviderRecoveryObservations"] = ["TR_AzureProviderRecoveryObservations_AppendOnly"],
        ["DeploymentEnvironments"] = ["TR_DeploymentEnvironments_ManagedInstanceBinding"],
        ["DeploymentRuns"] = ["TR_DeploymentRuns_ManagedInstanceBinding"],
        ["ElsaInstanceAuditEvents"] = ["TR_ElsaInstanceAuditEvents_AppendOnly"],
        ["ElsaInstanceIntentRevisions"] = ["TR_ElsaInstanceIntentRevisions_AppendOnly"],
        ["ElsaInstanceLifecycleOutbox"] = ["TR_ElsaInstanceLifecycleOutbox_AppendOnly"],
        ["ElsaInstanceMigrations"] = ["TR_ElsaInstanceMigrations_NoDelete"],
        ["ElsaInstanceOperations"] = ["TR_ElsaInstanceOperations_NoDelete"],
        ["ElsaInstanceRecoveryRequests"] = ["TR_ElsaInstanceRecoveryRequests_AppendOnly"],
        ["ElsaInstanceResolvedPlans"] = ["TR_ElsaInstanceResolvedPlans_AppendOnly"],
        ["ElsaInstances"] = ["TR_ElsaInstances_NoDelete"],
        ["ManagedElsaHandoffAuditEvents"] = ["TR_ManagedElsaHandoffAuditEvents_AppendOnly"],
        ["ManagedElsaHandoffReplayConsumptions"] = ["TR_ManagedElsaHandoffReplayConsumptions_AppendOnly"],
    };

    private static readonly IReadOnlyDictionary<string, string[]> Sqlite = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["AzureProviderAssignmentRebinds"] = ["TR_AzureProviderAssignmentRebinds_AppendOnly_Delete", "TR_AzureProviderAssignmentRebinds_AppendOnly_Update"],
        ["AzureProviderRecoveryObservations"] = ["TR_AzureProviderRecoveryObservations_AppendOnly_Delete", "TR_AzureProviderRecoveryObservations_AppendOnly_Update"],
        ["DeploymentEnvironments"] = ["TR_DeploymentEnvironments_ManagedInstanceBinding_Update"],
        ["DeploymentRuns"] = ["TR_DeploymentRuns_ManagedInstanceBinding_Insert", "TR_DeploymentRuns_ManagedInstanceBinding_Update"],
        ["ElsaInstanceAuditEvents"] = ["TR_ElsaInstanceAuditEvents_AppendOnly_Delete", "TR_ElsaInstanceAuditEvents_AppendOnly_Update"],
        ["ElsaInstanceIntentRevisions"] = ["TR_ElsaInstanceIntentRevisions_AppendOnly_Delete", "TR_ElsaInstanceIntentRevisions_AppendOnly_Update"],
        ["ElsaInstanceLifecycleOutbox"] = ["TR_ElsaInstanceLifecycleOutbox_AppendOnly_Delete", "TR_ElsaInstanceLifecycleOutbox_AppendOnly_Update"],
        ["ElsaInstanceMigrations"] = ["TR_ElsaInstanceMigrations_NoDelete"],
        ["ElsaInstanceOperations"] = ["TR_ElsaInstanceOperations_LeaseVersion_Range_Insert", "TR_ElsaInstanceOperations_LeaseVersion_Range_Update", "TR_ElsaInstanceOperations_NoDelete"],
        ["ElsaInstanceRecoveryRequests"] = ["TR_ElsaInstanceRecoveryRequests_AppendOnly_Delete", "TR_ElsaInstanceRecoveryRequests_AppendOnly_Update"],
        ["ElsaInstanceResolvedPlans"] = ["TR_ElsaInstanceResolvedPlans_AppendOnly_Delete", "TR_ElsaInstanceResolvedPlans_AppendOnly_Update"],
        ["ElsaInstances"] = ["TR_ElsaInstances_NoDelete"],
        ["ManagedElsaHandoffAuditEvents"] = ["TR_ManagedElsaHandoffAuditEvents_AppendOnly_Delete", "TR_ManagedElsaHandoffAuditEvents_AppendOnly_Update"],
        ["ManagedElsaHandoffReplayConsumptions"] = ["TR_ManagedElsaHandoffReplayConsumptions_AppendOnly_Update"],
    };

    public static void Declare(ModelBuilder modelBuilder, string? providerName)
    {
        var triggersByTable = providerName switch
        {
            "Microsoft.EntityFrameworkCore.SqlServer" => SqlServer,
            "Microsoft.EntityFrameworkCore.Sqlite" => Sqlite,
            _ => null,
        };
        if (triggersByTable is null)
            return;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            if (entityType.GetTableName() is not { } table || !triggersByTable.TryGetValue(table, out var triggers))
                continue;

            modelBuilder.Entity(entityType.Name).ToTable(tableBuilder =>
            {
                foreach (var trigger in triggers)
                    tableBuilder.HasTrigger(trigger);
            });
        }
    }
}
