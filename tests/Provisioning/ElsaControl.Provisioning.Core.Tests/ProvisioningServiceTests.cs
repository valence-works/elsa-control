using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core.Stores;

namespace ElsaControl.Provisioning.Core.Tests;

public sealed class ProvisioningServiceTests
{
    [Fact]
    public async Task Engine_stack_names_that_are_not_engine_guid_are_rejected()
    {
        var store = new InMemoryProvisioningStore();
        var service = new ProvisioningService(store, store);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProvisionAsync(ProvisioningFixtures.Request(
                ProvisioningCommand.Provision,
                ProvisioningFixtures.EngineTarget("acme-one"),
                ProvisioningFixtures.EngineSpec(),
                "bad-engine-name")));
        Assert.Equal("Engine stack names must be engine-{instanceId}.", error.Message);
    }

    [Fact]
    public async Task Provision_enqueues_and_does_not_run_on_the_request_thread()
    {
        var store = new InMemoryProvisioningStore();
        var runs = new ProvisioningRunRegistry();
        var provider = new FakeInfrastructureProvider();
        var service = new ProvisioningService(store, store, runs);
        var worker = new ProvisioningOperationWorker(store, store, provider, runs);

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
        var runs = new ProvisioningRunRegistry();
        var provider = new FakeInfrastructureProvider();
        var service = new ProvisioningService(store, store, runs);
        var worker = new ProvisioningOperationWorker(store, store, provider, runs);

        await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("staging"),
            ProvisioningFixtures.PlatformSpec("staging"),
            "svc-platform-staging"));
        await worker.ProcessOnceAsync();

        await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.EngineTarget(ProvisioningFixtures.EngineName(ProvisioningFixtures.EngineA)),
            ProvisioningFixtures.EngineSpec(),
            "svc-engine-one",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["engineName"] = ProvisioningFixtures.EngineName(ProvisioningFixtures.EngineA)
            }));
        await worker.ProcessOnceAsync();
        await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.EngineTarget(ProvisioningFixtures.EngineName(ProvisioningFixtures.EngineB)),
            ProvisioningFixtures.EngineSpec(),
            "svc-engine-two",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["engineName"] = ProvisioningFixtures.EngineName(ProvisioningFixtures.EngineB)
            }));
        await worker.ProcessOnceAsync();

        var one = provider.GetOutputs(ProvisioningFixtures.EngineName(ProvisioningFixtures.EngineA));
        var two = provider.GetOutputs(ProvisioningFixtures.EngineName(ProvisioningFixtures.EngineB));
        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.Equal("shared-container-environment", one!["platformEnvironmentKind"]);
        Assert.Equal("shared-container-environment", two!["platformEnvironmentKind"]);
        Assert.Equal(ProvisioningFixtures.EngineName(ProvisioningFixtures.EngineA), one["config.engineName"]);
        Assert.Equal(ProvisioningFixtures.EngineName(ProvisioningFixtures.EngineB), two["config.engineName"]);
        Assert.NotEqual(one["config.engineName"], two["config.engineName"]);
    }

    [Fact]
    public async Task Preview_refresh_and_destroy_round_trip_through_the_worker()
    {
        var store = new InMemoryProvisioningStore();
        var runs = new ProvisioningRunRegistry();
        var provider = new FakeInfrastructureProvider();
        var service = new ProvisioningService(store, store, runs);
        var worker = new ProvisioningOperationWorker(store, store, provider, runs);

        await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget(),
            ProvisioningFixtures.PlatformSpec(),
            "round-platform"));
        await worker.ProcessOnceAsync();

        var preview = await service.PreviewAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Preview,
            ProvisioningFixtures.EngineTarget(),
            ProvisioningFixtures.EngineSpec(),
            "round-preview"));
        await worker.ProcessOnceAsync();
        Assert.Equal(ProvisioningOperationState.Succeeded, (await service.GetOperationAsync(preview.OperationId))!.State);

        var provision = await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.EngineTarget(),
            ProvisioningFixtures.EngineSpec(),
            "round-provision"));
        await worker.ProcessOnceAsync();
        Assert.True(provider.StackExists(ProvisioningFixtures.EngineName()));

        var refresh = await service.RefreshAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Refresh,
            ProvisioningFixtures.EngineTarget(),
            ProvisioningFixtures.EngineSpec(),
            "round-refresh"));
        await worker.ProcessOnceAsync();
        Assert.Equal(ProvisioningOperationState.Succeeded, (await service.GetOperationAsync(refresh.OperationId))!.State);

        var destroy = await service.DestroyAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Destroy,
            ProvisioningFixtures.EngineTarget(),
            ProvisioningFixtures.EngineSpec(),
            "round-destroy"));
        await worker.ProcessOnceAsync();
        var destroyed = await service.GetOperationAsync(destroy.OperationId);
        Assert.Equal(ProvisioningOperationState.Succeeded, destroyed!.State);
        Assert.False(provider.StackExists(ProvisioningFixtures.EngineName()));
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
    public async Task Admin_password_override_locator_is_admitted()
    {
        var store = new InMemoryProvisioningStore();
        var service = new ProvisioningService(store, store);
        var result = await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("override"),
            ProvisioningFixtures.PlatformSpec("override"),
            "override-locator",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProvisioningEventSanitizer.AdminPasswordOverrideKey] = "secret://engine-admin"
            }));
        Assert.Equal(ProvisioningOperationState.Queued, result.State);
    }

    [Fact]
    public async Task Admin_password_override_plaintext_is_rejected()
    {
        var store = new InMemoryProvisioningStore();
        var service = new ProvisioningService(store, store);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProvisionAsync(ProvisioningFixtures.Request(
                ProvisioningCommand.Provision,
                ProvisioningFixtures.PlatformTarget("override-plain"),
                ProvisioningFixtures.PlatformSpec("override-plain"),
                "override-plain",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ProvisioningEventSanitizer.AdminPasswordOverrideKey] = "must-not-appear"
                })));
        Assert.Equal("Admin password override must be a secret locator.", error.Message);
    }

    [Fact]
    public async Task Stack_config_rejects_credential_bearing_urls()
    {
        var store = new InMemoryProvisioningStore();
        var service = new ProvisioningService(store, store);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProvisionAsync(ProvisioningFixtures.Request(
                ProvisioningCommand.Provision,
                ProvisioningFixtures.PlatformTarget(),
                ProvisioningFixtures.PlatformSpec(),
                "sas-config",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["artifact"] = "https://example.blob.core.windows.net/file?sig=not-a-real-signature"
                })));
        Assert.Equal("Stack config must not contain secrets or cloud identifiers.", error.Message);
        Assert.Empty(await store.ListRunnableAsync(10));
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
    public async Task Cancel_on_an_idle_stack_does_not_destroy()
    {
        var store = new InMemoryProvisioningStore();
        var runs = new ProvisioningRunRegistry();
        var provider = new FakeInfrastructureProvider();
        var service = new ProvisioningService(store, store, runs);
        var worker = new ProvisioningOperationWorker(store, store, provider, runs);

        await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget(),
            ProvisioningFixtures.PlatformSpec(),
            "cancel-platform"));
        await worker.ProcessOnceAsync();
        await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.EngineTarget(),
            ProvisioningFixtures.EngineSpec(),
            "cancel-provision",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["engineName"] = ProvisioningFixtures.EngineName()
            }));
        await worker.ProcessOnceAsync();
        Assert.True(provider.StackExists(ProvisioningFixtures.EngineName()));

        var cancel = await service.CancelAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Cancel,
            ProvisioningFixtures.EngineTarget(),
            ProvisioningFixtures.EngineSpec(),
            "cancel-run"));
        Assert.Equal(ProvisioningOperationState.Succeeded, cancel.State);
        Assert.Equal(ProvisioningFailureCodes.NothingToCancel, cancel.Code);
        Assert.Equal(0, await worker.ProcessOnceAsync());

        var completed = await service.GetOperationAsync(cancel.OperationId);
        Assert.Equal(ProvisioningOperationState.Succeeded, completed!.State);
        Assert.Equal(ProvisioningFailureCodes.NothingToCancel, completed.FailureCode);
        Assert.True(provider.StackExists(ProvisioningFixtures.EngineName()));
        var cancelEvents = await service.ListEventsAsync(cancel.OperationId);
        Assert.Contains(cancelEvents, value => value.Kind == ProvisioningEventKind.OperationSucceeded);
        foreach (var value in cancelEvents)
            Assert.False(ProvisioningEventSanitizer.ContainsSensitiveToken(value.Message));
    }

    [Fact]
    public async Task Cancel_signals_an_in_flight_run_and_does_not_destroy()
    {
        var store = new InMemoryProvisioningStore();
        var runs = new ProvisioningRunRegistry();
        var provider = new FakeInfrastructureProvider();
        var service = new ProvisioningService(store, store, runs);
        var worker = new ProvisioningOperationWorker(store, store, provider, runs);

        var provision = await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("inflight"),
            ProvisioningFixtures.PlatformSpec("inflight"),
            "inflight-provision"));
        var claimed = await store.ClaimAsync(provision.OperationId, DateTimeOffset.UtcNow);
        Assert.Equal(ProvisioningOperationState.Running, claimed!.State);
        using var cts = new CancellationTokenSource();
        using var registration = runs.Register(claimed, cts);

        var cancel = await service.CancelAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Cancel,
            ProvisioningFixtures.PlatformTarget("inflight"),
            ProvisioningFixtures.PlatformSpec("inflight"),
            "inflight-cancel"));
        Assert.False(cancel.LockConflict);
        Assert.Equal(ProvisioningOperationState.Succeeded, cancel.State);
        Assert.Equal(ProvisioningFailureCodes.Cancelled, cancel.Code);
        Assert.True(cts.IsCancellationRequested);
        Assert.True((await store.GetAsync(claimed.Id))!.CancelRequested);
        Assert.Equal(0, await worker.ProcessOnceAsync());
        var completed = await service.GetOperationAsync(cancel.OperationId);
        Assert.Equal(ProvisioningOperationState.Succeeded, completed!.State);
        Assert.Equal(ProvisioningFailureCodes.Cancelled, completed.FailureCode);
        Assert.False(provider.StackExists("inflight"));
    }

    [Fact]
    public async Task Platform_and_engine_locks_are_independent()
    {
        var store = new InMemoryProvisioningStore();
        var service = new ProvisioningService(store, store);
        var platform = await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("staging"),
            ProvisioningFixtures.PlatformSpec("staging"),
            "lock-platform-staging"));
        var claimed = await store.ClaimAsync(platform.OperationId, DateTimeOffset.UtcNow);
        Assert.Equal(ProvisioningOperationState.Running, claimed!.State);

        var engine = await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.EngineTarget(),
            ProvisioningFixtures.EngineSpec(),
            "lock-engine-staging"));
        Assert.False(engine.LockConflict);
        Assert.Equal(ProvisioningOperationState.Queued, engine.State);
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
        Assert.Equal(first.OperationId, await store.GetHolderAsync(ProvisioningStackKind.Platform, "lock-cancel"));

        var second = await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("lock-cancel"),
            ProvisioningFixtures.PlatformSpec("lock-cancel"),
            "lock-cancel-second"));
        Assert.True(second.LockConflict);
        Assert.Equal(ProvisioningOperationState.LockConflict, second.State);
    }

    [Fact]
    public async Task Replayed_cancel_does_not_signal_a_later_run()
    {
        var store = new InMemoryProvisioningStore();
        var runs = new ProvisioningRunRegistry();
        var service = new ProvisioningService(store, store, runs);
        var cancelRequest = ProvisioningFixtures.Request(
            ProvisioningCommand.Cancel,
            ProvisioningFixtures.PlatformTarget("replay-cancel"),
            ProvisioningFixtures.PlatformSpec("replay-cancel"),
            "replay-cancel-key");
        var first = await service.CancelAsync(cancelRequest);
        Assert.False(first.Replayed);

        var provision = await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("replay-cancel"),
            ProvisioningFixtures.PlatformSpec("replay-cancel"),
            "replay-cancel-provision"));
        var claimed = await store.ClaimAsync(provision.OperationId, DateTimeOffset.UtcNow);
        using var cts = new CancellationTokenSource();
        using var registration = runs.Register(claimed!, cts);

        var replayed = await service.CancelAsync(cancelRequest);
        Assert.True(replayed.Replayed);
        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task Host_cancellation_fails_the_running_operation_and_releases_the_lock()
    {
        var store = new InMemoryProvisioningStore();
        var runs = new ProvisioningRunRegistry();
        var provider = new FakeInfrastructureProvider { Stall = TimeSpan.FromSeconds(30) };
        var service = new ProvisioningService(store, store, runs);
        var worker = new ProvisioningOperationWorker(store, store, provider, runs);
        var provision = await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("interrupt"),
            ProvisioningFixtures.PlatformSpec("interrupt"),
            "interrupt-provision"));

        using var cts = new CancellationTokenSource();
        var processing = worker.ProcessOnceAsync(cancellationToken: cts.Token);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);

        var completed = await service.GetOperationAsync(provision.OperationId);
        Assert.Equal(ProvisioningOperationState.Cancelled, completed!.State);
        Assert.Equal(ProvisioningFailureCodes.Interrupted, completed.FailureCode);
        Assert.False(completed.StateLockMayBeHeld);
        Assert.Null(await store.GetHolderAsync(ProvisioningStackKind.Platform, "interrupt"));

        await worker.ReconcileInterruptedAsync();
        var cleared = await service.GetOperationAsync(provision.OperationId);
        Assert.False(cleared!.StateLockMayBeHeld);
        Assert.Null(await store.GetHolderAsync(ProvisioningStackKind.Platform, "interrupt"));
    }

    [Fact]
    public async Task Cancel_of_a_queued_create_does_not_run_the_provider()
    {
        var store = new InMemoryProvisioningStore();
        var provider = new FakeInfrastructureProvider();
        var service = new ProvisioningService(store, store);
        var worker = new ProvisioningOperationWorker(store, store, provider);
        var provision = await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("queued-cancel"),
            ProvisioningFixtures.PlatformSpec("queued-cancel"),
            "queued-cancel-create"));
        Assert.Equal(ProvisioningOperationState.Queued, provision.State);

        var cancel = await service.CancelAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Cancel,
            ProvisioningFixtures.PlatformTarget("queued-cancel"),
            ProvisioningFixtures.PlatformSpec("queued-cancel"),
            "queued-cancel-signal"));
        Assert.Equal(ProvisioningOperationState.Succeeded, cancel.State);
        Assert.Equal(ProvisioningFailureCodes.Cancelled, cancel.Code);
        Assert.Equal(ProvisioningOperationState.Cancelled, (await service.GetOperationAsync(provision.OperationId))!.State);
        Assert.Equal(0, await worker.ProcessOnceAsync());
        Assert.False(provider.StackExists("queued-cancel"));
    }
}
