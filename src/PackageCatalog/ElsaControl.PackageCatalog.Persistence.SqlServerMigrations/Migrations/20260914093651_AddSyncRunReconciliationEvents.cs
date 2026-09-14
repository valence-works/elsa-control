using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncRunReconciliationEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncRunReconciliationEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReconciledCount = table.Column<int>(type: "int", nullable: false),
                    ProcessStartedAt = table.Column<long>(type: "bigint", nullable: false),
                    CompletedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRunReconciliationEvents", x => x.Id);
                });

            migrationBuilder.Sql(
                """
                EXEC(N'CREATE TRIGGER dbo.TR_SyncRunReconciliationEvents_AppendOnly
                ON dbo.SyncRunReconciliationEvents
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    THROW 51019, ''Sync run reconciliation events are append-only'', 1;
                END;');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS dbo.TR_SyncRunReconciliationEvents_AppendOnly;");
            migrationBuilder.DropTable(
                name: "SyncRunReconciliationEvents");
        }
    }
}
