using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using ElsaControl.Provisioning.Providers.Azure;
using ElsaControl.Provisioning.Providers.Azure.Programs;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

public sealed class AzureInfrastructureProviderContractTests
{
    [Fact]
    public async Task Disposable_platform_and_engine_stacks_run_preview_provision_refresh_and_destroy()
    {
        var harness = Harness.Create();
        var platform = await harness.RunAsync(
            ProvisioningOperationKind.Preview,
            ElsaPlatformStack.ProjectName,
            "ci-platform");
        Assert.Equal(ProvisioningOperationStatus.Succeeded, platform.Operation.Status);
        Assert.Contains(platform.Events, e => e.Capability == ProvisioningCapabilities.Platform);

        var engine = await harness.RunAsync(
            ProvisioningOperationKind.Provision,
            ElsaEngineStack.ProjectName,
            "org-slug",
            "ci-platform",
            "engine-provision");
        Assert.Equal(ProvisioningOperationStatus.Succeeded, engine.Operation.Status);
        Assert.Contains(engine.Events, e => e.Capability == ProvisioningCapabilities.ContainerRuntime);
        var provisionRun = Assert.Single(harness.Automation.Runs, run => run.Kind == ProvisioningOperationKind.Provision);
        Assert.Equal("org-slug", provisionRun.Config["engineName"]);
        Assert.Equal("runtime-combined:test", provisionRun.Config["runtimeImage"]);
        Assert.Equal("elsa-platform/ci-platform", provisionRun.Config["platformStack"]);

        var refresh = await harness.RunAsync(
            ProvisioningOperationKind.Refresh,
            ElsaEngineStack.ProjectName,
            "org-slug",
            "ci-platform",
            "engine-refresh");
        Assert.Equal(ProvisioningOperationStatus.Succeeded, refresh.Operation.Status);

        var destroy = await harness.RunAsync(
            ProvisioningOperationKind.Destroy,
            ElsaEngineStack.ProjectName,
            "org-slug",
            "ci-platform",
            "engine-destroy");
        Assert.Equal(ProvisioningOperationStatus.Succeeded, destroy.Operation.Status);
        AssertValueFree(platform.Events.Concat(engine.Events).Concat(refresh.Events).Concat(destroy.Events));
    }

    [Fact]
    public async Task Second_provision_on_the_same_stack_is_lock_conflicted()
    {
        var harness = Harness.Create();
        var first = await harness.EnqueueAsync(
            ProvisioningOperationKind.Provision, ElsaEngineStack.ProjectName, "org-slug", "first");
        var accepted = Assert.IsType<ProvisioningCommandResult.Accepted>(first);
        var second = await harness.EnqueueAsync(
            ProvisioningOperationKind.Provision, ElsaEngineStack.ProjectName, "org-slug", "second");
        var locked = Assert.IsType<ProvisioningCommandResult.StackLocked>(second);
        Assert.Equal(accepted.Operation.Id, locked.HoldingOperationId);
    }

    [Fact]
    public async Task Destroy_of_an_unknown_engine_stack_proves_absence()
    {
        var harness = Harness.Create();
        var destroy = await harness.RunAsync(
            ProvisioningOperationKind.Destroy,
            ElsaEngineStack.ProjectName,
            "never-created",
            "ci-platform",
            "destroy-absent");
        Assert.Contains(destroy.Events, e => e.Kind == ProvisioningEventKind.AbsenceProved);
        AssertValueFree(destroy.Events);
    }

    private static void AssertValueFree(IEnumerable<ProvisioningEvent> events)
    {
        foreach (var @event in events)
        {
            Assert.False(ProvisioningEventSanitizer.LooksUnsafe(@event.ReasonCode));
            Assert.False(ProvisioningEventSanitizer.LooksUnsafe(@event.Capability));
            Assert.DoesNotContain("subscription", @event.ReasonCode, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class Harness
    {
        private Harness(
            IProvisioningService service,
            ProvisioningOperationWorker worker,
            FakePulumiAutomation automation)
        {
            Service = service;
            Worker = worker;
            Automation = automation;
        }

        public IProvisioningService Service { get; }

        public ProvisioningOperationWorker Worker { get; }

        public FakePulumiAutomation Automation { get; }

        public static Harness Create()
        {
            var store = new InMemoryProvisioningStore();
            var options = AzureBackendConfigurationTests.FixtureOptions();
            var automation = new FakePulumiAutomation();
            var provider = new AzureInfrastructureProvider(
                automation,
                new AzureBlobStateBackend(options),
                new AzureKeyVaultSecretsProvider(options),
                new AzureWorkloadIdentityCredentialResolver());
            var worker = new ProvisioningOperationWorker(store, store, store, [provider]);
            var service = new ProvisioningService(store, store, store, [provider]);
            return new Harness(service, worker, automation);
        }

        public Task<ProvisioningCommandResult> EnqueueAsync(
            ProvisioningOperationKind kind,
            string project,
            string stack,
            string idempotencyKey,
            string? platformStack = "ci-platform")
        {
            var command = new ProvisioningCommand(
                kind,
                new EngineSpec(
                    "runtime-combined:test",
                    new DatabaseCapability("sql", "basic", false),
                    stackConfig: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["environmentName"] = stack,
                        ["engineName"] = stack
                    }),
                new ProvisioningTarget(
                    "azure",
                    new CredentialReference(AzureWorkloadIdentityCredentialResolver.Kind, "mi-provisioning-fixture"),
                    new StackIdentity(project, stack),
                    platformStack is null ? null : new StackIdentity(ElsaPlatformStack.ProjectName, platformStack)),
                idempotencyKey);
            return kind switch
            {
                ProvisioningOperationKind.Preview => Service.PreviewAsync(command),
                ProvisioningOperationKind.Refresh => Service.RefreshAsync(command),
                ProvisioningOperationKind.Destroy => Service.DestroyAsync(command),
                ProvisioningOperationKind.Update => Service.UpdateAsync(command),
                _ => Service.ProvisionAsync(command)
            };
        }

        public async Task<(ProvisioningOperation Operation, IReadOnlyList<ProvisioningEvent> Events)> RunAsync(
            ProvisioningOperationKind kind,
            string project,
            string stack,
            string? platformStack = "ci-platform",
            string idempotencyKey = "key-1")
        {
            var enqueued = await EnqueueAsync(kind, project, stack, idempotencyKey, platformStack);
            var accepted = Assert.IsType<ProvisioningCommandResult.Accepted>(enqueued);
            await Worker.ProcessOnceAsync();
            return (
                (await Service.GetAsync(accepted.Operation.Id))!,
                await Service.ReadEventsAsync(accepted.Operation.Id));
        }
    }
}
