using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class OrganizationBillingLifecycleTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Clock_drives_each_deadline_without_early_deletion_and_retries_are_idempotent()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        var subscription = await fixture.StartTrialAsync(fixture.OrganizationId, Start);

        Assert.Empty(await fixture.Store.AdvanceDueAsync(subscription.TrialEndsAt.AddTicks(-1)));
        Assert.Equal(OrganizationSubscriptionState.Trial, await fixture.StateAsync(fixture.OrganizationId));

        await fixture.AdvanceAsync(subscription.TrialEndsAt, OrganizationSubscriptionState.PastDue);
        var pastDue = await fixture.SubscriptionAsync(fixture.OrganizationId);
        Assert.Equal(subscription.TrialEndsAt.AddDays(7), pastDue.GraceEndsAt);
        Assert.Empty(await fixture.Store.AdvanceDueAsync(pastDue.GraceEndsAt!.Value.AddTicks(-1)));

        await fixture.AdvanceAsync(pastDue.GraceEndsAt.Value, OrganizationSubscriptionState.Constrained);
        var constrained = await fixture.SubscriptionAsync(fixture.OrganizationId);
        Assert.Empty(await fixture.Store.AdvanceDueAsync(constrained.ConstrainedAt!.Value.AddDays(1).AddTicks(-1)));

        await fixture.AdvanceAsync(constrained.ConstrainedAt.Value.AddDays(1), OrganizationSubscriptionState.Suspended);
        var suspended = await fixture.SubscriptionAsync(fixture.OrganizationId);
        Assert.Equal(suspended.SuspendedAt!.Value.AddDays(30), suspended.RetentionEndsAt);
        Assert.Empty(await fixture.Store.AdvanceDueAsync(suspended.RetentionEndsAt!.Value.AddTicks(-1)));
        Assert.Empty(await fixture.Db.OrganizationBillingCleanups.ToListAsync());

        await fixture.AdvanceAsync(suspended.RetentionEndsAt.Value, OrganizationSubscriptionState.Retained);
        Assert.Single(await fixture.Db.OrganizationBillingCleanups.ToListAsync());
        Assert.Empty(await fixture.Store.AdvanceDueAsync(suspended.RetentionEndsAt.Value));
        Assert.Single(await fixture.Db.OrganizationBillingCleanups.ToListAsync());

        var notices = await fixture.Db.OrganizationBillingLifecycleNotices.OrderBy(x => x.Kind).ToListAsync();
        Assert.Equal(5, notices.Count);
        Assert.Equal(5, notices.Select(x => x.Kind).Distinct().Count());
        Assert.DoesNotContain(notices, x => x.State == OrganizationSubscriptionState.Deleted);
    }

    [Fact]
    public async Task Lifecycle_advancement_is_isolated_by_organization()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        var secondOrganizationId = Guid.NewGuid();
        fixture.Db.Organizations.Add(new Organization { Id = secondOrganizationId, Name = "Second" });
        await fixture.Db.SaveChangesAsync();
        var first = await fixture.StartTrialAsync(fixture.OrganizationId, Start);
        await fixture.StartTrialAsync(secondOrganizationId, Start.AddDays(1));

        await fixture.Store.AdvanceDueAsync(first.TrialEndsAt);

        Assert.Equal(OrganizationSubscriptionState.PastDue, await fixture.StateAsync(fixture.OrganizationId));
        Assert.Equal(OrganizationSubscriptionState.Trial, await fixture.StateAsync(secondOrganizationId));
        Assert.Single(await fixture.Db.OrganizationBillingLifecycleNotices.Where(x => x.OrganizationId == fixture.OrganizationId).ToListAsync());
        Assert.Empty(await fixture.Db.OrganizationBillingLifecycleNotices.Where(x => x.OrganizationId == secondOrganizationId).ToListAsync());
    }

    [Fact]
    public async Task Explicit_early_deletion_queues_immediate_cleanup_and_tombstones_only_after_confirmation()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        var subscription = await fixture.StartTrialAsync(fixture.OrganizationId, Start);
        subscription.ProviderCustomerReference = "cus_safe";
        subscription.ProviderSubscriptionReference = "sub_safe";
        await fixture.Db.SaveChangesAsync();
        var requestedAt = Start.AddDays(2);

        var requested = await fixture.Store.RequestDeletionAsync(fixture.OrganizationId, requestedAt);
        var repeated = await fixture.Store.RequestDeletionAsync(fixture.OrganizationId, requestedAt);

        Assert.NotNull(requested);
        Assert.NotNull(repeated);
        Assert.Equal(OrganizationSubscriptionState.Suspended, await fixture.StateAsync(fixture.OrganizationId));
        Assert.Single(await fixture.Db.OrganizationBillingCleanups.ToListAsync());
        Assert.Equal(3, await fixture.Db.OrganizationBillingLifecycleNotices.CountAsync());
        Assert.Equal(3, await fixture.Db.OrganizationBillingLifecycleNotices.Select(x => x.Kind).Distinct().CountAsync());
        var work = Assert.IsType<OrganizationBillingCleanupWorkItem>(await fixture.Store.TryClaimCleanupAsync("worker", requestedAt));

        var retry = await fixture.Store.CompleteCleanupAsync(new(work.Id, work.OrganizationId, work.SubscriptionId, work.LeaseToken, OrganizationBillingCleanupOutcome.RetryableFailure, requestedAt, "provider.retry"));
        Assert.Equal(OrganizationBillingCleanupState.Queued, retry.State);
        Assert.Equal(OrganizationSubscriptionState.Suspended, await fixture.StateAsync(fixture.OrganizationId));
        Assert.Null(await fixture.Store.TryClaimCleanupAsync("worker", requestedAt.AddSeconds(59)));

        var retryWork = Assert.IsType<OrganizationBillingCleanupWorkItem>(await fixture.Store.TryClaimCleanupAsync("worker", requestedAt.AddMinutes(1)));
        var completed = await fixture.Store.CompleteCleanupAsync(new(retryWork.Id, retryWork.OrganizationId, retryWork.SubscriptionId, retryWork.LeaseToken, OrganizationBillingCleanupOutcome.ConfirmedAbsent, requestedAt.AddMinutes(1)));
        Assert.True(completed.SubscriptionDeleted);
        var tombstone = await fixture.SubscriptionAsync(fixture.OrganizationId);
        Assert.Equal(OrganizationSubscriptionState.Deleted, tombstone.State);
        Assert.Null(tombstone.ProviderCustomerReference);
        Assert.Null(tombstone.ProviderSubscriptionReference);
        Assert.Null(tombstone.LastProviderEventId);
        var cleanup = await fixture.Db.OrganizationBillingCleanups.SingleAsync();
        Assert.Null(cleanup.ProviderCustomerReference);
        Assert.Null(cleanup.ProviderSubscriptionReference);
    }

    [Fact]
    public async Task Repeated_deletion_request_after_tombstone_does_not_mutate_the_tombstone()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        await fixture.StartTrialAsync(fixture.OrganizationId, Start);
        await fixture.Store.RequestDeletionAsync(fixture.OrganizationId, Start.AddDays(1));
        var work = Assert.IsType<OrganizationBillingCleanupWorkItem>(
            await fixture.Store.TryClaimCleanupAsync("worker", Start.AddDays(1)));
        await fixture.Store.CompleteCleanupAsync(new(
            work.Id,
            work.OrganizationId,
            work.SubscriptionId,
            work.LeaseToken,
            OrganizationBillingCleanupOutcome.ConfirmedAbsent,
            Start.AddDays(1).AddMinutes(1)));

        var tombstone = await fixture.SubscriptionAsync(fixture.OrganizationId);
        Assert.Equal(Start.AddDays(1), tombstone.EarlyDeletionRequestedAt);
        var deletedAt = tombstone.DeletedAt;

        await fixture.Store.RequestDeletionAsync(fixture.OrganizationId, Start.AddDays(2));

        tombstone = await fixture.SubscriptionAsync(fixture.OrganizationId);
        Assert.Equal(Start.AddDays(1), tombstone.EarlyDeletionRequestedAt);
        Assert.Equal(deletedAt, tombstone.DeletedAt);
    }

    [Fact]
    public async Task Tombstone_cannot_rebind_provider_references()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        await fixture.StartTrialAsync(fixture.OrganizationId, Start);
        await fixture.Store.RequestDeletionAsync(fixture.OrganizationId, Start.AddDays(1));
        var work = Assert.IsType<OrganizationBillingCleanupWorkItem>(
            await fixture.Store.TryClaimCleanupAsync("worker", Start.AddDays(1)));
        await fixture.Store.CompleteCleanupAsync(new(
            work.Id, work.OrganizationId, work.SubscriptionId, work.LeaseToken,
            OrganizationBillingCleanupOutcome.ConfirmedAbsent, Start.AddDays(1).AddMinutes(1)));
        var tombstone = await fixture.SubscriptionAsync(fixture.OrganizationId);
        tombstone.ProviderSubscriptionReference = "sub_rebound";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Db.SaveChangesAsync());

        Assert.Equal("Subscription ProviderSubscriptionReference must remain cleared after deletion.", error.Message);
    }

    [Fact]
    public async Task Worker_uses_TimeProvider_and_retries_provider_cleanup_safely()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        var subscription = await fixture.StartTrialAsync(fixture.OrganizationId, Start);
        await fixture.Store.RequestDeletionAsync(fixture.OrganizationId, Start.AddDays(1));
        var clock = new TestTimeProvider(Start.AddDays(1));
        var provider = new SequencedCleanupProvider(
            OrganizationBillingCleanupOutcome.RetryableFailure,
            OrganizationBillingCleanupOutcome.ConfirmedAbsent);
        var worker = new OrganizationBillingLifecycleWorker(fixture.Store, clock, provider);

        var first = await worker.ProcessAvailableAsync("worker");
        Assert.Equal(1, first.CleanupAttempts);
        Assert.Equal(OrganizationSubscriptionState.Suspended, await fixture.StateAsync(fixture.OrganizationId));

        clock.UtcNow = clock.UtcNow.AddSeconds(59);
        var tooEarly = await worker.ProcessAvailableAsync("worker");
        Assert.Equal(0, tooEarly.CleanupAttempts);

        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        var second = await worker.ProcessAvailableAsync("worker");
        Assert.Equal(1, second.CleanupAttempts);
        Assert.Equal(OrganizationSubscriptionState.Deleted, await fixture.StateAsync(fixture.OrganizationId));
        Assert.Equal(2, provider.Requests.Count);
        Assert.All(provider.Requests, request => Assert.Equal(subscription.Id, request.SubscriptionId));
    }

    [Fact]
    public async Task Internal_grant_deletion_cleanup_is_confirmed_without_any_provider_call()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        var granted = await fixture.Store.GrantInternalEntitlementAsync(
            new(fixture.OrganizationId, new("Internal dogfood grant", 2, Start.AddDays(30)), "operator"),
            Start);
        Assert.Equal(OrganizationInternalEntitlementOutcome.Granted, granted.Outcome);

        await fixture.Store.RequestDeletionAsync(fixture.OrganizationId, Start.AddDays(1));
        Assert.Equal(OrganizationSubscriptionState.Suspended, await fixture.StateAsync(fixture.OrganizationId));

        // A registered Stripe provider is present, but the queued cleanup is for
        // the "internal" provider, so it must never be dispatched to it.
        var provider = new SequencedCleanupProvider(OrganizationBillingCleanupOutcome.ConfirmedAbsent);
        var worker = new OrganizationBillingLifecycleWorker(fixture.Store, new TestTimeProvider(Start.AddDays(1)), provider);

        var result = await worker.ProcessAvailableAsync("worker");

        Assert.Equal(1, result.CleanupAttempts);
        Assert.Empty(provider.Requests);
        Assert.Equal(OrganizationSubscriptionState.Deleted, await fixture.StateAsync(fixture.OrganizationId));
        var cleanup = await fixture.Db.OrganizationBillingCleanups.SingleAsync();
        Assert.Equal(OrganizationBillingCleanupState.Confirmed, cleanup.State);
        Assert.Null(cleanup.LastFailureCode);

        // A second pass must not requeue or reclaim the confirmed cleanup.
        var second = await worker.ProcessAvailableAsync("worker");
        Assert.Equal(0, second.CleanupAttempts);
    }

    [Fact]
    public async Task Provider_timeout_is_recorded_as_retryable_when_the_host_token_is_not_cancelled()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        await fixture.StartTrialAsync(fixture.OrganizationId, Start);
        await fixture.Store.RequestDeletionAsync(fixture.OrganizationId, Start.AddDays(1));
        var clock = new TestTimeProvider(Start.AddDays(1));
        var worker = new OrganizationBillingLifecycleWorker(fixture.Store, clock, new TimeoutCleanupProvider());

        var result = await worker.ProcessAvailableAsync("worker");

        Assert.Equal(1, result.CleanupAttempts);
        var cleanup = await fixture.Db.OrganizationBillingCleanups.SingleAsync();
        Assert.Equal(OrganizationBillingCleanupState.Queued, cleanup.State);
        Assert.Equal("cleanup.provider-unavailable", cleanup.LastFailureCode);
    }

    [Fact]
    public async Task Provider_event_backfills_references_while_cleanup_is_leased_and_retry_preserves_them()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        await fixture.StartTrialAsync(fixture.OrganizationId, Start);
        var requestedAt = Start.AddDays(1);
        await fixture.Store.RequestDeletionAsync(fixture.OrganizationId, requestedAt);
        var claimed = Assert.IsType<OrganizationBillingCleanupWorkItem>(
            await fixture.Store.TryClaimCleanupAsync("worker", requestedAt));
        Assert.Null(claimed.ProviderCustomerReference);
        Assert.Null(claimed.ProviderSubscriptionReference);

        var providerEvent = new BillingProviderEvent(
            fixture.OrganizationId,
            "stripe",
            "evt_cleanup_refs",
            "customer.subscription.deleted",
            OrganizationSubscriptionState.Suspended,
            requestedAt.AddSeconds(1),
            "sha256:" + new string('a', 64),
            "cus_safe",
            "sub_safe");
        var consumed = await fixture.Store.ConsumeAsync(providerEvent, requestedAt.AddSeconds(1));
        Assert.Equal(BillingEventConsumptionOutcome.Applied, consumed.Outcome);

        await fixture.Store.CompleteCleanupAsync(new(
            claimed.Id,
            claimed.OrganizationId,
            claimed.SubscriptionId,
            claimed.LeaseToken,
            OrganizationBillingCleanupOutcome.RetryableFailure,
            requestedAt.AddSeconds(2),
            "provider.retry"));
        var retry = Assert.IsType<OrganizationBillingCleanupWorkItem>(
            await fixture.Store.TryClaimCleanupAsync("worker", requestedAt.AddMinutes(2)));
        Assert.Equal("cus_safe", retry.ProviderCustomerReference);
        Assert.Equal("sub_safe", retry.ProviderSubscriptionReference);
    }

    [Fact]
    public async Task One_pass_catches_up_every_overdue_milestone_without_skipping_notices()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        var subscription = await fixture.StartTrialAsync(fixture.OrganizationId, Start);
        var overdueAt = subscription.TrialEndsAt
            .Add(OrganizationSubscriptionLifecycle.PaymentGracePeriod)
            .Add(OrganizationSubscriptionLifecycle.ConstraintPeriod)
            .Add(OrganizationSubscriptionLifecycle.FinalRetentionPeriod);

        var advances = await fixture.Store.AdvanceDueAsync(overdueAt);

        Assert.Equal(
            [
                OrganizationSubscriptionState.PastDue,
                OrganizationSubscriptionState.Constrained,
                OrganizationSubscriptionState.Suspended,
                OrganizationSubscriptionState.Retained
            ],
            advances.Select(x => x.CurrentState));
        Assert.Equal(OrganizationSubscriptionState.Retained, await fixture.StateAsync(fixture.OrganizationId));
        Assert.Equal(5, await fixture.Db.OrganizationBillingLifecycleNotices.CountAsync());
        Assert.Single(await fixture.Db.OrganizationBillingCleanups.ToListAsync());
    }

    [Fact]
    public async Task Unrelated_state_cannot_bind_a_future_lifecycle_deadline()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        var subscription = await fixture.StartTrialAsync(fixture.OrganizationId, Start);
        subscription.GraceEndsAt = Start.AddDays(30);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Db.SaveChangesAsync());

        Assert.Equal("Subscription GraceEndsAt must match its lifecycle event.", error.Message);
    }

    [Fact]
    public async Task Overdue_grace_deadline_can_be_backfilled_from_canonical_past_due_timestamp()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        var subscription = await fixture.StartTrialAsync(fixture.OrganizationId, Start);
        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.PastDue, Start.AddDays(14), advanceLifecycleVersion: true);
        subscription.GraceEndsAt = null;
        await fixture.Db.SaveChangesAsync();

        fixture.Db.ChangeTracker.Clear();
        subscription = await fixture.SubscriptionAsync(fixture.OrganizationId);
        subscription.LastProviderEventOccurredAt = Start.AddDays(30);
        subscription.LastProviderEventId = "evt_later";
        subscription.GraceEndsAt = subscription.PastDueAt!.Value.Add(OrganizationSubscriptionLifecycle.PaymentGracePeriod);

        await fixture.Db.SaveChangesAsync();
        Assert.True(subscription.GraceEndsAt < subscription.LastProviderEventOccurredAt);
    }

    [Fact]
    public async Task Lifecycle_version_cannot_advance_without_a_state_transition()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        var subscription = await fixture.StartTrialAsync(fixture.OrganizationId, Start);
        subscription.LifecycleVersion++;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Db.SaveChangesAsync());

        Assert.Equal("Subscription lifecycle version can only advance with a state transition.", error.Message);
    }

    [Fact]
    public async Task Lifecycle_transition_cannot_bind_a_timestamp_for_an_unrelated_state()
    {
        await using var fixture = await LifecycleFixture.CreateAsync();
        var subscription = await fixture.StartTrialAsync(fixture.OrganizationId, Start);
        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.PastDue, Start.AddDays(14), advanceLifecycleVersion: true);
        subscription.ActivatedAt = Start.AddDays(14);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Db.SaveChangesAsync());

        Assert.Equal("Subscription ActivatedAt must match its lifecycle event.", error.Message);
    }

    private sealed class LifecycleFixture(SqliteConnection connection, CatalogDbContext db) : IAsyncDisposable
    {
        public Guid OrganizationId { get; } = Guid.NewGuid();
        public CatalogDbContext Db { get; } = db;
        public OrganizationBillingStore Store { get; } = new(db);

        public static async Task<LifecycleFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var fixture = new LifecycleFixture(connection, db);
            db.Organizations.Add(new Organization { Id = fixture.OrganizationId, Name = "Acme" });
            await db.SaveChangesAsync();
            return fixture;
        }

        public async Task<OrganizationSubscription> StartTrialAsync(Guid organizationId, DateTimeOffset startedAt)
        {
            var result = await Store.StartTrialAsync(organizationId, "stripe", startedAt);
            return Assert.IsType<OrganizationSubscription>(result.Subscription);
        }

        public async Task AdvanceAsync(DateTimeOffset now, OrganizationSubscriptionState expected)
        {
            var advances = await Store.AdvanceDueAsync(now);
            Assert.Single(advances);
            Assert.Equal(expected, advances[0].CurrentState);
            Assert.Equal(expected, await StateAsync(advances[0].OrganizationId));
        }

        public async Task<OrganizationSubscription> SubscriptionAsync(Guid organizationId)
        {
            Db.ChangeTracker.Clear();
            return await Db.OrganizationSubscriptions.SingleAsync(x => x.OrganizationId == organizationId);
        }

        public async Task<OrganizationSubscriptionState> StateAsync(Guid organizationId) =>
            (await SubscriptionAsync(organizationId)).State;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }


    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class SequencedCleanupProvider(params OrganizationBillingCleanupOutcome[] outcomes) : IOrganizationBillingCleanupProvider
    {
        private readonly Queue<OrganizationBillingCleanupOutcome> _outcomes = new(outcomes);
        public string Provider => "stripe";
        public List<OrganizationBillingCleanupRequest> Requests { get; } = [];

        public Task<OrganizationBillingCleanupOutcome> CleanupAsync(OrganizationBillingCleanupRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_outcomes.Dequeue());
        }
    }

    private sealed class TimeoutCleanupProvider : IOrganizationBillingCleanupProvider
    {
        public string Provider => "stripe";

        public Task<OrganizationBillingCleanupOutcome> CleanupAsync(OrganizationBillingCleanupRequest request, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException("provider timeout");
    }
}
