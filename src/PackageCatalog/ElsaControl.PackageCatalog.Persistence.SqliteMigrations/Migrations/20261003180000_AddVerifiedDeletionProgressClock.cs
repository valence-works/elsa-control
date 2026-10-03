using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261003180000_AddVerifiedDeletionProgressClock")]
public sealed class AddVerifiedDeletionProgressClock : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "ProgressChangedAt",
            table: "AzureProviderOperations",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);
        migrationBuilder.Sql("""
            UPDATE "AzureProviderOperations"
            SET "ProgressChangedAt" = "StatusChangedAt"
            WHERE "ProgressChangedAt" = 0;
            """);

        migrationBuilder.AddColumn<long>(
            name: "LastVerifiedProgressAt",
            table: "ElsaInstanceOperations",
            type: "INTEGER",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "LastVerifiedProgressReceipt",
            table: "ElsaInstanceOperations",
            type: "TEXT",
            maxLength: 64,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""ALTER TABLE "AzureProviderOperations" DROP COLUMN "ProgressChangedAt";""");
        migrationBuilder.Sql("""ALTER TABLE "ElsaInstanceOperations" DROP COLUMN "LastVerifiedProgressAt";""");
        migrationBuilder.Sql("""ALTER TABLE "ElsaInstanceOperations" DROP COLUMN "LastVerifiedProgressReceipt";""");
    }
}
