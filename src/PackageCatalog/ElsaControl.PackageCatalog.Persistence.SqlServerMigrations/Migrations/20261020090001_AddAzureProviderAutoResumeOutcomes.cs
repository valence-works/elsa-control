using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261020090001_AddAzureProviderAutoResumeOutcomes")]
public sealed class AddAzureProviderAutoResumeOutcomes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AutoResumeOutcomesJson",
            table: "AzureProviderOperations",
            type: "nvarchar(max)",
            maxLength: 4096,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "LateSuccessLeverApplied",
            table: "AzureProviderOperations",
            type: "bit",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "AutoResumeOutcomesJson", table: "AzureProviderOperations");
        migrationBuilder.DropColumn(name: "LateSuccessLeverApplied", table: "AzureProviderOperations");
    }
}
