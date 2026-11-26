using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261126223001_AddElsaInstanceProvisioningBackend")]
public sealed class AddElsaInstanceProvisioningBackend : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProvisioningBackend",
            table: "ElsaInstances",
            type: "nvarchar(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "bicep");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ProvisioningBackend",
            table: "ElsaInstances");
    }
}
