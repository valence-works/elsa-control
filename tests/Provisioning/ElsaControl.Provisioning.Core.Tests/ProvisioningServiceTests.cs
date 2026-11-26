using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core.Stores;

namespace ElsaControl.Provisioning.Core.Tests;

public sealed class ProvisioningServiceTests
{
    [Fact]
    public async Task Provision_enqueues_and_does_not_run_on_the_request_thread()
    {
        var store = new InMemoryProvisioningStore();
        var provider = new FakeInfrastructureProvider();
        var service = new ProvisioningService(store, store);
        var worker = new ProvisioningOperationWorker(store, store, provider);

        var platform = await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget(),
            ProvisioningFixtures.PlatformSpec(),
            "svc-platform"));
        Assert.Equal(ProvisioningOperationState.Queued, platform.State);
        Assert.False(platform.Replayed);
        Assert.False(provider.StackExists("staging"));

        Assert.Equal(1, await worker.ProcessOnceAsync());
        var completed = await service.GetOperationAsync(platform.OperationId);
        Assert.Equal(ProvisioningOperationState.Succeeded, completed!.State);
        Assert.True(provider.StackExists("staging"));
    }

    [Fact]
    public async Task Engine_stack_reads_platform_output_and_keeps_per_stack_config()
    {
        var store = new InMemoryProvisioningStore();
        var provider = new FakeInfrastructureProvider();
        var service = new ProvisioningService(store, store);
        var worker = new ProvisioningOperationWorker(store, store, provider);

        await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("staging"),
            ProvisioningFixtures.PlatformSpec("staging"),
            "svc-platform-staging"));
        await worker.ProcessOnceAsync();

        await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.EngineTarget("acme-one"),
            ProvisioningFixtures.EngineSpec(),
            "svc-engine-one",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "acme-one" }));
        await worker.ProcessOnceAsync();
        await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.EngineTarget("acme-two"),
            ProvisioningFixtures.EngineSpec(),
            "svc-engine-two",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "acme-two" }));
        await worker.ProcessOnceAsync();

        var one = provider.GetOutputs("acme-one");
        var two = provider.GetOutputs("acme-two");
        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.Equal("shared-container-environment", one!["platformEnvironmentKind"]);
        Assert.Equal("shared-container-environment", two!["platformEnvironmentKind"]);
        Assert.Equal("acme-one", one["config.engineName"]);
        Assert.Equal("acme-two", two["config.engineName"]);
        Assert.NotEqual(one["config.engineName"], two["config.engineName"]);
    }

    [Fact]
    public async Task Preview_refresh_and_destroy_round_trip_through_the_worker()
    {
        var store = new InMemoryProvisioningStore();
        var provider = new FakeInfrastructureProvider();
        var service = new ProvisioningService(store, store);
        var worker = new ProvisioningOperationWorker(store, store, provider);

        await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget(),
            ProvisioningFixtures.PlatformSpec(),
            "round-platform"));
        await worker.ProcessOnceAsync();

        var preview = await service.PreviewAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Preview,
            ProvisioningFixtures.EngineTarget("round-engine"),
            ProvisioningFixtures.EngineSpec(),
            "round-preview"));
        await worker.ProcessOnceAsync();
        Assert.Equal(ProvisioningOperationState.Succeeded, (await service.GetOperationAsync(preview.OperationId))!.State);

        var provision = await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.EngineTarget("round-engine"),
            ProvisioningFixtures.EngineSpec(),
            "round-provision"));
        await worker.ProcessOnceAsync();
        Assert.True(provider.StackExists("round-engine"));

        var refresh = await service.RefreshAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Refresh,
            ProvisioningFixtures.EngineTarget("round-engine"),
            ProvisioningFixtures.EngineSpec(),
            "round-refresh"));
        await worker.ProcessOnceAsync();
        Assert.Equal(ProvisioningOperationState.Succeeded, (await service.GetOperationAsync(refresh.OperationId))!.State);

        var destroy = await service.DestroyAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Destroy,
            ProvisioningFixtures.EngineTarget("round-engine"),
            ProvisioningFixtures.EngineSpec(),
            "round-destroy"));
        await worker.ProcessOnceAsync();
        Assert.Equal(ProvisioningOperationState.Succeeded, (await service.GetOperationAsync(destroy.OperationId))!.State);
        Assert.False(provider.StackExists("round-engine"));
        Assert.NotEqual(preview.OperationId, provision.OperationId);
        Assert.NotEqual(provision.OperationId, destroy.OperationId);
    }

    [Fact]
    public async Task Stack_config_rejects_secret_tokens()
    {
        var store = new InMemoryProvisioningStore();
        var service = new ProvisioningService(store, store);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProvisionAsync(ProvisioningFixtures.Request(
                ProvisioningCommand.Provision,
                ProvisioningFixtures.PlatformTarget(),
                ProvisioningFixtures.PlatformSpec(),
                "secret-config",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["password"] = "must-not-appear" })));
        Assert.Equal("Stack config must not contain secrets or cloud identifiers.", error.Message);
    }

    [Fact]
    public async Task Identical_idempotency_key_replays()
    {
        var store = new InMemoryProvisioningStore();
        var service = new ProvisioningService(store, store);
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget(),
            ProvisioningFixtures.PlatformSpec(),
            "replay-key");
        var first = await service.ProvisionAsync(request);
        var eventsAfterFirst = await service.ListEventsAsync(first.OperationId);
        var second = await service.ProvisionAsync(request);
        Assert.True(second.Replayed);
        Assert.Equal(first.OperationId, second.OperationId);
        Assert.Equal(eventsAfterFirst.Count, (await service.ListEventsAsync(first.OperationId)).Count);
    }

    [Fact]
    public async Task Cancel_through_the_worker_destroys_the_stack_and_marks_cancelled()
    {
        var store = new InMemoryProvisioningStore();
        var provider = new FakeInfrastructureProvider();
        var service = new ProvisioningService(store, store);
        var worker = new ProvisioningOperationWorker(store, store, provider);

        await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget(),
            ProvisioningFixtures.PlatformSpec(),
            "cancel-platform"));
        await worker.ProcessOnceAsync();
        await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.EngineTarget("cancel-engine"),
            ProvisioningFixtures.EngineSpec(),
            "cancel-provision",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "cancel-engine" }));
        await worker.ProcessOnceAsync();
        Assert.True(provider.StackExists("cancel-engine"));

        var cancel = await service.CancelAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Cancel,
            ProvisioningFixtures.EngineTarget("cancel-engine"),
            ProvisioningFixtures.EngineSpec(),
            "cancel-run"));
        Assert.Equal(ProvisioningOperationState.Queued, cancel.State);
        Assert.Equal(1, await worker.ProcessOnceAsync());

        var completed = await service.GetOperationAsync(cancel.OperationId);
        Assert.Equal(ProvisioningOperationState.Cancelled, completed!.State);
        Assert.False(provider.StackExists("cancel-engine"));
        var cancelEvents = await service.ListEventsAsync(cancel.OperationId);
        Assert.Contains(cancelEvents, value => value.Kind == ProvisioningEventKind.OperationCancelled);
        foreach (var value in cancelEvents)
            Assert.False(ProvisioningEventSanitizer.ContainsSensitiveToken(value.Message));
    }

    [Fact]
    public async Task Cancelling_keeps_the_stack_lock()
    {
        var store = new InMemoryProvisioningStore();
        var service = new ProvisioningService(store, store);
        var first = await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("lock-cancel"),
            ProvisioningFixtures.PlatformSpec("lock-cancel"),
            "lock-cancel-first"));
        var claimed = await store.ClaimAsync(first.OperationId, DateTimeOffset.UtcNow);
        Assert.Equal(ProvisioningOperationState.Running, claimed!.State);
        var cancelling = await store.CompleteAsync(
            first.OperationId,
            ProvisioningOperationState.Cancelling,
            DateTimeOffset.UtcNow,
            failureCode: null);
        Assert.Equal(ProvisioningOperationState.Cancelling, cancelling!.State);
        Assert.Equal(first.OperationId, await store.GetHolderAsync("lock-cancel"));

        var second = await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("lock-cancel"),
            ProvisioningFixtures.PlatformSpec("lock-cancel"),
            "lock-cancel-second"));
        Assert.True(second.LockConflict);
        Assert.Equal(ProvisioningOperationState.LockConflict, second.State);
    }
}
