using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261115060001_AddStripeEngineQuantityAndBillingOperatorAlerts")]
public sealed class AddStripeEngineQuantityAndBillingOperatorAlerts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Quantity",
            table: "BillingProviderEvents",
            type: "int",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "OrganizationBillingOperatorAlertOutbox",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                DedupeIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ActiveDedicatedCount = table.Column<int>(type: "int", nullable: true),
                MaxInstances = table.Column<int>(type: "int", nullable: true),
                Quantity = table.Column<int>(type: "int", nullable: true),
                Summary = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false)
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
        migrationBuilder.DropColumn(name: "Quantity", table: "BillingProviderEvents");
    }
}
