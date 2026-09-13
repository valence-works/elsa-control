using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddAzureProviderAssignmentRebinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AzureProviderAssignmentRebinds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromProviderScopeFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ToProviderScopeFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TriggeredBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TriggerOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AzureProviderAssignmentRebinds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AzureProviderAssignmentRebinds_AzureProviderResourceAssignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "AzureProviderResourceAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AzureProviderAssignmentRebinds_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AzureProviderAssignmentRebinds_AssignmentId_FromProviderScopeFingerprint_ToProviderScopeFingerprint",
                table: "AzureProviderAssignmentRebinds",
                columns: new[] { "AssignmentId", "FromProviderScopeFingerprint", "ToProviderScopeFingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_AzureProviderAssignmentRebinds_WorkspaceId_AssignmentId_OccurredAt",
                table: "AzureProviderAssignmentRebinds",
                columns: new[] { "WorkspaceId", "AssignmentId", "OccurredAt" });

            migrationBuilder.Sql(
                """
                EXEC(N'CREATE TRIGGER dbo.TR_AzureProviderAssignmentRebinds_AppendOnly
                ON dbo.AzureProviderAssignmentRebinds
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    THROW 51019, ''Azure provider assignment rebinds are append-only'', 1;
                END;');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS dbo.TR_AzureProviderAssignmentRebinds_AppendOnly;");
            migrationBuilder.DropTable(
                name: "AzureProviderAssignmentRebinds");
        }
    }
}
