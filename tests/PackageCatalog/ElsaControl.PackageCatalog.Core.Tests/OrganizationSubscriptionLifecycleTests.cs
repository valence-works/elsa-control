using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.PackageCatalog.Core.Tests;

public sealed class OrganizationSubscriptionLifecycleTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Trial_uses_the_decided_fourteen_day_window()
    {
        var subscription = OrganizationSubscriptionLifecycle.CreateTrial(Guid.NewGuid(), "stripe", StartedAt);

        Assert.Equal(OrganizationSubscriptionState.Trial, subscription.State);
        Assert.Equal(StartedAt, subscription.TrialStartedAt);
        Assert.Equal(StartedAt.AddDays(14), subscription.TrialEndsAt);
    }

    [Theory]
    [InlineData(OrganizationSubscriptionState.Trial, OrganizationSubscriptionState.Active, true)]
    [InlineData(OrganizationSubscriptionState.Active, OrganizationSubscriptionState.PastDue, true)]
    [InlineData(OrganizationSubscriptionState.Active, OrganizationSubscriptionState.Constrained, true)]
    [InlineData(OrganizationSubscriptionState.PastDue, OrganizationSubscriptionState.Constrained, true)]
    [InlineData(OrganizationSubscriptionState.Constrained, OrganizationSubscriptionState.Suspended, true)]
    [InlineData(OrganizationSubscriptionState.Suspended, OrganizationSubscriptionState.Retained, true)]
    [InlineData(OrganizationSubscriptionState.Suspended, OrganizationSubscriptionState.Active, false)]
    [InlineData(OrganizationSubscriptionState.Retained, OrganizationSubscriptionState.Deleted, true)]
    [InlineData(OrganizationSubscriptionState.Active, OrganizationSubscriptionState.Trial, false)]
    [InlineData(OrganizationSubscriptionState.Deleted, OrganizationSubscriptionState.Active, false)]
    public void Lifecycle_exposes_only_legal_transitions(
        OrganizationSubscriptionState current,
        OrganizationSubscriptionState next,
        bool expected)
    {
        Assert.Equal(expected, OrganizationSubscriptionLifecycle.CanTransition(current, next));
    }

    [Fact]
    public void Applying_a_state_records_the_transition_timestamp_in_utc()
    {
        var subscription = OrganizationSubscriptionLifecycle.CreateTrial(Guid.NewGuid(), "stripe", StartedAt);
        var occurredAt = StartedAt.AddHours(2).ToOffset(TimeSpan.FromHours(2));

        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Active, occurredAt);

        Assert.Equal(OrganizationSubscriptionState.Active, subscription.State);
        Assert.Equal(occurredAt.ToUniversalTime(), subscription.ActivatedAt);
        Assert.Equal(0, subscription.LifecycleVersion);
    }

    [Fact]
    public void Control_plane_transition_advances_the_lifecycle_version_once()
    {
        var subscription = OrganizationSubscriptionLifecycle.CreateTrial(Guid.NewGuid(), "stripe", StartedAt);

        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.PastDue, StartedAt.AddDays(14), advanceLifecycleVersion: true);

        Assert.Equal(1, subscription.LifecycleVersion);
    }

    [Fact]
    public void Same_state_event_backfills_grace_from_the_canonical_past_due_timestamp()
    {
        var subscription = OrganizationSubscriptionLifecycle.CreateTrial(Guid.NewGuid(), "stripe", StartedAt);
        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.PastDue, StartedAt.AddDays(14));
        subscription.GraceEndsAt = null;

        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.PastDue, StartedAt.AddDays(16));

        Assert.Equal(subscription.PastDueAt!.Value.Add(OrganizationSubscriptionLifecycle.PaymentGracePeriod), subscription.GraceEndsAt);
    }

    [Fact]
    public void Same_state_event_backfills_retention_from_the_canonical_suspension_timestamp()
    {
        var subscription = OrganizationSubscriptionLifecycle.CreateTrial(Guid.NewGuid(), "stripe", StartedAt);
        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Suspended, StartedAt.AddDays(14));
        subscription.RetentionEndsAt = null;

        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Suspended, StartedAt.AddDays(20));

        Assert.Equal(subscription.SuspendedAt!.Value.Add(OrganizationSubscriptionLifecycle.FinalRetentionPeriod), subscription.RetentionEndsAt);
    }

    [Fact]
    public void Suspended_stays_live_so_unrelated_source_conflicts_are_unchanged()
    {
        Assert.True(OrganizationSubscriptionLifecycle.IsLive(OrganizationSubscriptionState.Suspended));
        Assert.False(OrganizationSubscriptionLifecycle.IsLive(OrganizationSubscriptionState.Retained));
        Assert.False(OrganizationSubscriptionLifecycle.IsLive(OrganizationSubscriptionState.Deleted));
    }

    [Fact]
    public void Non_payment_suspension_can_return_to_active_on_the_same_row()
    {
        var subscription = OrganizationSubscriptionLifecycle.CreateTrial(Guid.NewGuid(), "stripe", StartedAt);
        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.PastDue, StartedAt.AddDays(14));
        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Constrained, StartedAt.AddDays(21));
        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Suspended, StartedAt.AddDays(22), advanceLifecycleVersion: true);
        subscription.ProviderCustomerReference = "cus_acme";
        subscription.ProviderSubscriptionReference = "sub_acme";
        var sameRef = new BillingProviderEvent(
            subscription.OrganizationId,
            "stripe",
            "evt-paid",
            "customer.subscription.updated",
            OrganizationSubscriptionState.Active,
            StartedAt.AddDays(23),
            "sha256:" + new string('a', 64),
            "cus_acme",
            "sub_acme");

        Assert.Equal(OrganizationSubscriptionSuspensionReason.NonPayment, subscription.SuspensionReason);
        Assert.True(OrganizationSubscriptionLifecycle.CanTransition(subscription, OrganizationSubscriptionState.Active));
        Assert.True(OrganizationSubscriptionLifecycle.AllowsNonPaymentResume(subscription, sameRef));
        Assert.False(OrganizationSubscriptionLifecycle.AllowsNonPaymentResume(
            subscription,
            sameRef with { ProviderSubscriptionReference = "sub_other" }));
        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Active, StartedAt.AddDays(23));

        Assert.Equal(OrganizationSubscriptionState.Active, subscription.State);
        Assert.Null(subscription.SuspensionReason);
        Assert.Equal(OrganizationBillingRecoveryActions.None, OrganizationSubscriptionLifecycle.RecoveryAction(subscription));
    }

    [Fact]
    public void Canceled_and_deletion_requested_suspensions_do_not_resume_on_the_same_row()
    {
        var canceled = OrganizationSubscriptionLifecycle.CreateTrial(Guid.NewGuid(), "stripe", StartedAt);
        OrganizationSubscriptionLifecycle.ApplyState(canceled, OrganizationSubscriptionState.Suspended, StartedAt.AddDays(1));
        Assert.Equal(OrganizationSubscriptionSuspensionReason.Canceled, canceled.SuspensionReason);
        Assert.Equal(OrganizationBillingRecoveryActions.Subscribe, OrganizationSubscriptionLifecycle.RecoveryAction(canceled));
        Assert.False(OrganizationSubscriptionLifecycle.CanTransition(canceled, OrganizationSubscriptionState.Active));
        Assert.Throws<InvalidOperationException>(() =>
            OrganizationSubscriptionLifecycle.ApplyState(canceled, OrganizationSubscriptionState.Active, StartedAt.AddDays(2)));

        var deletion = OrganizationSubscriptionLifecycle.CreateTrial(Guid.NewGuid(), "stripe", StartedAt);
        OrganizationSubscriptionLifecycle.ApplyState(deletion, OrganizationSubscriptionState.Active, StartedAt.AddDays(1));
        deletion.EarlyDeletionRequestedAt = StartedAt.AddDays(2);
        OrganizationSubscriptionLifecycle.ApplyState(deletion, OrganizationSubscriptionState.Suspended, StartedAt.AddDays(2), advanceLifecycleVersion: true);
        Assert.Equal(OrganizationSubscriptionSuspensionReason.DeletionRequested, deletion.SuspensionReason);
        Assert.Equal(OrganizationBillingRecoveryActions.None, OrganizationSubscriptionLifecycle.RecoveryAction(deletion));
        Assert.False(OrganizationSubscriptionLifecycle.CanTransition(deletion, OrganizationSubscriptionState.Active));
    }

    [Fact]
    public async Task Billing_service_uses_the_supplied_time_provider_for_trial_start()
    {
        var clock = new FixedTimeProvider(StartedAt.AddHours(3));
        var store = new RecordingBillingStore();

        await new OrganizationBillingService(store, clock).StartTrialAsync(Guid.NewGuid(), "stripe");

        Assert.Equal(clock.UtcNow, store.StartedAt);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class RecordingBillingStore : IOrganizationBillingStore
    {
        public DateTimeOffset StartedAt { get; private set; }

        public Task<BillingEventConsumptionResult> ConsumeAsync(BillingProviderEvent providerEvent, DateTimeOffset receivedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BillingEventConsumptionResult> RecordUnknownAsync(BillingProviderEvent providerEvent, DateTimeOffset receivedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BillingEventConsumptionResult> StartTrialAsync(Guid organizationId, string provider, DateTimeOffset startedAt, CancellationToken cancellationToken = default)
        {
            StartedAt = startedAt;
            return Task.FromResult(new BillingEventConsumptionResult(BillingEventConsumptionOutcome.Applied, null, null, null));
        }

        public Task<OrganizationSubscription?> GetSubscriptionAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<OrganizationSubscription?>(null);
    }
}
