using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261126180000_AddElsaInstanceOperationFailureClass")]
public sealed class AddElsaInstanceOperationFailureClass : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FailureClass",
            table: "ElsaInstanceOperations",
            type: "TEXT",
            maxLength: 32,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""ALTER TABLE "ElsaInstanceOperations" DROP COLUMN "FailureClass";""");
    }
}
