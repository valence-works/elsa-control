using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class ElsaInstanceHealthMonitorHostedServiceTests : IAsyncDisposable
{
    private const string Sensitive = "GET https://tenant.example.test/health?token=secret-value failed";
    private readonly InstanceStore _store = new();
    private readonly RecordingLogger _logger = new();
    private readonly ServiceProvider _services;
    private Func<IElsaInstanceProviderHealthProbePort> _probe = () => new ThrowingProbe(new HttpRequestException(Sensitive));
    private int _storeScopes;
    private int _probeCompositions;

    public ElsaInstanceHealthMonitorHostedServiceTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<IElsaInstanceHealthMonitorStore>(_ =>
        {
            Interlocked.Increment(ref _storeScopes);
            return _store;
        });
        services.AddScoped(_ =>
        {
            Interlocked.Increment(ref _probeCompositions);
            return _probe();
        });
        _services = services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(null, true, true, false)]
    [InlineData("false", true, true, false)]
    [InlineData("true", false, true, false)]
    [InlineData("true", true, false, false)]
    [InlineData("true", true, true, true)]
    public void The_monitor_runs_only_when_switched_on_with_the_provider_pipeline_outside_tests(
        string? enabled, bool providerPipelineComposed, bool runWorkers, bool expected)
    {
        var services = new ServiceCollection();

        var composed = ElsaInstanceHealthMonitorComposition.AddHealthMonitor(
            services, Configuration(new() { ["Deployment:ElsaInstanceHealthMonitor:Enabled"] = enabled }),
            providerPipelineComposed, runWorkers);

        Assert.Equal(expected, composed);
        Assert.Equal(expected, services.Any(x =>
            x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(ElsaInstanceHealthMonitorHostedService)));
        Assert.Contains(services, x => x.ServiceType == typeof(IConfigureOptions<ElsaInstanceHealthMonitorOptions>));
    }

    [Fact]
    public void Configured_bounds_bind_to_the_monitor()
    {
        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        ElsaInstanceHealthMonitorComposition.AddHealthMonitor(services, Configuration(new()
        {
            ["Deployment:ElsaInstanceHealthMonitor:Enabled"] = "true",
            ["Deployment:ElsaInstanceHealthMonitor:Interval"] = "00:02:00",
            ["Deployment:ElsaInstanceHealthMonitor:MaxJitter"] = "00:00:20",
            ["Deployment:ElsaInstanceHealthMonitor:ProbeTimeout"] = "00:00:05",
            ["Deployment:ElsaInstanceHealthMonitor:MaxConcurrency"] = "8",
            ["Deployment:ElsaInstanceHealthMonitor:UnhealthyThreshold"] = "4",
            ["Deployment:ElsaInstanceHealthMonitor:HealthyThreshold"] = "3"
        }), providerPipelineComposed: true, runWorkers: true);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            new ElsaInstanceHealthMonitorOptions
            {
                Enabled = true,
                Interval = TimeSpan.FromMinutes(2),
                MaxJitter = TimeSpan.FromSeconds(20),
                ProbeTimeout = TimeSpan.FromSeconds(5),
                MaxConcurrency = 8,
                UnhealthyThreshold = 4,
                HealthyThreshold = 3
            },
            provider.GetRequiredService<IOptions<ElsaInstanceHealthMonitorOptions>>().Value);
        Assert.NotNull(provider.GetRequiredService<ElsaInstanceHealthMonitor>());
    }

    [Fact]
    public async Task Each_evaluation_runs_in_its_own_scope()
    {
        _store.Add(3);

        var evaluations = await CreateService().RunCycleAsync(CancellationToken.None);

        Assert.Equal(3, evaluations.Count);
        Assert.Equal((1 + 3, 3), (_storeScopes, _probeCompositions));
    }

    [Fact]
    public async Task A_probe_that_cannot_be_composed_counts_as_an_unknown_failure()
    {
        var instanceId = _store.Add(1)[0];
        _probe = () => throw new InvalidOperationException(Sensitive);
        var service = CreateService();

        var evaluations = new List<ElsaInstanceHealthEvaluation>();
        for (var cycle = 0; cycle < 3; cycle++)
            evaluations.AddRange(await service.RunCycleAsync(CancellationToken.None));

        Assert.Equal(
            new[] { ElsaInstanceHealthMonitorOutcome.Unchanged, ElsaInstanceHealthMonitorOutcome.Unchanged, ElsaInstanceHealthMonitorOutcome.Transitioned },
            evaluations.Select(x => x.Outcome));
        Assert.All(evaluations, x => Assert.Equal((ElsaInstanceHealth.Unknown, nameof(InvalidOperationException)), (x.Observed, x.ExceptionType)));
        Assert.Equal(ElsaInstanceHealth.Unknown, _store[instanceId].Health);
        AssertValueFree(_logger.Messages);
    }

    [Fact]
    public async Task Cycles_log_changes_and_failures_with_opaque_ids_and_exception_types_only()
    {
        var instanceId = _store.Add(1)[0];
        var service = CreateService();

        for (var cycle = 0; cycle < 3; cycle++)
            await service.RunCycleAsync(CancellationToken.None);

        var change = Assert.Single(_logger.Messages, x => x.Level == LogLevel.Information);
        Assert.Contains($"instance {instanceId}: Healthy to Unknown, diagnostic {ElsaInstanceHealthMonitor.ProbeFailedCode}.", change.Message);
        var failures = _logger.Messages.Where(x => x.Level == LogLevel.Warning).ToArray();
        Assert.Equal(3, failures.Length);
        Assert.All(failures, x => Assert.Contains($"({nameof(HttpRequestException)})", x.Message));
        AssertValueFree(_logger.Messages);
    }

    [Fact]
    public async Task A_failed_cycle_is_logged_with_its_exception_type_only_and_the_service_keeps_running()
    {
        _store.ListFailure = new InvalidOperationException(Sensitive);
        var service = CreateService();

        await service.StartAsync(CancellationToken.None);
        var logged = await _logger.WaitForAsync(LogLevel.Warning);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal($"Managed Elsa instance health monitor cycle failed ({nameof(InvalidOperationException)}).", logged.Message);
        AssertValueFree(_logger.Messages);
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    private ElsaInstanceHealthMonitorHostedService CreateService()
    {
        var options = new ElsaInstanceHealthMonitorOptions { Enabled = true, MaxJitter = TimeSpan.Zero };
        return new(
            _services.GetRequiredService<IServiceScopeFactory>(),
            new ElsaInstanceHealthMonitor(options),
            Options.Create(options),
            TimeProvider.System,
            _logger);
    }

    private static void AssertValueFree(IEnumerable<(LogLevel Level, string Message, Exception? Exception)> messages) =>
        Assert.All(messages, x =>
        {
            Assert.Null(x.Exception);
            Assert.DoesNotContain("tenant.example.test", x.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-value", x.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("runtime.example.test", x.Message, StringComparison.Ordinal);
        });

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>Shared in-memory rows with the store's compare-and-set commit rule.</summary>
    private sealed class InstanceStore : IElsaInstanceHealthMonitorStore
    {
        private readonly Dictionary<Guid, ElsaInstanceHealthMonitorTarget> _targets = [];

        public Exception? ListFailure { get; set; }

        public ElsaInstanceHealthMonitorTarget this[Guid instanceId]
        {
            get
            {
                lock (_targets)
                    return _targets[instanceId];
            }
        }

        public Guid[] Add(int count)
        {
            lock (_targets)
            {
                return Enumerable.Range(0, count).Select(_ =>
                {
                    var target = new ElsaInstanceHealthMonitorTarget(
                        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, ElsaDesiredLifecycle.Running,
                        ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy,
                        new ElsaCurrentDeploymentReference("deployment-1", "attempt-1", "https://runtime.example.test"));
                    _targets[target.InstanceId] = target;
                    return target.InstanceId;
                }).ToArray();
            }
        }

        public Task<IReadOnlyList<ElsaInstanceHealthMonitorTarget>> ListHealthMonitorTargetsAsync(
            int offset, int limit, CancellationToken cancellationToken = default)
        {
            if (ListFailure is { } failure)
                throw failure;
            lock (_targets)
                return Task.FromResult<IReadOnlyList<ElsaInstanceHealthMonitorTarget>>(_targets.Values.Skip(offset).Take(limit).ToArray());
        }

        public Task<ElsaInstanceHealthMonitorTarget?> GetHealthMonitorTargetAsync(
            Guid workspaceId, Guid instanceId, CancellationToken cancellationToken = default)
        {
            lock (_targets)
                return Task.FromResult<ElsaInstanceHealthMonitorTarget?>(_targets[instanceId]);
        }

        public Task<int> CommitHealthTransitionAsync(
            ElsaInstanceHealthTransition transition, CancellationToken cancellationToken = default)
        {
            lock (_targets)
            {
                var current = _targets[transition.InstanceId];
                if (current.Version != transition.ExpectedVersion || current.Health != transition.ExpectedHealth)
                    throw new ElsaInstanceLifecycleConflictException("Instance health changed concurrently.");
                _targets[transition.InstanceId] = current with { Health = transition.Health, Version = current.Version + 1 };
                return Task.FromResult(current.Version + 1);
            }
        }
    }

    private sealed class ThrowingProbe(Exception exception) : IElsaInstanceProviderHealthProbePort
    {
        public Task<ElsaInstanceHealthProbeResult> ProbeAsync(
            ElsaInstanceHealthProbeRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<ElsaInstanceHealthProbeResult>(exception);
    }

    private sealed class RecordingLogger : ILogger<ElsaInstanceHealthMonitorHostedService>
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _messages = [];
        private readonly TaskCompletionSource _logged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Messages
        {
            get
            {
                lock (_messages)
                    return _messages.ToArray();
            }
        }

        public async Task<(LogLevel Level, string Message, Exception? Exception)> WaitForAsync(LogLevel level)
        {
            await _logged.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return Messages.First(x => x.Level == level);
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
                _messages.Add((logLevel, formatter(state, exception), exception));
            _logged.TrySetResult();
        }
    }
}
