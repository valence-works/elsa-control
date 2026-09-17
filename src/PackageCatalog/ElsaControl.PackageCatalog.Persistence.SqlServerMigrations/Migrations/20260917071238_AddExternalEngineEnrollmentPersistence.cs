using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalEngineEnrollmentPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalEngineConnectorIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Audience = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    KeyAlgorithm = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    KeyVersion = table.Column<int>(type: "int", nullable: false),
                    PublicKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PublicKeyThumbprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EnrolledAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalEngineConnectorIdentities", x => x.Id);
                    table.UniqueConstraint("AK_ExternalEngineConnectorIdentities_OrganizationId_WorkspaceId_ConnectionId_Id", x => new { x.OrganizationId, x.WorkspaceId, x.ConnectionId, x.Id });
                    table.CheckConstraint("CK_ExternalEngineConnectorIdentities_KeyVersion", "KeyVersion > 0");
                    table.ForeignKey(
                        name: "FK_ExternalEngineConnectorIdentities_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExternalEngineConnectorIdentities_Workspaces_OrganizationId_WorkspaceId",
                        columns: x => new { x.OrganizationId, x.WorkspaceId },
                        principalTable: "Workspaces",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExternalEngineEnrollmentAuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IdentityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OccurredAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalEngineEnrollmentAuditEvents", x => x.Id);
                    table.CheckConstraint("CK_ExternalEngineEnrollmentAuditEvents_Action", "Action IN ('ChallengeIssued', 'RedemptionSucceeded', 'RedemptionRejected', 'ProofNonceConsumed')");
                    table.CheckConstraint("CK_ExternalEngineEnrollmentAuditEvents_Reason", "Reason IN ('None', 'InvalidRequest', 'InvalidProof', 'Expired', 'Replay', 'AlreadyEnrolled')");
                });

            migrationBuilder.CreateTable(
                name: "ExternalEngineEnrollmentChallenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Audience = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ChallengeHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IssuedAt = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<long>(type: "bigint", nullable: false),
                    RedeemedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalEngineEnrollmentChallenges", x => x.Id);
                    table.CheckConstraint("CK_ExternalEngineEnrollmentChallenges_Lifetime", "ExpiresAt > IssuedAt");
                    table.ForeignKey(
                        name: "FK_ExternalEngineEnrollmentChallenges_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExternalEngineEnrollmentChallenges_Workspaces_OrganizationId_WorkspaceId",
                        columns: x => new { x.OrganizationId, x.WorkspaceId },
                        principalTable: "Workspaces",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExternalEngineConnectorProofNonces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdentityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KeyVersion = table.Column<int>(type: "int", nullable: false),
                    NonceHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IssuedAt = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<long>(type: "bigint", nullable: false),
                    ConsumedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalEngineConnectorProofNonces", x => x.Id);
                    table.CheckConstraint("CK_ExternalEngineConnectorProofNonces_KeyVersion", "KeyVersion > 0");
                    table.CheckConstraint("CK_ExternalEngineConnectorProofNonces_Lifetime", "ExpiresAt > IssuedAt AND ConsumedAt >= IssuedAt AND ConsumedAt < ExpiresAt");
                    table.ForeignKey(
                        name: "FK_ExternalEngineConnectorProofNonces_ExternalEngineConnectorIdentities_OrganizationId_WorkspaceId_ConnectionId_IdentityId",
                        columns: x => new { x.OrganizationId, x.WorkspaceId, x.ConnectionId, x.IdentityId },
                        principalTable: "ExternalEngineConnectorIdentities",
                        principalColumns: new[] { "OrganizationId", "WorkspaceId", "ConnectionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEngineConnectorIdentities_OrganizationId_WorkspaceId_ConnectionId",
                table: "ExternalEngineConnectorIdentities",
                columns: new[] { "OrganizationId", "WorkspaceId", "ConnectionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEngineConnectorIdentities_OrganizationId_WorkspaceId_PublicKeyThumbprint",
                table: "ExternalEngineConnectorIdentities",
                columns: new[] { "OrganizationId", "WorkspaceId", "PublicKeyThumbprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEngineConnectorProofNonces_ExpiresAt",
                table: "ExternalEngineConnectorProofNonces",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEngineConnectorProofNonces_OrganizationId_WorkspaceId_ConnectionId_IdentityId_KeyVersion_NonceHash",
                table: "ExternalEngineConnectorProofNonces",
                columns: new[] { "OrganizationId", "WorkspaceId", "ConnectionId", "IdentityId", "KeyVersion", "NonceHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEngineEnrollmentAuditEvents_ChallengeId",
                table: "ExternalEngineEnrollmentAuditEvents",
                column: "ChallengeId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEngineEnrollmentAuditEvents_IdentityId",
                table: "ExternalEngineEnrollmentAuditEvents",
                column: "IdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEngineEnrollmentAuditEvents_OrganizationId_WorkspaceId_ConnectionId_OccurredAt",
                table: "ExternalEngineEnrollmentAuditEvents",
                columns: new[] { "OrganizationId", "WorkspaceId", "ConnectionId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEngineEnrollmentChallenges_ExpiresAt",
                table: "ExternalEngineEnrollmentChallenges",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEngineEnrollmentChallenges_OrganizationId_WorkspaceId_ConnectionId_Id",
                table: "ExternalEngineEnrollmentChallenges",
                columns: new[] { "OrganizationId", "WorkspaceId", "ConnectionId", "Id" },
                unique: true);

            // Dynamic EXEC keeps CREATE TRIGGER safe in idempotent migration scripts.
            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER TR_ExternalEngineEnrollmentAuditEvents_AppendOnly
                ON ExternalEngineEnrollmentAuditEvents
                INSTEAD OF UPDATE, DELETE
                AS BEGIN
                    THROW 51020, ''External engine enrollment audit events are append-only'', 1;
                END;');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_ExternalEngineEnrollmentAuditEvents_AppendOnly;");

            migrationBuilder.DropTable(
                name: "ExternalEngineConnectorProofNonces");

            migrationBuilder.DropTable(
                name: "ExternalEngineEnrollmentAuditEvents");

            migrationBuilder.DropTable(
                name: "ExternalEngineEnrollmentChallenges");

            migrationBuilder.DropTable(
                name: "ExternalEngineConnectorIdentities");
        }
    }
}
