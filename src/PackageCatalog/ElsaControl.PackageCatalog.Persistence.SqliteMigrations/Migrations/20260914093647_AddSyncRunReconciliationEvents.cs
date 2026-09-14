using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReconciledCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessStartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRunReconciliationEvents", x => x.Id);
                });

            migrationBuilder.Sql(
                """
                CREATE TRIGGER TR_SyncRunReconciliationEvents_AppendOnly_Update
                BEFORE UPDATE ON SyncRunReconciliationEvents
                BEGIN SELECT RAISE(ABORT, 'Sync run reconciliation events are append-only'); END;
                CREATE TRIGGER TR_SyncRunReconciliationEvents_AppendOnly_Delete
                BEFORE DELETE ON SyncRunReconciliationEvents
                BEGIN SELECT RAISE(ABORT, 'Sync run reconciliation events are append-only'); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS TR_SyncRunReconciliationEvents_AppendOnly_Delete;
                DROP TRIGGER IF EXISTS TR_SyncRunReconciliationEvents_AppendOnly_Update;
                """);
            migrationBuilder.DropTable(
                name: "SyncRunReconciliationEvents");
        }
    }
}
