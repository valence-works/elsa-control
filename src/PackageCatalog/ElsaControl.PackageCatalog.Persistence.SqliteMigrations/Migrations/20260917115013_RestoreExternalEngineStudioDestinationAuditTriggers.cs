using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

/// <summary>Reinstalls append-only guards after SQLite rebuilds the audit table for its new action.</summary>
[DbContext(typeof(CatalogDbContext))]
[Migration("20260917115013_RestoreExternalEngineStudioDestinationAuditTriggers")]
public partial class RestoreExternalEngineStudioDestinationAuditTriggers : Migration
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

    // The preceding migration restores the guards after rebuilding this table during rollback.
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
