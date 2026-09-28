using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20260928120000_AddAzureProviderLateSuccessObservation")]
public sealed class AddAzureProviderLateSuccessObservation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "AttemptedStepStartedAt",
            table: "AzureProviderOperations",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "LastArmObservedAt",
            table: "AzureProviderOperations",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "AutoResumeCount",
            table: "AzureProviderOperations",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "ArmObservationBackoffSeconds",
            table: "AzureProviderOperations",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""ALTER TABLE "AzureProviderOperations" DROP COLUMN "AttemptedStepStartedAt";""");
        migrationBuilder.Sql("""ALTER TABLE "AzureProviderOperations" DROP COLUMN "LastArmObservedAt";""");
        migrationBuilder.Sql("""ALTER TABLE "AzureProviderOperations" DROP COLUMN "AutoResumeCount";""");
        migrationBuilder.Sql("""ALTER TABLE "AzureProviderOperations" DROP COLUMN "ArmObservationBackoffSeconds";""");
    }
}
