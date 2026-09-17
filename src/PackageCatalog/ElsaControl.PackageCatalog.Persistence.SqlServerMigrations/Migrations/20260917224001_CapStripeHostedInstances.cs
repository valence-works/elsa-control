using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20260917224001_CapStripeHostedInstances")]
public sealed class CapStripeHostedInstances : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        UPDATE [entitlement]
        SET [entitlement].[MaxInstances] = 1,
            [entitlement].[ManagedHostingExpiresAt] = CASE
                WHEN [subscription].[State] = N'Trial' THEN [subscription].[TrialEndsAt]
                ELSE NULL
            END
        FROM [OrganizationEntitlementSnapshots] AS [entitlement]
        INNER JOIN [OrganizationSubscriptions] AS [subscription]
            ON [subscription].[OrganizationId] = [entitlement].[OrganizationId]
           AND [subscription].[Id] = [entitlement].[SubscriptionId]
        WHERE [subscription].[Provider] = N'stripe'
          AND [subscription].[State] IN (N'Trial', N'Active');
        """);

    // The previous allowance was unbounded and cannot be reconstructed safely.
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
