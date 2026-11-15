using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class OrganizationBillingEngineResumeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrganizationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid InstanceId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private const string DatabaseIdentity = "deployment-intact";
    private const string TenantIdentity = "tenant-intact";
    private const string DeploymentEndpoint = "https://e1234567890abcde-app.region.azurecontainerapps.io";

    [Fact]
    public async Task Existing_engine_resumes_after_non_payment_without_reprovisioning()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.SeedReadyInstance();
        var operationId = fixture.SeedSucceededOperation(await fixture.InstanceAsync());
        var instance = await fixture.InstanceAsync();
        var store = fixture.Store;
        await fixture.SuspendForNonPaymentAsync();
        fixture.Db.ChangeTracker.Clear();

        AssertUnchangedReadyEngine(await fixture.InstanceAsync(), instance, operationId);
        Assert.Equal(ElsaInstanceCommercialOperation.LifecycleConstrained, (await fixture.Gate.EvaluateAsync(
            OrganizationId, ElsaInstanceOperationAction.Start)).Code);

        var resumed = await store.ConsumeAsync(
            Event("evt-paid", OrganizationSubscriptionState.Active, Now.AddDays(2)),
            Now.AddDays(2).AddMinutes(1));

        Assert.Equal(BillingEventConsumptionOutcome.Applied, resumed.Outcome);
        Assert.Equal(OrganizationSubscriptionState.Active, resumed.Subscription!.State);
        Assert.Equal(1, resumed.Entitlement!.MaxInstances);
        fixture.Db.ChangeTracker.Clear();
        AssertUnchangedReadyEngine(await fixture.InstanceAsync(), instance, operationId);
        Assert.Equal(1, await fixture.Db.ElsaInstances.CountAsync());
        Assert.True((await fixture.Gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Start)).Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.InstanceLimitReached, (await fixture.Gate.EvaluateAsync(
            OrganizationId, ElsaInstanceOperationAction.Create, activeInstanceCount: 1)).Code);
        Assert.Empty(await fixture.Db.ElsaInstanceOperations.Where(x =>
            x.State == ElsaInstanceOperationState.RecoveryRequired).ToListAsync());
        Assert.Equal(ElsaInstanceOperationState.Succeeded,
            (await fixture.Db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == operationId)).State);
    }

    [Fact]
    public async Task Existing_engine_resumes_after_canceled_resubscribe_without_reprovisioning()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.SeedReadyInstance();
        var operationId = fixture.SeedSucceededOperation(await fixture.InstanceAsync());
        var instance = await fixture.InstanceAsync();
        var store = fixture.Store;
        await fixture.SuspendForNonPaymentAsync();
        await store.ConsumeAsync(
            Event("evt-deleted", OrganizationSubscriptionState.Suspended, Now.AddDays(2)) with
            {
                EventType = "customer.subscription.deleted"
            },
            Now.AddDays(2).AddMinutes(1));
        fixture.Db.ChangeTracker.Clear();
        AssertUnchangedReadyEngine(await fixture.InstanceAsync(), instance, operationId);

        var resubscribed = await store.ConsumeAsync(
            Event(OrganizationId, "evt-resubscribe", OrganizationSubscriptionState.Active,
                Now.AddDays(2).AddMinutes(5), "cus_acme", "sub_paid_again") with
            {
                EventType = "customer.subscription.created"
            },
            Now.AddDays(2).AddMinutes(6));

        Assert.Equal(BillingEventConsumptionOutcome.Applied, resubscribed.Outcome);
        Assert.Equal(OrganizationSubscriptionState.Active, resubscribed.Subscription!.State);
        Assert.Equal("sub_paid_again", resubscribed.Subscription.ProviderSubscriptionReference);
        Assert.Equal(1, resubscribed.Entitlement!.MaxInstances);
        fixture.Db.ChangeTracker.Clear();
        AssertUnchangedReadyEngine(await fixture.InstanceAsync(), instance, operationId);
        Assert.Equal(1, await fixture.Db.ElsaInstances.CountAsync());
        Assert.Equal(InstanceId, (await fixture.Db.ElsaInstances.SingleAsync()).Id);
        Assert.True((await fixture.Gate.EvaluateAsync(OrganizationId, ElsaInstanceOperationAction.Start)).Allowed);
        Assert.True((await fixture.Gate.EvaluateAsync(
            OrganizationId, ElsaInstanceOperationAction.Create, activeInstanceCount: 0)).Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.InstanceLimitReached, (await fixture.Gate.EvaluateAsync(
            OrganizationId, ElsaInstanceOperationAction.Create, activeInstanceCount: 1)).Code);
        Assert.Empty(await fixture.Db.ElsaInstanceOperations.Where(x =>
            x.State == ElsaInstanceOperationState.RecoveryRequired).ToListAsync());
        Assert.Equal(ElsaInstanceOperationState.Succeeded,
            (await fixture.Db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == operationId)).State);
    }

    private static void AssertUnchangedReadyEngine(
        ElsaInstanceEntity stored,
        ElsaInstanceEntity original,
        Guid succeededOperationId)
    {
        Assert.Equal(original.Id, stored.Id);
        Assert.Equal(original.Version, stored.Version);
        Assert.Equal(original.CreatedAt, stored.CreatedAt);
        Assert.Null(stored.DeletedAt);
        Assert.Equal(ElsaDesiredLifecycle.Running, stored.DesiredLifecycle);
        Assert.Equal(ElsaObservedLifecycle.Ready, stored.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Healthy, stored.Health);
        Assert.Equal(DatabaseIdentity, stored.CurrentDeploymentId);
        Assert.Equal(TenantIdentity, stored.ElsaTenantId);
        Assert.Equal(DeploymentEndpoint, stored.CurrentDeploymentEndpointUri);
        Assert.Equal(succeededOperationId.ToString("D"), stored.LastOperationId);
    }

    private static BillingProviderEvent Event(
        string id,
        OrganizationSubscriptionState state,
        DateTimeOffset occurredAt) =>
        Event(OrganizationId, id, state, occurredAt, "cus_acme", "sub_acme");

    private static BillingProviderEvent Event(
        Guid organizationId,
        string id,
        OrganizationSubscriptionState state,
        DateTimeOffset occurredAt,
        string customerReference,
        string subscriptionReference) =>
        new(organizationId, "stripe", id, "subscription." + state.ToString().ToLowerInvariant(), state, occurredAt,
            "sha256:" + new string('a', 64), customerReference, subscriptionReference);

    private sealed class Fixture(SqliteConnection connection, CatalogDbContext db) : IAsyncDisposable
    {
        public CatalogDbContext Db { get; } = db;
        public OrganizationBillingStore Store { get; } = new(db);
        public EfCoreElsaInstanceCommercialGate Gate { get; } = new(db);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var fixture = new Fixture(connection, db);
            db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Acme" });
            db.Workspaces.Add(new Workspace
            {
                Id = WorkspaceId,
                OrganizationId = OrganizationId,
                Name = "Harness",
                Kind = WorkspaceKind.Shared
            });
            await db.SaveChangesAsync();
            return fixture;
        }

        public ElsaInstanceEntity SeedReadyInstance()
        {
            var instance = new ElsaInstanceEntity
            {
                Id = InstanceId,
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
                CurrentDeploymentId = DatabaseIdentity,
                CurrentDeploymentEndpointUri = DeploymentEndpoint,
                ElsaTenantId = TenantIdentity,
                Version = 4,
                CreatedAt = Now.AddDays(-10),
                UpdatedAt = Now.AddDays(-1)
            };
            Db.ElsaInstances.Add(instance);
            Db.SaveChanges();
            Db.ChangeTracker.Clear();
            return instance;
        }

        public Guid SeedSucceededOperation(ElsaInstanceEntity instance)
        {
            var operation = new ElsaInstanceOperationEntity
            {
                Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                InstanceId = instance.Id,
                OrganizationId = OrganizationId,
                WorkspaceId = WorkspaceId,
                Action = ElsaInstanceOperationAction.Create,
                IdempotencyScope = $"instance/{instance.Id:N}",
                IdempotencyKey = "create",
                RequestHash = new string('a', 64),
                ExpectedVersion = 1,
                State = ElsaInstanceOperationState.Succeeded,
                AttemptNumber = 1,
                AcceptedAt = Now.AddDays(-10),
                CreatedAt = Now.AddDays(-10),
                UpdatedAt = Now.AddDays(-9)
            };
            Db.ElsaInstanceOperations.Add(operation);
            var stored = Db.ElsaInstances.Single(x => x.Id == instance.Id);
            stored.LastOperationId = operation.Id.ToString("D");
            Db.SaveChanges();
            Db.ChangeTracker.Clear();
            return operation.Id;
        }

        public async Task SuspendForNonPaymentAsync()
        {
            await Store.ConsumeAsync(Event("evt-active", OrganizationSubscriptionState.Active, Now.AddMinutes(1)), Now.AddMinutes(2));
            await Store.ConsumeAsync(Event("evt-past-due", OrganizationSubscriptionState.PastDue, Now.AddMinutes(3)), Now.AddMinutes(4));
            await Store.ConsumeAsync(Event("evt-constrained", OrganizationSubscriptionState.Constrained, Now.AddMinutes(5)), Now.AddMinutes(6));
            var current = Assert.IsType<OrganizationSubscription>(await Store.GetSubscriptionAsync(OrganizationId));
            var suspensionAt = current.ConstrainedAt!.Value.Add(OrganizationSubscriptionLifecycle.ConstraintPeriod);
            await Store.AdvanceOneAsync(OrganizationId, current.Id, suspensionAt);
            Db.ChangeTracker.Clear();
        }

        public async Task<ElsaInstanceEntity> InstanceAsync()
        {
            Db.ChangeTracker.Clear();
            return await Db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == InstanceId);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
