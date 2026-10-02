using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261002120001_AddElsaInstanceOperationReasonClock")]
public sealed class AddElsaInstanceOperationReasonClock : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "ReasonEnteredAt",
            table: "ElsaInstanceOperations",
            type: "bigint",
            nullable: true);
        migrationBuilder.AddColumn<long>(
            name: "RequiresHumanAt",
            table: "ElsaInstanceOperations",
            type: "bigint",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "RequiresHumanAt", table: "ElsaInstanceOperations");
        migrationBuilder.DropColumn(name: "ReasonEnteredAt", table: "ElsaInstanceOperations");
    }
}
