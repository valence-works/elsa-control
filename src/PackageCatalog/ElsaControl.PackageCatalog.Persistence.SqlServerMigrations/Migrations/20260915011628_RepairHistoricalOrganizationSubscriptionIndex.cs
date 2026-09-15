using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations
{
    /// <inheritdoc />
    public partial class RepairHistoricalOrganizationSubscriptionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = N'IX_OrganizationSubscriptions_OrganizationId'
                      AND object_id = OBJECT_ID(N'dbo.OrganizationSubscriptions'))
                    DROP INDEX [IX_OrganizationSubscriptions_OrganizationId] ON [dbo].[OrganizationSubscriptions];

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = N'IX_OrganizationSubscriptions_OrganizationId'
                      AND object_id = OBJECT_ID(N'dbo.OrganizationSubscriptions'))
                    CREATE UNIQUE INDEX [IX_OrganizationSubscriptions_OrganizationId]
                        ON [dbo].[OrganizationSubscriptions] ([OrganizationId])
                        WHERE [State] IN ('Trial', 'Active', 'PastDue', 'Constrained', 'Suspended');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The corrected preceding migration already has the same model state.
        }
    }
}
