using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalEngineConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalEngineConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    OwnershipMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RuntimeHealth = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ConnectorReachability = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LastAuthenticatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ConnectorProtocol = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ConnectorVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ObservedDistribution = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ObservedVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ReleaseEvidenceLevel = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StudioDestination = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CapabilitiesObservedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ActiveIdentityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LastChallengeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreateRequestDigest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalEngineConnections", x => x.Id);
                    table.UniqueConstraint("AK_ExternalEngineConnections_OrganizationId_WorkspaceId_Id", x => new { x.OrganizationId, x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_ExternalEngineConnections_Evidence", "ReleaseEvidenceLevel IN ('None', 'SelfReported', 'VerifiedManifest')");
                    table.CheckConstraint("CK_ExternalEngineConnections_OwnershipMode", "OwnershipMode = 'CustomerOperated'");
                    table.CheckConstraint("CK_ExternalEngineConnections_Reachability", "ConnectorReachability IN ('Unknown', 'Reachable', 'Unreachable')");
                    table.CheckConstraint("CK_ExternalEngineConnections_RevokedState", "(Status = 'Revoked' AND RevokedAt IS NOT NULL) OR (Status <> 'Revoked' AND RevokedAt IS NULL)");
                    table.CheckConstraint("CK_ExternalEngineConnections_RuntimeHealth", "RuntimeHealth IN ('Unknown', 'Healthy', 'Unhealthy')");
                    table.CheckConstraint("CK_ExternalEngineConnections_Status", "Status IN ('Pending', 'Connected', 'Degraded', 'Revoked')");
                    table.CheckConstraint("CK_ExternalEngineConnections_Timestamps", "UpdatedAt >= CreatedAt AND (RevokedAt IS NULL OR RevokedAt >= CreatedAt)");
                    table.CheckConstraint("CK_ExternalEngineConnections_Version", "Version > 0");
                    table.ForeignKey(
                        name: "FK_ExternalEngineConnections_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExternalEngineConnections_Workspaces_OrganizationId_WorkspaceId",
                        columns: x => new { x.OrganizationId, x.WorkspaceId },
                        principalTable: "Workspaces",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExternalEngineConnectionAuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalEngineConnectionAuditEvents", x => x.Id);
                    table.CheckConstraint("CK_ExternalEngineConnectionAuditEvents_Action", "Action IN ('Created', 'PairingIssued', 'RepairStarted', 'IdentityEnrolled', 'Disconnected')");
                    table.ForeignKey(
                        name: "FK_ExternalEngineConnectionAuditEvents_ExternalEngineConnections_OrganizationId_WorkspaceId_ConnectionId",
                        columns: x => new { x.OrganizationId, x.WorkspaceId, x.ConnectionId },
                        principalTable: "ExternalEngineConnections",
                        principalColumns: new[] { "OrganizationId", "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExternalEngineConnectionCapabilities",
                columns: table => new
                {
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Capability = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalEngineConnectionCapabilities", x => new { x.ConnectionId, x.Capability });
                    table.ForeignKey(
                        name: "FK_ExternalEngineConnectionCapabilities_ExternalEngineConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "ExternalEngineConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEngineConnectionAuditEvents_OrganizationId_WorkspaceId_ConnectionId_OccurredAt",
                table: "ExternalEngineConnectionAuditEvents",
                columns: new[] { "OrganizationId", "WorkspaceId", "ConnectionId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEngineConnections_OrganizationId_WorkspaceId_Id",
                table: "ExternalEngineConnections",
                columns: new[] { "OrganizationId", "WorkspaceId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEngineConnections_OrganizationId_WorkspaceId_IdempotencyKey",
                table: "ExternalEngineConnections",
                columns: new[] { "OrganizationId", "WorkspaceId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE TRIGGER TR_ExternalEngineConnectionAuditEvents_AppendOnly_Update
                BEFORE UPDATE ON ExternalEngineConnectionAuditEvents
                BEGIN SELECT RAISE(ABORT, 'External engine connection audit events are append-only'); END;
                CREATE TRIGGER TR_ExternalEngineConnectionAuditEvents_AppendOnly_Delete
                BEFORE DELETE ON ExternalEngineConnectionAuditEvents
                BEGIN SELECT RAISE(ABORT, 'External engine connection audit events are append-only'); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS TR_ExternalEngineConnectionAuditEvents_AppendOnly_Delete;
                DROP TRIGGER IF EXISTS TR_ExternalEngineConnectionAuditEvents_AppendOnly_Update;
                """);

            migrationBuilder.DropTable(
                name: "ExternalEngineConnectionAuditEvents");

            migrationBuilder.DropTable(
                name: "ExternalEngineConnectionCapabilities");

            migrationBuilder.DropTable(
                name: "ExternalEngineConnections");
        }
    }
}
