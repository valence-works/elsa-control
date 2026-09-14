using System.Data.Common;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

/// <summary>
/// Health monitor persistence against a migrated SQLite catalog, so the migration-created
/// guard triggers on instances and their append-only audit stream are live.
/// </summary>
public sealed class ElsaInstanceHealthMonitorStoreTests : IAsyncDisposable
{
    private const string Endpoint = "https://e1234567890abcde-app.region.azurecontainerapps.io";
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly ElsaInstanceOperationState[] BlockingStates =
    [
        ElsaInstanceOperationState.Accepted,
        ElsaInstanceOperationState.WaitingForPriorOperation,
        ElsaInstanceOperationState.Queued,
        ElsaInstanceOperationState.EntitlementHeld,
        ElsaInstanceOperationState.Running,
        ElsaInstanceOperationState.RecoveryRequired
    ];

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CatalogDbContext _db;
    private readonly EfCoreElsaInstanceLifecycleStore _store;
    private readonly Workspace _workspace = new() { Name = "Health monitor workspace" };

    public ElsaInstanceHealthMonitorStoreTests()
    {
        _connection.Open();
        _db = CreateContext();
        _db.Database.Migrate();
        _db.Workspaces.Add(_workspace);
        _db.SaveChanges();
        _store = new(_db, new UnavailableElsaInstanceLifecycleResolutionInputSource(), new FixedTimeProvider(Now));
    }

    [Fact]
    public async Task Lists_exactly_live_ready_managed_instances_with_an_endpoint_and_no_blocking_operation()
    {
        var eligible = new[]
        {
            AddInstance(),
            AddInstance(health: ElsaInstanceHealth.Unreachable),
            AddInstance(operation: ElsaInstanceOperationState.Succeeded),
            AddInstance(operation: ElsaInstanceOperationState.Failed),
            AddInstance(operation: ElsaInstanceOperationState.Cancelled)
        };
        var ineligible = new List<ElsaInstanceEntity>
        {
            AddInstance(desired: ElsaDesiredLifecycle.Stopped),
            AddInstance(desired: ElsaDesiredLifecycle.Deleting, observed: ElsaObservedLifecycle.Deleting),
            AddInstance(desired: ElsaDesiredLifecycle.Deleting, observed: ElsaObservedLifecycle.Deleted),
            AddInstance(observed: ElsaObservedLifecycle.Degraded),
            AddInstance(observed: ElsaObservedLifecycle.Updating),
            AddInstance(targetMode: "self-hosted"),
            AddInstance(endpoint: false)
        };
        ineligible.AddRange(BlockingStates.Select(state => AddInstance(operation: state)));

        var targets = await _store.ListHealthMonitorTargetsAsync(0, 100);

        Assert.Equal(eligible.Select(x => x.Id).Order(), targets.Select(x => x.InstanceId).Order());
        var unreachable = Assert.Single(targets, x => x.InstanceId == eligible[1].Id);
        Assert.Equal(
            (_workspace.OrganizationId, _workspace.Id, 1, ElsaDesiredLifecycle.Running, ElsaObservedLifecycle.Ready,
                ElsaInstanceHealth.Unreachable, Endpoint),
            (unreachable.OrganizationId, unreachable.WorkspaceId, unreachable.Version, unreachable.DesiredLifecycle,
                unreachable.ObservedLifecycle, unreachable.Health, unreachable.CurrentDeployment.EndpointUri));
        foreach (var target in targets)
            Assert.Equal(target, await _store.GetHealthMonitorTargetAsync(_workspace.Id, target.InstanceId));
        foreach (var instance in ineligible)
            Assert.Null(await _store.GetHealthMonitorTargetAsync(_workspace.Id, instance.Id));
        Assert.Null(await _store.GetHealthMonitorTargetAsync(Guid.NewGuid(), eligible[0].Id));
    }

    [Fact]
    public async Task Listing_pages_through_every_target_without_overlap()
    {
        var instanceIds = Enumerable.Range(0, 5).Select(_ => AddInstance().Id).ToArray();

        var pages = new[]
        {
            await _store.ListHealthMonitorTargetsAsync(0, 2),
            await _store.ListHealthMonitorTargetsAsync(2, 2),
            await _store.ListHealthMonitorTargetsAsync(4, 2)
        };

        Assert.Equal(new[] { 2, 2, 1 }, pages.Select(page => page.Count));
        Assert.Equal(instanceIds.Order(), pages.SelectMany(page => page).Select(x => x.InstanceId).Order());
    }

