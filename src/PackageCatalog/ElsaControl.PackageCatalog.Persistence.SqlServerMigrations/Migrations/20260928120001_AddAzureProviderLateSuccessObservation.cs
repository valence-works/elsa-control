using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20260928120001_AddAzureProviderLateSuccessObservation")]
public sealed class AddAzureProviderLateSuccessObservation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "AttemptedStepStartedAt",
            table: "AzureProviderOperations",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "LastArmObservedAt",
            table: "AzureProviderOperations",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "AutoResumeCount",
            table: "AzureProviderOperations",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "ArmObservationBackoffSeconds",
            table: "AzureProviderOperations",
            type: "int",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "AttemptedStepStartedAt", table: "AzureProviderOperations");
        migrationBuilder.DropColumn(name: "LastArmObservedAt", table: "AzureProviderOperations");
        migrationBuilder.DropColumn(name: "AutoResumeCount", table: "AzureProviderOperations");
        migrationBuilder.DropColumn(name: "ArmObservationBackoffSeconds", table: "AzureProviderOperations");
    }
}
