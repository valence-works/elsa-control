using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261020090000_AddAzureProviderAutoResumeOutcomes")]
public sealed class AddAzureProviderAutoResumeOutcomes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AutoResumeOutcomesJson",
            table: "AzureProviderOperations",
            type: "TEXT",
            maxLength: 4096,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "LateSuccessLeverApplied",
            table: "AzureProviderOperations",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "AutoResumeBlocked",
            table: "AzureProviderOperations",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""ALTER TABLE "AzureProviderOperations" DROP COLUMN "AutoResumeOutcomesJson";""");
        migrationBuilder.Sql("""ALTER TABLE "AzureProviderOperations" DROP COLUMN "LateSuccessLeverApplied";""");
        migrationBuilder.Sql("""ALTER TABLE "AzureProviderOperations" DROP COLUMN "AutoResumeBlocked";""");
    }
}