    [Fact]
    public async Task Transitions_change_only_health_and_append_one_audit_event_each()
    {
        var instance = AddInstance();
        var before = await ReadAsync(instance.Id);

        var unhealthy = await _store.CommitHealthTransitionAsync(
            Transition(instance.Id, 1, ElsaInstanceHealth.Healthy, ElsaInstanceHealth.Unreachable, "azure.health.timed-out"));
        var recovered = await _store.CommitHealthTransitionAsync(
            Transition(instance.Id, unhealthy, ElsaInstanceHealth.Unreachable, ElsaInstanceHealth.Healthy, "azure.health.healthy"));

        var after = await ReadAsync(instance.Id);
        Assert.Equal((2, 3, ElsaInstanceHealth.Healthy, Now), (unhealthy, after.Version, after.Health, after.UpdatedAt));
        Assert.Equal(
            (before.DesiredLifecycle, before.ObservedLifecycle, before.CurrentDeploymentId, before.CurrentDeploymentEndpointUri, before.Name),
            (after.DesiredLifecycle, after.ObservedLifecycle, after.CurrentDeploymentId, after.CurrentDeploymentEndpointUri, after.Name));
        var audit = await AuditAsync(instance.Id);
        Assert.Equal(
            new (long, string, string?, string?, string?)[]
            {
                (1L, "lifecycle.health-changed", "Healthy", "Unreachable", "azure.health.timed-out"),
                (2L, "lifecycle.health-changed", "Unreachable", "Healthy", "azure.health.healthy")
            },
            audit.Select(x => (x.Sequence, x.EventType, x.PriorState, x.NewState, x.DiagnosticCode)));
        Assert.All(audit, x => Assert.Equal(((Guid?)null, (Guid?)null, x.DiagnosticCode, Now),
            (x.OperationId, x.ActorAccountId, x.Summary, x.OccurredAt)));
        Assert.Equal(3, recovered);
    }

