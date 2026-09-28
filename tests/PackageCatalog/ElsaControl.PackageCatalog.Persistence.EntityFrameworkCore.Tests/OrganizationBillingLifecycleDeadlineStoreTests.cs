using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class OrganizationBillingLifecycleDeadlineStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Moving_grace_into_the_past_then_advancing_projects_constraint_without_engine_writes()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.BecomePastDueAsync();
        var instance = fixture.SeedRunningInstance();
        var operationId = fixture.SeedRunningOperation(instance);
        var originalGrace = (await fixture.SubscriptionAsync()).GraceEndsAt;
        Assert.True(originalGrace > Now);

        var move = await fixture.Store.MoveDeadlineAsync(
            fixture.OrganizationId,
            OrganizationBillingLifecycleDeadline.GraceEndsAt,
            Now,
            "operator");
        fixture.Db.ChangeTracker.Clear();
        var advances = await fixture.Store.AdvanceDueAsync(Now);

        Assert.Equal(OrganizationBillingLifecycleDeadlineMoveOutcome.Moved, move.Outcome);
        Assert.Equal(originalGrace, move.PreviousDeadlineAt);
        Assert.Equal(Now, move.DeadlineAt);
        Assert.Equal(OrganizationSubscriptionState.PastDue, move.State);
        Assert.Single(advances);
        Assert.Equal(OrganizationSubscriptionState.PastDue, advances[0].PreviousState);
        Assert.Equal(OrganizationSubscriptionState.Constrained, advances[0].CurrentState);
        Assert.True(advances[0].NoticeCreated);

        var subscription = await fixture.SubscriptionAsync();
        var entitlement = await fixture.Db.OrganizationEntitlementSnapshots.AsNoTracking()
            .SingleAsync(x => x.OrganizationId == fixture.OrganizationId);
        Assert.Equal(OrganizationSubscriptionState.Constrained, subscription.State);
        Assert.Equal(Now, subscription.GraceEndsAt);
        Assert.Equal(Now, subscription.ConstrainedAt);
        Assert.Equal(OrganizationSubscriptionState.Constrained, entitlement.SubscriptionState);
        Assert.Contains(
            await fixture.Db.OrganizationBillingLifecycleNotices.AsNoTracking().Select(x => x.Kind).ToListAsync(),
            kind => kind == OrganizationBillingLifecycleNoticeKind.ConstraintStarted);

        var leverAudit = await fixture.Db.OrganizationAuditRecords.AsNoTracking().SingleAsync(x =>
            x.Action == OrganizationAuditAction.BillingLifecycleDeadlineMoved);
        Assert.Equal(OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject("operator"), leverAudit.OperatorSubject);
        Assert.Contains("GraceEndsAt", leverAudit.Summary, StringComparison.Ordinal);

        var storedInstance = await fixture.Db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == instance.Id);
        var storedOperation = await fixture.Db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == operationId);
        Assert.Equal(ElsaObservedLifecycle.Ready, storedInstance.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Healthy, storedInstance.Health);
        Assert.Equal(ElsaInstanceOperationState.Running, storedOperation.State);
        Assert.NotEqual(ElsaInstanceOperationState.RecoveryRequired, storedOperation.State);
    }

    [Fact]
    public async Task Moving_constraint_into_the_past_then_advancing_suspends_through_the_normal_advancer()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.BecomePastDueAsync();
        await fixture.Store.AdvanceDueAsync((await fixture.SubscriptionAsync()).GraceEndsAt!.Value);
        fixture.Db.ChangeTracker.Clear();
        var constrained = await fixture.SubscriptionAsync();
        Assert.Equal(OrganizationSubscriptionState.Constrained, constrained.State);

        var move = await fixture.Store.MoveDeadlineAsync(
            fixture.OrganizationId,
            OrganizationBillingLifecycleDeadline.ConstrainedAt,
            Now,
            "operator");
        fixture.Db.ChangeTracker.Clear();
        var advances = await fixture.Store.AdvanceDueAsync(Now);

        Assert.Equal(OrganizationBillingLifecycleDeadlineMoveOutcome.Moved, move.Outcome);
        Assert.Equal(Now.Subtract(OrganizationSubscriptionLifecycle.ConstraintPeriod), move.DeadlineAt);
        Assert.Single(advances);
        Assert.Equal(OrganizationSubscriptionState.Suspended, advances[0].CurrentState);
        Assert.Equal(OrganizationSubscriptionState.Suspended, (await fixture.SubscriptionAsync()).State);
    }

    [Fact]
    public async Task Already_due_constrained_at_is_refused_without_rewrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.BecomePastDueAsync();
        await fixture.Store.AdvanceDueAsync((await fixture.SubscriptionAsync()).GraceEndsAt!.Value);
        fixture.Db.ChangeTracker.Clear();
        var constrained = await fixture.SubscriptionAsync();
        var original = constrained.ConstrainedAt;
        Assert.Equal(OrganizationSubscriptionState.Constrained, constrained.State);
        Assert.NotNull(original);

        var move = await fixture.Store.MoveDeadlineAsync(
            fixture.OrganizationId,
            OrganizationBillingLifecycleDeadline.ConstrainedAt,
            original.Value.Add(OrganizationSubscriptionLifecycle.ConstraintPeriod),
            "operator");
        fixture.Db.ChangeTracker.Clear();

        Assert.Equal(OrganizationBillingLifecycleDeadlineMoveOutcome.DeadlineNotApplicable, move.Outcome);
        Assert.Equal(original, (await fixture.SubscriptionAsync()).ConstrainedAt);
        Assert.Equal(OrganizationSubscriptionState.Constrained, (await fixture.SubscriptionAsync()).State);
        Assert.Empty(await fixture.Db.OrganizationAuditRecords.AsNoTracking()
            .Where(x => x.Action == OrganizationAuditAction.BillingLifecycleDeadlineMoved)
            .ToListAsync());
    }

    [Fact]
    public async Task Unpermitted_deadline_rewrite_is_still_bind_once()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.BecomePastDueAsync();
        var subscription = await fixture.TrackedSubscriptionAsync();
        subscription.GraceEndsAt = Now;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Db.SaveChangesAsync());

        Assert.Equal("Subscription GraceEndsAt is bind-once.", error.Message);
    }

    [Fact]
    public async Task Permit_refuses_to_move_a_deadline_later()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.BecomePastDueAsync();
        var subscription = await fixture.TrackedSubscriptionAsync();
        var original = subscription.GraceEndsAt!.Value;
        fixture.Db.PermitLifecycleDeadlineOverride(subscription.Id, nameof(OrganizationSubscription.GraceEndsAt));
        subscription.GraceEndsAt = original.AddDays(1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Db.SaveChangesAsync());

        Assert.Equal("Subscription GraceEndsAt is bind-once.", error.Message);
    }

    private sealed class Fixture(SqliteConnection connection, CatalogDbContext db) : IAsyncDisposable
    {
        public Guid OrganizationId { get; } = Guid.NewGuid();
        public Guid WorkspaceId { get; } = Guid.NewGuid();
        public CatalogDbContext Db { get; } = db;
        public OrganizationBillingStore Store { get; } = new(db);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var fixture = new Fixture(connection, db);
            db.Organizations.Add(new Organization
            {
                Id = fixture.OrganizationId,
                Name = "Synthetic",
                CustomerReference = "synthetic"
            });
            db.Workspaces.Add(new Workspace
            {
                Id = fixture.WorkspaceId,
                OrganizationId = fixture.OrganizationId,
                Name = "Harness",
                Kind = WorkspaceKind.Shared
            });
            await db.SaveChangesAsync();
            return fixture;
        }

        public async Task BecomePastDueAsync()
        {
            await Store.StartTrialAsync(OrganizationId, BillingProviderNames.Stripe, Now.AddDays(-14));
            await Store.ConsumeAsync(
                new BillingProviderEvent(
                    OrganizationId,
                    BillingProviderNames.Stripe,
                    "evt_past_due",
                    "customer.subscription.updated",
                    OrganizationSubscriptionState.PastDue,
                    Now,
                    "sha256:" + new string('a', 64),
                    "cus_harness",
                    "sub_harness"),
                Now);
            Db.ChangeTracker.Clear();
        }

        public ElsaInstanceEntity SeedRunningInstance()
        {
            var instance = new ElsaInstanceEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = OrganizationId,
                WorkspaceId = WorkspaceId,
                Name = "Managed Elsa",
                Slug = "managed-elsa",
                DistributionId = "elsa",
                ReleaseLine = "3.0",
                Channel = "stable",
                PatchUpdates = "automatic-within-minor",
                MinorUpdates = "explicit-approval",
                MajorMigrations = "explicit-migration",
                TopologyId = "combined",
                FeatureOverridesJson = "{}",
                TargetMode = "managed",
                RegionCode = "westeurope",
                IsolationProfile = "dedicated",
                CapacityProfile = "standard",
                NetworkOutcome = "public",
                DomainOutcome = "managed",
                DesiredLifecycle = ElsaDesiredLifecycle.Running,
                ObservedLifecycle = ElsaObservedLifecycle.Ready,
                Health = ElsaInstanceHealth.Healthy,
                Version = 1,
                CreatedAt = Now,
                UpdatedAt = Now
            };
            Db.ElsaInstances.Add(instance);
            Db.SaveChanges();
            Db.ChangeTracker.Clear();
            return instance;
        }

        public Guid SeedRunningOperation(ElsaInstanceEntity instance)
        {
            var operation = new ElsaInstanceOperationEntity
            {
                Id = Guid.NewGuid(),
                InstanceId = instance.Id,
                OrganizationId = OrganizationId,
                WorkspaceId = WorkspaceId,
                Action = ElsaInstanceOperationAction.Reconcile,
                IdempotencyScope = $"instance/{instance.Id:N}",
                IdempotencyKey = "running",
                RequestHash = new string('a', 64),
                ExpectedVersion = 1,
                State = ElsaInstanceOperationState.Running,
                AttemptNumber = 1,
                AcceptedAt = Now,
                CreatedAt = Now,
                UpdatedAt = Now
            };
            Db.ElsaInstanceOperations.Add(operation);
            Db.SaveChanges();
            Db.ChangeTracker.Clear();
            return operation.Id;
        }

        public async Task<OrganizationSubscription> SubscriptionAsync()
        {
            Db.ChangeTracker.Clear();
            return await Db.OrganizationSubscriptions.AsNoTracking().SingleAsync(x => x.OrganizationId == OrganizationId);
        }

        public async Task<OrganizationSubscription> TrackedSubscriptionAsync()
        {
            Db.ChangeTracker.Clear();
            return await Db.OrganizationSubscriptions.SingleAsync(x => x.OrganizationId == OrganizationId);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
