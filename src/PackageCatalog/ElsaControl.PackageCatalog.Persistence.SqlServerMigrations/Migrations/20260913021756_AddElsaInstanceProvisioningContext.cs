using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddElsaInstanceProvisioningContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresProvisioningContext",
                table: "ElsaInstances",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ElsaInstanceProvisioningContexts",
                columns: table => new
                {
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BuilderIntentJson = table.Column<string>(type: "nvarchar(max)", maxLength: 131072, nullable: false),
                    ConfigurationDigest = table.Column<string>(type: "nvarchar(71)", maxLength: 71, nullable: false),
                    RuntimeConfigurationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConfigurationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PreviewDigest = table.Column<string>(type: "nvarchar(71)", maxLength: 71, nullable: true),
                    RequestDigest = table.Column<string>(type: "nvarchar(71)", maxLength: 71, nullable: true),
                    ResolvedPlanDigest = table.Column<string>(type: "nvarchar(71)", maxLength: 71, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElsaInstanceProvisioningContexts", x => x.InstanceId);
                    table.ForeignKey(
                        name: "FK_ElsaInstanceProvisioningContexts_ElsaInstances_OrganizationId_WorkspaceId_InstanceId",
                        columns: x => new { x.OrganizationId, x.WorkspaceId, x.InstanceId },
                        principalTable: "ElsaInstances",
                        principalColumns: new[] { "OrganizationId", "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ElsaInstanceProvisioningContexts_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ElsaInstanceProvisioningContexts_Workspaces_OrganizationId_WorkspaceId",
                        columns: x => new { x.OrganizationId, x.WorkspaceId },
                        principalTable: "Workspaces",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ElsaInstanceProvisioningContexts_OrganizationId_WorkspaceId_InstanceId",
                table: "ElsaInstanceProvisioningContexts",
                columns: new[] { "OrganizationId", "WorkspaceId", "InstanceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ElsaInstanceProvisioningContexts_WorkspaceId_ApplicationId_EnvironmentId",
                table: "ElsaInstanceProvisioningContexts",
                columns: new[] { "WorkspaceId", "ApplicationId", "EnvironmentId" },
                unique: true);

            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER dbo.TR_ElsaInstanceProvisioningContexts_AppendOnly
                ON dbo.ElsaInstanceProvisioningContexts
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    THROW 51019, ''Elsa instance provisioning contexts are append-only'', 1;
                END;');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS dbo.TR_ElsaInstanceProvisioningContexts_AppendOnly;");
            migrationBuilder.DropTable(
                name: "ElsaInstanceProvisioningContexts");

            migrationBuilder.DropColumn(
                name: "RequiresProvisioningContext",
                table: "ElsaInstances");
        }
    }
}
