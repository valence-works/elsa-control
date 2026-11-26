using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core.Tests;

/// <summary>
/// Contract suite every provider must pass: Preview / Provision / Refresh /
/// Cancel / Destroy, events, locking, and absence.
/// </summary>
public abstract class InfrastructureProviderContractTests
{
    protected abstract IInfrastructureProvider CreateProvider();

    [Fact]
    public async Task Preview_provision_refresh_cancel_and_destroy_emit_sanitised_events()
    {
        var provider = CreateProvider();
        var operation = Operation(ProvisioningCommand.Provision, "contract-engine");
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.EngineTarget("contract-engine"),
            ProvisioningFixtures.EngineSpec(),
            "contract-provision",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "contract-engine" });

        if (provider is FakeInfrastructureProvider fake)
        {
            var platformOp = Operation(ProvisioningCommand.Provision, "staging", ProvisioningStackKind.Platform);
            var platformRequest = ProvisioningFixtures.Request(
                ProvisioningCommand.Provision,
                ProvisioningFixtures.PlatformTarget(),
                ProvisioningFixtures.PlatformSpec(),
                "contract-platform");
            var platform = await fake.ProvisionAsync(platformOp, platformRequest);
            Assert.True(platform.Succeeded);
        }

        foreach (var command in new[]
                 {
                     ProvisioningCommand.Preview,
                     ProvisioningCommand.Provision,
                     ProvisioningCommand.Refresh,
                     ProvisioningCommand.Destroy
                 })
        {
            var result = command switch
            {
                ProvisioningCommand.Preview => await provider.PreviewAsync(operation, request),
                ProvisioningCommand.Refresh => await provider.RefreshAsync(operation, request),
                ProvisioningCommand.Destroy => await provider.DestroyAsync(operation, request),
                _ => await provider.ProvisionAsync(operation, request)
            };
            Assert.True(result.Succeeded);
            Assert.NotEmpty(result.Events);
            foreach (var value in result.Events)
            {
                Assert.False(ProvisioningEventSanitizer.ContainsSensitiveToken(value.Message));
                Assert.False(ProvisioningEventSanitizer.ContainsSensitiveToken(value.ResourceKind));
            }
        }

        var cancel = await provider.CancelAsync(operation, request);
        Assert.True(cancel.Succeeded);
        Assert.Equal(ProvisioningFailureCodes.NothingToCancel, cancel.FailureCode);
        Assert.NotEmpty(cancel.Events);
    }

    [Fact]
    public async Task Destroy_of_an_absent_stack_is_a_no_op_success()
    {
        var provider = CreateProvider();
        var operation = Operation(ProvisioningCommand.Destroy, "never-created");
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Destroy,
            ProvisioningFixtures.EngineTarget("never-created"),
            ProvisioningFixtures.EngineSpec(),
            "contract-absent");
        var result = await provider.DestroyAsync(operation, request);
        Assert.True(result.Succeeded);
        if (provider is FakeInfrastructureProvider fake)
            Assert.False(fake.StackExists("never-created"));
    }

    [Fact]
    public async Task Cancel_never_destroys_an_existing_stack()
    {
        var provider = CreateProvider();
        if (provider is not FakeInfrastructureProvider fake)
            return;

        var platformOp = Operation(ProvisioningCommand.Provision, "staging", ProvisioningStackKind.Platform);
        await fake.ProvisionAsync(
            platformOp,
            ProvisioningFixtures.Request(
                ProvisioningCommand.Provision,
                ProvisioningFixtures.PlatformTarget(),
                ProvisioningFixtures.PlatformSpec(),
                "cancel-platform"));
        var engineOp = Operation(ProvisioningCommand.Provision, "keep-engine");
        var engineRequest = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.EngineTarget("keep-engine"),
            ProvisioningFixtures.EngineSpec(),
            "cancel-engine",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "keep-engine" });
        await fake.ProvisionAsync(engineOp, engineRequest);
        Assert.True(fake.StackExists("keep-engine"));

        var cancel = await fake.CancelAsync(
            Operation(ProvisioningCommand.Cancel, "keep-engine"),
            ProvisioningFixtures.Request(
                ProvisioningCommand.Cancel,
                ProvisioningFixtures.EngineTarget("keep-engine"),
                ProvisioningFixtures.EngineSpec(),
                "cancel-idle"));
        Assert.True(cancel.Succeeded);
        Assert.Equal(ProvisioningFailureCodes.NothingToCancel, cancel.FailureCode);
        Assert.True(fake.StackExists("keep-engine"));
    }

    protected static ProvisioningOperation Operation(
        ProvisioningCommand command,
        string stackName,
        ProvisioningStackKind kind = ProvisioningStackKind.Engine) =>
        new(
            Guid.NewGuid(),
            command,
            ProvisioningOperationState.Running,
            FakeInfrastructureProvider.Id,
            kind,
            stackName,
            $"idem-{stackName}-{command}",
            $"hash-{stackName}-{command}",
            DateTimeOffset.UtcNow);
}

public sealed class FakeInfrastructureProviderContractTests : InfrastructureProviderContractTests
{
    protected override IInfrastructureProvider CreateProvider() => new FakeInfrastructureProvider();

    [Fact]
    public async Task Second_provision_on_a_held_stack_conflicts()
    {
        var store = new Stores.InMemoryProvisioningStore();
        var events = store;
        var provider = new FakeInfrastructureProvider();
        var service = new ProvisioningService(store, events);
        var worker = new ProvisioningOperationWorker(store, events, provider);
        var platform = await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget(),
            ProvisioningFixtures.PlatformSpec(),
            "lock-platform"));
        Assert.Equal(ProvisioningOperationState.Queued, platform.State);
        Assert.Equal(1, await worker.ProcessOnceAsync());

        var first = await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.EngineTarget("held-engine"),
            ProvisioningFixtures.EngineSpec(),
            "lock-first",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "held-engine" }));
        var claimed = await store.ClaimAsync(first.OperationId, DateTimeOffset.UtcNow);
        Assert.Equal(ProvisioningOperationState.Running, claimed!.State);

        var second = await service.ProvisionAsync(ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.EngineTarget("held-engine"),
            ProvisioningFixtures.EngineSpec(),
            "lock-second",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "held-engine" }));
        Assert.True(second.LockConflict);
        Assert.Equal(ProvisioningOperationState.LockConflict, second.State);
        var conflictEvents = await service.ListEventsAsync(second.OperationId);
        Assert.Contains(conflictEvents, value => value.Kind == ProvisioningEventKind.LockConflicted);
        foreach (var value in conflictEvents)
            Assert.False(ProvisioningEventSanitizer.ContainsSensitiveToken(value.Message));
    }
}
