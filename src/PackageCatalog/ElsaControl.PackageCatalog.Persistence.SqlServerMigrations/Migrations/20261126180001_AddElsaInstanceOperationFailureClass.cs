using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261126180001_AddElsaInstanceOperationFailureClass")]
public sealed class AddElsaInstanceOperationFailureClass : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FailureClass",
            table: "ElsaInstanceOperations",
            type: "nvarchar(32)",
            maxLength: 32,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "FailureClass",
            table: "ElsaInstanceOperations");
    }
}
