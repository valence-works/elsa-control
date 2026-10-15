using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20261015190001_AddBillingWebhookSource")]
public sealed class AddBillingWebhookSource : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "WebhookSource",
            table: "OrganizationSubscriptions",
            type: "nvarchar(32)",
            maxLength: 32,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "WebhookSource",
            table: "BillingProviderEvents",
            type: "nvarchar(32)",
            maxLength: 32,
            nullable: true);
        migrationBuilder.Sql("UPDATE [OrganizationSubscriptions] SET [WebhookSource] = N'control' WHERE [WebhookSource] IS NULL;");
        migrationBuilder.Sql("UPDATE [BillingProviderEvents] SET [WebhookSource] = N'control' WHERE [WebhookSource] IS NULL;");
        migrationBuilder.DropIndex(
            name: "IX_BillingProviderEvents_Provider_ProviderEventId",
            table: "BillingProviderEvents");
        migrationBuilder.CreateIndex(
            name: "IX_BillingProviderEvents_Provider_ProviderEventId_WebhookSource",
            table: "BillingProviderEvents",
            columns: ["Provider", "ProviderEventId", "WebhookSource"],
            unique: true,
            filter: "[WebhookSource] IS NOT NULL");
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
        migrationBuilder.DropColumn(name: "WebhookSource", table: "OrganizationSubscriptions");
        migrationBuilder.DropColumn(name: "WebhookSource", table: "BillingProviderEvents");
    }
}