    [Fact]
    public async Task A_lost_acknowledgement_after_a_health_transition_commit_returns_the_committed_version()
    {
        var instance = AddInstance();
        var acknowledgement = new LostCommitAcknowledgementInterceptor();
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(_connection,
                sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly),
                isTransient: exception => exception is LostCommitAcknowledgementException)
            .AddInterceptors(acknowledgement)
            .Options;
        await using var db = new CatalogDbContext(options);
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, new UnavailableElsaInstanceLifecycleResolutionInputSource(), new FixedTimeProvider(Now));

        var version = await store.CommitHealthTransitionAsync(
            Transition(instance.Id, 1, ElsaInstanceHealth.Healthy, ElsaInstanceHealth.Unreachable,
                "azure.health.timed-out"));

        Assert.Equal(2, version);
        Assert.Equal(1, acknowledgement.Committed);
        await using var verify = CreateContext();
        Assert.Equal(2, await verify.ElsaInstances.AsNoTracking().Where(x => x.Id == instance.Id)
            .Select(x => x.Version).SingleAsync());
        Assert.Single(await verify.ElsaInstanceAuditEvents.AsNoTracking()
            .Where(x => x.InstanceId == instance.Id && x.EventType == "lifecycle.health-changed").ToListAsync());
    }

    [Theory]
    [InlineData("renamed")]
    [InlineData("stop-accepted")]
    [InlineData("operation-row-only")]
    [InlineData("deleting")]
    [InlineData("stale-expected-health")]
    public async Task A_transition_over_a_concurrent_change_conflicts_and_leaves_that_change_intact(string change)
    {
        var instance = AddInstance();
        var expectedHealth = ElsaInstanceHealth.Healthy;
        await using (var writer = CreateContext())
        {
            var concurrent = await writer.ElsaInstances.SingleAsync(x => x.Id == instance.Id);
            switch (change)
            {
                case "renamed":
                    concurrent.Name = "Renamed concurrently";
                    break;
                case "stop-accepted":
                    concurrent.DesiredLifecycle = ElsaDesiredLifecycle.Stopped;
                    writer.ElsaInstanceOperations.Add(Operation(concurrent, ElsaInstanceOperationState.Accepted, ElsaInstanceOperationAction.Stop));
                    break;
                case "operation-row-only":
                    // A blocking operation that did not move the instance version must still win.
                    writer.ElsaInstanceOperations.Add(Operation(concurrent, ElsaInstanceOperationState.Queued));
                    break;
                case "deleting":
                    concurrent.DesiredLifecycle = ElsaDesiredLifecycle.Deleting;
                    break;
                default:
                    expectedHealth = ElsaInstanceHealth.Unreachable;
                    break;
            }
            await writer.SaveChangesAsync();
        }
        var concurrentState = await ReadAsync(instance.Id);

        await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() => _store.CommitHealthTransitionAsync(
            Transition(instance.Id, 1, expectedHealth, ElsaInstanceHealth.Degraded, "azure.health.not-healthy")));

        var after = await ReadAsync(instance.Id);
        Assert.Equal(
            (concurrentState.Version, concurrentState.Name, concurrentState.DesiredLifecycle, ElsaInstanceHealth.Healthy),
            (after.Version, after.Name, after.DesiredLifecycle, after.Health));
        Assert.Empty(await AuditAsync(instance.Id));
        Assert.False(_db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task The_monitor_records_a_completed_streak_through_the_relational_store()
    {
        var instance = AddInstance();
        var probe = new FixedProbe(new(ElsaInstanceHealth.Unreachable, "azure.health.unreachable"));
        var monitor = new ElsaInstanceHealthMonitor(new() { Enabled = true, MaxJitter = TimeSpan.Zero }, new FixedTimeProvider(Now));

        for (var cycle = 0; cycle < 3; cycle++)
            await monitor.RunCycleAsync(_store, () => new(_store, probe));

        var after = await ReadAsync(instance.Id);
        Assert.Equal((ElsaInstanceHealth.Unreachable, ElsaObservedLifecycle.Ready, 2), (after.Health, after.ObservedLifecycle, after.Version));
        var audit = Assert.Single(await AuditAsync(instance.Id));
        Assert.Equal(("Healthy", "Unreachable", "azure.health.unreachable"), (audit.PriorState, audit.NewState, audit.DiagnosticCode));
        Assert.Equal(3, probe.Requests.Count);
        Assert.All(probe.Requests, request => Assert.Equal(Endpoint, request.CurrentDeployment.EndpointUri));
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private ElsaInstanceEntity AddInstance(
        ElsaDesiredLifecycle desired = ElsaDesiredLifecycle.Running,
        ElsaObservedLifecycle observed = ElsaObservedLifecycle.Ready,
        ElsaInstanceHealth health = ElsaInstanceHealth.Healthy,
        string targetMode = "managed",
        bool endpoint = true,
        ElsaInstanceOperationState? operation = null)
    {
        var instance = new ElsaInstanceEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = _workspace.OrganizationId,
            WorkspaceId = _workspace.Id,
            Name = "Managed Elsa",
            Slug = "managed-elsa-" + Guid.NewGuid().ToString("N")[..8],
            DistributionId = "elsa",
            ReleaseLine = "3.0",
            Channel = "stable",
            PatchUpdates = "automatic-within-minor",
            MinorUpdates = "explicit-approval",
            MajorMigrations = "explicit-migration",
            TopologyId = "combined",
            FeatureOverridesJson = "{}",
            TargetMode = targetMode,
            RegionCode = "westeurope",
            IsolationProfile = "dedicated",
            CapacityProfile = "standard",
            NetworkOutcome = "public",
            DomainOutcome = "managed",
            DesiredLifecycle = desired,
            ObservedLifecycle = observed,
            Health = health,
            CurrentDeploymentId = endpoint ? "deployment-1" : null,
            CurrentDeploymentEndpointUri = endpoint ? Endpoint : null,
            DeletedAt = observed == ElsaObservedLifecycle.Deleted ? Now : null,
            Version = 1,
            CreatedAt = Now,
            UpdatedAt = Now
        };
        _db.ElsaInstances.Add(instance);
        if (operation is { } state)
            _db.ElsaInstanceOperations.Add(Operation(instance, state,
                state == ElsaInstanceOperationState.WaitingForPriorOperation ? ElsaInstanceOperationAction.Delete : ElsaInstanceOperationAction.Reconcile));
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        return instance;
    }

    private static ElsaInstanceOperationEntity Operation(
        ElsaInstanceEntity instance,
        ElsaInstanceOperationState state,
        ElsaInstanceOperationAction action = ElsaInstanceOperationAction.Reconcile) => new()
        {
            Id = Guid.NewGuid(),
            InstanceId = instance.Id,
            OrganizationId = instance.OrganizationId,
            WorkspaceId = instance.WorkspaceId,
            Action = action,
            IdempotencyScope = $"instance/{instance.Id:N}",
            IdempotencyKey = "operation-" + Guid.NewGuid().ToString("N"),
            RequestHash = new string('a', 64),
            ExpectedVersion = 1,
            State = state,
            AttemptNumber = 1,
            AcceptedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now
        };

    private ElsaInstanceHealthTransition Transition(
        Guid instanceId,
        int expectedVersion,
        ElsaInstanceHealth expectedHealth,
        ElsaInstanceHealth health,
        string diagnosticCode) =>
        new(_workspace.Id, instanceId, expectedVersion, expectedHealth, health, diagnosticCode, Now);

    private Task<ElsaInstanceEntity> ReadAsync(Guid instanceId) =>
        _db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == instanceId);

    private Task<List<ElsaInstanceAuditEventEntity>> AuditAsync(Guid instanceId) =>
        _db.ElsaInstanceAuditEvents.AsNoTracking().Where(x => x.InstanceId == instanceId).OrderBy(x => x.Sequence).ToListAsync();

    private CatalogDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(_connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class LostCommitAcknowledgementInterceptor(int failures = 1) : DbTransactionInterceptor
    {
        private int _failed;

        public int Committed { get; private set; }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Committed++;
            if (_failed < failures)
            {
                _failed++;
                throw new LostCommitAcknowledgementException();
            }

            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }

    private sealed class LostCommitAcknowledgementException : Exception;

    private sealed class FixedProbe(ElsaInstanceHealthProbeResult result) : IElsaInstanceProviderHealthProbePort
    {
        public List<ElsaInstanceHealthProbeRequest> Requests { get; } = [];

        public Task<ElsaInstanceHealthProbeResult> ProbeAsync(
            ElsaInstanceHealthProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}
