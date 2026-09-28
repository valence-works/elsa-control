using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

/// <summary>
/// Records whether a managed engine's admitted image supports the exact Studio grant set, bound to the
/// current deployment id. The columns are additive and outside every operation, plan and reconciliation
/// hash, so a Control build that predates them still restores, deletes and recovers the rows this build writes.
/// </summary>
[DbContext(typeof(CatalogDbContext))]
[Migration("20260928040001_AddManagedStudioGrantCapability")]
public sealed class AddManagedStudioGrantCapability : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "CurrentDeploymentStudioGrants",
            table: "ElsaInstances",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "CurrentDeploymentStudioGrantsDeploymentId",
            table: "ElsaInstances",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "ManagedHandoffStudioGrants",
            table: "AzureProviderOperations",
            type: "bit",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CurrentDeploymentStudioGrants",
            table: "ElsaInstances");

        migrationBuilder.DropColumn(
            name: "CurrentDeploymentStudioGrantsDeploymentId",
            table: "ElsaInstances");

        migrationBuilder.DropColumn(
            name: "ManagedHandoffStudioGrants",
            table: "AzureProviderOperations");
    }
}
