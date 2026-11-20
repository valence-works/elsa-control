using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261120120000_AddElsaInstanceOperationProviderSubmissionInFlight")]
public sealed class AddElsaInstanceOperationProviderSubmissionInFlight : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "ProviderSubmissionInFlightAt",
            table: "ElsaInstanceOperations",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""ALTER TABLE "ElsaInstanceOperations" DROP COLUMN "ProviderSubmissionInFlightAt";""");
    }
}
