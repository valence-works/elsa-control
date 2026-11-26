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

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
