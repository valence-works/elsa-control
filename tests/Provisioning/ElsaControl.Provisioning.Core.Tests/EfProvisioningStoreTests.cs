using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using ElsaControl.Provisioning.Core.Stores;
using ElsaControl.Provisioning.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.Provisioning.Core.Tests;

public sealed class EfProvisioningStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<ProvisioningDbContext> _factory;

    public EfProvisioningStoreTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<ProvisioningDbContext>()
            .UseSqlite(_connection)
            .Options;
        using (var db = new ProvisioningDbContext(options))
            db.Database.EnsureCreated();
        _factory = new TestFactory(options);
    }

    [Fact]
    public async Task Queue_survives_a_new_context()
    {
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget(),
            ProvisioningFixtures.PlatformSpec(),
            "ef-replay");
        var first = new EfProvisioningStore(_factory, new ProvisioningReplicaIdentity("first"));
        var enqueued = await first.CreateOrGetAsync(
            request,
            ProvisioningRequestHash.Compute(request),
            DateTimeOffset.UtcNow);
        Assert.Equal(ProvisioningOperationState.Queued, enqueued.State);

        var second = new EfProvisioningStore(_factory, new ProvisioningReplicaIdentity("second"));
        var loaded = await second.GetAsync(enqueued.OperationId);
        Assert.NotNull(loaded);
        Assert.Equal(ProvisioningOperationState.Queued, loaded.State);
        var replay = await second.CreateOrGetAsync(
            request,
            ProvisioningRequestHash.Compute(request),
            DateTimeOffset.UtcNow);
        Assert.True(replay.Replayed);
        Assert.Equal(enqueued.OperationId, replay.OperationId);
    }

    [Fact]
    public async Task Live_lease_is_left_alone_on_a_revision_swap()
    {
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("ef-live"),
            ProvisioningFixtures.PlatformSpec("ef-live"),
            "ef-live");
        var now = DateTimeOffset.UtcNow;
        var first = new EfProvisioningStore(
            _factory,
            new ProvisioningReplicaIdentity("live", TimeSpan.FromMinutes(5)));
        var enqueued = await first.CreateOrGetAsync(
            request,
            ProvisioningRequestHash.Compute(request),
            now);
        var claimed = await first.ClaimAsync(enqueued.OperationId, now);
        Assert.Equal(ProvisioningOperationState.Running, claimed!.State);
        Assert.Equal(1, claimed.FencingToken);
        Assert.Equal("live", claimed.LeaseOwner);

        var second = new EfProvisioningStore(
            _factory,
            new ProvisioningReplicaIdentity("swap", TimeSpan.FromMinutes(5)));
        var interrupted = await second.FailInterruptedAsync(now);
        Assert.DoesNotContain(interrupted, operation => operation.Id == claimed.Id);
        var loaded = await second.GetAsync(claimed.Id);
        Assert.Equal(ProvisioningOperationState.Running, loaded!.State);
        Assert.Equal(1, loaded.FencingToken);
        Assert.Equal(claimed.Id, await second.GetHolderAsync(ProvisioningStackKind.Platform, "ef-live"));
    }

    [Fact]
    public async Task Expired_lease_is_reclaimed()
    {
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("ef-expired"),
            ProvisioningFixtures.PlatformSpec("ef-expired"),
            "ef-expired");
        var now = DateTimeOffset.UtcNow;
        var first = new EfProvisioningStore(
            _factory,
            new ProvisioningReplicaIdentity("dead", TimeSpan.FromMilliseconds(1)));
        var enqueued = await first.CreateOrGetAsync(
            request,
            ProvisioningRequestHash.Compute(request),
            now);
        var claimed = await first.ClaimAsync(enqueued.OperationId, now);
        Assert.Equal(ProvisioningOperationState.Running, claimed!.State);

        await Task.Delay(1500);
        var second = new EfProvisioningStore(
            _factory,
            new ProvisioningReplicaIdentity("reclaim", TimeSpan.FromMinutes(5)));
        var interrupted = await second.FailInterruptedAsync(DateTimeOffset.UtcNow);
        Assert.Contains(interrupted, operation => operation.Id == claimed.Id);
        var loaded = await second.GetAsync(claimed.Id);
        Assert.Equal(ProvisioningOperationState.Failed, loaded!.State);
        Assert.Equal(ProvisioningFailureCodes.Interrupted, loaded.FailureCode);
        Assert.True(loaded.StateLockMayBeHeld);
        Assert.Equal(claimed.Id, await second.GetHolderAsync(ProvisioningStackKind.Platform, "ef-expired"));

        Assert.True(await second.MarkStateLockClearedAsync(claimed.Id));
        Assert.Null(await second.GetHolderAsync(ProvisioningStackKind.Platform, "ef-expired"));
        var next = await second.CreateOrGetAsync(
            ProvisioningFixtures.Request(
                ProvisioningCommand.Provision,
                ProvisioningFixtures.PlatformTarget("ef-expired"),
                ProvisioningFixtures.PlatformSpec("ef-expired"),
                "ef-expired-next"),
            ProvisioningRequestHash.Compute(request) + "-next",
            DateTimeOffset.UtcNow);
        Assert.False(next.LockConflict);
        Assert.Equal(ProvisioningOperationState.Queued, next.State);
    }

    [Fact]
    public async Task Stale_run_cannot_complete_clear_or_release_after_takeover()
    {
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("ef-stale"),
            ProvisioningFixtures.PlatformSpec("ef-stale"),
            "ef-stale");
        var first = new EfProvisioningStore(
            _factory,
            new ProvisioningReplicaIdentity("stale", TimeSpan.FromMilliseconds(1)));
        var enqueued = await first.CreateOrGetAsync(
            request,
            ProvisioningRequestHash.Compute(request),
            DateTimeOffset.UtcNow);
        var claimed = await first.ClaimAsync(enqueued.OperationId, DateTimeOffset.UtcNow);
        Assert.Equal(1, claimed!.FencingToken);

        await Task.Delay(1500);
        var second = new EfProvisioningStore(
            _factory,
            new ProvisioningReplicaIdentity("owner", TimeSpan.FromMinutes(5)));
        var interrupted = await second.FailInterruptedAsync(DateTimeOffset.UtcNow);
        Assert.Contains(interrupted, operation => operation.Id == claimed.Id);

        Assert.Null(await first.CompleteAsync(
            claimed.Id,
            ProvisioningOperationState.Succeeded,
            DateTimeOffset.UtcNow,
            failureCode: null,
            fencingToken: claimed.FencingToken));
        Assert.False(await first.ReleaseAsync(
            ProvisioningStackKind.Platform,
            "ef-stale",
            claimed.Id,
            fencingToken: claimed.FencingToken));
        Assert.False(await first.MarkStateLockClearedAsync(claimed.Id, fencingToken: claimed.FencingToken));
        Assert.Equal(claimed.Id, await second.GetHolderAsync(ProvisioningStackKind.Platform, "ef-stale"));
        Assert.True(await second.MarkStateLockClearedAsync(claimed.Id));
        Assert.Null(await second.GetHolderAsync(ProvisioningStackKind.Platform, "ef-stale"));
    }

    [Fact]
    public async Task Stale_in_flight_run_aborts_without_backend_cancel()
    {
        var first = new EfProvisioningStore(
            _factory,
            new ProvisioningReplicaIdentity("a", TimeSpan.FromMilliseconds(1)));
        var second = new EfProvisioningStore(
            _factory,
            new ProvisioningReplicaIdentity("b", TimeSpan.FromMinutes(5)));
        var provider = new FakeInfrastructureProvider { Stall = TimeSpan.FromSeconds(30) };
        var worker = new ProvisioningOperationWorker(
            first,
            first,
            provider,
            heartbeat: TimeSpan.FromSeconds(3));
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("ef-abort"),
            ProvisioningFixtures.PlatformSpec("ef-abort"),
            "ef-abort");
        var enqueued = await first.CreateOrGetAsync(
            request,
            ProvisioningRequestHash.Compute(request),
            DateTimeOffset.UtcNow);

        var processing = worker.ProcessOnceAsync();
        var started = provider.Started.Task;
        var firstDone = await Task.WhenAny(started, processing).WaitAsync(TimeSpan.FromSeconds(5));
        if (firstDone == processing)
            await processing;
        await started.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(1500);
        var interrupted = await second.FailInterruptedAsync(DateTimeOffset.UtcNow);
        Assert.Contains(interrupted, operation => operation.Id == enqueued.OperationId);
        await processing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, provider.CancelCalls);
        var loaded = await second.GetAsync(enqueued.OperationId);
        Assert.Equal(ProvisioningOperationState.Failed, loaded!.State);
        Assert.Equal(ProvisioningFailureCodes.Interrupted, loaded.FailureCode);
        Assert.Null(await first.CompleteAsync(
            enqueued.OperationId,
            ProvisioningOperationState.Succeeded,
            DateTimeOffset.UtcNow,
            failureCode: null,
            fencingToken: 1));
    }

    [Fact]
    public async Task Two_replicas_contend_and_the_second_fails_closed()
    {
        var now = DateTimeOffset.UtcNow;
        var first = new EfProvisioningStore(_factory, new ProvisioningReplicaIdentity("a"));
        var second = new EfProvisioningStore(_factory, new ProvisioningReplicaIdentity("b"));
        var operationId = Guid.NewGuid();
        Assert.True(await first.TryAcquireAsync(ProvisioningStackKind.Platform, "ef-contend", operationId, now));
        Assert.False(await second.TryAcquireAsync(ProvisioningStackKind.Platform, "ef-contend", Guid.NewGuid(), now));
        Assert.Equal(operationId, await second.GetHolderAsync(ProvisioningStackKind.Platform, "ef-contend"));
        await first.ReleaseAsync(ProvisioningStackKind.Platform, "ef-contend", operationId);
        Assert.True(await second.TryAcquireAsync(ProvisioningStackKind.Platform, "ef-contend", Guid.NewGuid(), now));
    }

    [Fact]
    public async Task Replica_lock_acquire_fails_closed_when_the_key_is_held()
    {
        var store = new EfProvisioningStore(_factory, new ProvisioningReplicaIdentity("lock"));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.True(await store.TryAcquireAsync(ProvisioningStackKind.Platform, "ef-lock", first, DateTimeOffset.UtcNow));
        Assert.False(await store.TryAcquireAsync(ProvisioningStackKind.Platform, "ef-lock", second, DateTimeOffset.UtcNow));
        await store.ReleaseAsync(ProvisioningStackKind.Platform, "ef-lock", first);
        Assert.True(await store.TryAcquireAsync(ProvisioningStackKind.Platform, "ef-lock", second, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Replica_reclaims_expired_lock_on_a_finished_operation()
    {
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("ef-terminal-lock"),
            ProvisioningFixtures.PlatformSpec("ef-terminal-lock"),
            "ef-terminal-lock");
        var first = new EfProvisioningStore(
            _factory,
            new ProvisioningReplicaIdentity("dead", TimeSpan.FromMilliseconds(1)));
        var enqueued = await first.CreateOrGetAsync(
            request,
            ProvisioningRequestHash.Compute(request),
            DateTimeOffset.UtcNow);
        var claimed = await first.ClaimAsync(enqueued.OperationId, DateTimeOffset.UtcNow);
        var completed = await first.CompleteAsync(
            claimed!.Id,
            ProvisioningOperationState.Cancelled,
            DateTimeOffset.UtcNow,
            ProvisioningFailureCodes.Interrupted,
            stateLockMayBeHeld: true,
            fencingToken: claimed.FencingToken);
        Assert.NotNull(completed);
        Assert.True(completed!.StateLockMayBeHeld);
        Assert.Equal(
            claimed.Id,
            await first.GetHolderAsync(ProvisioningStackKind.Platform, "ef-terminal-lock"));

        await Task.Delay(1500);
        var second = new EfProvisioningStore(
            _factory,
            new ProvisioningReplicaIdentity("reclaim", TimeSpan.FromMinutes(5)));
        var worker = new ProvisioningOperationWorker(
            second,
            second,
            new FakeInfrastructureProvider(),
            heartbeat: TimeSpan.FromSeconds(3));
        await worker.ReconcileInterruptedAsync();

        var loaded = await second.GetAsync(claimed.Id);
        Assert.Equal(ProvisioningOperationState.Cancelled, loaded!.State);
        Assert.False(loaded.StateLockMayBeHeld);
        Assert.Null(await second.GetHolderAsync(ProvisioningStackKind.Platform, "ef-terminal-lock"));

        var destroyRequest = ProvisioningFixtures.Request(
            ProvisioningCommand.Destroy,
            ProvisioningFixtures.PlatformTarget("ef-terminal-lock"),
            ProvisioningFixtures.PlatformSpec("ef-terminal-lock"),
            "ef-terminal-lock-destroy");
        var destroy = await second.CreateOrGetAsync(
            destroyRequest,
            ProvisioningRequestHash.Compute(destroyRequest),
            DateTimeOffset.UtcNow);
        Assert.False(destroy.LockConflict);
        Assert.Equal(ProvisioningOperationState.Queued, destroy.State);
        Assert.Equal(1, await worker.ProcessOnceAsync());
        var destroyed = await second.GetAsync(destroy.OperationId);
        Assert.Equal(ProvisioningOperationState.Succeeded, destroyed!.State);
    }

    public void Dispose() => _connection.Dispose();

    private sealed class TestFactory(DbContextOptions<ProvisioningDbContext> options)
        : IDbContextFactory<ProvisioningDbContext>
    {
        public ProvisioningDbContext CreateDbContext() => new(options);
    }
}
