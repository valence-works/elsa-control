using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261003180001_AddVerifiedDeletionProgressClock")]
public sealed class AddVerifiedDeletionProgressClock : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "ProgressChangedAt",
            table: "AzureProviderOperations",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);
        migrationBuilder.Sql("""
            UPDATE [AzureProviderOperations]
            SET [ProgressChangedAt] = [StatusChangedAt]
            WHERE [ProgressChangedAt] = 0;
            """);

        migrationBuilder.AddColumn<long>(
            name: "LastVerifiedProgressAt",
            table: "ElsaInstanceOperations",
            type: "bigint",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "LastVerifiedProgressReceipt",
            table: "ElsaInstanceOperations",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ProgressChangedAt", table: "AzureProviderOperations");
        migrationBuilder.DropColumn(name: "LastVerifiedProgressAt", table: "ElsaInstanceOperations");
        migrationBuilder.DropColumn(name: "LastVerifiedProgressReceipt", table: "ElsaInstanceOperations");
    }
}
