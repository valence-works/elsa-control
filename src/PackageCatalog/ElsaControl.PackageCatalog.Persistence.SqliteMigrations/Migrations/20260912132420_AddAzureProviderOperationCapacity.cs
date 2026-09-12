using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddAzureProviderOperationCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CapacityCpuMillicores",
                table: "AzureProviderOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CapacityMaxReplicas",
                table: "AzureProviderOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CapacityMemoryMiB",
                table: "AzureProviderOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CapacityMinReplicas",
                table: "AzureProviderOperations",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CapacityCpuMillicores",
                table: "AzureProviderOperations");

            migrationBuilder.DropColumn(
                name: "CapacityMaxReplicas",
                table: "AzureProviderOperations");

            migrationBuilder.DropColumn(
                name: "CapacityMemoryMiB",
                table: "AzureProviderOperations");

            migrationBuilder.DropColumn(
                name: "CapacityMinReplicas",
                table: "AzureProviderOperations");
        }
    }
}
