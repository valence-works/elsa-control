using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class BillingWebhookSourceMigrationTests : IAsyncDisposable
{
    private const string PreviousSqliteMigration = "20261015180000_AddExternalIdentityKind";
    private const string SqliteMigration = "20261015190000_AddBillingWebhookSource";
    private const string PreviousSqlServerMigration = "20261015180001_AddExternalIdentityKind";
    private const string SqlServerMigration = "20261015190001_AddBillingWebhookSource";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CatalogDbContext _sqlite;

    public BillingWebhookSourceMigrationTests()
    {
        _connection.Open();
        _sqlite = new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(_connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);
    }

    [Fact]
    public async Task Sqlite_migration_backfills_control_on_existing_billing_rows()
    {
        await _sqlite.Database.MigrateAsync(PreviousSqliteMigration);
        var organizationId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        await _sqlite.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Organizations" ("Id", "Name", "Status", "CreatedAt", "UpdatedAt")
            VALUES ({organizationId}, {"Acme"}, {"Active"}, {nowTicks}, {nowTicks});
            """);
        await _sqlite.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "OrganizationSubscriptions" (
                "Id", "OrganizationId", "Provider", "State", "TrialStartedAt", "TrialEndsAt",
                "LifecycleVersion", "LastProviderEventOccurredAt", "CreatedAt", "UpdatedAt")
            VALUES (
                {subscriptionId}, {organizationId}, {"stripe"}, {"Active"}, {nowTicks}, {nowTicks},
                {0}, {nowTicks}, {nowTicks}, {nowTicks});
            """);
        await _sqlite.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "BillingProviderEvents" (
                "Id", "OrganizationId", "Provider", "ProviderEventId", "EventType", "EventHash",
                "OccurredAt", "ReceivedAt", "ProcessingStatus")
            VALUES (
                {eventId}, {organizationId}, {"stripe"}, {"evt_legacy"}, {"customer.subscription.updated"},
                {"sha256:" + new string('a', 64)}, {nowTicks}, {nowTicks}, {"Applied"});
            """);

        await _sqlite.Database.MigrateAsync();

        _sqlite.ChangeTracker.Clear();
        Assert.Equal("control", (await _sqlite.OrganizationSubscriptions.SingleAsync()).WebhookSource);
        Assert.Equal("control", (await _sqlite.BillingProviderEvents.SingleAsync()).WebhookSource);
    }

    [Fact]
    public void SqlServer_migration_adds_source_columns_and_replaces_the_inbox_unique_index()
    {
        using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,1;Database=ElsaControlMigrationScriptTests;User ID=unused;Password=unused;Encrypt=False",
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);

        Assert.Contains(SqliteMigration, _sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqlServerMigration, db.GetService<IMigrationsAssembly>().Migrations.Keys);
        var script = db.GetService<IMigrator>().GenerateScript(fromMigration: PreviousSqlServerMigration, toMigration: SqlServerMigration);

        Assert.Contains("ALTER TABLE [OrganizationSubscriptions] ADD [WebhookSource] nvarchar(32) NULL;", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE [BillingProviderEvents] ADD [WebhookSource] nvarchar(32) NULL;", script, StringComparison.Ordinal);
        Assert.Contains("UPDATE [OrganizationSubscriptions] SET [WebhookSource] = N'control'", script, StringComparison.Ordinal);
        Assert.Contains("UPDATE [BillingProviderEvents] SET [WebhookSource] = N'control'", script, StringComparison.Ordinal);
        Assert.Contains("IX_BillingProviderEvents_Provider_ProviderEventId_WebhookSource", script, StringComparison.Ordinal);
        Assert.Contains("[WebhookSource] IS NOT NULL", script, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_live_", script, StringComparison.Ordinal);
        Assert.DoesNotContain("whsec_", script, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _sqlite.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
