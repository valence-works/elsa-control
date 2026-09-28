using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

/// <summary>
/// Records whether a managed engine's admitted image supports the exact Studio grant set. Both columns are additive
/// and outside every operation, plan and reconciliation hash, so a Control build that predates them still restores,
/// deletes and recovers the rows this build writes.
/// </summary>
[DbContext(typeof(CatalogDbContext))]
[Migration("20260928040000_AddManagedStudioGrantCapability")]
public sealed class AddManagedStudioGrantCapability : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "CurrentDeploymentStudioGrants",
            table: "ElsaInstances",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "ManagedHandoffStudioGrants",
            table: "AzureProviderOperations",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE ElsaInstances DROP COLUMN CurrentDeploymentStudioGrants;");
        migrationBuilder.Sql("ALTER TABLE AzureProviderOperations DROP COLUMN ManagedHandoffStudioGrants;");
    }
}
