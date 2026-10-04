using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class StripeHostedInstanceLimitMigrationTests : IAsyncDisposable
{
    private const string PreviousSqliteMigration = "20260917115013_RestoreExternalEngineStudioDestinationAuditTriggers";
    private const string SqliteMigration = "20260917224000_CapStripeHostedInstances";
    private const string PreviousSqlServerMigration = "20260917115012_ConfirmExternalEngineStudioDestination";
    private const string SqlServerMigration = "20260917224001_CapStripeHostedInstances";
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 22, 40, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CatalogDbContext _sqlite;

    public StripeHostedInstanceLimitMigrationTests()
    {
        _connection.Open();
        _sqlite = new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(
                _connection,
                sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);
    }

    [Fact]
    public async Task Existing_active_Stripe_entitlement_is_capped_without_changing_other_providers()
    {
        await _sqlite.Database.MigrateAsync(PreviousSqliteMigration);
        var stripe = await AddEntitlementAsync(BillingProviderNames.Stripe, OrganizationSubscriptionState.Active, 42);
        var internalGrant = await AddEntitlementAsync(BillingProviderNames.Internal, OrganizationSubscriptionState.Active, 3);

        await _sqlite.Database.MigrateAsync(SqliteMigration);
        _sqlite.ChangeTracker.Clear();

        Assert.Equal(1, (await _sqlite.OrganizationEntitlementSnapshots.SingleAsync(x => x.OrganizationId == stripe)).MaxInstances);
        Assert.Null((await _sqlite.OrganizationEntitlementSnapshots.SingleAsync(x => x.OrganizationId == stripe)).ManagedHostingExpiresAt);
        var internalEntitlement = await _sqlite.OrganizationEntitlementSnapshots.SingleAsync(x => x.OrganizationId == internalGrant);
        Assert.Equal(3, internalEntitlement.MaxInstances);
        Assert.Equal(Now.AddDays(30), internalEntitlement.ManagedHostingExpiresAt);
    }

    [Fact]
    public void Migrations_are_discoverable_and_SQL_Server_script_is_provider_scoped()
    {
        using var sqlServer = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,1;Database=ElsaControlMigrationScriptTests;User ID=unused;Password=unused;Encrypt=False",
                options => options.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);

        Assert.Contains(SqliteMigration, _sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqlServerMigration, sqlServer.GetService<IMigrationsAssembly>().Migrations.Keys);
        var script = sqlServer.GetService<IMigrator>().GenerateScript(PreviousSqlServerMigration, SqlServerMigration);
        Assert.Contains("UPDATE [entitlement]", script, StringComparison.Ordinal);
        Assert.Contains("[subscription].[Provider] = N'stripe'", script, StringComparison.Ordinal);
        Assert.Contains("[subscription].[State] IN (N'Trial', N'Active')", script, StringComparison.Ordinal);
    }

    private async Task<Guid> AddEntitlementAsync(string provider, OrganizationSubscriptionState state, int maxInstances)
    {
        var organization = new Organization { Name = $"{provider} organization" };
        _sqlite.Organizations.Add(organization);
        await _sqlite.SaveChangesAsync();

        if (provider == BillingProviderNames.Stripe)
        {
            // Seed the pre-cap schema without the current billing inbox model so
            // this fixture stays valid after later inbox columns are added.
            var subscription = new OrganizationSubscription
            {
                OrganizationId = organization.Id,
                Provider = provider,
                ProviderCustomerReference = $"cus-{organization.Id:N}"[..20],
                ProviderSubscriptionReference = $"sub-{organization.Id:N}"[..20],
                State = state,
                TrialStartedAt = Now,
                TrialEndsAt = Now.AddDays(14),
                ActivatedAt = state == OrganizationSubscriptionState.Active ? Now.AddMinutes(1) : null,
                LastProviderEventOccurredAt = Now.AddMinutes(1),
                LastProviderEventId = $"evt-{organization.Id:N}",
                CreatedAt = Now,
                UpdatedAt = Now.AddMinutes(1),
                LifecycleVersion = 1
            };
            _sqlite.OrganizationSubscriptions.Add(subscription);
            _sqlite.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
            {
                OrganizationId = organization.Id,
                SubscriptionId = subscription.Id,
                SubscriptionState = state,
                CanCreateCustomSources = true,
                MaxSources = 1,
                MaxWorkspaces = 1,
                MaxInstances = maxInstances,
                ManagedHostingEnabled = true,
                ManagedHostingExpiresAt = Now.AddDays(30),
                DeploymentTargetsEnabled = true,
                SyncedAt = Now.AddMinutes(1),
                CreatedAt = Now,
                UpdatedAt = Now.AddMinutes(1)
            });
            await _sqlite.SaveChangesAsync();
        }
        else
        {
            await new OrganizationBillingStore(_sqlite).GrantInternalEntitlementAsync(
                new(organization.Id, new("Migration fixture", maxInstances, Now.AddDays(30)), "test-operator"),
                Now);
        }

        var entitlement = await _sqlite.OrganizationEntitlementSnapshots.SingleAsync(x => x.OrganizationId == organization.Id);
        entitlement.MaxInstances = maxInstances;
        if (provider == BillingProviderNames.Stripe)
            entitlement.ManagedHostingExpiresAt = Now.AddDays(30);
        await _sqlite.SaveChangesAsync();
        return organization.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _sqlite.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
