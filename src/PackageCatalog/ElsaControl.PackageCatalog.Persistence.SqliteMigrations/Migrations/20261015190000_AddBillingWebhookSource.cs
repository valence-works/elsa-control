using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261015190000_AddBillingWebhookSource")]
public sealed class AddBillingWebhookSource : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "WebhookSource",
            table: "OrganizationSubscriptions",
            type: "TEXT",
            maxLength: 32,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "WebhookSource",
            table: "BillingProviderEvents",
            type: "TEXT",
            maxLength: 32,
            nullable: true);
        migrationBuilder.Sql("""UPDATE "OrganizationSubscriptions" SET "WebhookSource" = 'control' WHERE "WebhookSource" IS NULL;""");
        migrationBuilder.Sql("""UPDATE "BillingProviderEvents" SET "WebhookSource" = 'control' WHERE "WebhookSource" IS NULL;""");
        migrationBuilder.DropIndex(
            name: "IX_BillingProviderEvents_Provider_ProviderEventId",
            table: "BillingProviderEvents");
        migrationBuilder.CreateIndex(
            name: "IX_BillingProviderEvents_Provider_ProviderEventId_WebhookSource",
            table: "BillingProviderEvents",
            columns: ["Provider", "ProviderEventId", "WebhookSource"],
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_BillingProviderEvents_Provider_ProviderEventId_WebhookSource",
            table: "BillingProviderEvents");
        migrationBuilder.CreateIndex(
            name: "IX_BillingProviderEvents_Provider_ProviderEventId",
            table: "BillingProviderEvents",
            columns: ["Provider", "ProviderEventId"],
            unique: true);
        migrationBuilder.Sql("""ALTER TABLE "OrganizationSubscriptions" DROP COLUMN "WebhookSource";""");
        migrationBuilder.Sql("""ALTER TABLE "BillingProviderEvents" DROP COLUMN "WebhookSource";""");
    }
}
