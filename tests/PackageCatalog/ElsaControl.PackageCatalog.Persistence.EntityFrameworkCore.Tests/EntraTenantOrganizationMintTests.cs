using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

/// <summary>
/// A customer Microsoft Entra tenant maps to exactly one organization: the tenant's first sign-in
/// mints it with the binder as owner, every later account from the tenant joins it, and no
/// sign-in merges into an organization that belongs to a different identity.
/// </summary>
public sealed class EntraTenantOrganizationMintTests : IAsyncDisposable
{
    private const string TenantId = "0b6f5c4e-2d8a-4f3b-9c1e-7a5d3e2f1b0c";
    private const string TenantIssuer = $"https://login.microsoftonline.com/{TenantId}/v2.0";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CatalogDbContext _db;
    private readonly AccountWorkspaceService _service;

    public EntraTenantOrganizationMintTests()
    {
        _connection.Open();
        _db = CreateDbContext();
        _db.Database.EnsureCreated();
        _service = new AccountWorkspaceService(new AccountWorkspaceStore(_db));
    }

    [Fact]
    public async Task First_tenant_sign_in_mints_a_tenant_bound_organization_owned_by_the_binder()
    {
        var context = await _service.GetOrCreateAsync(TenantIdentity("binder-oid"));

        var organization = await _db.Organizations.AsNoTracking().SingleAsync();
        var binding = await _db.OrganizationIdentityBindings.AsNoTracking().SingleAsync();
        var workspace = Assert.Single(context.Workspaces);
        Assert.Equal((organization.Id, TenantId), (binding.OrganizationId, binding.EntraTenantId));
        Assert.Equal("contoso.example", organization.Name);
        Assert.Equal(new OrganizationSummary(organization.Id, organization.Name, OrganizationRole.Owner), Assert.Single(context.Organizations));
        Assert.Equal((WorkspaceKind.Shared, WorkspaceRole.Owner, organization.Id), (workspace.Kind, workspace.Role, workspace.OrganizationId));
    }

    [Fact]
    public async Task Later_tenant_sign_ins_join_the_tenant_organization_as_members_without_workspace_access()
    {
        var binder = await _service.GetOrCreateAsync(TenantIdentity("binder-oid"));

        var member = await _service.GetOrCreateAsync(TenantIdentity("member-oid"));
        var memberAgain = await _service.GetOrCreateAsync(TenantIdentity("member-oid"));

        Assert.Equal(1, await _db.Organizations.CountAsync());
        Assert.Equal(new OrganizationSummary(binder.Organizations.Single().Id, "contoso.example", OrganizationRole.Member), Assert.Single(member.Organizations));
        Assert.Empty(member.Workspaces);
        Assert.Equal(member.Organizations, memberAgain.Organizations);
    }

    [Fact]
    public async Task Store_refuses_a_second_organization_for_the_same_tenant()
    {
        _db.OrganizationIdentityBindings.AddRange(
            new OrganizationIdentityBinding { Organization = new Organization { Name = "First" }, EntraTenantId = TenantId },
            new OrganizationIdentityBinding { Organization = new Organization { Name = "Second" }, EntraTenantId = TenantId });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task First_sign_in_that_loses_a_concurrent_tenant_mint_joins_the_winner()
    {
        await using var db = CreateDbContext(new ConcurrentTenantMint(() => CreateDbContext()));
        var service = new AccountWorkspaceService(new AccountWorkspaceStore(db));

        var context = await service.GetOrCreateAsync(TenantIdentity("late-oid"));

        var organization = await _db.Organizations.AsNoTracking().SingleAsync();
        Assert.Equal(new OrganizationSummary(organization.Id, organization.Name, OrganizationRole.Member), Assert.Single(context.Organizations));
    }

    [Fact]
    public async Task Account_is_keyed_by_tenant_issuer_and_object_id_never_by_display_claims()
    {
        var first = await _service.GetOrCreateAsync(TenantIdentity("binder-oid"));

        var renamed = await _service.GetOrCreateAsync(TenantIdentity("binder-oid", "ada.lovelace@fabrikam.example", "Ada Lovelace"));

        var identity = await _db.ExternalIdentities.AsNoTracking().SingleAsync();
        Assert.Equal((TenantIssuer, "binder-oid"), (identity.Issuer, identity.Subject));
        Assert.Equal(first.Account.Id, renamed.Account.Id);
        Assert.Equal(first.Organizations, renamed.Organizations);
    }

    [Fact]
    public async Task Tenant_sign_in_never_merges_into_a_stripe_organization_of_the_same_person()
    {
        var stripeOwner = await _service.GetOrCreateAsync(new TrustedWorkspaceIdentity("https://identity.example", "stripe-sub", "Ada", "ada@contoso.example"));
        var stripeOrganizationId = stripeOwner.Organizations.Single().Id;
        var now = DateTimeOffset.UtcNow;
        _db.OrganizationSubscriptions.Add(new OrganizationSubscription
        {
            OrganizationId = stripeOrganizationId,
            Provider = BillingProviderNames.Stripe,
            TrialStartedAt = now,
            TrialEndsAt = now.AddDays(14),
            LastProviderEventOccurredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var tenant = await _service.GetOrCreateAsync(TenantIdentity("binder-oid"));

        Assert.NotEqual(stripeOwner.Account.Id, tenant.Account.Id);
        Assert.DoesNotContain(tenant.Organizations, x => x.Id == stripeOrganizationId);
        Assert.Equal(1, await _db.OrganizationMemberships.CountAsync(x => x.OrganizationId == stripeOrganizationId));
        Assert.False(await _db.OrganizationIdentityBindings.AnyAsync(x => x.OrganizationId == stripeOrganizationId));
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private CatalogDbContext CreateDbContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(_connection)
            .AddInterceptors(interceptors)
            .Options);

    private static TrustedWorkspaceIdentity TenantIdentity(
        string objectId,
        string email = "ada@contoso.example",
        string displayName = "Ada") =>
        new(TenantIssuer, objectId, displayName, email) { CustomerEntraTenantId = TenantId };

    /// <summary>
    /// Lands another binder's first sign-in between this sign-in's tenant lookup and its save.
    /// </summary>
    private sealed class ConcurrentTenantMint(Func<CatalogDbContext> createDbContext) : SaveChangesInterceptor
    {
        private bool _minted;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_minted)
                return result;

            _minted = true;
            await using var winner = createDbContext();
            await new AccountWorkspaceService(new AccountWorkspaceStore(winner))
                .GetOrCreateAsync(TenantIdentity("winner-oid"), cancellationToken);
            return result;
        }
    }
}
