using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqliteMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20260917224000_CapStripeHostedInstances")]
public sealed class CapStripeHostedInstances : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        UPDATE "OrganizationEntitlementSnapshots"
        SET "MaxInstances" = 1,
            "ManagedHostingExpiresAt" = (
                SELECT CASE
                    WHEN "subscription"."State" = 'Trial' THEN "subscription"."TrialEndsAt"
                    ELSE NULL
                END
                FROM "OrganizationSubscriptions" AS "subscription"
                WHERE "subscription"."OrganizationId" = "OrganizationEntitlementSnapshots"."OrganizationId"
                  AND "subscription"."Id" = "OrganizationEntitlementSnapshots"."SubscriptionId")
        WHERE EXISTS (
            SELECT 1
            FROM "OrganizationSubscriptions" AS "subscription"
            WHERE "subscription"."OrganizationId" = "OrganizationEntitlementSnapshots"."OrganizationId"
              AND "subscription"."Id" = "OrganizationEntitlementSnapshots"."SubscriptionId"
              AND "subscription"."Provider" = 'stripe'
              AND "subscription"."State" IN ('Trial', 'Active'));
        """);

    // The previous allowance was unbounded and cannot be reconstructed safely.
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
