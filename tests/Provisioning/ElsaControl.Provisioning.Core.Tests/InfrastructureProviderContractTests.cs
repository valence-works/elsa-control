using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;

namespace ElsaControl.Provisioning.Core.Tests;

/// <summary>
/// Contract suite every provider must pass. This run uses the fake provider.
/// </summary>
public sealed class InfrastructureProviderContractTests
{
    [Fact]
    public async Task Preview_provision_refresh_and_destroy_run_off_the_caller_thread()
    {
        var harness = Harness.Create();
        var platform = await harness.RunAsync(
            Kind: ProvisioningOperationKind.Preview,
            Project: ProvisioningProjects.Platform,
            Stack: "ci-platform",
            IdempotencyKey: "preview-platform");
        Assert.Equal(ProvisioningOperationStatus.Succeeded, platform.Operation.Status);
        Assert.Contains(platform.Events, e => e.Kind == ProvisioningEventKind.OperationQueued);
        Assert.Contains(platform.Events, e => e.Kind == ProvisioningEventKind.OperationStarted);
        Assert.Contains(platform.Events, e => e.Kind == ProvisioningEventKind.ResourceStarted);
        Assert.Contains(platform.Events, e => e.Kind == ProvisioningEventKind.OperationSucceeded);

        var engine = await harness.RunAsync(
            Kind: ProvisioningOperationKind.Provision,
            Project: ProvisioningProjects.Engine,
            Stack: "org-slug",
            PlatformStack: "ci-platform",
            IdempotencyKey: "provision-engine");
        Assert.Equal(ProvisioningOperationStatus.Succeeded, engine.Operation.Status);
        Assert.Contains(engine.Events, e => e.Capability == ProvisioningCapabilities.ContainerRuntime);
        Assert.Contains(engine.Events, e => e.Capability == ProvisioningCapabilities.Database);

        var refresh = await harness.RunAsync(
            Kind: ProvisioningOperationKind.Refresh,
            Project: ProvisioningProjects.Engine,
            Stack: "org-slug",
            PlatformStack: "ci-platform",
            IdempotencyKey: "refresh-1");
        Assert.Equal(ProvisioningOperationStatus.Succeeded, refresh.Operation.Status);

        var destroy = await harness.RunAsync(
            Kind: ProvisioningOperationKind.Destroy,
            Project: ProvisioningProjects.Engine,
            Stack: "org-slug",
            PlatformStack: "ci-platform",
            IdempotencyKey: "destroy-1");
        Assert.Equal(ProvisioningOperationStatus.Succeeded, destroy.Operation.Status);
        AssertValueFree(platform.Events.Concat(engine.Events).Concat(refresh.Events).Concat(destroy.Events));
    }

    [Fact]
    public async Task Second_provision_on_the_same_stack_observes_a_lock_conflict()
    {
        var harness = Harness.Create();
        var first = await harness.EnqueueAsync(ProvisioningOperationKind.Provision, ProvisioningProjects.Engine, "org-slug");
        var accepted = Assert.IsType<ProvisioningCommandResult.Accepted>(first);
        var second = await harness.EnqueueAsync(ProvisioningOperationKind.Provision, ProvisioningProjects.Engine, "org-slug", "other-key");
        var locked = Assert.IsType<ProvisioningCommandResult.StackLocked>(second);
        Assert.Equal(accepted.Operation.StackKey, locked.StackKey);
        Assert.Equal(accepted.Operation.Id, locked.HoldingOperationId);
    }

    [Fact]
    public async Task Idempotent_replay_returns_the_same_operation()
    {
        var harness = Harness.Create();
        var first = await harness.EnqueueAsync(ProvisioningOperationKind.Provision, ProvisioningProjects.Engine, "org-slug", "same-key");
        var accepted = Assert.IsType<ProvisioningCommandResult.Accepted>(first);
        var replay = await harness.EnqueueAsync(ProvisioningOperationKind.Provision, ProvisioningProjects.Engine, "org-slug", "same-key");
        var replayed = Assert.IsType<ProvisioningCommandResult.Replayed>(replay);
        Assert.Equal(accepted.Operation.Id, replayed.Operation.Id);
    }

    [Fact]
    public async Task Destroy_of_an_unknown_stack_proves_absence()
    {
        var harness = Harness.Create();
        var destroy = await harness.RunAsync(
            Kind: ProvisioningOperationKind.Destroy,
            Project: ProvisioningProjects.Engine,
            Stack: "never-created",
            IdempotencyKey: "destroy-absent");
        Assert.Equal(ProvisioningOperationStatus.Succeeded, destroy.Operation.Status);
        Assert.Contains(destroy.Events, e => e.Kind == ProvisioningEventKind.AbsenceProved);
        Assert.Contains(destroy.Events, e => e.ReasonCode == ProvisioningReasonCodes.AbsenceProved);
        AssertValueFree(destroy.Events);
    }

    [Fact]
    public async Task Cancel_stops_a_queued_operation_before_the_provider_runs()
    {
        var harness = Harness.Create();
        var enqueued = await harness.EnqueueAsync(ProvisioningOperationKind.Provision, ProvisioningProjects.Engine, "org-slug");
        var accepted = Assert.IsType<ProvisioningCommandResult.Accepted>(enqueued);
        var cancelled = await harness.Service.CancelAsync(accepted.Operation.Id);
        var result = Assert.IsType<ProvisioningCommandResult.Accepted>(cancelled);
        Assert.Equal(ProvisioningOperationStatus.Cancelled, result.Operation.Status);
        var processed = await harness.Worker.ProcessOnceAsync();
        Assert.Equal(0, processed);
    }

