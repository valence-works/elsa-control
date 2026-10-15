using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class AccountIdentityLinkStoreTests
{
    private const string CloudIssuer = "https://cloud-account.test/auth/v1";
    private const string HubIssuer = "https://hub-account.test/auth/v1";

    [Fact]
    public async Task Linking_attaches_the_hub_identity_and_writes_an_account_id_audit()
    {
        await using var db = await CreateDatabaseAsync();
        var cloud = await SeedAccountAsync(db, CloudIssuer, "cloud-user");
        var store = new AccountIdentityLinkStore(db);

        var result = await store.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user"));

        Assert.Equal(AccountIdentityLinkOutcome.Linked, result.Outcome);
        Assert.Equal(cloud.Id, result.AccountId);
        var identities = await db.ExternalIdentities.ToListAsync();
        Assert.Equal(2, identities.Count);
        Assert.Contains(identities, identity =>
            identity.AccountId == cloud.Id &&
            identity.Kind == ExternalIdentityKinds.Hub &&
            identity.Issuer == HubIssuer &&
            identity.Subject == "hub-user");
        var audit = Assert.Single(await db.AccountIdentityLinkAuditRecords.ToListAsync());
        Assert.Equal(AccountIdentityLinkAction.Linked, audit.Action);
        Assert.Equal(cloud.Id, audit.AccountId);
        Assert.Equal(cloud.Id, audit.ActorAccountId);
        Assert.Null(audit.SourceAccountId);
        Assert.DoesNotContain("example.test", audit.OperatorSubject ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linking_twice_is_a_no_op_and_does_not_add_audit()
    {
        await using var db = await CreateDatabaseAsync();
        var cloud = await SeedAccountAsync(db, CloudIssuer, "cloud-user");
        var store = new AccountIdentityLinkStore(db);
        await store.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user"));

        var result = await store.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user"));

        Assert.Equal(AccountIdentityLinkOutcome.AlreadyLinked, result.Outcome);
        Assert.Equal(cloud.Id, result.AccountId);
        Assert.Single(await db.AccountIdentityLinkAuditRecords.ToListAsync());
        Assert.Equal(2, await db.ExternalIdentities.CountAsync());
    }

    [Fact]
    public async Task Conflicting_hub_account_with_engines_is_refused_and_changes_nothing()
    {
        await using var db = await CreateDatabaseAsync();
        var cloud = await SeedAccountAsync(db, CloudIssuer, "cloud-user");
        var hub = await SeedAccountAsync(db, HubIssuer, "hub-user", ExternalIdentityKinds.Hub);
        await SeedEngineAsync(db, hub);
        var store = new AccountIdentityLinkStore(db);

        var result = await store.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user"));

        Assert.Equal(AccountIdentityLinkOutcome.HubAccountHasEngines, result.Outcome);
        Assert.Empty(await db.AccountIdentityLinkAuditRecords.ToListAsync());
        Assert.Equal(hub.Id, (await db.ExternalIdentities.SingleAsync(x => x.Subject == "hub-user")).AccountId);
        Assert.DoesNotContain(await db.ExternalIdentities.ToListAsync(), identity =>
            identity.AccountId == cloud.Id && identity.Kind == ExternalIdentityKinds.Hub);
    }

    [Fact]
    public async Task Hub_identity_without_engines_moves_onto_the_cloud_account()
    {
        await using var db = await CreateDatabaseAsync();
        var cloud = await SeedAccountAsync(db, CloudIssuer, "cloud-user");
        var hub = await SeedAccountAsync(db, HubIssuer, "hub-user", ExternalIdentityKinds.Hub);
        var store = new AccountIdentityLinkStore(db);

        var result = await store.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user"));

        Assert.Equal(AccountIdentityLinkOutcome.Linked, result.Outcome);
        Assert.Equal(cloud.Id, (await db.ExternalIdentities.SingleAsync(x => x.Subject == "hub-user")).AccountId);
        var audit = Assert.Single(await db.AccountIdentityLinkAuditRecords.ToListAsync());
        Assert.Equal(hub.Id, audit.SourceAccountId);
        Assert.Equal(cloud.Id, audit.AccountId);
    }

    [Fact]
    public async Task Unlink_removes_the_hub_identity_and_is_audited()
    {
        await using var db = await CreateDatabaseAsync();
        var cloud = await SeedAccountAsync(db, CloudIssuer, "cloud-user");
        var store = new AccountIdentityLinkStore(db);
        await store.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user"));

        var result = await store.UnlinkAsync(cloud.Id, "sha256:operator");

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(await db.ExternalIdentities.ToListAsync(), identity => identity.Kind == ExternalIdentityKinds.Hub);
        var unlink = Assert.Single(await db.AccountIdentityLinkAuditRecords.Where(x => x.Action == AccountIdentityLinkAction.Unlinked).ToListAsync());
        Assert.Equal(cloud.Id, unlink.AccountId);
        Assert.Equal("sha256:operator", unlink.OperatorSubject);
        Assert.DoesNotContain("@", unlink.OperatorSubject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Email_is_never_used_to_match_accounts()
    {
        await using var db = await CreateDatabaseAsync();
        await SeedAccountAsync(db, CloudIssuer, "cloud-user", email: "shared@example.test");
        await SeedAccountAsync(db, HubIssuer, "hub-user", ExternalIdentityKinds.Hub, email: "shared@example.test");
        var store = new AccountIdentityLinkStore(db);

        var missing = await store.LinkAsync(CloudIdentity("unknown-cloud"), HubIdentity("unknown-hub"));

        Assert.Equal(AccountIdentityLinkOutcome.CloudAccountMissing, missing.Outcome);
        Assert.Equal(2, await db.Accounts.CountAsync());
        Assert.Equal(2, await db.ExternalIdentities.CountAsync());
        Assert.Empty(await db.AccountIdentityLinkAuditRecords.ToListAsync());
    }

    private static async Task<CatalogDbContext> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<Account> SeedAccountAsync(
        CatalogDbContext db,
        string issuer,
        string subject,
        string? kind = null,
        string? email = null)
    {
        var account = new Account
        {
            DisplayName = subject,
            Email = email ?? $"{subject}@example.test"
        };
        account.ExternalIdentities.Add(new ExternalIdentity
        {
            Account = account,
            Issuer = issuer,
            Subject = subject,
            Kind = kind,
            DisplayName = subject,
            Email = account.Email
        });
        var workspace = new Workspace { Name = subject, Kind = WorkspaceKind.Personal };
        var organization = new Organization { Name = subject, CreatedByAccountId = account.Id, Workspaces = { workspace } };
        account.OrganizationMemberships.Add(new OrganizationMembership
        {
            Account = account,
            Organization = organization,
            Role = OrganizationRole.Owner
        });
        account.Memberships.Add(new WorkspaceMembership
        {
            Account = account,
            Workspace = workspace,
            Role = WorkspaceRole.Owner
        });
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return account;
    }

    private static async Task SeedEngineAsync(CatalogDbContext db, Account account)
    {
        var membership = await db.WorkspaceMemberships.AsNoTracking().SingleAsync(x => x.AccountId == account.Id);
        var organizationId = await db.Workspaces.Where(x => x.Id == membership.WorkspaceId).Select(x => x.OrganizationId).SingleAsync();
        var now = DateTimeOffset.UtcNow;
        db.ElsaInstances.Add(new ElsaInstanceEntity
        {
            OrganizationId = organizationId,
            WorkspaceId = membership.WorkspaceId,
            Name = "Hub runtime",
            Slug = "hub-runtime",
            DistributionId = "valence-runtime",
            ReleaseLine = "3.8",
            Channel = "stable",
            PatchUpdates = "automatic-within-minor",
            MinorUpdates = "explicit-approval",
            MajorMigrations = "explicit-migration",
            TopologyId = "combined",
            FeatureOverridesJson = "{}",
            TargetMode = "managed",
            RegionCode = "westeurope",
            IsolationProfile = "dedicated",
            CapacityProfile = "standard-small",
            NetworkOutcome = "public",
            DomainOutcome = "managed",
            DesiredLifecycle = ElsaDesiredLifecycle.Running,
            ObservedLifecycle = ElsaObservedLifecycle.Ready,
            Health = ElsaInstanceHealth.Healthy,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static TrustedWorkspaceIdentity CloudIdentity(string subject) =>
        new(CloudIssuer, subject, subject, $"{subject}@example.test");

    private static TrustedWorkspaceIdentity HubIdentity(string subject) =>
        new(HubIssuer, subject, subject, $"{subject}@example.test") { Kind = ExternalIdentityKinds.Hub };
}
