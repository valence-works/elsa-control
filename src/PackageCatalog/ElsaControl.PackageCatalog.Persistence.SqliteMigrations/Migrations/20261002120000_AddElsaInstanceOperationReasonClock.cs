using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261002120000_AddElsaInstanceOperationReasonClock")]
public sealed class AddElsaInstanceOperationReasonClock : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "ReasonEnteredAt",
            table: "ElsaInstanceOperations",
            type: "INTEGER",
            nullable: true);
        migrationBuilder.AddColumn<long>(
            name: "RequiresHumanAt",
            table: "ElsaInstanceOperations",
            type: "INTEGER",
            nullable: true);
        migrationBuilder.Sql(
            """
            UPDATE "ElsaInstanceOperations"
            SET "ReasonEnteredAt" = COALESCE("UpdatedAt", "StartedAt", "AcceptedAt")
            WHERE "State" = 'RecoveryRequired' AND "ReasonEnteredAt" IS NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""ALTER TABLE "ElsaInstanceOperations" DROP COLUMN "RequiresHumanAt";""");
        migrationBuilder.Sql("""ALTER TABLE "ElsaInstanceOperations" DROP COLUMN "ReasonEnteredAt";""");
    }
}
