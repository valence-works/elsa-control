using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalEngineConnectorIdentityLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ExternalEngineEnrollmentAuditEvents_Action",
                table: "ExternalEngineEnrollmentAuditEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExternalEngineEnrollmentAuditEvents_Reason",
                table: "ExternalEngineEnrollmentAuditEvents");

            migrationBuilder.AddColumn<long>(
                name: "PreviousKeyValidUntil",
                table: "ExternalEngineConnectorIdentities",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreviousKeyVersion",
                table: "ExternalEngineConnectorIdentities",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousPublicKey",
                table: "ExternalEngineConnectorIdentities",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousPublicKeyThumbprint",
                table: "ExternalEngineConnectorIdentities",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RevokedAt",
                table: "ExternalEngineConnectorIdentities",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RotatedAt",
                table: "ExternalEngineConnectorIdentities",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalEngineEnrollmentAuditEvents_Action",
                table: "ExternalEngineEnrollmentAuditEvents",
                sql: "Action IN ('ChallengeIssued', 'RedemptionSucceeded', 'RedemptionRejected', 'ProofNonceConsumed', 'ConnectorProofRejected', 'KeyRotated', 'IdentityRevoked', 'IdentityRepaired')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalEngineEnrollmentAuditEvents_Reason",
                table: "ExternalEngineEnrollmentAuditEvents",
                sql: "Reason IN ('None', 'InvalidRequest', 'InvalidProof', 'Expired', 'Future', 'Replay', 'AlreadyEnrolled', 'ScopeMismatch', 'KeyVersionMismatch', 'Revoked')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalEngineConnectorIdentities_PreviousKey",
                table: "ExternalEngineConnectorIdentities",
                sql: "(PreviousKeyVersion IS NULL AND PreviousPublicKey IS NULL AND PreviousPublicKeyThumbprint IS NULL AND PreviousKeyValidUntil IS NULL) OR (PreviousKeyVersion > 0 AND PreviousKeyVersion < KeyVersion AND PreviousPublicKey IS NOT NULL AND PreviousPublicKeyThumbprint IS NOT NULL AND PreviousKeyValidUntil IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [ExternalEngineConnectorIdentities] WHERE [RevokedAt] IS NOT NULL)
                    THROW 51000, 'Cannot downgrade connector identity lifecycle while revoked identities exist.', 1;
                """);

            // Audit rows are append-only. Preserve the expanded allowlist during downgrade so
            // lifecycle events written after this migration do not make rollback destructive.
            migrationBuilder.DropCheckConstraint(
                name: "CK_ExternalEngineEnrollmentAuditEvents_Action",
                table: "ExternalEngineEnrollmentAuditEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExternalEngineEnrollmentAuditEvents_Reason",
                table: "ExternalEngineEnrollmentAuditEvents");

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

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalEngineEnrollmentAuditEvents_Action",
                table: "ExternalEngineEnrollmentAuditEvents",
                sql: "Action IN ('ChallengeIssued', 'RedemptionSucceeded', 'RedemptionRejected', 'ProofNonceConsumed', 'ConnectorProofRejected', 'KeyRotated', 'IdentityRevoked', 'IdentityRepaired')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExternalEngineEnrollmentAuditEvents_Reason",
                table: "ExternalEngineEnrollmentAuditEvents",
                sql: "Reason IN ('None', 'InvalidRequest', 'InvalidProof', 'Expired', 'Future', 'Replay', 'AlreadyEnrolled', 'ScopeMismatch', 'KeyVersionMismatch', 'Revoked')");
        }
    }
}
