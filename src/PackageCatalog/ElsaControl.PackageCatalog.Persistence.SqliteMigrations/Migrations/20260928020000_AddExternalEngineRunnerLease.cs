using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20260928020000_AddExternalEngineRunnerLease")]
public sealed class AddExternalEngineRunnerLease : Migration
{
    private const string ExpandedActions = """
        Action IN ('Created', 'PairingIssued', 'RepairStarted', 'IdentityEnrolled', 'HeartbeatConnected', 'HeartbeatDegraded', 'HeartbeatRecovered', 'StudioDestinationConfirmed', 'Disconnected', 'external-engine.runner-changed', 'external-engine.label-changed')
        """;

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ActiveRunnerId",
            table: "ExternalEngineConnections",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "RunnerLeaseExpiresAt",
            table: "ExternalEngineConnections",
            type: "INTEGER",
            nullable: true);

        // SQLite cannot ALTER TABLE ADD CHECK. The lease pair is enforced in
        // application code; SQL Server ships the table check. Rebuild the audit
        // table so the new value-free actions are legal.
        RebuildAuditEvents(migrationBuilder, ExpandedActions);
        RestoreAuditTriggers(migrationBuilder);

        // The retired connector grader was the only writer of VerifiedManifest /
        // SupportedRelease on this customer-operated table. Cap any leftover
        // rows so they cannot outrank a later Valence-operated observation.
        migrationBuilder.Sql("""
            UPDATE "ExternalEngineConnections"
            SET "ReleaseEvidenceLevel" = 'SelfReported',
                "ReleaseEvidenceReference" = NULL
            WHERE "ReleaseEvidenceLevel" IN ('SupportedRelease', 'VerifiedManifest');
            """);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The evidence backfill is intentionally irreversible. Down drops the lease
    /// columns only and leaves the expanded audit-action list in place so
    /// existing <c>external-engine.runner-changed</c> / <c>label-changed</c>
    /// rows do not fail the rebuild.
    /// </remarks>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "ExternalEngineConnections" DROP COLUMN "ActiveRunnerId";
            ALTER TABLE "ExternalEngineConnections" DROP COLUMN "RunnerLeaseExpiresAt";
            """);
    }

    private static void RebuildAuditEvents(MigrationBuilder migrationBuilder, string actionCheck)
    {
        migrationBuilder.Sql($"""
            DROP TRIGGER IF EXISTS TR_ExternalEngineConnectionAuditEvents_AppendOnly_Delete;
            DROP TRIGGER IF EXISTS TR_ExternalEngineConnectionAuditEvents_AppendOnly_Update;
            CREATE TABLE "ExternalEngineConnectionAuditEvents_new" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ExternalEngineConnectionAuditEvents" PRIMARY KEY,
                "OrganizationId" TEXT NOT NULL,
                "WorkspaceId" TEXT NOT NULL,
                "ConnectionId" TEXT NOT NULL,
                "Action" TEXT NOT NULL,
                "OccurredAt" INTEGER NOT NULL,
                CONSTRAINT "CK_ExternalEngineConnectionAuditEvents_Action" CHECK ({actionCheck}),
                CONSTRAINT "FK_ExternalEngineConnectionAuditEvents_ExternalEngineConnections_OrganizationId_WorkspaceId_ConnectionId"
                    FOREIGN KEY ("OrganizationId", "WorkspaceId", "ConnectionId")
                    REFERENCES "ExternalEngineConnections" ("OrganizationId", "WorkspaceId", "Id") ON DELETE RESTRICT
            );
            INSERT INTO "ExternalEngineConnectionAuditEvents_new"
            SELECT "Id", "OrganizationId", "WorkspaceId", "ConnectionId", "Action", "OccurredAt"
            FROM "ExternalEngineConnectionAuditEvents";
            DROP TABLE "ExternalEngineConnectionAuditEvents";
            ALTER TABLE "ExternalEngineConnectionAuditEvents_new" RENAME TO "ExternalEngineConnectionAuditEvents";
            CREATE INDEX "IX_ExternalEngineConnectionAuditEvents_OrganizationId_WorkspaceId_ConnectionId_OccurredAt"
                ON "ExternalEngineConnectionAuditEvents" ("OrganizationId", "WorkspaceId", "ConnectionId", "OccurredAt");
            """);
    }

    private static void RestoreAuditTriggers(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS TR_ExternalEngineConnectionAuditEvents_AppendOnly_Delete;
            DROP TRIGGER IF EXISTS TR_ExternalEngineConnectionAuditEvents_AppendOnly_Update;
            CREATE TRIGGER TR_ExternalEngineConnectionAuditEvents_AppendOnly_Update
            BEFORE UPDATE ON ExternalEngineConnectionAuditEvents
            BEGIN SELECT RAISE(ABORT, 'External engine connection audit events are append-only'); END;
            CREATE TRIGGER TR_ExternalEngineConnectionAuditEvents_AppendOnly_Delete
            BEFORE DELETE ON ExternalEngineConnectionAuditEvents
            BEGIN SELECT RAISE(ABORT, 'External engine connection audit events are append-only'); END;
            """);
}
