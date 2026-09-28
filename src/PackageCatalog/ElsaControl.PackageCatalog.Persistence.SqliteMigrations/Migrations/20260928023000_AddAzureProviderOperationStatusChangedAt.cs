using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20260928023000_AddAzureProviderOperationStatusChangedAt")]
public sealed class AddAzureProviderOperationStatusChangedAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "StatusChangedAt",
            table: "AzureProviderOperations",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.Sql("""
            UPDATE "AzureProviderOperations"
            SET "StatusChangedAt" = "CreatedAt"
            WHERE "StatusChangedAt" = 0;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(
            name: "StatusChangedAt",
            table: "AzureProviderOperations");
}
