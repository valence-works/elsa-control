using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261004230001_AddBillingProviderEventReplayFacts")]
public sealed class AddBillingProviderEventReplayFacts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProviderObjectReference",
            table: "BillingProviderEvents",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PriceReference",
            table: "BillingProviderEvents",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true);
        migrationBuilder.AddColumn<long>(
            name: "AmountMinorUnits",
            table: "BillingProviderEvents",
            type: "bigint",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ProviderObjectReference", table: "BillingProviderEvents");
        migrationBuilder.DropColumn(name: "PriceReference", table: "BillingProviderEvents");
        migrationBuilder.DropColumn(name: "AmountMinorUnits", table: "BillingProviderEvents");
    }
}
