using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class StripeEngineQuantityMigrationTests : IAsyncDisposable
{
    private const string PreviousSqliteMigration = "20261112210000_AddOrganizationSubscriptionSuspensionReason";
    private const string SqliteMigration = "20261115060000_AddStripeEngineQuantityAndBillingOperatorAlerts";
    private const string PreviousSqlServerMigration = "20261112210001_AddOrganizationSubscriptionSuspensionReason";
    private const string SqlServerMigration = "20261115060001_AddStripeEngineQuantityAndBillingOperatorAlerts";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CatalogDbContext _sqlite;

    public StripeEngineQuantityMigrationTests()
    {
        _connection.Open();
        _sqlite = new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(_connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);
    }

    [Fact]
    public async Task Sqlite_migration_adds_nullable_quantity_and_the_operator_alert_outbox()
    {
        await _sqlite.Database.MigrateAsync(PreviousSqliteMigration);
        var organizationId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        await _sqlite.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Organizations" ("Id", "Name", "Status", "CreatedAt", "UpdatedAt")
            VALUES ({organizationId}, {"Acme"}, {"Active"}, {nowTicks}, {nowTicks});
            """);
        await _sqlite.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "BillingProviderEvents" (
                "Id", "OrganizationId", "Provider", "ProviderEventId", "EventType", "EventHash",
                "OccurredAt", "ReceivedAt", "ProcessingStatus", "WebhookSource")
            VALUES (
                {eventId}, {organizationId}, {"stripe"}, {"evt_legacy_qty"}, {"customer.subscription.updated"},
                {"sha256:" + new string('a', 64)}, {nowTicks}, {nowTicks}, {"Applied"}, {"control"});
            """);

        await _sqlite.Database.MigrateAsync();

        _sqlite.ChangeTracker.Clear();
        Assert.Null((await _sqlite.BillingProviderEvents.SingleAsync()).Quantity);
        Assert.Empty(await _sqlite.OrganizationBillingOperatorAlertOutbox.ToListAsync());
        Assert.Equal(0, await _sqlite.Database.SqlQueryRaw<int>("""
            SELECT COUNT(1) AS "Value" FROM "OrganizationBillingOperatorAlertOutbox"
            """).SingleAsync());
    }

    [Fact]
    public void SqlServer_migration_adds_quantity_and_the_operator_alert_outbox()
    {
        using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,1;Database=ElsaControlMigrationScriptTests;User ID=unused;Password=unused;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);

        Assert.Contains(SqliteMigration, _sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqlServerMigration, db.GetService<IMigrationsAssembly>().Migrations.Keys);
        var script = db.GetService<IMigrator>().GenerateScript(fromMigration: PreviousSqlServerMigration, toMigration: SqlServerMigration);

        Assert.Contains("ALTER TABLE [BillingProviderEvents] ADD [Quantity] int NULL;", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [OrganizationBillingOperatorAlertOutbox]", script, StringComparison.Ordinal);
        Assert.Contains("IX_OrganizationBillingOperatorAlertOutbox_DedupeIdentity", script, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_live_", script, StringComparison.Ordinal);
        Assert.DoesNotContain("whsec_", script, StringComparison.Ordinal);
        Assert.DoesNotContain("cus_", script, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _sqlite.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
