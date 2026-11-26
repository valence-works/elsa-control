using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core.Stores;

namespace ElsaControl.Provisioning.Core.Tests;

public sealed class SqliteProvisioningStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"elsa-provisioning-{Guid.NewGuid():N}.sqlite");

    [Fact]
    public async Task Queue_survives_a_process_restart()
    {
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget(),
            ProvisioningFixtures.PlatformSpec(),
            "sqlite-replay");
        Guid operationId;
        using (var first = new SqliteProvisioningStore(_path))
        {
            var enqueued = await first.CreateOrGetAsync(
                request,
                InMemoryProvisioningStore.HashRequest(request),
                DateTimeOffset.UtcNow);
            Assert.Equal(ProvisioningOperationState.Queued, enqueued.State);
            operationId = enqueued.OperationId;
        }

        using (var second = new SqliteProvisioningStore(_path))
        {
            var loaded = await second.GetAsync(operationId);
            Assert.NotNull(loaded);
            Assert.Equal(ProvisioningOperationState.Queued, loaded.State);
            var replay = await second.CreateOrGetAsync(
                request,
                InMemoryProvisioningStore.HashRequest(request),
                DateTimeOffset.UtcNow);
            Assert.True(replay.Replayed);
            Assert.Equal(operationId, replay.OperationId);
        }
    }

    [Fact]
    public async Task Interrupted_running_operation_is_failed_and_the_lock_is_released()
    {
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("sqlite-running"),
            ProvisioningFixtures.PlatformSpec("sqlite-running"),
            "sqlite-running");
        Guid operationId;
        using (var first = new SqliteProvisioningStore(_path))
        {
            var enqueued = await first.CreateOrGetAsync(
                request,
                InMemoryProvisioningStore.HashRequest(request),
                DateTimeOffset.UtcNow);
            var claimed = await first.ClaimAsync(enqueued.OperationId, DateTimeOffset.UtcNow);
            Assert.Equal(ProvisioningOperationState.Running, claimed!.State);
            Assert.Equal(claimed.Id, await first.GetHolderAsync(ProvisioningStackKind.Platform, "sqlite-running"));
            operationId = claimed.Id;
        }

        using (var second = new SqliteProvisioningStore(_path))
        {
            var interrupted = await second.FailInterruptedAsync(DateTimeOffset.UtcNow);
            Assert.Contains(interrupted, operation => operation.Id == operationId);
            var loaded = await second.GetAsync(operationId);
            Assert.Equal(ProvisioningOperationState.Failed, loaded!.State);
            Assert.Equal(ProvisioningFailureCodes.Interrupted, loaded.FailureCode);
            Assert.Null(await second.GetHolderAsync(ProvisioningStackKind.Platform, "sqlite-running"));

            var next = await second.CreateOrGetAsync(
                ProvisioningFixtures.Request(
                    ProvisioningCommand.Provision,
                    ProvisioningFixtures.PlatformTarget("sqlite-running"),
                    ProvisioningFixtures.PlatformSpec("sqlite-running"),
                    "sqlite-running-next"),
                InMemoryProvisioningStore.HashRequest(request) + "-next",
                DateTimeOffset.UtcNow);
            Assert.False(next.LockConflict);
            Assert.Equal(ProvisioningOperationState.Queued, next.State);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
