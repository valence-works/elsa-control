using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261002210001_AddRecoveryRequiredAlertOutbox")]
public sealed class AddRecoveryRequiredAlertOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ElsaInstanceRecoveryRequiredAlertOutbox",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AttemptNumber = table.Column<int>(type: "int", nullable: false),
                DedupeIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ElsaInstanceRecoveryRequiredAlertOutbox", x => x.Id);
                table.ForeignKey(
                    name: "FK_ElsaInstanceRecoveryRequiredAlertOutbox_ElsaInstanceOperations_OperationId",
                    column: x => x.OperationId,
                    principalTable: "ElsaInstanceOperations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ElsaInstanceRecoveryRequiredAlertOutbox_ElsaInstances_OrganizationId_WorkspaceId_InstanceId",
                    columns: x => new { x.OrganizationId, x.WorkspaceId, x.InstanceId },
                    principalTable: "ElsaInstances",
                    principalColumns: ["OrganizationId", "WorkspaceId", "Id"],
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ElsaInstanceRecoveryRequiredAlertOutbox_Organizations_OrganizationId",
                    column: x => x.OrganizationId,
                    principalTable: "Organizations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ElsaInstanceRecoveryRequiredAlertOutbox_Workspaces_OrganizationId_WorkspaceId",
                    columns: x => new { x.OrganizationId, x.WorkspaceId },
                    principalTable: "Workspaces",
                    principalColumns: ["OrganizationId", "Id"],
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ElsaInstanceRecoveryRequiredAlertOutbox_OperationId_AttemptNumber",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            columns: ["OperationId", "AttemptNumber"],
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_ElsaInstanceRecoveryRequiredAlertOutbox_WorkspaceId_CreatedAt",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            columns: ["WorkspaceId", "CreatedAt"]);
        migrationBuilder.CreateIndex(
            name: "IX_ElsaInstanceRecoveryRequiredAlertOutbox_OrganizationId_WorkspaceId_InstanceId",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            columns: ["OrganizationId", "WorkspaceId", "InstanceId"]);

        migrationBuilder.Sql("""
            CREATE TRIGGER TR_ElsaInstanceRecoveryRequiredAlertOutbox_AppendOnly
            ON ElsaInstanceRecoveryRequiredAlertOutbox
            AFTER UPDATE, DELETE
            AS
            BEGIN
                THROW 50000, 'RecoveryRequired alert outbox records are append-only', 1;
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_ElsaInstanceRecoveryRequiredAlertOutbox_AppendOnly;");
        migrationBuilder.DropTable(name: "ElsaInstanceRecoveryRequiredAlertOutbox");
    }
}
