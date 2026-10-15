using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class AccountWorkspaceStoreTests
{
    [Fact]
    public async Task Updating_external_identity_is_compatible_with_retrying_execution_strategy()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection)
            .Options;
        await using var db = new CatalogDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var identity = new ExternalIdentity
        {
            Issuer = "https://identity.example",
            Subject = "operator",
            Account = new Account()
        };
        db.ExternalIdentities.Add(identity);
        await db.SaveChangesAsync();
        var store = new AccountWorkspaceStore(db);

        await store.UpdateExternalIdentitySeenAsync(identity.Id, "Updated Operator", "operator@example.com");

        db.ChangeTracker.Clear();
        var updatedIdentity = await db.ExternalIdentities.SingleAsync();
        var updatedAccount = await db.Accounts.SingleAsync();
        Assert.Equal("Updated Operator", updatedIdentity.DisplayName);
        Assert.Equal("operator@example.com", updatedIdentity.Email);
        Assert.Equal("Updated Operator", updatedAccount.DisplayName);
        Assert.Equal("operator@example.com", updatedAccount.Email);
    }

    [Fact]
    public async Task Creating_an_organization_returns_the_committed_result_after_a_lost_commit_acknowledgement()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var acknowledgement = new LostCommitAcknowledgementInterceptor();
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, isTransient: exception => exception is LostCommitAcknowledgementException)
            .AddInterceptors(acknowledgement)
            .Options;
        await using (var setup = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(connection).Options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Accounts.Add(new Account { DisplayName = "Owner", Email = "owner@example.com" });
            await setup.SaveChangesAsync();
        }

        await using var db = new CatalogDbContext(options);
        var account = await db.Accounts.SingleAsync();
        var store = new AccountWorkspaceStore(db);

        var result = await store.CreateOrganizationAsync(new CreateOrganizationRequest("Acme", account.Id, account.Id, null));

        Assert.True(result.Succeeded);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Organizations.CountAsync());
        Assert.Equal(result.OrganizationId, (await db.Organizations.SingleAsync()).Id);
        Assert.Equal(result.WorkspaceId, (await db.Workspaces.SingleAsync()).Id);
        Assert.Equal(1, await db.OrganizationAuditRecords.CountAsync());
    }

    [Fact]
    public async Task Active_managed_instance_count_includes_only_live_dedicated_placements()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateStoreContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = new Workspace { Name = "Dedicated limit workspace" };
        var other = new Workspace { Name = "Other organization workspace" };
        db.Workspaces.AddRange(workspace, other);
        await db.SaveChangesAsync();

        db.ElsaInstances.AddRange(
            CreatePlacement(workspace, isolationProfile: "dedicated"),
            CreatePlacement(workspace, isolationProfile: "dedicated"),
            CreatePlacement(workspace, isolationProfile: "shared"),
            CreateLabClassPlacement(workspace),
            CreatePlacement(workspace, isolationProfile: "dedicated", targetMode: "customer-owned"),
            Tombstone(CreatePlacement(workspace, isolationProfile: "dedicated")),
            CreatePlacement(other, isolationProfile: "dedicated"));
        await db.SaveChangesAsync();
        var store = new AccountWorkspaceStore(db);

        Assert.Equal(2, await store.ActiveManagedInstanceCountAsync(workspace.OrganizationId));
        Assert.Equal(1, await store.ActiveManagedInstanceCountAsync(other.OrganizationId));
    }

    [Theory]
    [InlineData("managed", "dedicated", true)]
    [InlineData("managed", "Dedicated", true)]
    [InlineData("managed", "shared", false)]
    [InlineData("managed", "data-isolated", false)]
    [InlineData("customer-owned", "dedicated", false)]
    public void Dedicated_limit_is_consumed_only_by_managed_dedicated_isolation(
        string targetMode,
        string isolationProfile,
        bool consumes)
    {
        Assert.Equal(
            consumes,
            ElsaInstanceDedicatedLimit.ConsumesDedicatedInstanceLimit(targetMode, isolationProfile));
    }

    private static CatalogDbContext CreateStoreContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(connection).Options);

    private static ElsaInstanceEntity CreatePlacement(
        Workspace workspace,
        string isolationProfile,
        string targetMode = "managed")
    {
        var instance = ElsaInstancePersistenceTests.NewInstance(workspace.OrganizationId, workspace.Id);
        instance.IsolationProfile = isolationProfile;
        instance.TargetMode = targetMode;
        return instance;
    }

    private static ElsaInstanceEntity CreateLabClassPlacement(Workspace workspace)
    {
        // Lab-class is a shared-shard class, not Dedicated isolation (ADR-0017 §1a).
        var instance = CreatePlacement(workspace, isolationProfile: "shared");
        instance.CapacityProfile = "lab";
        return instance;
    }

    private static ElsaInstanceEntity Tombstone(ElsaInstanceEntity instance)
    {
        instance.DesiredLifecycle = ElsaDesiredLifecycle.Deleting;
        instance.ObservedLifecycle = ElsaObservedLifecycle.Deleted;
        instance.DeletedAt = DateTimeOffset.UtcNow;
        return instance;
    }
}
