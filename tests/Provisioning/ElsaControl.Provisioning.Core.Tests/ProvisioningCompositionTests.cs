using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElsaControl.Provisioning.Core.Tests;

public sealed class ProvisioningCompositionTests
{
    [Fact]
    public void Worker_is_not_registered_when_the_flag_is_off()
    {
        var services = new ServiceCollection();
        services.AddProvisioning(Configuration(("Provisioning:WorkerEnabled", "false")), runWorker: true);
        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType == typeof(ProvisioningOperationHostedService));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<UnconfiguredInfrastructureProvider>(provider.GetRequiredService<IInfrastructureProvider>());
        Assert.NotNull(provider.GetRequiredService<IProvisioningService>());
        Assert.IsType<NoOpProvisioningStepLedger>(provider.GetRequiredService<IProvisioningStepLedger>());
        Assert.IsType<ProvisioningLiveEventSink>(provider.GetRequiredService<IProvisioningLiveEventSink>());
    }

    [Fact]
    public void Live_event_sink_resolves_with_the_generic_logger()
    {
        var services = new ServiceCollection();
        services.AddProvisioning(Configuration(("Provisioning:WorkerEnabled", "false")));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<ProvisioningLiveEventSink>(provider.GetRequiredService<IProvisioningLiveEventSink>());
        var logger = provider.GetRequiredService<ILogger<ProvisioningLiveEventSink>>();
        Assert.IsNotType<NullLogger<ProvisioningLiveEventSink>>(logger);
    }

    [Fact]
    public async Task Resolved_live_event_sink_logs_through_the_injected_generic_logger()
    {
        var logger = new RecordingSinkLogger();
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<ProvisioningLiveEventSink>>(logger);
        services.AddProvisioning(Configuration(("Provisioning:WorkerEnabled", "false")));
        services.AddSingleton<IProvisioningStepLedger, ThrowingStepLedger>();
        using var provider = services.BuildServiceProvider();
        var sink = Assert.IsType<ProvisioningLiveEventSink>(provider.GetRequiredService<IProvisioningLiveEventSink>());
        Assert.Same(logger, provider.GetRequiredService<ILogger<ProvisioningLiveEventSink>>());

        var store = provider.GetRequiredService<IProvisioningOperationStore>();
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.EngineTarget(),
            ProvisioningFixtures.EngineSpec(),
            "sink-di-logger");
        var enqueued = await store.CreateOrGetAsync(request, "hash", DateTimeOffset.UtcNow);
        var operation = await store.ClaimAsync(enqueued.OperationId, DateTimeOffset.UtcNow);
        Assert.NotNull(operation);

        using (sink.Bind(operation, request))
        {
            await sink.PublishAsync(operation.Id, [
                new ProvisioningEvent(
                    operation.Id,
                    ProvisioningEventKind.ResourceStarted,
                    "resource-group",
                    "Resource started.",
                    DateTimeOffset.UtcNow,
                    1)
            ]);
        }

        Assert.Contains(logger.Entries, item => item.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, item => item.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Worker_is_registered_only_when_the_flag_is_on_and_runWorker_is_true()
    {
        var off = new ServiceCollection();
        off.AddProvisioning(Configuration(("Provisioning:WorkerEnabled", "true")), runWorker: false);
        Assert.DoesNotContain(off, descriptor => descriptor.ImplementationType == typeof(ProvisioningOperationHostedService));

        var on = new ServiceCollection();
        on.AddProvisioning(Configuration(("Provisioning:WorkerEnabled", "true")), runWorker: true);
        Assert.Contains(on, descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(ProvisioningOperationHostedService));
    }

    [Fact]
    public void Fake_provider_is_registered_only_when_tests_opt_in()
    {
        var production = new ServiceCollection();
        production.AddProvisioning(
            Configuration(("Provisioning:WorkerEnabled", "true")),
            runWorker: true,
            allowFakeProvider: false);
        using var productionProvider = production.BuildServiceProvider();
        Assert.IsType<UnconfiguredInfrastructureProvider>(productionProvider.GetRequiredService<IInfrastructureProvider>());

        var tests = new ServiceCollection();
        tests.AddProvisioning(
            Configuration(("Provisioning:WorkerEnabled", "true")),
            runWorker: true,
            allowFakeProvider: true);
        using var testProvider = tests.BuildServiceProvider();
        Assert.IsType<FakeInfrastructureProvider>(testProvider.GetRequiredService<IInfrastructureProvider>());
    }

    [Fact]
    public async Task Guard_refuses_to_start_when_the_flag_is_on_and_the_fake_is_selected()
    {
        var services = new ServiceCollection();
        services.AddProvisioning(
            Configuration(("Provisioning:WorkerEnabled", "true")),
            runWorker: true,
            allowFakeProvider: false);
        services.AddSingleton<IInfrastructureProvider, FakeInfrastructureProvider>();
        using var provider = services.BuildServiceProvider();
        var guard = provider.GetRequiredService<ProvisioningProviderGuard>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.StartAsync(CancellationToken.None));
        Assert.Equal("Provisioning worker cannot run with the fake provider outside tests.", error.Message);
    }

    [Fact]
    public async Task Guard_refuses_to_start_when_the_flag_is_on_and_no_real_provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProvisioning(
            Configuration(("Provisioning:WorkerEnabled", "true")),
            runWorker: true,
            allowFakeProvider: false);
        using var provider = services.BuildServiceProvider();
        var guard = provider.GetRequiredService<ProvisioningProviderGuard>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.StartAsync(CancellationToken.None));
        Assert.Equal("Provisioning worker is enabled but no real infrastructure provider is registered.", error.Message);
    }

    [Fact]
    public async Task Guard_refuses_to_start_when_the_flag_is_on_without_a_durable_store()
    {
        var services = new ServiceCollection();
        services.AddProvisioning(
            Configuration(("Provisioning:WorkerEnabled", "true")),
            runWorker: true,
            allowFakeProvider: false);
        services.AddSingleton<IInfrastructureProvider>(new UnconfiguredInfrastructureProvider());
        using var provider = services.BuildServiceProvider();
        // Replace the unconfigured provider with a stand-in that is not fake, still no durable store.
        var guard = new ProvisioningProviderGuard(
            new AzureStandIn(),
            provider.GetRequiredService<Stores.IProvisioningOperationStore>(),
            Microsoft.Extensions.Options.Options.Create(new ProvisioningOptions
            {
                WorkerEnabled = true,
                AllowFakeProvider = false
            }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.StartAsync(CancellationToken.None));
        Assert.Equal("Provisioning worker is enabled but no durable store is configured.", error.Message);
    }

    [Fact]
    public async Task Guard_refuses_to_start_when_heartbeat_is_not_well_below_the_lease()
    {
        var guard = new ProvisioningProviderGuard(
            new AzureStandIn(),
            new Stores.InMemoryProvisioningStore(),
            Microsoft.Extensions.Options.Options.Create(new ProvisioningOptions
            {
                WorkerEnabled = true,
                AllowFakeProvider = true,
                HeartbeatInterval = TimeSpan.FromSeconds(30),
                LeaseDuration = TimeSpan.FromSeconds(30)
            }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.StartAsync(CancellationToken.None));
        Assert.Equal(
            "Provisioning heartbeat must be at most one third of the lease duration.",
            error.Message);
    }

    [Fact]
    public void Hosted_catalog_store_requires_sql_server()
    {
        Assert.False(ElsaControl.Provisioning.Persistence.EntityFrameworkCore.ProvisioningPersistenceServiceCollectionExtensions.TryGetCatalogStore(
            Configuration(("Database:Provider", "Sqlite"), ("ConnectionStrings:Catalog", "Data Source=:memory:")),
            out _,
            out _,
            requireSqlServer: true));
        Assert.False(ElsaControl.Provisioning.Persistence.EntityFrameworkCore.ProvisioningPersistenceServiceCollectionExtensions.TryGetCatalogStore(
            Configuration(("ConnectionStrings:Catalog", "Server=catalog")),
            out _,
            out _,
            requireSqlServer: true));
        Assert.True(ElsaControl.Provisioning.Persistence.EntityFrameworkCore.ProvisioningPersistenceServiceCollectionExtensions.TryGetCatalogStore(
            Configuration(("Database:Provider", "SqlServer"), ("ConnectionStrings:Catalog", "Server=catalog")),
            out var provider,
            out _,
            requireSqlServer: true));
        Assert.Equal("SqlServer", provider);
    }

    private sealed class ThrowingStepLedger : IProvisioningStepLedger
    {
        public Task<ProvisioningStepLedgerResult> TryWriteAsync(
            ProvisioningOperation operation,
            ProvisioningRequest? request,
            IReadOnlyList<ProvisioningEvent> events,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("password=hunter2");

        public Task HeartbeatAsync(
            ProvisioningOperation operation,
            ProvisioningRequest? request,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingSinkLogger : ILogger<ProvisioningLiveEventSink>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
        }
    }

    private sealed class AzureStandIn : IInfrastructureProvider
    {
        public string ProviderId => "azure";
        public IReadOnlySet<string> Capabilities { get; } = new HashSet<string>(StringComparer.Ordinal);

        public Task<ProviderRunResult> PreviewAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ProviderRunResult> ProvisionAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ProviderRunResult> UpdateAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ProviderRunResult> RefreshAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ProviderRunResult> CancelAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ProviderRunResult> DestroyAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal))
            .Build();
}
