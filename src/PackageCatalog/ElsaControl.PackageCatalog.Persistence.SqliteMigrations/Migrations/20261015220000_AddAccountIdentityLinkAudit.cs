using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261015220000_AddAccountIdentityLinkAudit")]
public sealed class AddAccountIdentityLinkAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AccountIdentityLinkAuditRecords",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                ActorAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                ActorKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                OperatorSubject = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AccountIdentityLinkAuditRecords", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_AccountIdentityLinkAuditRecords_AccountId_CreatedAt",
            table: "AccountIdentityLinkAuditRecords",
            columns: ["AccountId", "CreatedAt"]);

        migrationBuilder.CreateIndex(
            name: "IX_ExternalIdentities_AccountId_Hub",
            table: "ExternalIdentities",
            column: "AccountId",
            unique: true,
            filter: "Kind = 'hub'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ExternalIdentities_AccountId_Hub",
            table: "ExternalIdentities");
        migrationBuilder.DropTable(name: "AccountIdentityLinkAuditRecords");
    }
}
