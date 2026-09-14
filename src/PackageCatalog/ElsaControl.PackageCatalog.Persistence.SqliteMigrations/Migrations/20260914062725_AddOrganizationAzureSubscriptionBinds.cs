using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationAzureSubscriptionBinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrganizationAzureSubscriptionBinds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerTenantId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SubscriptionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ManagingTenantId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ManagingPrincipalObjectId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ManagingPrincipalClientId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    RegistrationDefinitionId = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    RegistrationDefinitionFingerprint = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    VerifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastPreflightCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedByAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UnbindReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationAzureSubscriptionBinds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizationAzureSubscriptionBinds_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationAzureSubscriptionBinds_OneActive",
                table: "OrganizationAzureSubscriptionBinds",
                column: "OrganizationId",
                unique: true,
                filter: "State = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationAzureSubscriptionBinds_OneInFlight",
                table: "OrganizationAzureSubscriptionBinds",
                column: "OrganizationId",
                unique: true,
                filter: "State IN ('PendingConsent', 'Verifying')");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationAzureSubscriptionBinds_OrganizationId_CreatedAt",
                table: "OrganizationAzureSubscriptionBinds",
                columns: new[] { "OrganizationId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrganizationAzureSubscriptionBinds");
        }
    }
}
