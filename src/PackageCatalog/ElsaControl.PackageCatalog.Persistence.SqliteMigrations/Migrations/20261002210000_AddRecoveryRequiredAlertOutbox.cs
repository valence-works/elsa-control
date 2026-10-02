using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261002210000_AddRecoveryRequiredAlertOutbox")]
public sealed class AddRecoveryRequiredAlertOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ElsaInstanceRecoveryRequiredAlertOutbox",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                OrganizationId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                InstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                DedupeIdentity = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
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

        migrationBuilder.Sql("""
            CREATE TRIGGER TR_ElsaInstanceRecoveryRequiredAlertOutbox_AppendOnly_Update
            BEFORE UPDATE ON ElsaInstanceRecoveryRequiredAlertOutbox
            BEGIN SELECT RAISE(ABORT, 'RecoveryRequired alert outbox records are append-only'); END;
            CREATE TRIGGER TR_ElsaInstanceRecoveryRequiredAlertOutbox_AppendOnly_Delete
            BEFORE DELETE ON ElsaInstanceRecoveryRequiredAlertOutbox
            BEGIN SELECT RAISE(ABORT, 'RecoveryRequired alert outbox records are append-only'); END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS TR_ElsaInstanceRecoveryRequiredAlertOutbox_AppendOnly_Delete;
            DROP TRIGGER IF EXISTS TR_ElsaInstanceRecoveryRequiredAlertOutbox_AppendOnly_Update;
            """);
        migrationBuilder.DropTable(name: "ElsaInstanceRecoveryRequiredAlertOutbox");
    }
}
