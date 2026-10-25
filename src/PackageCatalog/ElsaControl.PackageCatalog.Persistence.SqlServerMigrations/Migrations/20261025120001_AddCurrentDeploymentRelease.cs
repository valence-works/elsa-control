using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

/// <summary>
/// Records the provider-confirmed deployment's release line and version, bound to the current
/// deployment id. CurrentReleaseLine is the resolved plan and can move before confirmation.
/// The columns are additive and outside every operation, plan and reconciliation hash.
/// </summary>
[DbContext(typeof(CatalogDbContext))]
[Migration("20261025120001_AddCurrentDeploymentRelease")]
public sealed class AddCurrentDeploymentRelease : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CurrentDeploymentReleaseLine",
            table: "ElsaInstances",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CurrentDeploymentReleaseVersion",
            table: "ElsaInstances",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CurrentDeploymentReleaseDeploymentId",
            table: "ElsaInstances",
            type: "nvarchar(128)",
            maxLength: 128,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CurrentDeploymentReleaseLine", table: "ElsaInstances");
        migrationBuilder.DropColumn(name: "CurrentDeploymentReleaseVersion", table: "ElsaInstances");
        migrationBuilder.DropColumn(name: "CurrentDeploymentReleaseDeploymentId", table: "ElsaInstances");
    }
}
