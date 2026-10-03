using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261003120000_BackfillElsaInstanceOperationReasonEnteredAt")]
public sealed class BackfillElsaInstanceOperationReasonEnteredAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            UPDATE "ElsaInstanceOperations"
            SET "ReasonEnteredAt" = COALESCE("UpdatedAt", "StartedAt", "AcceptedAt")
            WHERE "State" = 'RecoveryRequired' AND "ReasonEnteredAt" IS NULL;
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
