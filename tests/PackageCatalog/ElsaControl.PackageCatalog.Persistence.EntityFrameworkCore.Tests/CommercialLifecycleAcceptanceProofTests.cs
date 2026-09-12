using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class CommercialLifecycleAcceptanceProofTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Complete_provider_neutral_lifecycle_projects_entitlements_and_preserves_safe_exits()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var organization = new Organization { Name = "Proof organization" };
        db.Organizations.Add(organization);
        db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = organization.Id,
            ManagedHostingEnabled = true,
            DeploymentTargetsEnabled = true,
            MaxInstances = 1,
            MaxWorkspaces = 2,
            MaxSources = 3,
            CreatedAt = Start,
            UpdatedAt = Start,
            SyncedAt = Start
        });
        await db.SaveChangesAsync();
        var store = new OrganizationBillingStore(db);
        var gate = new EfCoreElsaInstanceCommercialGate(db);

        var trial = await store.StartTrialAsync(organization.Id, "stripe", Start);
        Assert.Equal(OrganizationSubscriptionState.Trial, trial.Entitlement!.SubscriptionState);
        Assert.True((await gate.EvaluateAsync(organization.Id, ElsaInstanceOperationAction.Create, 0)).Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.InstanceLimitReached,
            (await gate.EvaluateAsync(organization.Id, ElsaInstanceOperationAction.Create, 1)).Code);
        db.ChangeTracker.Clear();

        var activeEvent = Event(organization.Id, "evt_active", OrganizationSubscriptionState.Active, Start.AddHours(1));
        var active = await store.ConsumeAsync(activeEvent, Start.AddHours(1));
        var replay = await store.ConsumeAsync(activeEvent, Start.AddHours(2));
        Assert.Equal(BillingEventConsumptionOutcome.Applied, active.Outcome);
        Assert.Equal(BillingEventConsumptionOutcome.Replayed, replay.Outcome);
        Assert.Equal(OrganizationSubscriptionState.Active, active.Entitlement!.SubscriptionState);
        Assert.True(active.Entitlement.ManagedHostingEnabled);
        Assert.Equal(1, active.Entitlement.MaxInstances);
        db.ChangeTracker.Clear();

        var pastDue = await store.ConsumeAsync(
            Event(organization.Id, "evt_past_due", OrganizationSubscriptionState.PastDue, Start.AddHours(3)),
            Start.AddHours(3));
        Assert.Equal(OrganizationSubscriptionState.PastDue, pastDue.Entitlement!.SubscriptionState);
        Assert.True((await gate.EvaluateAsync(organization.Id, ElsaInstanceOperationAction.UpdateIntent)).Allowed);
        db.ChangeTracker.Clear();
        var outOfOrder = await store.ConsumeAsync(
            Event(organization.Id, "evt_old", OrganizationSubscriptionState.Active, Start.AddHours(2)),
            Start.AddHours(4));
        Assert.Equal(BillingEventConsumptionOutcome.IgnoredOutOfOrder, outOfOrder.Outcome);
        db.ChangeTracker.Clear();

        var subscription = await ReloadSubscriptionAsync(db);
        var graceEndsAt = subscription.GraceEndsAt!.Value;
        db.ChangeTracker.Clear();
        var constrainedAdvances = await store.AdvanceDueAsync(graceEndsAt.AddTicks(1));
        Assert.Single(constrainedAdvances);
        Assert.Equal(OrganizationSubscriptionState.Constrained, constrainedAdvances[0].CurrentState);
        db.ChangeTracker.Clear();
        Assert.Equal(OrganizationSubscriptionState.Constrained, (await CurrentEntitlementAsync(db)).SubscriptionState);
        Assert.False((await gate.EvaluateAsync(organization.Id, ElsaInstanceOperationAction.Create, 0)).Allowed);
        Assert.True((await gate.EvaluateAsync(organization.Id, ElsaInstanceOperationAction.Stop)).Allowed);
        Assert.True((await gate.EvaluateAsync(organization.Id, ElsaInstanceOperationAction.Delete)).Allowed);
        db.ChangeTracker.Clear();

        subscription = await ReloadSubscriptionAsync(db);
        var suspensionAt = subscription.ConstrainedAt!.Value.Add(OrganizationSubscriptionLifecycle.ConstraintPeriod);
        db.ChangeTracker.Clear();
        await store.AdvanceDueAsync(suspensionAt.AddTicks(1));
        db.ChangeTracker.Clear();
        Assert.Equal(OrganizationSubscriptionState.Suspended, (await CurrentEntitlementAsync(db)).SubscriptionState);
        Assert.True((await gate.EvaluateAsync(organization.Id, ElsaInstanceOperationAction.Stop)).Allowed);
        db.ChangeTracker.Clear();

        subscription = await ReloadSubscriptionAsync(db);
        var retentionEndsAt = subscription.RetentionEndsAt!.Value;
        db.ChangeTracker.Clear();
        await store.AdvanceDueAsync(retentionEndsAt.AddTicks(1));
        db.ChangeTracker.Clear();
        Assert.Equal(OrganizationSubscriptionState.Retained, (await CurrentEntitlementAsync(db)).SubscriptionState);
        var notices = await db.OrganizationBillingLifecycleNotices.AsNoTracking().ToListAsync();
        Assert.Equal(4, notices.Count);
        Assert.Equal(
            [
                OrganizationBillingLifecycleNoticeKind.ConstraintStarted,
                OrganizationBillingLifecycleNoticeKind.SuspensionStarted,
                OrganizationBillingLifecycleNoticeKind.ExportAvailable,
                OrganizationBillingLifecycleNoticeKind.DeletionScheduled
            ],
            notices.Select(x => x.Kind).Order().ToArray());
        Assert.All(notices, x => Assert.Equal(organization.Id, x.OrganizationId));
        Assert.DoesNotContain(notices, x => x.LastFailureCode is not null);

        var cleanupProvider = new RecordingCleanupProvider(
            OrganizationBillingCleanupOutcome.RetryableFailure,
            OrganizationBillingCleanupOutcome.ConfirmedAbsent);
        var cleanupClock = new MutableTimeProvider(retentionEndsAt.AddTicks(1));
        var worker = new OrganizationBillingLifecycleWorker(
            store,
            cleanupClock,
            cleanupProvider);
        var firstWorkerResult = await worker.ProcessAvailableAsync("proof-worker");
        Assert.Equal(1, firstWorkerResult.CleanupAttempts);
        Assert.Equal(OrganizationSubscriptionState.Retained, (await CurrentEntitlementAsync(db)).SubscriptionState);
        cleanupClock.UtcNow = cleanupClock.UtcNow.AddSeconds(59);
        Assert.Equal(0, (await worker.ProcessAvailableAsync("proof-worker")).CleanupAttempts);
        cleanupClock.UtcNow = cleanupClock.UtcNow.AddSeconds(1);
        Assert.Equal(1, (await worker.ProcessAvailableAsync("proof-worker")).CleanupAttempts);
        var cleanupRequest = Assert.Single(cleanupProvider.Requests.DistinctBy(x => x.SubscriptionId));
        Assert.Equal(organization.Id, cleanupRequest.OrganizationId);
        Assert.Equal("cus_proof", cleanupRequest.ProviderCustomerReference);
        Assert.Equal("sub_proof", cleanupRequest.ProviderSubscriptionReference);
        db.ChangeTracker.Clear();
        Assert.Equal(OrganizationSubscriptionState.Deleted, (await CurrentEntitlementAsync(db)).SubscriptionState);
        Assert.False((await gate.EvaluateAsync(organization.Id, ElsaInstanceOperationAction.Create, 0)).Allowed);
        Assert.True((await gate.EvaluateAsync(organization.Id, ElsaInstanceOperationAction.Delete)).Allowed);

        var providerEvents = await db.BillingProviderEvents.AsNoTracking().ToListAsync();
        Assert.Equal(3, providerEvents.Count);
        var audits = await db.OrganizationAuditRecords.AsNoTracking().ToListAsync();
        Assert.NotEmpty(audits);
        Assert.All(audits,
            audit => Assert.Equal(organization.Id, audit.OrganizationId));
        var cleanups = await db.OrganizationBillingCleanups.AsNoTracking().ToListAsync();
        var persistedEvidence = string.Join(' ',
            audits.Select(x => $"{x.TargetType} {x.TargetId} {x.Summary}")
                .Concat(providerEvents.Select(x => $"{x.EventType} {x.RejectionCode} {x.ProviderCustomerReference} {x.ProviderSubscriptionReference}"))
                .Concat(notices.Select(x => $"{x.Kind} {x.State} {x.DeliveryStatus} {x.LastFailureCode}"))
                .Concat(cleanups.Select(x => $"{x.CleanupKey} {x.LastFailureCode} {x.ProviderCustomerReference} {x.ProviderSubscriptionReference}")));
        Assert.DoesNotContain("price_", persistedEvidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", persistedEvidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Early_deletion_atomically_suspends_the_commercial_gate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var organization = new Organization { Name = "Early deletion proof" };
        db.Organizations.Add(organization);
        db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = organization.Id,
            ManagedHostingEnabled = true,
            MaxInstances = 1,
            CreatedAt = Start,
            UpdatedAt = Start,
            SyncedAt = Start
        });
        await db.SaveChangesAsync();
        var store = new OrganizationBillingStore(db);
        await store.StartTrialAsync(organization.Id, "stripe", Start);
        await store.ConsumeAsync(
            Event(organization.Id, "evt_active_delete", OrganizationSubscriptionState.Active, Start.AddHours(1)),
            Start.AddHours(1));
        db.ChangeTracker.Clear();

        await store.RequestDeletionAsync(organization.Id, Start.AddHours(2));
        db.ChangeTracker.Clear();

        Assert.Equal(OrganizationSubscriptionState.Suspended, (await CurrentEntitlementAsync(db)).SubscriptionState);
        var gate = new EfCoreElsaInstanceCommercialGate(db);
        Assert.False((await gate.EvaluateAsync(organization.Id, ElsaInstanceOperationAction.Create, 0)).Allowed);
        Assert.True((await gate.EvaluateAsync(organization.Id, ElsaInstanceOperationAction.Stop)).Allowed);
        Assert.True((await gate.EvaluateAsync(organization.Id, ElsaInstanceOperationAction.Delete)).Allowed);
    }

    private static BillingProviderEvent Event(Guid organizationId, string id, OrganizationSubscriptionState state, DateTimeOffset occurredAt) =>
        new(organizationId, "stripe", id, "customer.subscription.updated", state, occurredAt,
            "sha256:" + new string('a', 64), "cus_proof", "sub_proof");

    private static async Task<OrganizationSubscription> ReloadSubscriptionAsync(CatalogDbContext db)
    {
        db.ChangeTracker.Clear();
        return await db.OrganizationSubscriptions.AsNoTracking().SingleAsync();
    }

    private static async Task<OrganizationEntitlementSnapshot> CurrentEntitlementAsync(CatalogDbContext db)
    {
        db.ChangeTracker.Clear();
        return await db.OrganizationEntitlementSnapshots.AsNoTracking().SingleAsync();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class RecordingCleanupProvider(params OrganizationBillingCleanupOutcome[] outcomes) : IOrganizationBillingCleanupProvider
    {
        private readonly Queue<OrganizationBillingCleanupOutcome> _outcomes = new(outcomes);
        public string Provider => "stripe";
        public List<OrganizationBillingCleanupRequest> Requests { get; } = [];

        public Task<OrganizationBillingCleanupOutcome> CleanupAsync(
            OrganizationBillingCleanupRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_outcomes.Dequeue());
        }
    }
}
