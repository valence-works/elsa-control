using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261003120000_AddRecoveryRequiredAlertOutboxDelivery")]
public sealed class AddRecoveryRequiredAlertOutboxDelivery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "SentAt",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            type: "INTEGER",
            nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "DeliveryAttempts",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
        migrationBuilder.AddColumn<long>(
            name: "NextAttemptAt",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            type: "INTEGER",
            nullable: true);
        migrationBuilder.CreateIndex(
            name: "IX_ElsaInstanceRecoveryRequiredAlertOutbox_SentAt_NextAttemptAt",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox",
            columns: ["SentAt", "NextAttemptAt"]);
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
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS TR_ElsaInstanceRecoveryRequiredAlertOutbox_AppendOnly_Update;
            CREATE TRIGGER TR_ElsaInstanceRecoveryRequiredAlertOutbox_AppendOnly_Update
            BEFORE UPDATE ON ElsaInstanceRecoveryRequiredAlertOutbox
            BEGIN SELECT RAISE(ABORT, 'RecoveryRequired alert outbox records are append-only'); END;
            """);
        migrationBuilder.DropIndex(
            name: "IX_ElsaInstanceRecoveryRequiredAlertOutbox_SentAt_NextAttemptAt",
            table: "ElsaInstanceRecoveryRequiredAlertOutbox");
        migrationBuilder.Sql("""ALTER TABLE "ElsaInstanceRecoveryRequiredAlertOutbox" DROP COLUMN "NextAttemptAt";""");
        migrationBuilder.Sql("""ALTER TABLE "ElsaInstanceRecoveryRequiredAlertOutbox" DROP COLUMN "DeliveryAttempts";""");
        migrationBuilder.Sql("""ALTER TABLE "ElsaInstanceRecoveryRequiredAlertOutbox" DROP COLUMN "SentAt";""");
    }
}
