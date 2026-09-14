using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class OrganizationAzureBoundEntitlementPersistenceTests : IAsyncLifetime
{
    private const string Reason = "Guided Azure design partner";
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrganizationId = Guid.Parse("abababab-abab-abab-abab-abababababab");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private CatalogDbContext _db = null!;
    private OrganizationBillingStore _store = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = CreateDb(_connection);
        await _db.Database.EnsureCreatedAsync();
        _db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Azure design partner" });
        await _db.SaveChangesAsync();
        _store = new OrganizationBillingStore(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Mint_requires_an_active_bind_and_writes_nothing_when_it_is_missing()
    {
        var result = await MintAsync();

        Assert.Equal(OrganizationAzureBoundEntitlementOutcome.BindingRequired, result.Outcome);
        Assert.Empty(await _db.OrganizationSubscriptions.ToListAsync());
        Assert.Empty(await _db.OrganizationEntitlementSnapshots.ToListAsync());
        Assert.Empty(await _db.OrganizationAuditRecords.ToListAsync());
    }

    [Theory]
    [InlineData(OrganizationAzureSubscriptionBindState.PendingConsent)]
    [InlineData(OrganizationAzureSubscriptionBindState.Verifying)]
    [InlineData(OrganizationAzureSubscriptionBindState.Degraded)]
    [InlineData(OrganizationAzureSubscriptionBindState.Unbound)]
    public async Task Mint_fails_closed_for_every_non_active_bind(OrganizationAzureSubscriptionBindState state)
    {
        await AddBindAsync(state);

        Assert.Equal(OrganizationAzureBoundEntitlementOutcome.BindingRequired, (await MintAsync()).Outcome);
        Assert.Empty(await _db.OrganizationSubscriptions.ToListAsync());
    }

    [Fact]
    public async Task Active_bind_with_a_blank_subscription_id_fails_closed()
    {
        await AddBindAsync(OrganizationAzureSubscriptionBindState.Active, "   ");

        Assert.Equal(OrganizationAzureBoundEntitlementOutcome.BindingRequired, (await MintAsync()).Outcome);
        Assert.Empty(await _db.OrganizationSubscriptions.ToListAsync());
    }

    [Fact]
    public async Task Mint_creates_an_active_azure_bound_subscription_and_owned_projection()
    {
        await AddBindAsync(OrganizationAzureSubscriptionBindState.Active);

        var result = await MintAsync(maxInstances: 1, expiresAt: null);

        Assert.Equal(OrganizationAzureBoundEntitlementOutcome.Minted, result.Outcome);
        Assert.Equal(
            new OrganizationAzureBoundEntitlementStatus(
                OrganizationId,
                OrganizationAzureBoundEntitlementState.Active,
                1,
                null,
                Now),
            result.Status);
        var subscription = await _db.OrganizationSubscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(BillingProviderNames.AzureBound, subscription.Provider);
        Assert.Equal(OrganizationSubscriptionState.Active, subscription.State);
        Assert.Null(subscription.ProviderCustomerReference);
        Assert.Null(subscription.ProviderSubscriptionReference);
        var entitlement = await _db.OrganizationEntitlementSnapshots.AsNoTracking().SingleAsync();
        Assert.True(entitlement.ManagedHostingEnabled);
        Assert.Equal(1, entitlement.MaxInstances);
        Assert.Null(entitlement.ManagedHostingExpiresAt);
        Assert.Equal(subscription.Id, entitlement.SubscriptionId);
        var audit = await _db.OrganizationAuditRecords.AsNoTracking().SingleAsync();
        Assert.Equal("azure-bound-entitlement", audit.TargetType);
        Assert.Contains("max instances 1, expires none", audit.Summary, StringComparison.Ordinal);
        Assert.NotNull(audit.OperatorSubject);
    }

    [Theory]
    [InlineData("stripe", OrganizationSubscriptionState.Active)]
    [InlineData("internal", OrganizationSubscriptionState.Active)]
    [InlineData("stripe", OrganizationSubscriptionState.Constrained)]
    [InlineData("internal", OrganizationSubscriptionState.Suspended)]
    public async Task Non_terminal_commercial_state_refuses_azure_bound_mint(
        string provider,
        OrganizationSubscriptionState state)
    {
        await AddBindAsync(OrganizationAzureSubscriptionBindState.Active);
        await AddSubscriptionAsync(provider, state);

        var result = await MintAsync();

        Assert.Equal(OrganizationAzureBoundEntitlementOutcome.CommercialSubscriptionExists, result.Outcome);
        Assert.Equal(provider, (await _db.OrganizationSubscriptions.AsNoTracking().SingleAsync()).Provider);
        Assert.Empty(await _db.OrganizationAuditRecords.ToListAsync());
    }

    [Fact]
    public async Task Active_azure_bound_subscription_refuses_a_second_mint()
    {
        await AddBindAsync(OrganizationAzureSubscriptionBindState.Active);
        await AddSubscriptionAsync(BillingProviderNames.AzureBound, OrganizationSubscriptionState.Active);

        var result = await MintAsync();

        Assert.Equal(OrganizationAzureBoundEntitlementOutcome.AlreadyMinted, result.Outcome);
        Assert.Empty(await _db.OrganizationAuditRecords.ToListAsync());
    }

    [Theory]
    [InlineData("stripe", OrganizationSubscriptionState.Retained)]
    [InlineData("internal", OrganizationSubscriptionState.Deleted)]
    public async Task Terminal_prior_provider_state_can_be_replaced_while_preserving_history(
        string provider,
        OrganizationSubscriptionState state)
    {
        await AddBindAsync(OrganizationAzureSubscriptionBindState.Active);
        var prior = await AddSubscriptionAsync(provider, state);

        var result = await MintAsync(maxInstances: 3, expiresAt: Now.AddDays(30));

        Assert.Equal(OrganizationAzureBoundEntitlementOutcome.Minted, result.Outcome);
        var subscriptions = await _db.OrganizationSubscriptions.AsNoTracking().ToListAsync();
        Assert.Equal(2, subscriptions.Count);
        var subscription = Assert.Single(subscriptions, x => x.Provider == BillingProviderNames.AzureBound);
        Assert.NotEqual(prior.Id, subscription.Id);
        Assert.Equal(BillingProviderNames.AzureBound, subscription.Provider);
        Assert.Equal(OrganizationSubscriptionState.Active, subscription.State);
        Assert.Equal(3, (await _db.OrganizationEntitlementSnapshots.AsNoTracking().SingleAsync()).MaxInstances);
    }

    [Fact]
    public async Task Entitlement_and_bind_are_both_required_by_the_commercial_gate()
    {
        var subscription = await AddSubscriptionAsync(BillingProviderNames.AzureBound, OrganizationSubscriptionState.Active);
        var entitlement = await _db.OrganizationEntitlementSnapshots.SingleAsync();
        entitlement.ManagedHostingEnabled = true;
        entitlement.MaxInstances = 1;
        await _db.SaveChangesAsync();
        var gate = new EfCoreElsaInstanceCommercialGate(_db, new FixedTimeProvider(Now));

        var entitlementOnly = await gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Create, 0);
        Assert.False(entitlementOnly.Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.BindingRequired, entitlementOnly.Code);

        await AddBindAsync(OrganizationAzureSubscriptionBindState.Active);
        _db.ChangeTracker.Clear();
        Assert.True((await gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Create, 0)).Allowed);

        _db.OrganizationEntitlementSnapshots.Remove(await _db.OrganizationEntitlementSnapshots.SingleAsync());
        await _db.SaveChangesAsync();
        var bindOnly = await gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Create, 0);
        Assert.False(bindOnly.Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.EntitlementRequired, bindOnly.Code);
        Assert.NotEqual(Guid.Empty, subscription.Id);
    }

    [Fact]
    public async Task Revoke_tombstones_the_azure_bound_subscription_and_allows_a_new_checkout_trial()
    {
        await AddBindAsync(OrganizationAzureSubscriptionBindState.Active);
        await MintAsync(expiresAt: Now.AddDays(30));

        var revoked = await _store.RevokeAzureBoundEntitlementAsync(OrganizationId, "operator", Now.AddHours(1));
        var repeated = await _store.RevokeAzureBoundEntitlementAsync(OrganizationId, "operator", Now.AddHours(2));

        Assert.Equal(OrganizationAzureBoundEntitlementOutcome.Revoked, revoked.Outcome);
        Assert.Equal(OrganizationAzureBoundEntitlementOutcome.Unchanged, repeated.Outcome);
        var gate = new EfCoreElsaInstanceCommercialGate(_db, new FixedTimeProvider(Now.AddHours(2)));
        Assert.Equal(
            ElsaInstanceCommercialOperation.EntitlementRequired,
            (await gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Create, 0)).Code);
        Assert.True((await gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Stop)).Allowed);
        Assert.True((await gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Delete)).Allowed);
        var trial = await _store.StartTrialAsync(OrganizationId, BillingProviderNames.Stripe, Now.AddHours(3));
        Assert.Equal(BillingEventConsumptionOutcome.Applied, trial.Outcome);
        Assert.Equal(BillingProviderNames.Stripe, trial.Subscription!.Provider);
        Assert.Equal(OrganizationSubscriptionState.Trial, trial.Subscription.State);
        var subscriptions = await _db.OrganizationSubscriptions.AsNoTracking()
            .Where(x => x.OrganizationId == OrganizationId)
            .OrderBy(x => x.Provider)
            .ToListAsync();
        Assert.Equal(2, subscriptions.Count);
        Assert.Equal(OrganizationSubscriptionState.Deleted, Assert.Single(subscriptions, x => x.Provider == BillingProviderNames.AzureBound).State);
    }

    [Fact]
    public async Task Prior_provider_cleanup_cannot_tombstone_a_replacement_azure_bound_subscription()
    {
        await AddBindAsync(OrganizationAzureSubscriptionBindState.Active);
        var subscription = await AddSubscriptionAsync(BillingProviderNames.Stripe, OrganizationSubscriptionState.Retained);
        var cleanup = new OrganizationBillingCleanup
        {
            OrganizationId = OrganizationId,
            SubscriptionId = subscription.Id,
            CleanupKey = "cleanup-prior-stripe",
            Provider = BillingProviderNames.Stripe,
            State = OrganizationBillingCleanupState.InProgress,
            RequestedAt = Now.AddHours(-2),
            NotBeforeAt = Now.AddHours(-2),
            LastAttemptAt = Now.AddHours(-1),
            AttemptCount = 1,
            LeaseOwner = "worker",
            LeaseToken = "lease-token",
            LeaseExpiresAt = Now.AddHours(1)
        };
        _db.OrganizationBillingCleanups.Add(cleanup);
        await _db.SaveChangesAsync();

        Assert.Equal(OrganizationAzureBoundEntitlementOutcome.Minted, (await MintAsync()).Outcome);
        _db.ChangeTracker.Clear();
        var completion = await _store.CompleteCleanupAsync(new(
            cleanup.Id,
            OrganizationId,
            subscription.Id,
            "lease-token",
            OrganizationBillingCleanupOutcome.ConfirmedAbsent,
            Now.AddMinutes(1)));

        Assert.True(completion.SubscriptionDeleted);
        var prior = await _db.OrganizationSubscriptions.AsNoTracking()
            .SingleAsync(x => x.Id == subscription.Id);
        Assert.Equal(OrganizationSubscriptionState.Deleted, prior.State);
        var current = await _db.OrganizationSubscriptions.AsNoTracking()
            .SingleAsync(x => x.Provider == BillingProviderNames.AzureBound);
        Assert.Equal(BillingProviderNames.AzureBound, current.Provider);
        Assert.Equal(OrganizationSubscriptionState.Active, current.State);
        var entitlement = await _db.OrganizationEntitlementSnapshots.AsNoTracking().SingleAsync();
        Assert.Equal(current.Id, entitlement.SubscriptionId);
        Assert.True(entitlement.ManagedHostingEnabled);
        var gate = new EfCoreElsaInstanceCommercialGate(_db, new FixedTimeProvider(Now.AddMinutes(1)));
        Assert.True((await gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Create, 0)).Allowed);
    }

    [Fact]
    public async Task Historical_provider_event_replay_does_not_return_the_replacement_provider()
    {
        await AddBindAsync(OrganizationAzureSubscriptionBindState.Active);
        var prior = await AddSubscriptionAsync(BillingProviderNames.Stripe, OrganizationSubscriptionState.Retained);
        const string eventId = "evt-historical-stripe";
        const string eventHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        prior.LastProviderEventId = eventId;
        prior.LastProviderEventOccurredAt = Now.AddDays(-1);
        _db.BillingProviderEvents.Add(new BillingProviderEventInboxEntry
        {
            OrganizationId = OrganizationId,
            Provider = BillingProviderNames.Stripe,
            ProviderEventId = eventId,
            EventType = "subscription.retained",
            State = OrganizationSubscriptionState.Retained,
            EventHash = eventHash,
            OccurredAt = Now.AddDays(-1),
            ReceivedAt = Now.AddDays(-1),
            ProcessedAt = Now.AddDays(-1),
            ProcessingStatus = BillingProviderEventProcessingStatus.Applied
        });
        await _db.SaveChangesAsync();
        Assert.Equal(OrganizationAzureBoundEntitlementOutcome.Minted, (await MintAsync()).Outcome);
        _db.ChangeTracker.Clear();

        var replay = await _store.ConsumeAsync(
            new BillingProviderEvent(
                OrganizationId,
                BillingProviderNames.Stripe,
                eventId,
                "subscription.retained",
                OrganizationSubscriptionState.Retained,
                Now.AddDays(-1),
                eventHash),
            Now.AddMinutes(1));

        Assert.Equal(BillingEventConsumptionOutcome.Replayed, replay.Outcome);
        Assert.Equal(prior.Id, replay.Subscription!.Id);
        Assert.Equal(BillingProviderNames.Stripe, replay.Subscription.Provider);
    }

    [Fact]
    public async Task First_seen_provider_event_uses_the_matching_historical_provider_after_replacement()
    {
        await AddBindAsync(OrganizationAzureSubscriptionBindState.Active);
        var prior = await AddSubscriptionAsync(BillingProviderNames.Stripe, OrganizationSubscriptionState.Retained);
        Assert.Equal(OrganizationAzureBoundEntitlementOutcome.Minted, (await MintAsync()).Outcome);
        _db.ChangeTracker.Clear();

        var applied = await _store.ConsumeAsync(
            new BillingProviderEvent(
                OrganizationId,
                BillingProviderNames.Stripe,
                "evt-late-stripe-delete",
                "subscription.deleted",
                OrganizationSubscriptionState.Deleted,
                Now.AddMinutes(1),
                "sha256:" + new string('b', 64)),
            Now.AddMinutes(2));

        Assert.Equal(BillingEventConsumptionOutcome.Applied, applied.Outcome);
        Assert.Equal(prior.Id, applied.Subscription!.Id);
        Assert.Equal(BillingProviderNames.Stripe, applied.Subscription.Provider);
        Assert.Equal(OrganizationSubscriptionState.Deleted, applied.Subscription.State);
    }

    private Task<OrganizationAzureBoundEntitlementResult> MintAsync(
        int maxInstances = 1,
        DateTimeOffset? expiresAt = null) =>
        _store.MintAzureBoundEntitlementAsync(
            new(
                OrganizationId,
                new OrganizationAzureBoundEntitlementTerms(Reason, maxInstances, expiresAt),
                "operator"),
            Now);

    private async Task AddBindAsync(OrganizationAzureSubscriptionBindState state, string? subscriptionId = null)
    {
        _db.OrganizationAzureSubscriptionBinds.Add(new OrganizationAzureSubscriptionBind
        {
            OrganizationId = OrganizationId,
            CustomerTenantId = Guid.NewGuid().ToString("D"),
            SubscriptionId = subscriptionId ?? Guid.NewGuid().ToString("D"),
            ManagingTenantId = Guid.NewGuid().ToString("D"),
            ManagingPrincipalObjectId = Guid.NewGuid().ToString("D"),
            ManagingPrincipalClientId = Guid.NewGuid().ToString("D"),
            RegistrationDefinitionId = "/providers/Microsoft.ManagedServices/registrationDefinitions/" + Guid.NewGuid().ToString("D"),
            State = state,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await _db.SaveChangesAsync();
    }

    private async Task<OrganizationSubscription> AddSubscriptionAsync(
        string provider,
        OrganizationSubscriptionState state)
    {
        var subscription = OrganizationSubscriptionLifecycle.CreateTrial(OrganizationId, provider, Now.AddDays(-30));
        subscription.State = state;
        subscription.UpdatedAt = Now.AddDays(-1);
        _db.OrganizationSubscriptions.Add(subscription);
        _db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = OrganizationId,
            SubscriptionId = subscription.Id,
            SubscriptionState = state,
            ManagedHostingEnabled = false,
            MaxInstances = int.MaxValue,
            CreatedAt = Now.AddDays(-30),
            UpdatedAt = Now.AddDays(-1),
            SyncedAt = Now.AddDays(-1)
        });
        await _db.SaveChangesAsync();
        return subscription;
    }

    private static CatalogDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(connection).Options);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
