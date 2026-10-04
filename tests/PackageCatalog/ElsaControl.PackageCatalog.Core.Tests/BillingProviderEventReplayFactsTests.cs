using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.PackageCatalog.Core.Tests;

public sealed class BillingProviderEventReplayFactsTests
{
    private static readonly Guid OrganizationId = Guid.Parse("b7e3f4d0-14d9-4b7b-87b6-6b1b05dd6d21");
    private static readonly DateTimeOffset OccurredAt = new(2026, 10, 4, 21, 39, 24, TimeSpan.Zero);

    [Fact]
    public void Same_core_facts_do_not_conflict_when_only_the_raw_body_hash_would_differ()
    {
        var first = Event(hash: "sha256:" + new string('a', 64));
        var redelivery = first with { EventHash = "sha256:" + new string('b', 64) };

        Assert.False(BillingProviderEventReplayFacts.From(first).ConflictsWith(BillingProviderEventReplayFacts.From(redelivery)));
    }

    [Fact]
    public void Historical_inbox_row_without_extra_facts_replays_when_core_facts_match()
    {
        var stored = Inbox(objectReference: null, priceReference: null, amount: null);
        var incoming = Event(objectReference: "cs_123", priceReference: "price_123", amount: 4900);

        Assert.False(BillingProviderEventReplayFacts.From(stored).ConflictsWith(BillingProviderEventReplayFacts.From(incoming)));
    }

    [Fact]
    public void Same_id_delivery_conflicts_when_a_listed_business_fact_changes()
    {
        var stored = Inbox(
            eventType: "customer.subscription.updated",
            customer: "cus_123",
            subscription: "sub_123",
            objectReference: "sub_123",
            priceReference: "price_123",
            amount: 4900);

        Assert.True(Conflicts(stored, Event(eventType: "checkout.session.completed")));
        Assert.True(Conflicts(stored, Event(customer: "cus_other")));
        Assert.True(Conflicts(stored, Event(subscription: "sub_other")));
        Assert.True(Conflicts(stored, Event(objectReference: "cs_other")));
        Assert.True(Conflicts(stored, Event(priceReference: "price_other")));
        Assert.True(Conflicts(stored, Event(amount: 9900)));
        Assert.True(Conflicts(stored, Event(organizationId: Guid.NewGuid())));
        Assert.True(Conflicts(stored, Event(state: OrganizationSubscriptionState.PastDue)));
    }

    [Fact]
    public void Recorded_extra_facts_replay_when_they_match()
    {
        var stored = Inbox(objectReference: "cs_123", priceReference: "price_123", amount: 4900);
        var incoming = Event(objectReference: "cs_123", priceReference: "price_123", amount: 4900, hash: "sha256:" + new string('c', 64));

        Assert.False(BillingProviderEventReplayFacts.From(stored).ConflictsWith(BillingProviderEventReplayFacts.From(incoming)));
    }

    private static bool Conflicts(BillingProviderEventInboxEntry stored, BillingProviderEvent incoming) =>
        BillingProviderEventReplayFacts.From(stored).ConflictsWith(BillingProviderEventReplayFacts.From(incoming));

    private static BillingProviderEvent Event(
        Guid? organizationId = null,
        string eventType = "checkout.session.completed",
        OrganizationSubscriptionState? state = null,
        string? customer = "cus_123",
        string? subscription = null,
        string? objectReference = "cs_123",
        string? priceReference = "price_123",
        long? amount = 4900,
        string? hash = null) =>
        new(
            organizationId ?? OrganizationId,
            BillingProviderNames.Stripe,
            "evt_1UMx7tR",
            eventType,
            state,
            OccurredAt,
            hash ?? "sha256:" + new string('a', 64),
            customer,
            subscription,
            objectReference,
            priceReference,
            amount);

    private static BillingProviderEventInboxEntry Inbox(
        string eventType = "checkout.session.completed",
        string? customer = "cus_123",
        string? subscription = null,
        string? objectReference = "cs_123",
        string? priceReference = "price_123",
        long? amount = 4900) =>
        new()
        {
            OrganizationId = OrganizationId,
            Provider = BillingProviderNames.Stripe,
            ProviderEventId = "evt_1UMx7tR",
            EventType = eventType,
            State = null,
            EventHash = "sha256:" + new string('a', 64),
            ProviderCustomerReference = customer,
            ProviderSubscriptionReference = subscription,
            ProviderObjectReference = objectReference,
            PriceReference = priceReference,
            AmountMinorUnits = amount,
            OccurredAt = OccurredAt
        };
}
