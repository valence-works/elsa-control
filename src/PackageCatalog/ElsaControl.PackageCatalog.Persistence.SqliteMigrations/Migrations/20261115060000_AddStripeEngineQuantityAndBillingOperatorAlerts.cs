using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261115060000_AddStripeEngineQuantityAndBillingOperatorAlerts")]
public sealed class AddStripeEngineQuantityAndBillingOperatorAlerts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Quantity",
            table: "BillingProviderEvents",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "OrganizationBillingOperatorAlertOutbox",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                OrganizationId = table.Column<Guid>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                DedupeIdentity = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ActiveDedicatedCount = table.Column<int>(type: "INTEGER", nullable: true),
                MaxInstances = table.Column<int>(type: "INTEGER", nullable: true),
                Quantity = table.Column<int>(type: "INTEGER", nullable: true),
                Summary = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrganizationBillingOperatorAlertOutbox", x => x.Id);
                table.ForeignKey(
                    name: "FK_OrganizationBillingOperatorAlertOutbox_Organizations_OrganizationId",
                    column: x => x.OrganizationId,
                    principalTable: "Organizations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_OrganizationBillingOperatorAlertOutbox_DedupeIdentity",
            table: "OrganizationBillingOperatorAlertOutbox",
            column: "DedupeIdentity",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OrganizationBillingOperatorAlertOutbox_OrganizationId_Kind_CreatedAt",
            table: "OrganizationBillingOperatorAlertOutbox",
            columns: ["OrganizationId", "Kind", "CreatedAt"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "OrganizationBillingOperatorAlertOutbox");
        migrationBuilder.Sql("""ALTER TABLE "BillingProviderEvents" DROP COLUMN "Quantity";""");
    }
}
