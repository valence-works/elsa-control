using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20260928023001_AddAzureProviderOperationStatusChangedAt")]
public sealed class AddAzureProviderOperationStatusChangedAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "StatusChangedAt",
            table: "AzureProviderOperations",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.Sql("""
            UPDATE [AzureProviderOperations]
            SET [StatusChangedAt] = [UpdatedAt]
            WHERE [StatusChangedAt] = 0;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(
            name: "StatusChangedAt",
            table: "AzureProviderOperations");
}
