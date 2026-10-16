using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261016040000_AddHandoffKeyRingSnapshotIds")]
public sealed class AddHandoffKeyRingSnapshotIds : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "KeyRingSnapshotId",
            table: "ManagedElsaHandoffAuditEvents",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "KeyRingPreviousSnapshotId",
            table: "ManagedElsaHandoffAuditEvents",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_ManagedElsaHandoffAuditEvents_KeyRingSnapshotId",
            table: "ManagedElsaHandoffAuditEvents",
            column: "KeyRingSnapshotId");

        migrationBuilder.CreateIndex(
            name: "IX_ManagedElsaHandoffAuditEvents_KeyRingPreviousSnapshot",
            table: "ManagedElsaHandoffAuditEvents",
            column: "KeyRingPreviousSnapshotId",
            unique: true,
            filter: "KeyRingPreviousSnapshotId IS NOT NULL AND Action LIKE 'key_ring.active:%'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ManagedElsaHandoffAuditEvents_KeyRingPreviousSnapshot",
            table: "ManagedElsaHandoffAuditEvents");
        migrationBuilder.DropIndex(
            name: "IX_ManagedElsaHandoffAuditEvents_KeyRingSnapshotId",
            table: "ManagedElsaHandoffAuditEvents");
        // SQLite 3.35+ native DROP COLUMN keeps the append-only triggers intact.
        migrationBuilder.Sql("""ALTER TABLE "ManagedElsaHandoffAuditEvents" DROP COLUMN "KeyRingPreviousSnapshotId";""");
        migrationBuilder.Sql("""ALTER TABLE "ManagedElsaHandoffAuditEvents" DROP COLUMN "KeyRingSnapshotId";""");
    }
}