    [Fact]
    public async Task Cancel_of_a_running_operation_signals_the_provider()
    {
        var harness = Harness.Create();
        harness.Provider.HoldExecutions = true;
        var enqueued = await harness.EnqueueAsync(ProvisioningOperationKind.Provision, ProvisioningProjects.Engine, "org-slug");
        var accepted = Assert.IsType<ProvisioningCommandResult.Accepted>(enqueued);
        var processing = harness.Worker.ProcessOnceAsync();
        await harness.Provider.WaitUntilHeldAsync(accepted.Operation.Id, TimeSpan.FromSeconds(2));
        var cancelled = await harness.Service.CancelAsync(accepted.Operation.Id);
        Assert.IsType<ProvisioningCommandResult.Accepted>(cancelled);
        await processing;
        var completed = await harness.Service.GetAsync(accepted.Operation.Id);
        Assert.Equal(ProvisioningOperationStatus.Cancelled, completed!.Status);
    }

    [Fact]
    public async Task Engine_stack_config_is_per_stack_and_names_the_platform_stack()
    {
        var harness = Harness.Create();
        var command = harness.Command(
            ProvisioningOperationKind.Provision,
            ProvisioningProjects.Engine,
            "Acme Org/my engine!",
            "ci-platform",
            "engine-1");
        Assert.Equal("Acme Org/my engine!", command.Target.Stack.Name);
        var enqueued = await harness.Service.ProvisionAsync(command);
        var accepted = Assert.IsType<ProvisioningCommandResult.Accepted>(enqueued);
        Assert.Equal("elsa-engine/Acme-Org-my-engine", accepted.Operation.StackKey);
        await harness.Worker.ProcessOnceAsync();
        var completed = await harness.Service.GetAsync(accepted.Operation.Id);
        Assert.Equal(ProvisioningOperationStatus.Succeeded, completed!.Status);
    }

    private static void AssertValueFree(IEnumerable<ProvisioningEvent> events)
    {
        foreach (var @event in events)
        {
            Assert.False(ProvisioningEventSanitizer.LooksUnsafe(@event.ReasonCode));
            Assert.False(ProvisioningEventSanitizer.LooksUnsafe(@event.Capability));
            Assert.DoesNotContain("subscription", @event.ReasonCode, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", @event.ReasonCode, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class Harness
    {
        private Harness(
            IProvisioningService service,
            ProvisioningOperationWorker worker,
            FakeInfrastructureProvider provider)
        {
            Service = service;
            Worker = worker;
            Provider = provider;
        }

        public IProvisioningService Service { get; }

        public ProvisioningOperationWorker Worker { get; }

        public FakeInfrastructureProvider Provider { get; }

        public static Harness Create()
        {
            var store = new InMemoryProvisioningStore();
            var provider = new FakeInfrastructureProvider();
            var worker = new ProvisioningOperationWorker(store, store, store, [provider]);
            var service = new ProvisioningService(store, store, store, [provider]);
            return new Harness(service, worker, provider);
        }

        public ProvisioningCommand Command(
            ProvisioningOperationKind kind,
            string project,
            string stack,
            string? platformStack = null,
            string idempotencyKey = "key-1") =>
            new(
                kind,
                new EngineSpec(
                    "runtime-combined:test",
                    new DatabaseCapability("sql", "basic", HighlyAvailable: false),
                    [new SecretCapability("database", "secret://fixture/database")],
                    new EndpointCapability(true, null),
                    new ScaleCapability(1, 1, 0.5, 1024),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["environmentName"] = stack,
                        ["engineName"] = stack
                    }),
                new ProvisioningTarget(
                    "fake",
                    new CredentialReference(FakeCredentialResolver.Kind, "fixture"),
                    new StackIdentity(project, stack),
                    platformStack is null ? null : new StackIdentity(ProvisioningProjects.Platform, platformStack)),
                idempotencyKey);

        public Task<ProvisioningCommandResult> EnqueueAsync(
            ProvisioningOperationKind kind,
            string project,
            string stack,
            string idempotencyKey = "key-1",
            string? platformStack = "ci-platform") =>
            kind switch
            {
                ProvisioningOperationKind.Preview => Service.PreviewAsync(Command(kind, project, stack, platformStack, idempotencyKey)),
                ProvisioningOperationKind.Update => Service.UpdateAsync(Command(kind, project, stack, platformStack, idempotencyKey)),
                ProvisioningOperationKind.Refresh => Service.RefreshAsync(Command(kind, project, stack, platformStack, idempotencyKey)),
                ProvisioningOperationKind.Destroy => Service.DestroyAsync(Command(kind, project, stack, platformStack, idempotencyKey)),
                _ => Service.ProvisionAsync(Command(kind, project, stack, platformStack, idempotencyKey))
            };

        public async Task<(ProvisioningOperation Operation, IReadOnlyList<ProvisioningEvent> Events)> RunAsync(
            ProvisioningOperationKind Kind,
            string Project,
            string Stack,
            string? PlatformStack = "ci-platform",
            string IdempotencyKey = "key-1")
        {
            var enqueued = await EnqueueAsync(Kind, Project, Stack, IdempotencyKey, PlatformStack);
            var accepted = Assert.IsType<ProvisioningCommandResult.Accepted>(enqueued);
            await Worker.ProcessOnceAsync();
            var completed = await Service.GetAsync(accepted.Operation.Id);
            var events = await Service.ReadEventsAsync(accepted.Operation.Id);
            return (completed!, events);
        }
    }
}
