using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261003120001_AddRecoveryRequiredAlertOutboxDelivery")]
public sealed class AddRecoveryRequiredAlertOutboxDelivery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "SentAt",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            type: "bigint",
            nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "DeliveryAttempts",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            type: "int",
            nullable: false,
            defaultValue: 0);
        migrationBuilder.AddColumn<long>(
            name: "NextAttemptAt",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            type: "bigint",
            nullable: true);
        migrationBuilder.CreateIndex(
            name: "IX_ElsaInstanceRecoveryRequiredAlertOutbox_SentAt_NextAttemptAt",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            columns: ["SentAt", "NextAttemptAt"]);
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS TR_ElsaInstanceRecoveryRequiredAlertOutbox_AppendOnly;
            """);
        migrationBuilder.Sql("""
            CREATE TRIGGER TR_ElsaInstanceRecoveryRequiredAlertOutbox_AppendOnly
            ON ElsaInstanceRecoveryRequiredAlertOutbox
            AFTER UPDATE, DELETE
            AS
            BEGIN
                IF EXISTS (SELECT 1 FROM deleted WHERE NOT EXISTS (SELECT 1 FROM inserted WHERE inserted.Id = deleted.Id))
                    THROW 50000, 'RecoveryRequired alert outbox records are append-only', 1;
                IF EXISTS (
                    SELECT 1
                    FROM inserted
                    INNER JOIN deleted ON inserted.Id = deleted.Id
                    WHERE inserted.OrganizationId <> deleted.OrganizationId
                       OR inserted.WorkspaceId <> deleted.WorkspaceId
                       OR inserted.InstanceId <> deleted.InstanceId
                       OR inserted.OperationId <> deleted.OperationId
                       OR inserted.AttemptNumber <> deleted.AttemptNumber
                       OR inserted.DedupeIdentity <> deleted.DedupeIdentity
                       OR inserted.CreatedAt <> deleted.CreatedAt
                       OR inserted.DeliveryAttempts < deleted.DeliveryAttempts
                       OR (deleted.SentAt IS NOT NULL AND (inserted.SentAt IS NULL OR inserted.SentAt <> deleted.SentAt))
                )
                    THROW 50000, 'RecoveryRequired alert outbox payload is append-only', 1;
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_ElsaInstanceRecoveryRequiredAlertOutbox_AppendOnly;");
        migrationBuilder.Sql("""
            CREATE TRIGGER TR_ElsaInstanceRecoveryRequiredAlertOutbox_AppendOnly
            ON ElsaInstanceRecoveryRequiredAlertOutbox
            AFTER UPDATE, DELETE
            AS
            BEGIN
                THROW 50000, 'RecoveryRequired alert outbox records are append-only', 1;
            END;
            """);
        migrationBuilder.DropIndex(
            name: "IX_ElsaInstanceRecoveryRequiredAlertOutbox_SentAt_NextAttemptAt",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox");
        migrationBuilder.DropColumn(name: "NextAttemptAt", table: "ElsaInstanceRecoveryRequiredAlertOutbox");
        migrationBuilder.DropColumn(name: "DeliveryAttempts", table: "ElsaInstanceRecoveryRequiredAlertOutbox");
        migrationBuilder.DropColumn(name: "SentAt", table: "ElsaInstanceRecoveryRequiredAlertOutbox");
    }
}
