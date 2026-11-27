using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261126223000_AddElsaInstanceProvisioningBackend")]
public sealed class AddElsaInstanceProvisioningBackend : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProvisioningBackend",
            table: "ElsaInstances",
            type: "TEXT",
            maxLength: 16,
            nullable: false,
            defaultValue: "bicep");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""ALTER TABLE "ElsaInstances" DROP COLUMN "ProvisioningBackend";""");
    }
}
