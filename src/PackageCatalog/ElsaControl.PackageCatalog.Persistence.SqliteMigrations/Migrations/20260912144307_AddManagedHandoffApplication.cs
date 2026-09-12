using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddManagedHandoffApplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CurrentDeploymentManagedHandoff",
                table: "ElsaInstances",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ManagedHandoff",
                table: "AzureProviderOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite 3.35+ supports native DROP COLUMN. Using it keeps the native triggers that
            // reference ElsaInstances intact; the provider's table-rebuild fallback invalidates them.
            migrationBuilder.Sql("ALTER TABLE ElsaInstances DROP COLUMN CurrentDeploymentManagedHandoff;");
            migrationBuilder.Sql("ALTER TABLE AzureProviderOperations DROP COLUMN ManagedHandoff;");
        }
    }
}
