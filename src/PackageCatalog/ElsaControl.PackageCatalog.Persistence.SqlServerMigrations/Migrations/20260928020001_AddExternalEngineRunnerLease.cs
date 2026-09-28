using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20260928020001_AddExternalEngineRunnerLease")]
public sealed class AddExternalEngineRunnerLease : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_ExternalEngineConnectionAuditEvents_Action",
            table: "ExternalEngineConnectionAuditEvents");

        migrationBuilder.AddColumn<string>(
            name: "ActiveRunnerId",
            table: "ExternalEngineConnections",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "RunnerLeaseExpiresAt",
            table: "ExternalEngineConnections",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "CK_ExternalEngineConnections_RunnerLease",
            table: "ExternalEngineConnections",
            sql: "(ActiveRunnerId IS NULL AND RunnerLeaseExpiresAt IS NULL) OR (ActiveRunnerId IS NOT NULL AND RunnerLeaseExpiresAt IS NOT NULL)");

        migrationBuilder.AddCheckConstraint(
            name: "CK_ExternalEngineConnectionAuditEvents_Action",
            table: "ExternalEngineConnectionAuditEvents",
            sql: "Action IN ('Created', 'PairingIssued', 'RepairStarted', 'IdentityEnrolled', 'HeartbeatConnected', 'HeartbeatDegraded', 'HeartbeatRecovered', 'StudioDestinationConfirmed', 'Disconnected', 'external-engine.runner-changed', 'external-engine.label-changed')");

        // The retired connector grader was the only writer of VerifiedManifest /
        // SupportedRelease on this customer-operated table. Cap any leftover
        // rows so they cannot outrank a later Valence-operated observation.
        migrationBuilder.Sql("""
            UPDATE [ExternalEngineConnections]
            SET [ReleaseEvidenceLevel] = N'SelfReported',
                [ReleaseEvidenceReference] = NULL
            WHERE [ReleaseEvidenceLevel] IN (N'SupportedRelease', N'VerifiedManifest');
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_ExternalEngineConnectionAuditEvents_Action",
            table: "ExternalEngineConnectionAuditEvents");

        migrationBuilder.DropCheckConstraint(
            name: "CK_ExternalEngineConnections_RunnerLease",
            table: "ExternalEngineConnections");

        migrationBuilder.DropColumn(
            name: "ActiveRunnerId",
            table: "ExternalEngineConnections");

        migrationBuilder.DropColumn(
            name: "RunnerLeaseExpiresAt",
            table: "ExternalEngineConnections");

        migrationBuilder.AddCheckConstraint(
            name: "CK_ExternalEngineConnectionAuditEvents_Action",
            table: "ExternalEngineConnectionAuditEvents",
            sql: "Action IN ('Created', 'PairingIssued', 'RepairStarted', 'IdentityEnrolled', 'HeartbeatConnected', 'HeartbeatDegraded', 'HeartbeatRecovered', 'StudioDestinationConfirmed', 'Disconnected')");
    }
}
