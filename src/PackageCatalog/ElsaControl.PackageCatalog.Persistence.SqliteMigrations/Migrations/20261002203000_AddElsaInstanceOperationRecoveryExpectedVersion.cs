using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261002203000_AddElsaInstanceOperationRecoveryExpectedVersion")]
public sealed class AddElsaInstanceOperationRecoveryExpectedVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "RecoveryExpectedVersion",
            table: "ElsaInstanceOperations",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""ALTER TABLE "ElsaInstanceOperations" DROP COLUMN "RecoveryExpectedVersion";""");
    }
}
