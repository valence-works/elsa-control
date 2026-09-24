using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddManagedStudioGrantCapability : Migration
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
}
