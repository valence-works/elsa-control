using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261120120001_AddElsaInstanceOperationProviderSubmissionInFlight")]
public sealed class AddElsaInstanceOperationProviderSubmissionInFlight : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "ProviderSubmissionInFlightAt",
            table: "ElsaInstanceOperations",
            type: "bigint",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ProviderSubmissionInFlightAt",
            table: "ElsaInstanceOperations");
    }
}
