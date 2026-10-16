using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261016040001_AddHandoffKeyRingSnapshotIds")]
public sealed class AddHandoffKeyRingSnapshotIds : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "KeyRingSnapshotId",
            table: "ManagedElsaHandoffAuditEvents",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "KeyRingPreviousSnapshotId",
            table: "ManagedElsaHandoffAuditEvents",
            type: "uniqueidentifier",
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
        migrationBuilder.DropColumn(
            name: "KeyRingPreviousSnapshotId",
            table: "ManagedElsaHandoffAuditEvents");
        migrationBuilder.DropColumn(
            name: "KeyRingSnapshotId",
            table: "ManagedElsaHandoffAuditEvents");
    }
}
