using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261003150001_AddRecoveryRequiredAlertOutboxLeaseAndRunId")]
public sealed class AddRecoveryRequiredAlertOutboxLeaseAndRunId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "RunId",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            type: "uniqueidentifier",
            nullable: true);
        migrationBuilder.AddColumn<long>(
            name: "LeasedUntil",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            type: "bigint",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "LeasedBy",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);
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
                       OR ISNULL(inserted.RunId, '00000000-0000-0000-0000-000000000000')
                          <> ISNULL(deleted.RunId, '00000000-0000-0000-0000-000000000000')
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
        migrationBuilder.DropColumn(name: "LeasedBy", table: "ElsaInstanceRecoveryRequiredAlertOutbox");
        migrationBuilder.DropColumn(name: "LeasedUntil", table: "ElsaInstanceRecoveryRequiredAlertOutbox");
        migrationBuilder.DropColumn(name: "RunId", table: "ElsaInstanceRecoveryRequiredAlertOutbox");
    }
}
