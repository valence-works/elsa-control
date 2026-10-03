using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261003150000_AddRecoveryRequiredAlertOutboxLeaseAndRunId")]
public sealed class AddRecoveryRequiredAlertOutboxLeaseAndRunId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "RunId",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<long>(
            name: "LeasedUntil",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            type: "INTEGER",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "LeasedBy",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            type: "TEXT",
            maxLength: 64,
            nullable: true);
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS TR_ElsaInstanceRecoveryRequiredAlertOutbox_AppendOnly_Update;
            CREATE TRIGGER TR_ElsaInstanceRecoveryRequiredAlertOutbox_AppendOnly_Update
            BEFORE UPDATE ON ElsaInstanceRecoveryRequiredAlertOutbox
            FOR EACH ROW
            WHEN NEW.Id IS NOT OLD.Id
              OR NEW.OrganizationId IS NOT OLD.OrganizationId
              OR NEW.WorkspaceId IS NOT OLD.WorkspaceId
              OR NEW.InstanceId IS NOT OLD.InstanceId
              OR NEW.OperationId IS NOT OLD.OperationId
              OR NEW.AttemptNumber IS NOT OLD.AttemptNumber
              OR NEW.RunId IS NOT OLD.RunId
              OR NEW.DedupeIdentity IS NOT OLD.DedupeIdentity
              OR NEW.CreatedAt IS NOT OLD.CreatedAt
              OR NEW.DeliveryAttempts < OLD.DeliveryAttempts
              OR (OLD.SentAt IS NOT NULL AND (NEW.SentAt IS NULL OR NEW.SentAt IS NOT OLD.SentAt))
            BEGIN
              SELECT RAISE(ABORT, 'RecoveryRequired alert outbox payload is append-only');
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS TR_ElsaInstanceRecoveryRequiredAlertOutbox_AppendOnly_Update;
            CREATE TRIGGER TR_ElsaInstanceRecoveryRequiredAlertOutbox_AppendOnly_Update
            BEFORE UPDATE ON ElsaInstanceRecoveryRequiredAlertOutbox
            FOR EACH ROW
            WHEN NEW.Id IS NOT OLD.Id
              OR NEW.OrganizationId IS NOT OLD.OrganizationId
              OR NEW.WorkspaceId IS NOT OLD.WorkspaceId
              OR NEW.InstanceId IS NOT OLD.InstanceId
              OR NEW.OperationId IS NOT OLD.OperationId
              OR NEW.AttemptNumber IS NOT OLD.AttemptNumber
              OR NEW.DedupeIdentity IS NOT OLD.DedupeIdentity
              OR NEW.CreatedAt IS NOT OLD.CreatedAt
              OR NEW.DeliveryAttempts < OLD.DeliveryAttempts
              OR (OLD.SentAt IS NOT NULL AND (NEW.SentAt IS NULL OR NEW.SentAt IS NOT OLD.SentAt))
            BEGIN
              SELECT RAISE(ABORT, 'RecoveryRequired alert outbox payload is append-only');
            END;
            """);
        migrationBuilder.Sql("""ALTER TABLE "ElsaInstanceRecoveryRequiredAlertOutbox" DROP COLUMN "LeasedBy";""");
        migrationBuilder.Sql("""ALTER TABLE "ElsaInstanceRecoveryRequiredAlertOutbox" DROP COLUMN "LeasedUntil";""");
        migrationBuilder.Sql("""ALTER TABLE "ElsaInstanceRecoveryRequiredAlertOutbox" DROP COLUMN "RunId";""");
    }
}
