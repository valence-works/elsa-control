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
}
