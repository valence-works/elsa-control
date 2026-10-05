using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261004230000_AddBillingProviderEventReplayFacts")]
public sealed class AddBillingProviderEventReplayFacts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProviderObjectReference",
            table: "BillingProviderEvents",
            type: "TEXT",
            maxLength: 256,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PriceReference",
            table: "BillingProviderEvents",
            type: "TEXT",
            maxLength: 256,
            nullable: true);
        migrationBuilder.AddColumn<long>(
            name: "AmountMinorUnits",
            table: "BillingProviderEvents",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""ALTER TABLE "BillingProviderEvents" DROP COLUMN "ProviderObjectReference";""");
        migrationBuilder.Sql("""ALTER TABLE "BillingProviderEvents" DROP COLUMN "PriceReference";""");
        migrationBuilder.Sql("""ALTER TABLE "BillingProviderEvents" DROP COLUMN "AmountMinorUnits";""");
    }
}
