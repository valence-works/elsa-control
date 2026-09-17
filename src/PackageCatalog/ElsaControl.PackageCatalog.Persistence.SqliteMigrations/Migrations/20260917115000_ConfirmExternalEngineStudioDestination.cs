using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations
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
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConnectorCompatibilityStatus",
                table: "ExternalEngineConnections",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "StudioDestinationCandidate",
                table: "ExternalEngineConnections",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StudioDestinationCandidateId",
                table: "ExternalEngineConnections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StudioDestinationConfirmedAt",
                table: "ExternalEngineConnections",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StudioDestinationConfirmedByAccountId",
                table: "ExternalEngineConnections",
                type: "TEXT",
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
                DROP TRIGGER IF EXISTS __ExternalEngineStudioDestination_DowngradeGuard;
                DROP TABLE IF EXISTS __ExternalEngineStudioDestination_DowngradeGuard;
                CREATE TEMP TABLE __ExternalEngineStudioDestination_DowngradeGuard (Id INTEGER);
                CREATE TEMP TRIGGER __ExternalEngineStudioDestination_DowngradeGuard
                BEFORE INSERT ON __ExternalEngineStudioDestination_DowngradeGuard
                WHEN EXISTS (
                    SELECT 1 FROM ExternalEngineConnections
                    WHERE StudioDestination IS NOT NULL
                       OR StudioDestinationCandidate IS NOT NULL
                       OR StudioDestinationCandidateId IS NOT NULL
                       OR StudioDestinationConfirmedAt IS NOT NULL
                       OR StudioDestinationConfirmedByAccountId IS NOT NULL
                       OR ConnectorCompatibilityStatus <> 'Unknown'
                       OR ConnectorCompatibilityObservedAt IS NOT NULL)
                   OR EXISTS (
                    SELECT 1 FROM ExternalEngineConnectionAuditEvents
                    WHERE Action = 'StudioDestinationConfirmed')
                BEGIN
                    SELECT RAISE(ABORT, 'Cannot roll back external-engine confirmation or compatibility data.');
                END;
                INSERT INTO __ExternalEngineStudioDestination_DowngradeGuard (Id) VALUES (1);
                DROP TABLE __ExternalEngineStudioDestination_DowngradeGuard;
                """);

            // Rebuild both tables explicitly. SQLite cannot emit standalone DROP
            // CHECK CONSTRAINT operations, and EF's deferred rebuild would remove
            // the append-only triggers after the custom downgrade guard runs.
            migrationBuilder.Sql("""
                PRAGMA foreign_keys = 0;
                BEGIN TRANSACTION;

                CREATE TABLE ef_temp_ExternalEngineConnections (
                    Id TEXT NOT NULL CONSTRAINT PK_ExternalEngineConnections PRIMARY KEY,
                    ActiveIdentityId TEXT NULL,
                    CapabilitiesObservedAt INTEGER NULL,
                    ConnectorProtocol TEXT NULL,
                    ConnectorReachability TEXT NOT NULL,
                    ConnectorVersion TEXT NULL,
                    CreateRequestDigest TEXT NOT NULL,
                    CreatedAt INTEGER NOT NULL,
                    DisplayName TEXT NOT NULL,
                    IdempotencyKey TEXT NOT NULL,
                    LastAuthenticatedAt INTEGER NULL,
                    LastChallengeId TEXT NULL,
                    LastHeartbeatObservedAt INTEGER NULL,
                    LastHeartbeatSequence INTEGER NULL,
                    ObservedDistribution TEXT NULL,
                    ObservedRuntimeKind TEXT NULL,
                    ObservedVersion TEXT NULL,
                    OrganizationId TEXT NOT NULL,
                    OwnershipMode TEXT NOT NULL,
                    ReleaseEvidenceLevel TEXT NOT NULL,
                    ReleaseEvidenceReference TEXT NULL,
                    RevokedAt INTEGER NULL,
                    RuntimeHealth TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    StudioDestination TEXT NULL,
                    UpdatedAt INTEGER NOT NULL,
                    Version INTEGER NOT NULL,
                    WorkspaceId TEXT NOT NULL,
                    CONSTRAINT AK_ExternalEngineConnections_OrganizationId_WorkspaceId_Id UNIQUE (OrganizationId, WorkspaceId, Id),
                    CONSTRAINT CK_ExternalEngineConnections_Evidence CHECK (ReleaseEvidenceLevel IN ('None', 'SelfReported', 'SupportedRelease', 'VerifiedManifest')),
                    CONSTRAINT CK_ExternalEngineConnections_HeartbeatSequence CHECK (LastHeartbeatSequence IS NULL OR LastHeartbeatSequence > 0),
                    CONSTRAINT CK_ExternalEngineConnections_OwnershipMode CHECK (OwnershipMode = 'CustomerOperated'),
                    CONSTRAINT CK_ExternalEngineConnections_Reachability CHECK (ConnectorReachability IN ('Unknown', 'Reachable', 'Unreachable')),
                    CONSTRAINT CK_ExternalEngineConnections_RevokedState CHECK ((Status = 'Revoked' AND RevokedAt IS NOT NULL) OR (Status <> 'Revoked' AND RevokedAt IS NULL)),
                    CONSTRAINT CK_ExternalEngineConnections_RuntimeHealth CHECK (RuntimeHealth IN ('Unknown', 'Healthy', 'Unhealthy')),
                    CONSTRAINT CK_ExternalEngineConnections_Status CHECK (Status IN ('Pending', 'Connected', 'Degraded', 'Revoked')),
                    CONSTRAINT CK_ExternalEngineConnections_Timestamps CHECK (UpdatedAt >= CreatedAt AND (RevokedAt IS NULL OR RevokedAt >= CreatedAt)),
                    CONSTRAINT CK_ExternalEngineConnections_Version CHECK (Version > 0),
                    CONSTRAINT FK_ExternalEngineConnections_Organizations_OrganizationId FOREIGN KEY (OrganizationId) REFERENCES Organizations (Id) ON DELETE RESTRICT,
                    CONSTRAINT FK_ExternalEngineConnections_Workspaces_OrganizationId_WorkspaceId FOREIGN KEY (OrganizationId, WorkspaceId) REFERENCES Workspaces (OrganizationId, Id) ON DELETE RESTRICT);

                INSERT INTO ef_temp_ExternalEngineConnections
                    (Id, ActiveIdentityId, CapabilitiesObservedAt, ConnectorProtocol, ConnectorReachability, ConnectorVersion,
                     CreateRequestDigest, CreatedAt, DisplayName, IdempotencyKey, LastAuthenticatedAt, LastChallengeId,
                     LastHeartbeatObservedAt, LastHeartbeatSequence, ObservedDistribution, ObservedRuntimeKind, ObservedVersion,
                     OrganizationId, OwnershipMode, ReleaseEvidenceLevel, ReleaseEvidenceReference, RevokedAt, RuntimeHealth,
                     Status, StudioDestination, UpdatedAt, Version, WorkspaceId)
                SELECT Id, ActiveIdentityId, CapabilitiesObservedAt, ConnectorProtocol, ConnectorReachability, ConnectorVersion,
                       CreateRequestDigest, CreatedAt, DisplayName, IdempotencyKey, LastAuthenticatedAt, LastChallengeId,
                       LastHeartbeatObservedAt, LastHeartbeatSequence, ObservedDistribution, ObservedRuntimeKind, ObservedVersion,
                       OrganizationId, OwnershipMode, ReleaseEvidenceLevel, ReleaseEvidenceReference, RevokedAt, RuntimeHealth,
                       Status, StudioDestination, UpdatedAt, Version, WorkspaceId
                FROM ExternalEngineConnections;

                CREATE TABLE ef_temp_ExternalEngineConnectionAuditEvents (
                    Id TEXT NOT NULL CONSTRAINT PK_ExternalEngineConnectionAuditEvents PRIMARY KEY,
                    Action TEXT NOT NULL,
                    ConnectionId TEXT NOT NULL,
                    OccurredAt INTEGER NOT NULL,
                    OrganizationId TEXT NOT NULL,
                    WorkspaceId TEXT NOT NULL,
                    CONSTRAINT CK_ExternalEngineConnectionAuditEvents_Action CHECK (Action IN ('Created', 'PairingIssued', 'RepairStarted', 'IdentityEnrolled', 'HeartbeatConnected', 'HeartbeatDegraded', 'HeartbeatRecovered', 'Disconnected')),
                    CONSTRAINT FK_ExternalEngineConnectionAuditEvents_ExternalEngineConnections_OrganizationId_WorkspaceId_ConnectionId
                        FOREIGN KEY (OrganizationId, WorkspaceId, ConnectionId)
                        REFERENCES ExternalEngineConnections (OrganizationId, WorkspaceId, Id) ON DELETE RESTRICT);

                INSERT INTO ef_temp_ExternalEngineConnectionAuditEvents
                    (Id, Action, ConnectionId, OccurredAt, OrganizationId, WorkspaceId)
                SELECT Id, Action, ConnectionId, OccurredAt, OrganizationId, WorkspaceId
                FROM ExternalEngineConnectionAuditEvents;

                DROP TABLE ExternalEngineConnections;
                ALTER TABLE ef_temp_ExternalEngineConnections RENAME TO ExternalEngineConnections;
                DROP TABLE ExternalEngineConnectionAuditEvents;
                ALTER TABLE ef_temp_ExternalEngineConnectionAuditEvents RENAME TO ExternalEngineConnectionAuditEvents;

                CREATE UNIQUE INDEX IX_ExternalEngineConnections_OrganizationId_WorkspaceId_Id
                    ON ExternalEngineConnections (OrganizationId, WorkspaceId, Id);
                CREATE UNIQUE INDEX IX_ExternalEngineConnections_OrganizationId_WorkspaceId_IdempotencyKey
                    ON ExternalEngineConnections (OrganizationId, WorkspaceId, IdempotencyKey);
                CREATE INDEX IX_ExternalEngineConnectionAuditEvents_OrganizationId_WorkspaceId_ConnectionId_OccurredAt
                    ON ExternalEngineConnectionAuditEvents (OrganizationId, WorkspaceId, ConnectionId, OccurredAt);

                CREATE TRIGGER TR_ExternalEngineConnectionAuditEvents_AppendOnly_Update
                BEFORE UPDATE ON ExternalEngineConnectionAuditEvents
                BEGIN SELECT RAISE(ABORT, 'External engine connection audit events are append-only'); END;
                CREATE TRIGGER TR_ExternalEngineConnectionAuditEvents_AppendOnly_Delete
                BEFORE DELETE ON ExternalEngineConnectionAuditEvents
                BEGIN SELECT RAISE(ABORT, 'External engine connection audit events are append-only'); END;

                COMMIT;
                PRAGMA foreign_keys = 1;
                """, suppressTransaction: true);
        }
    }
}
