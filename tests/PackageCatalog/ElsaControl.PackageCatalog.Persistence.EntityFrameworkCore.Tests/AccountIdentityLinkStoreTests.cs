using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.ExternalConnections;
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
        Assert.Equal(AccountIdentityLinkActorKind.Account, audit.ActorKind);
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
        Assert.Equal(AccountIdentityLinkAction.Linked, audit.Action);
        Assert.Equal(AccountIdentityLinkActorKind.Account, audit.ActorKind);
        Assert.Equal(hub.Id, audit.SourceAccountId);
        Assert.Equal(cloud.Id, audit.AccountId);
        Assert.Equal(cloud.Id, audit.ActorAccountId);
        Assert.DoesNotContain(
            await db.AccountIdentityLinkAuditRecords.ToListAsync(),
            record => record.Action == AccountIdentityLinkAction.Unlinked);
    }

    [Fact]
    public async Task Already_linked_hub_identity_is_not_moved_to_a_different_account()
    {
        await using var db = await CreateDatabaseAsync();
        var first = await SeedAccountAsync(db, CloudIssuer, "cloud-user");
        var second = await SeedAccountAsync(db, CloudIssuer, "other-cloud");
        var store = new AccountIdentityLinkStore(db);
        await store.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user"));

        var result = await store.LinkAsync(CloudIdentity("other-cloud"), HubIdentity("hub-user"));

        Assert.Equal(AccountIdentityLinkOutcome.AccountAlreadyLinked, result.Outcome);
        Assert.Equal(first.Id, (await db.ExternalIdentities.SingleAsync(x => x.Subject == "hub-user")).AccountId);
        Assert.DoesNotContain(await db.ExternalIdentities.ToListAsync(), identity =>
            identity.AccountId == second.Id && identity.Kind == ExternalIdentityKinds.Hub);
        Assert.Single(await db.AccountIdentityLinkAuditRecords.ToListAsync());
        Assert.Equal(first.Id, (await db.AccountIdentityLinkAuditRecords.SingleAsync()).AccountId);
    }

    [Fact]
    public async Task Hub_account_with_an_active_external_engine_connection_is_refused_and_is_not_migrated()
    {
        await using var db = await CreateDatabaseAsync();
        var cloud = await SeedAccountAsync(db, CloudIssuer, "cloud-user");
        var hub = await SeedAccountAsync(db, HubIssuer, "hub-user", ExternalIdentityKinds.Hub);
        await SeedExternalEngineConnectionAsync(db, hub, ExternalEngineConnectionStatus.Connected);
        var store = new AccountIdentityLinkStore(db);

        var result = await store.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user"));

        Assert.Equal(AccountIdentityLinkOutcome.HubAccountHasEngines, result.Outcome);
        Assert.Empty(await db.AccountIdentityLinkAuditRecords.ToListAsync());
        Assert.Equal(hub.Id, (await db.ExternalIdentities.SingleAsync(x => x.Subject == "hub-user")).AccountId);
        Assert.DoesNotContain(await db.ExternalIdentities.ToListAsync(), identity =>
            identity.AccountId == cloud.Id && identity.Kind == ExternalIdentityKinds.Hub);
    }

    [Fact]
    public async Task Hub_account_with_extra_workspace_memberships_is_refused_and_is_not_migrated()
    {
        await using var db = await CreateDatabaseAsync();
        var cloud = await SeedAccountAsync(db, CloudIssuer, "cloud-user");
        var hub = await SeedAccountAsync(db, HubIssuer, "hub-user", ExternalIdentityKinds.Hub);
        await SeedExtraWorkspaceMembershipAsync(db, hub);
        var store = new AccountIdentityLinkStore(db);

        var result = await store.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user"));

        Assert.Equal(AccountIdentityLinkOutcome.HubAccountHasState, result.Outcome);
        Assert.Empty(await db.AccountIdentityLinkAuditRecords.ToListAsync());
        Assert.Equal(hub.Id, (await db.ExternalIdentities.SingleAsync(x => x.Subject == "hub-user")).AccountId);
        Assert.DoesNotContain(await db.ExternalIdentities.ToListAsync(), identity =>
            identity.AccountId == cloud.Id && identity.Kind == ExternalIdentityKinds.Hub);
    }

    [Fact]
    public async Task Hub_account_with_a_revoked_external_engine_connection_still_moves()
    {
        await using var db = await CreateDatabaseAsync();
        var cloud = await SeedAccountAsync(db, CloudIssuer, "cloud-user");
        var hub = await SeedAccountAsync(db, HubIssuer, "hub-user", ExternalIdentityKinds.Hub);
        await SeedExternalEngineConnectionAsync(db, hub, ExternalEngineConnectionStatus.Revoked);
        var store = new AccountIdentityLinkStore(db);

        var result = await store.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user"));

        Assert.Equal(AccountIdentityLinkOutcome.Linked, result.Outcome);
        Assert.Equal(cloud.Id, (await db.ExternalIdentities.SingleAsync(x => x.Subject == "hub-user")).AccountId);
        var audit = Assert.Single(await db.AccountIdentityLinkAuditRecords.ToListAsync());
        Assert.Equal(hub.Id, audit.SourceAccountId);
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
        Assert.Null(unlink.ActorAccountId);
        Assert.Equal(AccountIdentityLinkActorKind.Operator, unlink.ActorKind);
        Assert.Equal("sha256:operator", unlink.OperatorSubject);
        Assert.DoesNotContain("@", unlink.OperatorSubject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hub_account_with_other_org_memberships_is_refused_and_is_not_migrated()
    {
        await using var db = await CreateDatabaseAsync();
        var cloud = await SeedAccountAsync(db, CloudIssuer, "cloud-user");
        var hub = await SeedAccountAsync(db, HubIssuer, "hub-user", ExternalIdentityKinds.Hub);
        await SeedExtraOrganizationMembershipAsync(db, hub);
        var store = new AccountIdentityLinkStore(db);

        var result = await store.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user"));

        Assert.Equal(AccountIdentityLinkOutcome.HubAccountHasState, result.Outcome);
        Assert.Empty(await db.AccountIdentityLinkAuditRecords.ToListAsync());
        Assert.Equal(hub.Id, (await db.ExternalIdentities.SingleAsync(x => x.Subject == "hub-user")).AccountId);
        Assert.DoesNotContain(await db.ExternalIdentities.ToListAsync(), identity =>
            identity.AccountId == cloud.Id && identity.Kind == ExternalIdentityKinds.Hub);
    }

    [Fact]
    public async Task Hub_account_with_a_hub_sourced_subscription_is_refused_and_is_not_migrated()
    {
        await using var db = await CreateDatabaseAsync();
        var cloud = await SeedAccountAsync(db, CloudIssuer, "cloud-user");
        var hub = await SeedAccountAsync(db, HubIssuer, "hub-user", ExternalIdentityKinds.Hub);
        await SeedHubSourcedSubscriptionAsync(db, hub);
        var store = new AccountIdentityLinkStore(db);

        var result = await store.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user"));

        Assert.Equal(AccountIdentityLinkOutcome.HubAccountHasState, result.Outcome);
        Assert.Empty(await db.AccountIdentityLinkAuditRecords.ToListAsync());
        Assert.Equal(hub.Id, (await db.ExternalIdentities.SingleAsync(x => x.Subject == "hub-user")).AccountId);
        Assert.DoesNotContain(await db.ExternalIdentities.ToListAsync(), identity =>
            identity.AccountId == cloud.Id && identity.Kind == ExternalIdentityKinds.Hub);
    }

    [Fact]
    public async Task Concurrent_links_do_not_throw_and_leave_one_hub_identity()
    {
        await using var shared = await SharedLinkDatabase.CreateAsync();
        var cloud = await SeedAccountAsync(shared.Anchor, CloudIssuer, "cloud-user");
        var first = new AccountIdentityLinkStore(shared.Open());
        var second = new AccountIdentityLinkStore(shared.Open());

        var results = await Task.WhenAll(
            first.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user")),
            second.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user")));

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Contains(results, result => result.Outcome is AccountIdentityLinkOutcome.Linked or AccountIdentityLinkOutcome.AlreadyLinked);
        Assert.All(results, result => Assert.Equal(cloud.Id, result.AccountId));
        shared.Anchor.ChangeTracker.Clear();
        Assert.Equal(cloud.Id, (await shared.Anchor.ExternalIdentities.SingleAsync(x => x.Subject == "hub-user")).AccountId);
        Assert.Single(await shared.Anchor.AccountIdentityLinkAuditRecords.ToListAsync());
    }

    [Fact]
    public async Task Unique_violation_remap_returns_already_linked_when_the_same_pair_won()
    {
        await using var db = await CreateDatabaseAsync();
        var cloud = await SeedAccountAsync(db, CloudIssuer, "cloud-user");
        var store = new AccountIdentityLinkStore(db);
        await store.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user"));

        var remapped = await store.MapUniqueViolationAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user"), CancellationToken.None);

        Assert.Equal(AccountIdentityLinkOutcome.AlreadyLinked, remapped.Outcome);
        Assert.Equal(cloud.Id, remapped.AccountId);
    }

    [Fact]
    public async Task Unique_violation_remap_returns_account_already_linked_for_a_different_hub()
    {
        await using var db = await CreateDatabaseAsync();
        var cloud = await SeedAccountAsync(db, CloudIssuer, "cloud-user");
        var store = new AccountIdentityLinkStore(db);
        await store.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user"));

        var remapped = await store.MapUniqueViolationAsync(CloudIdentity("cloud-user"), HubIdentity("other-hub"), CancellationToken.None);

        Assert.Equal(AccountIdentityLinkOutcome.AccountAlreadyLinked, remapped.Outcome);
        Assert.Single(await db.ExternalIdentities.Where(identity => identity.Kind == ExternalIdentityKinds.Hub).ToListAsync());
    }

    [Fact]
    public async Task Unique_violation_remap_returns_account_already_linked_when_hub_is_on_another_cloud_account()
    {
        await using var db = await CreateDatabaseAsync();
        var first = await SeedAccountAsync(db, CloudIssuer, "cloud-user");
        await SeedAccountAsync(db, CloudIssuer, "other-cloud");
        var store = new AccountIdentityLinkStore(db);
        await store.LinkAsync(CloudIdentity("cloud-user"), HubIdentity("hub-user"));

        var remapped = await store.MapUniqueViolationAsync(CloudIdentity("other-cloud"), HubIdentity("hub-user"), CancellationToken.None);

        Assert.Equal(AccountIdentityLinkOutcome.AccountAlreadyLinked, remapped.Outcome);
        Assert.Equal(first.Id, (await db.ExternalIdentities.SingleAsync(x => x.Subject == "hub-user")).AccountId);
        Assert.Single(await db.AccountIdentityLinkAuditRecords.ToListAsync());
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

    private static async Task SeedExtraWorkspaceMembershipAsync(CatalogDbContext db, Account account)
    {
        var organizationId = await db.OrganizationMemberships
            .Where(membership => membership.AccountId == account.Id)
            .Select(membership => membership.OrganizationId)
            .SingleAsync();
        var workspace = new Workspace
        {
            OrganizationId = organizationId,
            Name = "extra-workspace",
            Kind = WorkspaceKind.Shared
        };
        db.Workspaces.Add(workspace);
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            AccountId = account.Id,
            Workspace = workspace,
            Role = WorkspaceRole.Owner
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task SeedExtraOrganizationMembershipAsync(CatalogDbContext db, Account account)
    {
        var organization = new Organization { Name = "extra-org", CreatedByAccountId = account.Id };
        db.Organizations.Add(organization);
        db.OrganizationMemberships.Add(new OrganizationMembership
        {
            AccountId = account.Id,
            Organization = organization,
            Role = OrganizationRole.Member
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task SeedExternalEngineConnectionAsync(
        CatalogDbContext db,
        Account account,
        ExternalEngineConnectionStatus status)
    {
        var membership = await db.WorkspaceMemberships.AsNoTracking().SingleAsync(x => x.AccountId == account.Id);
        var organizationId = await db.Workspaces.Where(x => x.Id == membership.WorkspaceId).Select(x => x.OrganizationId).SingleAsync();
        var now = DateTimeOffset.UtcNow;
        db.ExternalEngineConnections.Add(new ExternalEngineConnectionEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            WorkspaceId = membership.WorkspaceId,
            DisplayName = "Hub connection",
            OwnershipMode = ExternalEngineConnection.OwnershipMode,
            Status = status.ToString(),
            RuntimeHealth = ExternalEngineRuntimeHealth.Unknown.ToString(),
            ConnectorReachability = ExternalEngineConnectorReachability.Unknown.ToString(),
            ReleaseEvidenceLevel = ExternalEngineReleaseEvidenceLevel.None.ToString(),
            ConnectorCompatibilityStatus = ExternalEngineConnectorCompatibilityStatus.Unknown.ToString(),
            IdempotencyKey = $"hub-connection-{account.Id:N}",
            CreateRequestDigest = "digest",
            CreatedAt = now,
            UpdatedAt = now,
            RevokedAt = status == ExternalEngineConnectionStatus.Revoked ? now : null,
            Version = 1
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task SeedHubSourcedSubscriptionAsync(CatalogDbContext db, Account account)
    {
        var organizationId = await db.OrganizationMemberships
            .Where(membership => membership.AccountId == account.Id)
            .Select(membership => membership.OrganizationId)
            .SingleAsync();
        var subscription = OrganizationSubscriptionLifecycle.CreateTrial(
            organizationId,
            BillingProviderNames.Stripe,
            DateTimeOffset.UtcNow);
        subscription.WebhookSource = BillingWebhookSources.Hub;
        db.OrganizationSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static TrustedWorkspaceIdentity CloudIdentity(string subject) =>
        new(CloudIssuer, subject, subject, $"{subject}@example.test");

    private static TrustedWorkspaceIdentity HubIdentity(string subject) =>
        new(HubIssuer, subject, subject, $"{subject}@example.test") { Kind = ExternalIdentityKinds.Hub };

    private sealed class SharedLinkDatabase : IAsyncDisposable
    {
        private readonly string _connectionString =
            $"Data Source=file:account-link-{Guid.NewGuid():N}?mode=memory&cache=shared";
        private readonly List<IAsyncDisposable> _resources = [];

        public CatalogDbContext Anchor { get; private set; } = null!;

        public static async Task<SharedLinkDatabase> CreateAsync()
        {
            var database = new SharedLinkDatabase();
            database.Anchor = database.Open();
            await database.Anchor.Database.EnsureCreatedAsync();
            return database;
        }

        public CatalogDbContext Open()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            _resources.Add(connection);
            var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
                .UseRetryingSqlite(connection)
                .Options);
            _resources.Add(db);
            return db;
        }

        public async ValueTask DisposeAsync()
        {
            for (var index = _resources.Count - 1; index >= 0; index--)
                await _resources[index].DisposeAsync();
        }
    }

}
