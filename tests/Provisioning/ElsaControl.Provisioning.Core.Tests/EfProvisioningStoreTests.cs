using ElsaControl.Provisioning.Abstractions;
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

        var second = new EfProvisioningStore(
            _factory,
            new ProvisioningReplicaIdentity("reclaim", TimeSpan.FromMinutes(5)));
        var interrupted = await second.FailInterruptedAsync(now.AddSeconds(1));
        Assert.Contains(interrupted, operation => operation.Id == claimed.Id);
        var loaded = await second.GetAsync(claimed.Id);
        Assert.Equal(ProvisioningOperationState.Failed, loaded!.State);
        Assert.Equal(ProvisioningFailureCodes.Interrupted, loaded.FailureCode);
        Assert.True(loaded.StateLockMayBeHeld);
        Assert.Equal(claimed.Id, await second.GetHolderAsync(ProvisioningStackKind.Platform, "ef-expired"));

        await second.MarkStateLockClearedAsync(claimed.Id);
        Assert.Null(await second.GetHolderAsync(ProvisioningStackKind.Platform, "ef-expired"));
        var next = await second.CreateOrGetAsync(
            ProvisioningFixtures.Request(
                ProvisioningCommand.Provision,
                ProvisioningFixtures.PlatformTarget("ef-expired"),
                ProvisioningFixtures.PlatformSpec("ef-expired"),
                "ef-expired-next"),
            ProvisioningRequestHash.Compute(request) + "-next",
            now.AddSeconds(2));
        Assert.False(next.LockConflict);
        Assert.Equal(ProvisioningOperationState.Queued, next.State);
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

    public void Dispose() => _connection.Dispose();

    private sealed class TestFactory(DbContextOptions<ProvisioningDbContext> options)
        : IDbContextFactory<ProvisioningDbContext>
    {
        public ProvisioningDbContext CreateDbContext() => new(options);
    }
}
