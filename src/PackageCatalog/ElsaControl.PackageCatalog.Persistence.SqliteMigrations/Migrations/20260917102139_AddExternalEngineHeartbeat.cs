using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations
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
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastHeartbeatSequence",
                table: "ExternalEngineConnections",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ObservedRuntimeKind",
                table: "ExternalEngineConnections",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseEvidenceReference",
                table: "ExternalEngineConnections",
                type: "TEXT",
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
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS __ExternalEngineHeartbeat_DowngradeGuard;
                DROP TABLE IF EXISTS __ExternalEngineHeartbeat_DowngradeGuard;
                CREATE TEMP TABLE __ExternalEngineHeartbeat_DowngradeGuard (Id INTEGER);
                CREATE TEMP TRIGGER __ExternalEngineHeartbeat_DowngradeGuard
                BEFORE INSERT ON __ExternalEngineHeartbeat_DowngradeGuard
                WHEN EXISTS (
                    SELECT 1 FROM ExternalEngineConnections
                    WHERE ReleaseEvidenceLevel = 'SupportedRelease'
                       OR LastHeartbeatSequence IS NOT NULL
                       OR LastHeartbeatObservedAt IS NOT NULL
                       OR ObservedRuntimeKind IS NOT NULL
                       OR ReleaseEvidenceReference IS NOT NULL)
                  OR EXISTS (
                    SELECT 1 FROM ExternalEngineConnectionAuditEvents
                    WHERE Action IN ('HeartbeatConnected', 'HeartbeatDegraded', 'HeartbeatRecovered'))
                BEGIN
                    SELECT RAISE(ABORT, 'Cannot roll back external-engine heartbeat migration after heartbeat data has been recorded.');
                END;
                INSERT INTO __ExternalEngineHeartbeat_DowngradeGuard (Id) VALUES (1);
                DROP TABLE __ExternalEngineHeartbeat_DowngradeGuard;
                """);

            // Use an explicit rebuild so the previous schema's append-only triggers can be
            // restored after SQLite replaces both tables. EF otherwise schedules its rebuild
            // after custom SQL and silently drops the migration-owned triggers.
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
                    ObservedDistribution TEXT NULL,
                    ObservedVersion TEXT NULL,
                    OrganizationId TEXT NOT NULL,
                    OwnershipMode TEXT NOT NULL,
                    ReleaseEvidenceLevel TEXT NOT NULL,
                    RevokedAt INTEGER NULL,
                    RuntimeHealth TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    StudioDestination TEXT NULL,
                    UpdatedAt INTEGER NOT NULL,
                    Version INTEGER NOT NULL,
                    WorkspaceId TEXT NOT NULL,
                    CONSTRAINT AK_ExternalEngineConnections_OrganizationId_WorkspaceId_Id UNIQUE (OrganizationId, WorkspaceId, Id),
                    CONSTRAINT CK_ExternalEngineConnections_Evidence CHECK (ReleaseEvidenceLevel IN ('None', 'SelfReported', 'VerifiedManifest')),
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
                     ObservedDistribution, ObservedVersion, OrganizationId, OwnershipMode, ReleaseEvidenceLevel, RevokedAt,
                     RuntimeHealth, Status, StudioDestination, UpdatedAt, Version, WorkspaceId)
                SELECT Id, ActiveIdentityId, CapabilitiesObservedAt, ConnectorProtocol, ConnectorReachability, ConnectorVersion,
                       CreateRequestDigest, CreatedAt, DisplayName, IdempotencyKey, LastAuthenticatedAt, LastChallengeId,
                       ObservedDistribution, ObservedVersion, OrganizationId, OwnershipMode, ReleaseEvidenceLevel, RevokedAt,
                       RuntimeHealth, Status, StudioDestination, UpdatedAt, Version, WorkspaceId
                FROM ExternalEngineConnections;

                CREATE TABLE ef_temp_ExternalEngineConnectionAuditEvents (
                    Id TEXT NOT NULL CONSTRAINT PK_ExternalEngineConnectionAuditEvents PRIMARY KEY,
                    Action TEXT NOT NULL,
                    ConnectionId TEXT NOT NULL,
                    OccurredAt INTEGER NOT NULL,
                    OrganizationId TEXT NOT NULL,
                    WorkspaceId TEXT NOT NULL,
                    CONSTRAINT CK_ExternalEngineConnectionAuditEvents_Action CHECK (Action IN ('Created', 'PairingIssued', 'RepairStarted', 'IdentityEnrolled', 'Disconnected')),
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
