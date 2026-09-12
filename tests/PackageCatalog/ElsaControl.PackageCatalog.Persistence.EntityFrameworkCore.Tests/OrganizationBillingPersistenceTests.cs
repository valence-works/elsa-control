using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class OrganizationBillingPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrganizationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task Trial_projection_preserves_existing_capability_fields()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = OrganizationId,
            CanCreateCustomSources = true,
            MaxSources = 17,
            MaxWorkspaces = 4,
            MaxPackagesIndexed = 1000,
            MaxVersionsPerPackage = 7,
            MaxSyncsPerDay = 12,
            PrivateFeedsEnabled = true,
            ManagedHostingEnabled = true,
            DeploymentTargetsEnabled = true,
            SubscriptionState = OrganizationSubscriptionState.Active,
            CreatedAt = Now,
            UpdatedAt = Now,
            SyncedAt = Now
        });
        await db.SaveChangesAsync();

        var result = await new OrganizationBillingStore(db).StartTrialAsync(OrganizationId, "stripe", Now);

        Assert.Equal(BillingEventConsumptionOutcome.Applied, result.Outcome);
        var entitlement = await db.OrganizationEntitlementSnapshots.SingleAsync();
        Assert.True(entitlement.CanCreateCustomSources);
        Assert.Equal(17, entitlement.MaxSources);
        Assert.Equal(4, entitlement.MaxWorkspaces);
        Assert.True(entitlement.PrivateFeedsEnabled);
        Assert.True(entitlement.ManagedHostingEnabled);
        Assert.True(entitlement.DeploymentTargetsEnabled);
        Assert.Equal(OrganizationSubscriptionState.Trial, entitlement.SubscriptionState);
    }

    [Fact]
    public async Task Suspended_provider_event_backfills_references_on_queued_cleanup()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();

        var store = new OrganizationBillingStore(db);
        await store.StartTrialAsync(OrganizationId, "stripe", Now);
        await store.RequestDeletionAsync(OrganizationId, Now.AddMinutes(1));

        await store.ConsumeAsync(
            new BillingProviderEvent(
                OrganizationId,
                "stripe",
                "evt-suspended",
                "customer.subscription.deleted",
                OrganizationSubscriptionState.Suspended,
                Now.AddMinutes(2),
                "sha256:" + new string('f', 64),
                "cus_123",
                "sub_123"),
            Now.AddMinutes(3));

        var cleanup = await db.OrganizationBillingCleanups.SingleAsync();
        Assert.Equal("cus_123", cleanup.ProviderCustomerReference);
        Assert.Equal("sub_123", cleanup.ProviderSubscriptionReference);
    }

    [Fact]
    public async Task Correlated_unknown_event_is_recorded_without_subscription_or_entitlement_projection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();
        var providerEvent = new BillingProviderEvent(
            OrganizationId,
            "stripe",
            "evt-unknown",
            "checkout.session.completed",
            null,
            Now,
            "sha256:" + new string('a', 64));

        var result = await new OrganizationBillingStore(db).RecordUnknownAsync(providerEvent, Now.AddMinutes(1));
        var replay = await new OrganizationBillingStore(db).RecordUnknownAsync(providerEvent, Now.AddMinutes(2));

        Assert.Equal(BillingEventConsumptionOutcome.RecordedUnknown, result.Outcome);
        Assert.Equal(BillingEventConsumptionOutcome.Replayed, replay.Outcome);
        Assert.Null(result.Event!.State);
        Assert.Equal(BillingProviderEventProcessingStatus.RecordedUnknown, result.Event.ProcessingStatus);
        Assert.Null(await db.OrganizationSubscriptions.SingleOrDefaultAsync(x => x.OrganizationId == OrganizationId));
        Assert.Null(await db.OrganizationEntitlementSnapshots.SingleOrDefaultAsync(x => x.OrganizationId == OrganizationId));
        Assert.Contains(await db.OrganizationAuditRecords.ToListAsync(), x => x.Summary.Contains("unsupported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_event_with_lifecycle_state_is_rejected_without_persistence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();

        var providerEvent = new BillingProviderEvent(
            OrganizationId,
            "stripe",
            "evt-misclassified",
            "checkout.session.completed",
            OrganizationSubscriptionState.Active,
            Now,
            "sha256:" + new string('b', 64));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            new OrganizationBillingStore(db).RecordUnknownAsync(providerEvent, Now.AddMinutes(1)));

        Assert.StartsWith("Unknown billing events must not contain a lifecycle state.", exception.Message);

        Assert.Equal(0, await db.BillingProviderEvents.CountAsync());
        Assert.Equal(0, await db.OrganizationAuditRecords.CountAsync());
        Assert.Null(await db.OrganizationSubscriptions.SingleOrDefaultAsync(x => x.OrganizationId == OrganizationId));
    }

    [Fact]
    public async Task Unknown_event_for_missing_organization_is_rejected_without_persistence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var missingOrganizationId = Guid.NewGuid();
        var providerEvent = new BillingProviderEvent(
            missingOrganizationId,
            BillingProviderNames.Stripe,
            "evt-unknown-missing-organization",
            "checkout.session.completed",
            null,
            Now,
            "sha256:" + new string('e', 64));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            new OrganizationBillingStore(db).RecordUnknownAsync(providerEvent, Now.AddMinutes(1)));

        Assert.StartsWith("Billing event organization does not exist.", exception.Message);
        Assert.Equal(0, await db.BillingProviderEvents.CountAsync());
        Assert.Equal(0, await db.OrganizationAuditRecords.CountAsync());
    }

    [Fact]
    public async Task Known_event_without_lifecycle_state_is_rejected_with_a_distinct_validation_message()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();

        var providerEvent = new BillingProviderEvent(
            OrganizationId,
            "stripe",
            "evt-missing-state",
            "customer.subscription.updated",
            null,
            Now,
            "sha256:" + new string('c', 64));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            new OrganizationBillingStore(db).ConsumeAsync(providerEvent, Now.AddMinutes(1)));

        Assert.StartsWith("Known billing events require a lifecycle state.", exception.Message);
    }

    [Fact]
    public async Task Duplicate_event_is_replayed_without_duplicate_audit_or_inbox_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();
        var providerEvent = Event("evt-active", OrganizationSubscriptionState.Active, Now.AddMinutes(1));
        var store = new OrganizationBillingStore(db);

        var first = await store.ConsumeAsync(providerEvent, Now.AddMinutes(2));
        var replay = await store.ConsumeAsync(providerEvent, Now.AddMinutes(3));

        Assert.Equal(BillingEventConsumptionOutcome.Applied, first.Outcome);
        Assert.Equal(BillingEventConsumptionOutcome.Replayed, replay.Outcome);
        Assert.Equal(1, await db.BillingProviderEvents.CountAsync());
        Assert.Equal(1, await db.OrganizationAuditRecords.CountAsync());
        Assert.Equal(OrganizationSubscriptionState.Active, (await db.OrganizationSubscriptions.SingleAsync()).State);
    }

    [Fact]
    public async Task Maximum_length_event_id_uses_inbox_guid_for_audit_target()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();
        var eventId = new string('e', 256);

        await new OrganizationBillingStore(db).ConsumeAsync(Event(eventId, OrganizationSubscriptionState.Active, Now.AddMinutes(1)), Now.AddMinutes(2));

        var inbox = await db.BillingProviderEvents.SingleAsync();
        var audit = await db.OrganizationAuditRecords.SingleAsync();
        Assert.Equal(256, inbox.ProviderEventId.Length);
        Assert.Equal(inbox.Id.ToString("D"), audit.TargetId);
        Assert.True(audit.TargetId.Length <= 128);
    }

    [Fact]
    public async Task Provider_binding_references_allow_256_characters_and_reject_257()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();
        var store = new OrganizationBillingStore(db);
        var acceptedCustomerReference = new string('c', OrganizationBillingLimits.ProviderReferenceMaxLength);
        var acceptedSubscriptionReference = new string('s', OrganizationBillingLimits.ProviderReferenceMaxLength);

        var accepted = await store.ConsumeAsync(
            Event(OrganizationId, "evt-reference-limit", OrganizationSubscriptionState.Active, Now.AddMinutes(1), acceptedCustomerReference, acceptedSubscriptionReference),
            Now.AddMinutes(2));

        Assert.Equal(BillingEventConsumptionOutcome.Applied, accepted.Outcome);
        Assert.Equal(OrganizationBillingLimits.ProviderReferenceMaxLength, accepted.Subscription!.ProviderCustomerReference!.Length);
        Assert.Equal(OrganizationBillingLimits.ProviderReferenceMaxLength, accepted.Subscription.ProviderSubscriptionReference!.Length);

        var oversized = Event(
            OrganizationId,
            "evt-reference-too-long",
            OrganizationSubscriptionState.PastDue,
            Now.AddMinutes(3),
            new string('c', OrganizationBillingLimits.ProviderReferenceMaxLength + 1),
            acceptedSubscriptionReference);

        await Assert.ThrowsAsync<ArgumentException>(() => store.ConsumeAsync(oversized, Now.AddMinutes(4)));
        Assert.Equal(1, await db.BillingProviderEvents.CountAsync());
        Assert.Equal(1, await db.OrganizationAuditRecords.CountAsync());
    }

    [Fact]
    public async Task Billing_provider_event_inbox_is_append_only()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();
        await new OrganizationBillingStore(db).ConsumeAsync(Event("evt-append-only", OrganizationSubscriptionState.Active, Now.AddMinutes(1)), Now.AddMinutes(2));

        var inbox = await db.BillingProviderEvents.SingleAsync();
        inbox.State = OrganizationSubscriptionState.PastDue;

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(OrganizationSubscriptionState.Active, (await db.BillingProviderEvents.SingleAsync()).State);
    }

    [Fact]
    public async Task Billing_provider_event_inbox_cannot_be_deleted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();
        await new OrganizationBillingStore(db).ConsumeAsync(Event("evt-append-only-delete", OrganizationSubscriptionState.Active, Now.AddMinutes(1)), Now.AddMinutes(2));

        var inbox = await db.BillingProviderEvents.SingleAsync();
        db.BillingProviderEvents.Remove(inbox);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.BillingProviderEvents.CountAsync());
    }

    [Theory]
    [InlineData("other_customer", "sub_acme")]
    [InlineData("cus_acme", "other_subscription")]
    public async Task Conflicting_provider_references_fail_closed_without_new_durable_records(
        string customerReference,
        string subscriptionReference)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();
        var store = new OrganizationBillingStore(db);
        await store.ConsumeAsync(Event("evt-active", OrganizationSubscriptionState.Active, Now.AddMinutes(1)), Now.AddMinutes(2));

        var conflicting = Event("evt-past-due", OrganizationSubscriptionState.PastDue, Now.AddMinutes(3)) with
        {
            ProviderCustomerReference = customerReference,
            ProviderSubscriptionReference = subscriptionReference
        };
        await Assert.ThrowsAsync<BillingProviderEventConflictException>(() => store.ConsumeAsync(conflicting, Now.AddMinutes(4)));

        // The conflict must leave no tracked Added inbox that a later save could commit.
        await db.SaveChangesAsync();
        var subscription = await db.OrganizationSubscriptions.SingleAsync();
        Assert.Equal(OrganizationSubscriptionState.Active, subscription.State);
        Assert.Equal("cus_acme", subscription.ProviderCustomerReference);
        Assert.Equal("sub_acme", subscription.ProviderSubscriptionReference);
        Assert.Equal(1, await db.BillingProviderEvents.CountAsync());
        Assert.Equal(1, await db.OrganizationAuditRecords.CountAsync());
    }

    [Fact]
    public async Task Provider_customer_reference_is_unique_across_organizations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var firstOrganizationId = Guid.NewGuid();
        var secondOrganizationId = Guid.NewGuid();
        db.Organizations.AddRange(
            new Organization { Id = firstOrganizationId, Name = "First" },
            new Organization { Id = secondOrganizationId, Name = "Second" });
        await db.SaveChangesAsync();
        var store = new OrganizationBillingStore(db);
        await store.ConsumeAsync(Event(firstOrganizationId, "evt-first", OrganizationSubscriptionState.Active, Now.AddMinutes(1), "cus-shared", "sub-first"), Now.AddMinutes(2));

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            store.ConsumeAsync(Event(secondOrganizationId, "evt-second", OrganizationSubscriptionState.Active, Now.AddMinutes(1), "cus-shared", "sub-second"), Now.AddMinutes(2)));

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.BillingProviderEvents.CountAsync());
        Assert.Equal(1, await db.OrganizationSubscriptions.CountAsync());
        Assert.Equal(1, await db.OrganizationAuditRecords.CountAsync());
    }

    [Fact]
    public async Task Provider_subscription_reference_is_unique_across_organizations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var firstOrganizationId = Guid.NewGuid();
        var secondOrganizationId = Guid.NewGuid();
        db.Organizations.AddRange(
            new Organization { Id = firstOrganizationId, Name = "First" },
            new Organization { Id = secondOrganizationId, Name = "Second" });
        await db.SaveChangesAsync();
        var store = new OrganizationBillingStore(db);
        await store.ConsumeAsync(Event(firstOrganizationId, "evt-first", OrganizationSubscriptionState.Active, Now.AddMinutes(1), "cus-first", "sub-shared"), Now.AddMinutes(2));

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            store.ConsumeAsync(Event(secondOrganizationId, "evt-second", OrganizationSubscriptionState.Active, Now.AddMinutes(1), "cus-second", "sub-shared"), Now.AddMinutes(2)));

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.BillingProviderEvents.CountAsync());
        Assert.Equal(1, await db.OrganizationSubscriptions.CountAsync());
        Assert.Equal(1, await db.OrganizationAuditRecords.CountAsync());
    }

    [Fact]
    public async Task Out_of_order_event_is_recorded_without_regressing_state_or_entitlement()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();
        var store = new OrganizationBillingStore(db);
        await store.ConsumeAsync(Event("evt-active", OrganizationSubscriptionState.Active, Now.AddMinutes(2)), Now.AddMinutes(3));

        var result = await store.ConsumeAsync(Event("evt-trial", OrganizationSubscriptionState.Trial, Now.AddMinutes(1)), Now.AddMinutes(4));

        Assert.Equal(BillingEventConsumptionOutcome.IgnoredOutOfOrder, result.Outcome);
        Assert.Equal(OrganizationSubscriptionState.Active, (await db.OrganizationSubscriptions.SingleAsync()).State);
        Assert.Equal(OrganizationSubscriptionState.Active, (await db.OrganizationEntitlementSnapshots.SingleAsync()).SubscriptionState);
        Assert.Equal(2, await db.BillingProviderEvents.CountAsync());
        Assert.Equal(2, await db.OrganizationAuditRecords.CountAsync());
        Assert.Contains(await db.OrganizationAuditRecords.ToListAsync(), x => x.Summary.Contains("ignored as out of order", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ignored_event_cannot_bind_provider_references_before_a_valid_event()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();
        var store = new OrganizationBillingStore(db);
        await store.StartTrialAsync(OrganizationId, "stripe", Now);

        var ignored = await store.ConsumeAsync(
            Event(OrganizationId, "evt-old", OrganizationSubscriptionState.Active, Now.AddMinutes(-1), "cus-ignored", "sub-ignored"),
            Now.AddMinutes(1));

        Assert.Equal(BillingEventConsumptionOutcome.IgnoredOutOfOrder, ignored.Outcome);
        var subscription = await db.OrganizationSubscriptions.SingleAsync();
        Assert.Null(subscription.ProviderCustomerReference);
        Assert.Null(subscription.ProviderSubscriptionReference);

        await store.ConsumeAsync(
            Event(OrganizationId, "evt-valid", OrganizationSubscriptionState.Active, Now.AddMinutes(1), "cus-valid", "sub-valid"),
            Now.AddMinutes(2));

        subscription = await db.OrganizationSubscriptions.SingleAsync();
        Assert.Equal("cus-valid", subscription.ProviderCustomerReference);
        Assert.Equal("sub-valid", subscription.ProviderSubscriptionReference);
    }

    [Fact]
    public async Task Rejected_event_cannot_bind_provider_references_before_a_valid_event()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();
        var store = new OrganizationBillingStore(db);
        await store.StartTrialAsync(OrganizationId, "stripe", Now);

        var rejected = await store.ConsumeAsync(
            Event(OrganizationId, "evt-rejected", OrganizationSubscriptionState.Deleted, Now.AddMinutes(1), "cus-rejected", "sub-rejected"),
            Now.AddMinutes(2));

        Assert.Equal(BillingEventConsumptionOutcome.Rejected, rejected.Outcome);
        var subscription = await db.OrganizationSubscriptions.SingleAsync();
        Assert.Null(subscription.ProviderCustomerReference);
        Assert.Null(subscription.ProviderSubscriptionReference);

        await store.ConsumeAsync(
            Event(OrganizationId, "evt-valid", OrganizationSubscriptionState.Active, Now.AddMinutes(2), "cus-valid", "sub-valid"),
            Now.AddMinutes(3));

        subscription = await db.OrganizationSubscriptions.SingleAsync();
        Assert.Equal("cus-valid", subscription.ProviderCustomerReference);
        Assert.Equal("sub-valid", subscription.ProviderSubscriptionReference);
    }

    [Fact]
    public async Task Invalid_transition_is_stored_as_rejected_without_projecting_entitlements()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();
        var store = new OrganizationBillingStore(db);
        await store.ConsumeAsync(Event("evt-active", OrganizationSubscriptionState.Active, Now.AddMinutes(1)), Now.AddMinutes(2));

        var result = await store.ConsumeAsync(Event("evt-trial", OrganizationSubscriptionState.Trial, Now.AddMinutes(3)), Now.AddMinutes(4));

        Assert.Equal(BillingEventConsumptionOutcome.Rejected, result.Outcome);
        Assert.Equal("subscription.transition.invalid", result.RejectionCode);
        Assert.Equal(OrganizationSubscriptionState.Active, (await db.OrganizationSubscriptions.SingleAsync()).State);
        Assert.Equal(2, await db.OrganizationAuditRecords.CountAsync());
        Assert.Contains(await db.OrganizationAuditRecords.ToListAsync(), x => x.Summary == "A normalized billing provider event was rejected.");
    }

    [Fact]
    public async Task Same_timestamp_events_use_provider_event_id_as_a_deterministic_tiebreaker()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();
        var store = new OrganizationBillingStore(db);

        var timestamp = Now.AddMinutes(2);
        await store.ConsumeAsync(Event("evt-b", OrganizationSubscriptionState.Active, timestamp), Now.AddMinutes(3));

        var olderTie = await store.ConsumeAsync(Event("evt-a", OrganizationSubscriptionState.PastDue, timestamp), Now.AddMinutes(4));
        var newerTie = await store.ConsumeAsync(Event("evt-z", OrganizationSubscriptionState.PastDue, timestamp), Now.AddMinutes(5));

        Assert.Equal(BillingEventConsumptionOutcome.IgnoredOutOfOrder, olderTie.Outcome);
        Assert.Equal(BillingEventConsumptionOutcome.Applied, newerTie.Outcome);
        var subscription = await db.OrganizationSubscriptions.SingleAsync();
        Assert.Equal(OrganizationSubscriptionState.PastDue, subscription.State);
        Assert.Equal("evt-z", subscription.LastProviderEventId);
        Assert.Equal(3, await db.BillingProviderEvents.CountAsync());
        Assert.Equal(3, await db.OrganizationAuditRecords.CountAsync());
    }

    [Fact]
    public async Task Direct_subscription_state_change_requires_transition_and_cursor_advance()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();
        var store = new OrganizationBillingStore(db);
        await store.ConsumeAsync(Event("evt-active", OrganizationSubscriptionState.Active, Now.AddMinutes(1)), Now.AddMinutes(2));

        var subscription = await db.OrganizationSubscriptions.SingleAsync();
        subscription.State = OrganizationSubscriptionState.Trial;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        subscription = await db.OrganizationSubscriptions.SingleAsync();
        subscription.State = OrganizationSubscriptionState.PastDue;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        subscription = await db.OrganizationSubscriptions.SingleAsync();
        subscription.LastProviderEventOccurredAt = subscription.LastProviderEventOccurredAt.AddMinutes(-1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Subscription_identity_bindings_and_lifecycle_timestamps_are_protected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();
        var store = new OrganizationBillingStore(db);
        await store.ConsumeAsync(Event("evt-active", OrganizationSubscriptionState.Active, Now.AddMinutes(1)), Now.AddMinutes(2));

        async Task RejectAsync(Action<OrganizationSubscription> mutate)
        {
            db.ChangeTracker.Clear();
            var current = await db.OrganizationSubscriptions.SingleAsync();
            mutate(current);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await RejectAsync(current => current.Provider = "other-provider");
        await RejectAsync(current => current.ProviderCustomerReference = "other-customer");
        await RejectAsync(current => current.ProviderSubscriptionReference = "other-subscription");
        await RejectAsync(current => current.CreatedAt = current.CreatedAt.AddMinutes(1));
        await RejectAsync(current => current.TrialStartedAt = current.TrialStartedAt.AddMinutes(1));
        await RejectAsync(current => current.TrialEndsAt = current.TrialEndsAt.AddMinutes(1));
        await RejectAsync(current => current.ActivatedAt = current.ActivatedAt!.Value.AddMinutes(1));

        db.ChangeTracker.Clear();
        var subscription = await db.OrganizationSubscriptions.SingleAsync();
        db.OrganizationSubscriptions.Remove(subscription);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Entitlement_subscription_binding_requires_matching_organization()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var firstOrganizationId = Guid.NewGuid();
        var secondOrganizationId = Guid.NewGuid();
        db.Organizations.AddRange(
            new Organization { Id = firstOrganizationId, Name = "First" },
            new Organization { Id = secondOrganizationId, Name = "Second" });
        var subscription = OrganizationSubscriptionLifecycle.CreateTrial(firstOrganizationId, "stripe", Now);
        db.OrganizationSubscriptions.Add(subscription);
        db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = secondOrganizationId,
            SubscriptionId = subscription.Id,
            CreatedAt = Now,
            UpdatedAt = Now,
            SyncedAt = Now
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OrganizationEntitlementSnapshots.CountAsync());
    }

    [Fact]
    public async Task Provider_event_and_references_remain_case_distinct()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var firstOrganizationId = Guid.NewGuid();
        var secondOrganizationId = Guid.NewGuid();
        db.Organizations.AddRange(
            new Organization { Id = firstOrganizationId, Name = "First" },
            new Organization { Id = secondOrganizationId, Name = "Second" });
        await db.SaveChangesAsync();
        var store = new OrganizationBillingStore(db);

        await store.ConsumeAsync(Event(firstOrganizationId, "evt-case", OrganizationSubscriptionState.Active, Now.AddMinutes(1), "cus-case", "sub-case"), Now.AddMinutes(2));
        await store.ConsumeAsync(Event(secondOrganizationId, "EVT-CASE", OrganizationSubscriptionState.Active, Now.AddMinutes(1), "CUS-CASE", "SUB-CASE"), Now.AddMinutes(2));

        Assert.Equal(2, await db.BillingProviderEvents.CountAsync());
        Assert.Equal(2, await db.OrganizationSubscriptions.CountAsync());
    }

    [Fact]
    public void Sql_server_opaque_identity_properties_use_binary_collation()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer("Server=(local);Database=ElsaControlDesign;TrustServerCertificate=True")
            .Options;
        using var db = new CatalogDbContext(options);
        var model = db.GetService<IDesignTimeModel>().Model;
        const string expected = "Latin1_General_100_BIN2";
        Assert.Equal(expected, model.FindEntityType(typeof(OrganizationSubscription))!.FindProperty(nameof(OrganizationSubscription.Provider))!.GetCollation());
        Assert.Equal(expected, model.FindEntityType(typeof(OrganizationSubscription))!.FindProperty(nameof(OrganizationSubscription.LastProviderEventId))!.GetCollation());
        Assert.Equal(expected, model.FindEntityType(typeof(BillingProviderEventInboxEntry))!.FindProperty(nameof(BillingProviderEventInboxEntry.ProviderEventId))!.GetCollation());
        Assert.Equal(OrganizationBillingLimits.ProviderReferenceMaxLength, model.FindEntityType(typeof(OrganizationSubscription))!.FindProperty(nameof(OrganizationSubscription.ProviderCustomerReference))!.GetMaxLength());
        Assert.Equal(OrganizationBillingLimits.ProviderReferenceMaxLength, model.FindEntityType(typeof(OrganizationSubscription))!.FindProperty(nameof(OrganizationSubscription.ProviderSubscriptionReference))!.GetMaxLength());
        Assert.Equal(OrganizationBillingLimits.ProviderReferenceMaxLength, model.FindEntityType(typeof(BillingProviderEventInboxEntry))!.FindProperty(nameof(BillingProviderEventInboxEntry.ProviderCustomerReference))!.GetMaxLength());
        Assert.Equal(OrganizationBillingLimits.ProviderReferenceMaxLength, model.FindEntityType(typeof(BillingProviderEventInboxEntry))!.FindProperty(nameof(BillingProviderEventInboxEntry.ProviderSubscriptionReference))!.GetMaxLength());
    }

    [Fact]
    public async Task Start_trial_is_idempotent_when_called_again_for_the_same_organization()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await db.SaveChangesAsync();
        var store = new OrganizationBillingStore(db);

        var first = await store.StartTrialAsync(OrganizationId, "stripe", Now);
        var replay = await store.StartTrialAsync(OrganizationId, "stripe", Now.AddDays(1));

        Assert.Equal(BillingEventConsumptionOutcome.Applied, first.Outcome);
        Assert.Equal(BillingEventConsumptionOutcome.Replayed, replay.Outcome);
        Assert.Equal(first.Subscription!.Id, replay.Subscription!.Id);
        Assert.Equal(Now.AddDays(14), replay.Subscription.TrialEndsAt);
        Assert.Equal(1, await db.OrganizationSubscriptions.CountAsync());
        Assert.Equal(1, await db.OrganizationAuditRecords.CountAsync());
    }

    [Fact]
    public async Task Concurrent_start_trial_calls_converge_on_one_subscription_and_audit()
    {
        const string connectionString = "Data Source=file:billing-start-trial-concurrency?mode=memory&cache=shared;Default Timeout=5";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using var setup = CreateDb(anchor);
        await setup.Database.EnsureCreatedAsync();
        setup.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await setup.SaveChangesAsync();

        await using var firstConnection = new SqliteConnection(connectionString);
        await using var secondConnection = new SqliteConnection(connectionString);
        await firstConnection.OpenAsync();
        await secondConnection.OpenAsync();
        await using var firstDb = CreateDb(firstConnection);
        await using var secondDb = CreateDb(secondConnection);

        var results = await Task.WhenAll(
            new OrganizationBillingStore(firstDb).StartTrialAsync(OrganizationId, "stripe", Now),
            new OrganizationBillingStore(secondDb).StartTrialAsync(OrganizationId, "stripe", Now));

        Assert.Equal(1, results.Count(x => x.Outcome == BillingEventConsumptionOutcome.Applied));
        Assert.Equal(1, results.Count(x => x.Outcome == BillingEventConsumptionOutcome.Replayed));
        Assert.Equal(1, await setup.OrganizationSubscriptions.CountAsync());
        Assert.Equal(1, await setup.OrganizationAuditRecords.CountAsync());
    }

    [Fact]
    public async Task Concurrent_delivery_of_the_same_provider_event_is_replayed_once()
    {
        const string connectionString = "Data Source=file:billing-event-concurrency?mode=memory&cache=shared;Default Timeout=5";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using var setup = CreateDb(anchor);
        await setup.Database.EnsureCreatedAsync();
        setup.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        await setup.SaveChangesAsync();

        await using var firstConnection = new SqliteConnection(connectionString);
        await using var secondConnection = new SqliteConnection(connectionString);
        await firstConnection.OpenAsync();
        await secondConnection.OpenAsync();
        await using var firstDb = CreateDb(firstConnection);
        await using var secondDb = CreateDb(secondConnection);
        var providerEvent = Event("evt-concurrent", OrganizationSubscriptionState.Active, Now.AddMinutes(1));

        var results = await Task.WhenAll(
            new OrganizationBillingStore(firstDb).ConsumeAsync(providerEvent, Now.AddMinutes(2)),
            new OrganizationBillingStore(secondDb).ConsumeAsync(providerEvent, Now.AddMinutes(2)));

        Assert.Equal(1, results.Count(x => x.Outcome == BillingEventConsumptionOutcome.Applied));
        Assert.Equal(1, results.Count(x => x.Outcome == BillingEventConsumptionOutcome.Replayed));
        Assert.Equal(1, await setup.BillingProviderEvents.CountAsync());
        Assert.Equal(1, await setup.OrganizationSubscriptions.CountAsync());
        Assert.Equal(1, await setup.OrganizationEntitlementSnapshots.CountAsync());
        Assert.Equal(1, await setup.OrganizationAuditRecords.CountAsync());
    }

    [Fact]
    public async Task Known_event_for_missing_organization_is_rejected_without_persistence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();

        var providerEvent = Event(Guid.NewGuid().ToString("N"), OrganizationSubscriptionState.Active, Now.AddMinutes(1)) with
        {
            OrganizationId = OrganizationId
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            new OrganizationBillingStore(db).ConsumeAsync(providerEvent, Now.AddMinutes(2)));

        Assert.StartsWith("Billing event organization does not exist.", exception.Message);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.BillingProviderEvents.CountAsync());
        Assert.Equal(0, await db.OrganizationSubscriptions.CountAsync());
        Assert.Equal(0, await db.OrganizationEntitlementSnapshots.CountAsync());
        Assert.Equal(0, await db.OrganizationAuditRecords.CountAsync());
    }

    [Fact]
    public async Task Migration_leaves_existing_entitlement_lifecycle_unprojected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedDb(connection);
        const string preBillingMigration = "20260901224243_PersistGovernedReleasePackageDeclarations";
        await db.Database.MigrateAsync(preBillingMigration);
        var organizationId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Organizations (Id, Name, Status, CreatedAt, UpdatedAt)
            VALUES ({organizationId}, {"Legacy organization"}, {"Active"}, {Now.UtcTicks}, {Now.UtcTicks});
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO OrganizationEntitlementSnapshots
                (Id, OrganizationId, CanCreateCustomSources, MaxSources, MaxWorkspaces,
                 MaxPackagesIndexed, MaxVersionsPerPackage, MaxSyncsPerDay, PrivateFeedsEnabled,
                 ManagedHostingEnabled, DeploymentTargetsEnabled, SyncedAt, CreatedAt, UpdatedAt)
            VALUES
                ({snapshotId}, {organizationId}, 1, 10, 2, NULL, NULL, NULL, 1, 0, 0,
                 {Now.UtcTicks}, {Now.UtcTicks}, {Now.UtcTicks});
            """);

        await db.Database.MigrateAsync();

        db.ChangeTracker.Clear();
        await db.Database.MigrateAsync(preBillingMigration);
        await db.Database.MigrateAsync();

        var snapshot = await db.OrganizationEntitlementSnapshots.SingleAsync(x => x.Id == snapshotId);
        Assert.Null(snapshot.SubscriptionState);
        Assert.Null(snapshot.SubscriptionId);
        Assert.True(snapshot.CanCreateCustomSources);
        Assert.Equal(10, snapshot.MaxSources);
    }

    [Fact]
    public async Task Rolling_back_unknown_event_state_migration_restores_null_rows_to_suspended()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedDb(connection);
        await db.Database.MigrateAsync();

        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
        db.BillingProviderEvents.Add(new BillingProviderEventInboxEntry
        {
            OrganizationId = OrganizationId,
            Provider = "stripe",
            ProviderEventId = "evt-rollback-null-state",
            EventType = "checkout.session.completed",
            State = null,
            EventHash = "sha256:" + new string('d', 64),
            OccurredAt = Now,
            ReceivedAt = Now,
            ProcessedAt = Now,
            ProcessingStatus = BillingProviderEventProcessingStatus.RecordedUnknown,
            RejectionCode = "provider.event.unknown"
        });
        await db.SaveChangesAsync();

        await db.Database.MigrateAsync("20260904013824_AddOrganizationBillingLifecycle");

        db.ChangeTracker.Clear();
        var restored = await db.BillingProviderEvents.SingleAsync();
        Assert.Equal(OrganizationSubscriptionState.Suspended, restored.State);
    }

    private static BillingProviderEvent Event(string id, OrganizationSubscriptionState state, DateTimeOffset occurredAt) =>
        Event(OrganizationId, id, state, occurredAt, "cus_acme", "sub_acme");

    private static BillingProviderEvent Event(
        Guid organizationId,
        string id,
        OrganizationSubscriptionState state,
        DateTimeOffset occurredAt,
        string customerReference,
        string subscriptionReference) =>
        new(organizationId, "stripe", id, "subscription." + state.ToString().ToLowerInvariant(), state, occurredAt, "sha256:" + new string('a', 64), customerReference, subscriptionReference);

    private static CatalogDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(connection).Options);

    private static CatalogDbContext CreateMigratedDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);
}
