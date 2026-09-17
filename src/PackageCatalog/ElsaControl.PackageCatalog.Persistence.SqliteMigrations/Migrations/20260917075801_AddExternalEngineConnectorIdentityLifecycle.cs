using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalEngineConnectorIdentityLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RebuildAuditTablePreservingLifecycleValues(migrationBuilder);

            migrationBuilder.AddColumn<long>(
                name: "PreviousKeyValidUntil",
                table: "ExternalEngineConnectorIdentities",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreviousKeyVersion",
                table: "ExternalEngineConnectorIdentities",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousPublicKey",
                table: "ExternalEngineConnectorIdentities",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousPublicKeyThumbprint",
                table: "ExternalEngineConnectorIdentities",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RevokedAt",
                table: "ExternalEngineConnectorIdentities",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RotatedAt",
                table: "ExternalEngineConnectorIdentities",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalEngineConnectorIdentities_PreviousKey",
                table: "ExternalEngineConnectorIdentities",
                sql: "(PreviousKeyVersion IS NULL AND PreviousPublicKey IS NULL AND PreviousPublicKeyThumbprint IS NULL AND PreviousKeyValidUntil IS NULL) OR (PreviousKeyVersion > 0 AND PreviousKeyVersion < KeyVersion AND PreviousPublicKey IS NOT NULL AND PreviousPublicKeyThumbprint IS NOT NULL AND PreviousKeyValidUntil IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE ef_lifecycle_revocation_downgrade_guard (
                    Value INTEGER NOT NULL CONSTRAINT CK_ExternalEngineConnectorIdentities_NoRevokedDowngrade CHECK (Value = 0)
                );
                INSERT INTO ef_lifecycle_revocation_downgrade_guard (Value)
                SELECT 1 FROM ExternalEngineConnectorIdentities WHERE RevokedAt IS NOT NULL LIMIT 1;
                DROP TABLE ef_lifecycle_revocation_downgrade_guard;
                """);

            // Audit rows are append-only. Preserve the expanded allowlist during downgrade so
            // lifecycle events written after this migration do not make rollback destructive.
            RebuildAuditTablePreservingLifecycleValues(migrationBuilder);

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExternalEngineConnectorIdentities_PreviousKey",
                table: "ExternalEngineConnectorIdentities");

            migrationBuilder.DropColumn(
                name: "PreviousKeyValidUntil",
                table: "ExternalEngineConnectorIdentities");

            migrationBuilder.DropColumn(
                name: "PreviousKeyVersion",
                table: "ExternalEngineConnectorIdentities");

            migrationBuilder.DropColumn(
                name: "PreviousPublicKey",
                table: "ExternalEngineConnectorIdentities");

            migrationBuilder.DropColumn(
                name: "PreviousPublicKeyThumbprint",
                table: "ExternalEngineConnectorIdentities");

            migrationBuilder.DropColumn(
                name: "RevokedAt",
                table: "ExternalEngineConnectorIdentities");

            migrationBuilder.DropColumn(
                name: "RotatedAt",
                table: "ExternalEngineConnectorIdentities");

        }

        private static void RebuildAuditTablePreservingLifecycleValues(MigrationBuilder migrationBuilder)
        {
            const string actions = "'ChallengeIssued', 'RedemptionSucceeded', 'RedemptionRejected', 'ProofNonceConsumed', 'ConnectorProofRejected', 'KeyRotated', 'IdentityRevoked', 'IdentityRepaired'";
            const string reasons = "'None', 'InvalidRequest', 'InvalidProof', 'Expired', 'Future', 'Replay', 'AlreadyEnrolled', 'ScopeMismatch', 'KeyVersionMismatch', 'Revoked'";

            migrationBuilder.Sql($$"""
                DROP TRIGGER IF EXISTS TR_ExternalEngineEnrollmentAuditEvents_AppendOnly_Update;
                DROP TRIGGER IF EXISTS TR_ExternalEngineEnrollmentAuditEvents_AppendOnly_Delete;
                CREATE TABLE ef_lifecycle_ExternalEngineEnrollmentAuditEvents (
                    Id TEXT NOT NULL CONSTRAINT PK_ExternalEngineEnrollmentAuditEvents PRIMARY KEY,
                    Action TEXT NOT NULL,
                    ChallengeId TEXT NULL,
                    ConnectionId TEXT NOT NULL,
                    IdentityId TEXT NULL,
                    OccurredAt INTEGER NOT NULL,
                    OrganizationId TEXT NOT NULL,
                    Reason TEXT NOT NULL,
                    WorkspaceId TEXT NOT NULL,
                    CONSTRAINT CK_ExternalEngineEnrollmentAuditEvents_Action CHECK (Action IN ({{actions}})),
                    CONSTRAINT CK_ExternalEngineEnrollmentAuditEvents_Reason CHECK (Reason IN ({{reasons}}))
                );
                INSERT INTO ef_lifecycle_ExternalEngineEnrollmentAuditEvents
                    (Id, Action, ChallengeId, ConnectionId, IdentityId, OccurredAt, OrganizationId, Reason, WorkspaceId)
                SELECT Id, Action, ChallengeId, ConnectionId, IdentityId, OccurredAt, OrganizationId, Reason, WorkspaceId
                FROM ExternalEngineEnrollmentAuditEvents;
                DROP TABLE ExternalEngineEnrollmentAuditEvents;
                ALTER TABLE ef_lifecycle_ExternalEngineEnrollmentAuditEvents RENAME TO ExternalEngineEnrollmentAuditEvents;
                CREATE INDEX IX_ExternalEngineEnrollmentAuditEvents_ChallengeId
                    ON ExternalEngineEnrollmentAuditEvents (ChallengeId);
                CREATE INDEX IX_ExternalEngineEnrollmentAuditEvents_IdentityId
                    ON ExternalEngineEnrollmentAuditEvents (IdentityId);
                CREATE INDEX IX_ExternalEngineEnrollmentAuditEvents_OrganizationId_WorkspaceId_ConnectionId_OccurredAt
                    ON ExternalEngineEnrollmentAuditEvents (OrganizationId, WorkspaceId, ConnectionId, OccurredAt);
                CREATE TRIGGER TR_ExternalEngineEnrollmentAuditEvents_AppendOnly_Update
                BEFORE UPDATE ON ExternalEngineEnrollmentAuditEvents
                BEGIN SELECT RAISE(ABORT, 'External engine enrollment audit events are append-only'); END;
                CREATE TRIGGER TR_ExternalEngineEnrollmentAuditEvents_AppendOnly_Delete
                BEFORE DELETE ON ExternalEngineEnrollmentAuditEvents
                BEGIN SELECT RAISE(ABORT, 'External engine enrollment audit events are append-only'); END;
                """);
        }
    }
}
