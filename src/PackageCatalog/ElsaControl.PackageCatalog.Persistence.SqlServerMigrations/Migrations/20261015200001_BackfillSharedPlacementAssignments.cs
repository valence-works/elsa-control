using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

/// <summary>
/// Backfills Active Shared assignments for live managed Shared instances that
/// existed before <c>ElsaPlacementAssignments</c>. Instances that cannot be
/// reconstructed stay unassigned; the Shared count fallback still includes them.
/// </summary>
[DbContext(typeof(CatalogDbContext))]
[Migration("20261015200001_BackfillSharedPlacementAssignments")]
public sealed class BackfillSharedPlacementAssignments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            INSERT INTO ElsaPlacementAssignments (
                Id, InstanceId, OrganizationId, WorkspaceId,
                Kind, ShardClass, State, SupersedesAssignmentId,
                ShardId, PackageSetDigest, CreatedAt)
            SELECT
                NEWID(),
                i.Id,
                i.OrganizationId,
                i.WorkspaceId,
                N'shared-shard',
                N'standard',
                N'Active',
                NULL,
                NULL,
                NULL,
                i.CreatedAt
            FROM ElsaInstances AS i
            WHERE i.TargetMode = N'managed'
              AND i.IsolationProfile = N'shared'
              AND i.DeletedAt IS NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM ElsaPlacementAssignments AS a
                  WHERE a.InstanceId = i.Id);
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
