using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromProviderScopeFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ToProviderScopeFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TriggeredBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TriggerOperationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false)
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
                CREATE TRIGGER TR_AzureProviderAssignmentRebinds_AppendOnly_Update
                BEFORE UPDATE ON AzureProviderAssignmentRebinds
                BEGIN SELECT RAISE(ABORT, 'Azure provider assignment rebinds are append-only'); END;
                CREATE TRIGGER TR_AzureProviderAssignmentRebinds_AppendOnly_Delete
                BEFORE DELETE ON AzureProviderAssignmentRebinds
                BEGIN SELECT RAISE(ABORT, 'Azure provider assignment rebinds are append-only'); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS TR_AzureProviderAssignmentRebinds_AppendOnly_Delete;
                DROP TRIGGER IF EXISTS TR_AzureProviderAssignmentRebinds_AppendOnly_Update;
                """);
            migrationBuilder.DropTable(
                name: "AzureProviderAssignmentRebinds");
        }
    }
}
