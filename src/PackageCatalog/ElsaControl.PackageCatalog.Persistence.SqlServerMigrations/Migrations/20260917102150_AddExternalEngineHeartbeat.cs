using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalEngineHeartbeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ExternalEngineConnections_Evidence",
                table: "ExternalEngineConnections");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExternalEngineConnectionAuditEvents_Action",
                table: "ExternalEngineConnectionAuditEvents");

            migrationBuilder.AddColumn<long>(
                name: "LastHeartbeatObservedAt",
                table: "ExternalEngineConnections",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastHeartbeatSequence",
                table: "ExternalEngineConnections",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ObservedRuntimeKind",
                table: "ExternalEngineConnections",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseEvidenceReference",
                table: "ExternalEngineConnections",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalEngineConnections_Evidence",
                table: "ExternalEngineConnections",
                sql: "ReleaseEvidenceLevel IN ('None', 'SelfReported', 'SupportedRelease', 'VerifiedManifest')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalEngineConnections_HeartbeatSequence",
                table: "ExternalEngineConnections",
                sql: "LastHeartbeatSequence IS NULL OR LastHeartbeatSequence > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalEngineConnectionAuditEvents_Action",
                table: "ExternalEngineConnectionAuditEvents",
                sql: "Action IN ('Created', 'PairingIssued', 'RepairStarted', 'IdentityEnrolled', 'HeartbeatConnected', 'HeartbeatDegraded', 'HeartbeatRecovered', 'Disconnected')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Preserve the heartbeat projection and append-only audit history. Once the new
            // protocol has written heartbeat-only data, this schema cannot be downgraded safely.
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM dbo.ExternalEngineConnections
                    WHERE ReleaseEvidenceLevel = 'SupportedRelease'
                       OR LastHeartbeatSequence IS NOT NULL
                       OR LastHeartbeatObservedAt IS NOT NULL
                       OR ObservedRuntimeKind IS NOT NULL
                       OR ReleaseEvidenceReference IS NOT NULL)
                   OR EXISTS (
                    SELECT 1 FROM dbo.ExternalEngineConnectionAuditEvents
                    WHERE [Action] IN ('HeartbeatConnected', 'HeartbeatDegraded', 'HeartbeatRecovered'))
                BEGIN
                    THROW 51022, 'Cannot roll back external-engine heartbeat migration after heartbeat data has been recorded.', 1;
                END;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExternalEngineConnections_Evidence",
                table: "ExternalEngineConnections");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExternalEngineConnections_HeartbeatSequence",
                table: "ExternalEngineConnections");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExternalEngineConnectionAuditEvents_Action",
                table: "ExternalEngineConnectionAuditEvents");

            migrationBuilder.DropColumn(
                name: "LastHeartbeatObservedAt",
                table: "ExternalEngineConnections");

            migrationBuilder.DropColumn(
                name: "LastHeartbeatSequence",
                table: "ExternalEngineConnections");

            migrationBuilder.DropColumn(
                name: "ObservedRuntimeKind",
                table: "ExternalEngineConnections");

            migrationBuilder.DropColumn(
                name: "ReleaseEvidenceReference",
                table: "ExternalEngineConnections");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalEngineConnections_Evidence",
                table: "ExternalEngineConnections",
                sql: "ReleaseEvidenceLevel IN ('None', 'SelfReported', 'VerifiedManifest')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalEngineConnectionAuditEvents_Action",
                table: "ExternalEngineConnectionAuditEvents",
                sql: "Action IN ('Created', 'PairingIssued', 'RepairStarted', 'IdentityEnrolled', 'Disconnected')");
        }
    }
}
