using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
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
}
