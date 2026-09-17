using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

/// <summary>
/// SQLite rebuilds the append-only audit table while changing the heartbeat action check
/// constraint. This follow-up migration reinstalls the triggers after that rebuild completes.
/// </summary>
[DbContext(typeof(CatalogDbContext))]
[Migration("20260917102140_RestoreExternalEngineConnectionAuditTriggers")]
public partial class RestoreExternalEngineConnectionAuditTriggers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TRIGGER IF EXISTS TR_ExternalEngineConnectionAuditEvents_AppendOnly_Delete;
        DROP TRIGGER IF EXISTS TR_ExternalEngineConnectionAuditEvents_AppendOnly_Update;
        CREATE TRIGGER TR_ExternalEngineConnectionAuditEvents_AppendOnly_Update
        BEFORE UPDATE ON ExternalEngineConnectionAuditEvents
        BEGIN SELECT RAISE(ABORT, 'External engine connection audit events are append-only'); END;
        CREATE TRIGGER TR_ExternalEngineConnectionAuditEvents_AppendOnly_Delete
        BEFORE DELETE ON ExternalEngineConnectionAuditEvents
        BEGIN SELECT RAISE(ABORT, 'External engine connection audit events are append-only'); END;
        """);

    // Keep the repaired guards installed if this metadata-only migration is rolled back.
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
