using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationIdentityBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrganizationIdentityBindings",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntraTenantId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationIdentityBindings", x => x.OrganizationId);
                    table.ForeignKey(
                        name: "FK_OrganizationIdentityBindings_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationIdentityBindings_EntraTenantId",
                table: "OrganizationIdentityBindings",
                column: "EntraTenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrganizationIdentityBindings");
        }
    }
}
