using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations
{
    /// <inheritdoc />
    public partial class ConfirmExternalEngineStudioDestination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ExternalEngineConnectionAuditEvents_Action",
                table: "ExternalEngineConnectionAuditEvents");

            migrationBuilder.AddColumn<long>(
                name: "ConnectorCompatibilityObservedAt",
                table: "ExternalEngineConnections",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConnectorCompatibilityStatus",
                table: "ExternalEngineConnections",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "StudioDestinationCandidate",
                table: "ExternalEngineConnections",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StudioDestinationCandidateId",
                table: "ExternalEngineConnections",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StudioDestinationConfirmedAt",
                table: "ExternalEngineConnections",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StudioDestinationConfirmedByAccountId",
                table: "ExternalEngineConnections",
                type: "uniqueidentifier",
                nullable: true);

            // Older heartbeats did not require customer confirmation; none of their URLs are approved.
            migrationBuilder.Sql("UPDATE ExternalEngineConnections SET StudioDestination = NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalEngineConnections_ConnectorCompatibilityStatus",
                table: "ExternalEngineConnections",
                sql: "ConnectorCompatibilityStatus IN ('Unknown', 'Compatible', 'UnsupportedProtocol')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalEngineConnections_StudioDestinationApproval",
                table: "ExternalEngineConnections",
                sql: "(StudioDestination IS NULL AND StudioDestinationConfirmedAt IS NULL AND StudioDestinationConfirmedByAccountId IS NULL) OR (StudioDestination IS NOT NULL AND StudioDestination = StudioDestinationCandidate AND StudioDestinationCandidateId IS NOT NULL AND StudioDestinationConfirmedAt IS NOT NULL AND StudioDestinationConfirmedByAccountId IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalEngineConnections_StudioDestinationCandidate",
                table: "ExternalEngineConnections",
                sql: "(StudioDestinationCandidate IS NULL AND StudioDestinationCandidateId IS NULL) OR (StudioDestinationCandidate IS NOT NULL AND StudioDestinationCandidateId IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalEngineConnectionAuditEvents_Action",
                table: "ExternalEngineConnectionAuditEvents",
                sql: "Action IN ('Created', 'PairingIssued', 'RepairStarted', 'IdentityEnrolled', 'HeartbeatConnected', 'HeartbeatDegraded', 'HeartbeatRecovered', 'StudioDestinationConfirmed', 'Disconnected')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM dbo.ExternalEngineConnections
                    WHERE StudioDestination IS NOT NULL
                       OR StudioDestinationCandidate IS NOT NULL
                       OR StudioDestinationCandidateId IS NOT NULL
                       OR StudioDestinationConfirmedAt IS NOT NULL
                       OR StudioDestinationConfirmedByAccountId IS NOT NULL
                       OR ConnectorCompatibilityStatus <> 'Unknown'
                       OR ConnectorCompatibilityObservedAt IS NOT NULL)
                   OR EXISTS (
                    SELECT 1 FROM dbo.ExternalEngineConnectionAuditEvents
                    WHERE Action = 'StudioDestinationConfirmed')
                BEGIN
                    THROW 51023, 'Cannot roll back external-engine confirmation or compatibility data.', 1;
                END;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExternalEngineConnections_ConnectorCompatibilityStatus",
                table: "ExternalEngineConnections");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExternalEngineConnections_StudioDestinationApproval",
                table: "ExternalEngineConnections");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExternalEngineConnections_StudioDestinationCandidate",
                table: "ExternalEngineConnections");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExternalEngineConnectionAuditEvents_Action",
                table: "ExternalEngineConnectionAuditEvents");

            migrationBuilder.DropColumn(
                name: "ConnectorCompatibilityObservedAt",
                table: "ExternalEngineConnections");

            migrationBuilder.DropColumn(
                name: "ConnectorCompatibilityStatus",
                table: "ExternalEngineConnections");

            migrationBuilder.DropColumn(
                name: "StudioDestinationCandidate",
                table: "ExternalEngineConnections");

            migrationBuilder.DropColumn(
                name: "StudioDestinationCandidateId",
                table: "ExternalEngineConnections");

            migrationBuilder.DropColumn(
                name: "StudioDestinationConfirmedAt",
                table: "ExternalEngineConnections");

            migrationBuilder.DropColumn(
                name: "StudioDestinationConfirmedByAccountId",
                table: "ExternalEngineConnections");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalEngineConnectionAuditEvents_Action",
                table: "ExternalEngineConnectionAuditEvents",
                sql: "Action IN ('Created', 'PairingIssued', 'RepairStarted', 'IdentityEnrolled', 'HeartbeatConnected', 'HeartbeatDegraded', 'HeartbeatRecovered', 'Disconnected')");
        }
    }
}
