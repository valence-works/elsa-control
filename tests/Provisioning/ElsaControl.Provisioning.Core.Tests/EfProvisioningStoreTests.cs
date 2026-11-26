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
        var first = new EfProvisioningStore(_factory);
        var enqueued = await first.CreateOrGetAsync(
            request,
            ProvisioningRequestHash.Compute(request),
            DateTimeOffset.UtcNow);
        Assert.Equal(ProvisioningOperationState.Queued, enqueued.State);

        var second = new EfProvisioningStore(_factory);
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
    public async Task Interrupted_running_operation_is_failed_and_the_lock_is_released()
    {
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("ef-running"),
            ProvisioningFixtures.PlatformSpec("ef-running"),
            "ef-running");
        var first = new EfProvisioningStore(_factory);
        var enqueued = await first.CreateOrGetAsync(
            request,
            ProvisioningRequestHash.Compute(request),
            DateTimeOffset.UtcNow);
        var claimed = await first.ClaimAsync(enqueued.OperationId, DateTimeOffset.UtcNow);
        Assert.Equal(ProvisioningOperationState.Running, claimed!.State);
        Assert.Equal(claimed.Id, await first.GetHolderAsync(ProvisioningStackKind.Platform, "ef-running"));

        var second = new EfProvisioningStore(_factory);
        var interrupted = await second.FailInterruptedAsync(DateTimeOffset.UtcNow);
        Assert.Contains(interrupted, operation => operation.Id == claimed.Id);
        var loaded = await second.GetAsync(claimed.Id);
        Assert.Equal(ProvisioningOperationState.Failed, loaded!.State);
        Assert.Equal(ProvisioningFailureCodes.Interrupted, loaded.FailureCode);
        Assert.Null(await second.GetHolderAsync(ProvisioningStackKind.Platform, "ef-running"));

        var next = await second.CreateOrGetAsync(
            ProvisioningFixtures.Request(
                ProvisioningCommand.Provision,
                ProvisioningFixtures.PlatformTarget("ef-running"),
                ProvisioningFixtures.PlatformSpec("ef-running"),
                "ef-running-next"),
            ProvisioningRequestHash.Compute(request) + "-next",
            DateTimeOffset.UtcNow);
        Assert.False(next.LockConflict);
        Assert.Equal(ProvisioningOperationState.Queued, next.State);
    }

    [Fact]
    public async Task Replica_lock_acquire_fails_closed_when_the_key_is_held()
    {
        var store = new EfProvisioningStore(_factory);
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
