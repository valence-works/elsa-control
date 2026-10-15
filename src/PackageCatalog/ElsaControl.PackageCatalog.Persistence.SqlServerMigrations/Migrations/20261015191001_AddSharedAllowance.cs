using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

/// <summary>
/// Adds MaxSharedTenants and the assignment/shard tables used to enforce
/// ADR-0017 Amendment 1. Existing entitlement rows keep a null allowance (zero).
/// </summary>
[DbContext(typeof(CatalogDbContext))]
[Migration("20261015191001_AddSharedAllowance")]
public sealed class AddSharedAllowance : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "MaxSharedTenants",
            table: "OrganizationEntitlementSnapshots",
            type: "int",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "ElsaSharedShards",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RegionCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                ShardClass = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                Capacity = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ElsaSharedShards", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_ElsaSharedShards_RegionCode_ShardClass",
            table: "ElsaSharedShards",
            columns: ["RegionCode", "ShardClass"]);

        migrationBuilder.CreateTable(
            name: "ElsaPlacementAssignments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ShardClass = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                SupersedesAssignmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ShardId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PackageSetDigest = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ElsaPlacementAssignments", x => x.Id);
                table.ForeignKey(
                    name: "FK_ElsaPlacementAssignments_ElsaInstances_InstanceId",
                    column: x => x.InstanceId,
                    principalTable: "ElsaInstances",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ElsaPlacementAssignments_OrganizationId_Kind_ShardClass_State",
            table: "ElsaPlacementAssignments",
            columns: ["OrganizationId", "Kind", "ShardClass", "State"]);

        migrationBuilder.CreateIndex(
            name: "IX_ElsaPlacementAssignments_InstanceId_State",
            table: "ElsaPlacementAssignments",
            columns: ["InstanceId", "State"],
            unique: true,
            filter: "State = 'Active'");

        migrationBuilder.CreateIndex(
            name: "IX_ElsaPlacementAssignments_ShardId",
            table: "ElsaPlacementAssignments",
            column: "ShardId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ElsaPlacementAssignments");
        migrationBuilder.DropTable(name: "ElsaSharedShards");
        migrationBuilder.DropColumn(name: "MaxSharedTenants", table: "OrganizationEntitlementSnapshots");
    }
}
