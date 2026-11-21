using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261120070354_AddPartnerApiKeys")]
public sealed class AddPartnerApiKeys : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PartnerApiKeys",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                KeyId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProvisionerAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SecretHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                Scopes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                CreatedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CreatedByOperatorSubject = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                ExpiresAt = table.Column<long>(type: "bigint", nullable: true),
                LastUsedAt = table.Column<long>(type: "bigint", nullable: true),
                RevokedAt = table.Column<long>(type: "bigint", nullable: true),
                RevokedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                RevokedByOperatorSubject = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PartnerApiKeys", x => x.Id);
                table.ForeignKey(
                    name: "FK_PartnerApiKeys_Accounts_ProvisionerAccountId",
                    column: x => x.ProvisionerAccountId,
                    principalTable: "Accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PartnerApiKeys_Organizations_OrganizationId",
                    column: x => x.OrganizationId,
                    principalTable: "Organizations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PartnerApiKeys_Workspaces_OrganizationId_WorkspaceId",
                    columns: x => new { x.OrganizationId, x.WorkspaceId },
                    principalTable: "Workspaces",
                    principalColumns: ["OrganizationId", "Id"],
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PartnerApiKeys_KeyId",
            table: "PartnerApiKeys",
            column: "KeyId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PartnerApiKeys_OrganizationId_RevokedAt_ExpiresAt",
            table: "PartnerApiKeys",
            columns: ["OrganizationId", "RevokedAt", "ExpiresAt"]);

        migrationBuilder.CreateIndex(
            name: "IX_PartnerApiKeys_OrganizationId_WorkspaceId",
            table: "PartnerApiKeys",
            columns: ["OrganizationId", "WorkspaceId"]);

        migrationBuilder.CreateIndex(
            name: "IX_PartnerApiKeys_ProvisionerAccountId",
            table: "PartnerApiKeys",
            column: "ProvisionerAccountId");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "PartnerApiKeys");
}
