using System.Diagnostics;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class StripeEngineQuantityPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrganizationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Theory]
    [InlineData(null, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public async Task Active_cloud_quantity_projects_max_instances(int? quantity, int expected)
    {
        await using var db = await SeedOrganizationAsync();
        var result = await new OrganizationBillingStore(db).ConsumeAsync(
            Event("evt-qty", OrganizationSubscriptionState.Active, Now.AddMinutes(1), quantity),
            Now.AddMinutes(2));

        Assert.Equal(BillingEventConsumptionOutcome.Applied, result.Outcome);
        Assert.Equal(expected, result.Entitlement!.MaxInstances);
        Assert.True(result.Entitlement.ManagedHostingEnabled);
        Assert.Equal(expected, (await db.OrganizationEntitlementSnapshots.SingleAsync()).MaxInstances);
    }

    [Fact]
    public async Task Trial_stays_at_one_engine_even_when_the_item_quantity_is_higher()
    {
        await using var db = await SeedOrganizationAsync(linkHub: true);
        var result = await new OrganizationBillingStore(db).ConsumeAsync(
            Event("evt-trial-qty", OrganizationSubscriptionState.Trial, Now.AddMinutes(1), 2) with
            {
                WebhookSource = BillingWebhookSources.Hub
            },
            Now.AddMinutes(2));

        Assert.Equal(BillingEventConsumptionOutcome.Applied, result.Outcome);
        Assert.Equal(1, result.Entitlement!.MaxInstances);
        Assert.Equal(result.Subscription!.TrialEndsAt, result.Entitlement.ManagedHostingExpiresAt);
    }

    [Fact]
    public async Task Quantity_five_is_rejected_and_does_not_raise_or_clamp_the_allowance()
    {
        await using var db = await SeedOrganizationAsync();
        var store = new OrganizationBillingStore(db);
        await store.ConsumeAsync(Event("evt-active", OrganizationSubscriptionState.Active, Now.AddMinutes(1), 2), Now.AddMinutes(2));

        var rejected = await store.ConsumeAsync(
            Event("evt-qty-5", OrganizationSubscriptionState.Active, Now.AddMinutes(3), 5),
            Now.AddMinutes(4));

        Assert.Equal(BillingEventConsumptionOutcome.Applied, rejected.Outcome);
        Assert.Equal(2, rejected.Entitlement!.MaxInstances);
        var alert = Assert.Single(await db.OrganizationBillingOperatorAlertOutbox.ToListAsync());
        Assert.Equal(OrganizationBillingOperatorAlert.QuantityOutOfRangeKind, alert.Kind);
        Assert.Equal(5, alert.Quantity);
        Assert.Equal(OrganizationBillingOperatorAlert.QuantityOutOfRangeSummary, alert.Summary);
        Assert.DoesNotContain("@", alert.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("cus_", alert.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, (await db.OrganizationEntitlementSnapshots.SingleAsync()).MaxInstances);
    }

    [Fact]
    public async Task Hub_trial_replaces_an_internal_grant_and_stays_at_one_engine()
    {
        await using var db = await SeedOrganizationAsync(linkHub: true);
        var store = new OrganizationBillingStore(db);
        Assert.Equal(
            OrganizationInternalEntitlementOutcome.Granted,
            (await store.GrantInternalEntitlementAsync(
                new(OrganizationId, new("Internal dogfood", 3, Now.AddDays(30)), "operator"),
                Now)).Outcome);

        var trial = await store.ConsumeAsync(
            Event("evt-hub-trial", OrganizationSubscriptionState.Trial, Now.AddMinutes(1), 2, BillingWebhookSources.Hub),
            Now.AddMinutes(2));

        Assert.Equal(BillingEventConsumptionOutcome.Applied, trial.Outcome);
        Assert.Equal(BillingProviderNames.Stripe, trial.Subscription!.Provider);
        Assert.Equal(OrganizationSubscriptionState.Trial, trial.Subscription.State);
        Assert.Equal(1, trial.Entitlement!.MaxInstances);
        Assert.DoesNotContain(
            await db.OrganizationSubscriptions.ToListAsync(),
            x => x.Provider == BillingProviderNames.Internal && OrganizationSubscriptionLifecycle.IsLive(x.State));
    }

    [Fact]
    public async Task Later_event_without_quantity_keeps_the_projected_allowance()
    {
        await using var db = await SeedOrganizationAsync();
        var store = new OrganizationBillingStore(db);
        await store.ConsumeAsync(Event("evt-qty-2", OrganizationSubscriptionState.Active, Now.AddMinutes(1), 2), Now.AddMinutes(2));

        var kept = await store.ConsumeAsync(
            Event("evt-no-qty", OrganizationSubscriptionState.Active, Now.AddMinutes(3), quantity: null),
            Now.AddMinutes(4));

        Assert.Equal(BillingEventConsumptionOutcome.Applied, kept.Outcome);
        Assert.Equal(2, kept.Entitlement!.MaxInstances);
        Assert.Equal(2, (await db.OrganizationEntitlementSnapshots.SingleAsync()).MaxInstances);
    }

    [Fact]
    public async Task Item_on_another_price_keeps_today_s_single_engine_allowance()
    {
        await using var db = await SeedOrganizationAsync();
        var result = await new OrganizationBillingStore(db).ConsumeAsync(
            Event("evt-other-price", OrganizationSubscriptionState.Active, Now.AddMinutes(1), quantity: null),
            Now.AddMinutes(2));

        Assert.Equal(1, result.Entitlement!.MaxInstances);
    }

    [Fact]
    public async Task Older_event_that_would_lower_quantity_is_ignored()
    {
        await using var db = await SeedOrganizationAsync();
        var store = new OrganizationBillingStore(db);
        await store.ConsumeAsync(Event("evt-qty-2", OrganizationSubscriptionState.Active, Now.AddMinutes(2), 2), Now.AddMinutes(3));

        var older = await store.ConsumeAsync(
            Event("evt-qty-1-older", OrganizationSubscriptionState.Active, Now.AddMinutes(1), 1),
            Now.AddMinutes(4));

        Assert.Equal(BillingEventConsumptionOutcome.IgnoredOutOfOrder, older.Outcome);
        Assert.Equal(2, (await db.OrganizationEntitlementSnapshots.SingleAsync()).MaxInstances);
    }

    [Fact]
    public async Task Quantity_change_writes_an_audit_with_the_old_and_new_values()
    {
        await using var db = await SeedOrganizationAsync();
        var store = new OrganizationBillingStore(db);
        await store.ConsumeAsync(Event("evt-qty-1", OrganizationSubscriptionState.Active, Now.AddMinutes(1), 1), Now.AddMinutes(2));

        var updated = await store.ConsumeAsync(
            Event("evt-qty-2", OrganizationSubscriptionState.Active, Now.AddMinutes(3), 2),
            Now.AddMinutes(4));

        Assert.Equal(2, updated.Entitlement!.MaxInstances);
        var audit = Assert.Single(
            await db.OrganizationAuditRecords.Where(x => x.Action == OrganizationAuditAction.EntitlementChanged).ToListAsync());
        Assert.Equal("entitlement-snapshot", audit.TargetType);
        Assert.Equal("Managed-instance allowance changed from 1 to 2.", audit.Summary);
        Assert.DoesNotContain("cus_", audit.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sub_", audit.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(BillingWebhookSources.Control)]
    [InlineData(BillingWebhookSources.Hub)]
    public async Task Quantity_change_from_either_webhook_source_updates_max_instances(string webhookSource)
    {
        await using var db = await SeedOrganizationAsync(linkHub: webhookSource == BillingWebhookSources.Hub);
        var store = new OrganizationBillingStore(db);
        await store.ConsumeAsync(
            Event("evt-qty-1", OrganizationSubscriptionState.Active, Now.AddMinutes(1), 1, webhookSource),
            Now.AddMinutes(2));

        var updated = await store.ConsumeAsync(
            Event("evt-qty-2", OrganizationSubscriptionState.Active, Now.AddMinutes(3), 2, webhookSource),
            Now.AddMinutes(4));

        Assert.Equal(BillingEventConsumptionOutcome.Applied, updated.Outcome);
        Assert.Equal(2, updated.Entitlement!.MaxInstances);
        Assert.Equal(webhookSource, BillingWebhookSources.Normalize(updated.Subscription!.WebhookSource));
    }

    [Fact]
    public async Task Second_create_succeeds_at_quantity_two_and_the_third_is_denied()
    {
        await using var db = await SeedOrganizationAsync();
        await new OrganizationBillingStore(db).ConsumeAsync(
            Event("evt-qty-2", OrganizationSubscriptionState.Active, Now.AddMinutes(1), 2),
            Now.AddMinutes(2));
        var gate = new EfCoreElsaInstanceCommercialGate(db);

        Assert.True((await gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Create, 0)).Allowed);
        Assert.True((await gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Create, 1)).Allowed);
        var denied = await gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Create, 2);
        Assert.False(denied.Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.InstanceLimitReached, denied.Code);
        Assert.Equal(2, denied.CurrentInstanceCount);
        Assert.Equal(2, denied.MaxInstances);
    }

    [Fact]
    public async Task Quantity_drop_below_running_engines_does_not_enqueue_provider_work()
    {
        await using var db = await SeedOrganizationAsync();
        var workspace = await SeedWorkspaceAsync(db);
        await SeedDedicatedInstanceAsync(db, workspace, "engine-one");
        await SeedDedicatedInstanceAsync(db, workspace, "engine-two");
        var store = new OrganizationBillingStore(db);
        await store.ConsumeAsync(Event("evt-qty-2", OrganizationSubscriptionState.Active, Now.AddMinutes(1), 2), Now.AddMinutes(2));

        var dropped = await store.ConsumeAsync(
            Event("evt-qty-1", OrganizationSubscriptionState.Active, Now.AddMinutes(3), 1),
            Now.AddMinutes(4));

        Assert.Equal(1, dropped.Entitlement!.MaxInstances);
        Assert.Equal(2, await db.ElsaInstances.CountAsync(x => x.OrganizationId == OrganizationId && x.DeletedAt == null));
        Assert.Empty(await db.ElsaInstanceOperations.ToListAsync());
        Assert.Empty(await db.ElsaInstanceLifecycleOutbox.ToListAsync());
        Assert.Empty(await db.AzureProviderOperations.ToListAsync());
        var gate = new EfCoreElsaInstanceCommercialGate(db);
        Assert.False((await gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Create, 2)).Allowed);
        Assert.False((await gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Start, 2)).Allowed);
        Assert.False((await gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Restart, 2)).Allowed);
        Assert.True((await gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Stop)).Allowed);
        Assert.True((await gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Delete)).Allowed);
        var alert = Assert.Single(await db.OrganizationBillingOperatorAlertOutbox.ToListAsync());
        Assert.Equal(OrganizationBillingOperatorAlert.OverAllowanceKind, alert.Kind);
        Assert.Equal(1, alert.MaxInstances);
        Assert.Equal(2, alert.ActiveDedicatedCount);
        Assert.Equal(OrganizationBillingOperatorAlert.OverAllowanceSummary, alert.Summary);
    }

    [Fact]
    public async Task Over_allowance_alert_fires_once_per_org_per_transition()
    {
        await using var db = await SeedOrganizationAsync();
        var workspace = await SeedWorkspaceAsync(db);
        await SeedDedicatedInstanceAsync(db, workspace, "engine-one");
        await SeedDedicatedInstanceAsync(db, workspace, "engine-two");
        var store = new OrganizationBillingStore(db);
        await store.ConsumeAsync(Event("evt-qty-2", OrganizationSubscriptionState.Active, Now.AddMinutes(1), 2), Now.AddMinutes(2));
        await store.ConsumeAsync(Event("evt-qty-1", OrganizationSubscriptionState.Active, Now.AddMinutes(3), 1), Now.AddMinutes(4));
        await store.ConsumeAsync(Event("evt-qty-1-again", OrganizationSubscriptionState.Active, Now.AddMinutes(5), 1), Now.AddMinutes(6));

        Assert.Equal(1, await db.OrganizationBillingOperatorAlertOutbox.CountAsync());
    }

    [Fact]
    public async Task Over_allowance_alert_activity_carries_no_customer_data()
    {
        await using var db = await SeedOrganizationAsync();
        var workspace = await SeedWorkspaceAsync(db);
        await SeedDedicatedInstanceAsync(db, workspace, "engine-one");
        await SeedDedicatedInstanceAsync(db, workspace, "engine-two");
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OrganizationBillingOperatorAlert.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        var activities = new List<Activity>();
        listener.ActivityStopped = activities.Add;
        ActivitySource.AddActivityListener(listener);

        var store = new OrganizationBillingStore(db);
        await store.ConsumeAsync(Event("evt-qty-2", OrganizationSubscriptionState.Active, Now.AddMinutes(1), 2), Now.AddMinutes(2));
        await store.ConsumeAsync(Event("evt-qty-1", OrganizationSubscriptionState.Active, Now.AddMinutes(3), 1), Now.AddMinutes(4));

        var activity = Assert.Single(activities, x => x.OperationName == OrganizationBillingOperatorAlert.ActivityName);
        var tags = activity.Tags.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        Assert.Equal(OrganizationId.ToString("D"), tags[OrganizationBillingOperatorAlert.OrganizationIdTag]);
        Assert.Equal(OrganizationBillingOperatorAlert.OverAllowanceKind, tags[OrganizationBillingOperatorAlert.KindTag]);
        Assert.All(tags.Values, value =>
        {
            Assert.DoesNotContain("@", value, StringComparison.Ordinal);
            Assert.DoesNotContain("cus_", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sub_", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Acme", value, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Cancel_then_resubscribe_restores_the_new_active_quantity()
    {
        await using var db = await SeedOrganizationAsync();
        var store = new OrganizationBillingStore(db);
        await store.ConsumeAsync(Event("evt-original", OrganizationSubscriptionState.Active, Now.AddMinutes(1), 2), Now.AddMinutes(2));
        await store.ConsumeAsync(
            Event("evt-cancelled", OrganizationSubscriptionState.Suspended, Now.AddMinutes(3), 2) with
            {
                EventType = "customer.subscription.deleted"
            },
            Now.AddMinutes(4));

        var resubscribed = await store.ConsumeAsync(
            Event(OrganizationId, "evt-resubscribe", OrganizationSubscriptionState.Active, Now.AddMinutes(5), "cus_acme", "sub_paid_again", 2) with
            {
                EventType = "customer.subscription.created"
            },
            Now.AddMinutes(6));

        Assert.Equal(BillingEventConsumptionOutcome.Applied, resubscribed.Outcome);
        Assert.Equal(OrganizationSubscriptionState.Active, resubscribed.Subscription!.State);
        Assert.True(resubscribed.Entitlement!.ManagedHostingEnabled);
        Assert.Equal(2, resubscribed.Entitlement.MaxInstances);
        Assert.True((await new EfCoreElsaInstanceCommercialGate(db).EvaluateAsync(
            OrganizationId, ElsaInstanceOperationAction.Create, 1)).Allowed);
        Assert.False((await new EfCoreElsaInstanceCommercialGate(db).EvaluateAsync(
            OrganizationId, ElsaInstanceOperationAction.Create, 2)).Allowed);
    }

    private static async Task<CatalogDbContext> SeedOrganizationAsync(bool linkHub = false)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        if (linkHub)
        {
            var account = new Account { Email = "hub-qty@example.test" };
            db.Accounts.Add(account);
            db.ExternalIdentities.Add(new ExternalIdentity
            {
                Account = account,
                Issuer = "https://hub-account.test/auth/v1",
                Subject = OrganizationId.ToString("D"),
                Kind = ExternalIdentityKinds.Hub,
                Email = account.Email
            });
            db.OrganizationMemberships.Add(new OrganizationMembership
            {
                Account = account,
                OrganizationId = OrganizationId,
                Role = OrganizationRole.Owner
            });
        }

        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<Workspace> SeedWorkspaceAsync(CatalogDbContext db)
    {
        var workspace = new Workspace { OrganizationId = OrganizationId, Name = "Quantity workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        return workspace;
    }

    private static async Task SeedDedicatedInstanceAsync(CatalogDbContext db, Workspace workspace, string slug)
    {
        var instance = ElsaInstancePersistenceTests.NewInstance(workspace.OrganizationId, workspace.Id);
        instance.Slug = slug;
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();
    }

    private static BillingProviderEvent Event(
        string id,
        OrganizationSubscriptionState state,
        DateTimeOffset occurredAt,
        int? quantity = null,
        string? webhookSource = null) =>
        Event(OrganizationId, id, state, occurredAt, "cus_acme", "sub_acme", quantity) with
        {
            WebhookSource = webhookSource
        };

    private static BillingProviderEvent Event(
        Guid organizationId,
        string id,
        OrganizationSubscriptionState state,
        DateTimeOffset occurredAt,
        string customerReference,
        string subscriptionReference,
        int? quantity) =>
        new(
            organizationId,
            "stripe",
            id,
            "subscription." + state.ToString().ToLowerInvariant(),
            state,
            occurredAt,
            "sha256:" + new string('a', 64),
            customerReference,
            subscriptionReference,
            Quantity: quantity);
}
