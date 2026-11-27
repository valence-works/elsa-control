using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using Microsoft.Extensions.Options;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzurePulumiProvisioningDispatcherTests
{
    [Fact]
    public void IsComplete_requires_seed_vault_https_uri()
    {
        var options = CompleteOptions();
        Assert.True(options.IsComplete);

        options.SeedVaultUri = null;
        Assert.False(options.IsComplete);
        options.SeedVaultUri = "http://kv.example.vault.azure.net/";
        Assert.False(options.IsComplete);
        options.SeedVaultUri = "not-a-uri";
        Assert.False(options.IsComplete);
        options.SeedVaultUri = "https://kv.example.vault.azure.net/";
        Assert.True(options.IsComplete);
    }

    [Fact]
    public async Task Incomplete_options_fail_closed_without_enqueueing()
    {
        var provisioning = new RecordingProvisioningService();
        var dispatcher = Dispatcher(provisioning, new AzurePulumiDispatchOptions(), workerEnabled: true);
        var result = await dispatcher.DispatchAsync(Operation(), Plan());
        Assert.False(result.Dispatched);
        Assert.Empty(provisioning.Requests);
    }

    [Fact]
    public async Task Complete_options_enqueue_a_correlated_engine_stack()
    {
        var provisioning = new RecordingProvisioningService();
        var dispatcher = Dispatcher(provisioning, CompleteOptions());
        var operation = Operation();
        var result = await dispatcher.DispatchAsync(operation, Plan());
        Assert.True(result.Dispatched);
        Assert.NotNull(result.ProvisioningOperationId);
        var request = Assert.Single(provisioning.Requests);
        Assert.Equal(ProvisioningCommand.Provision, request.Command);
        Assert.Equal(ProvisioningStackKind.Engine, request.Target.StackKind);
        Assert.Equal(AzureWorkloadPlanProvisioningMapper.EngineStackName(operation.InstanceId!.Value), request.Target.StackName);
        Assert.Equal(operation.IdempotencyKey, request.IdempotencyKey);
        Assert.Equal(operation.WorkspaceId.ToString("D"), request.StackConfig[PulumiStepLedgerMapper.ControlWorkspaceIdKey]);
        Assert.Equal(operation.Id.ToString("D"), request.StackConfig[PulumiStepLedgerMapper.ControlProviderOperationIdKey]);
        Assert.DoesNotContain(request.StackConfig.Values, value =>
            value.Contains("password", StringComparison.OrdinalIgnoreCase)
            || value.Contains("subscription", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Duplicate_admit_reuses_the_idempotency_key()
    {
        var provisioning = new RecordingProvisioningService();
        var dispatcher = Dispatcher(provisioning, CompleteOptions());
        var operation = Operation();
        Assert.True((await dispatcher.DispatchAsync(operation, Plan())).Dispatched);
        Assert.True((await dispatcher.DispatchAsync(operation, Plan())).Dispatched);
        Assert.Equal(2, provisioning.Requests.Count);
        Assert.All(provisioning.Requests, request => Assert.Equal(operation.IdempotencyKey, request.IdempotencyKey));
        Assert.Equal(provisioning.Requests[0].Target.StackName, provisioning.Requests[1].Target.StackName);
    }

    [Fact]
    public async Task Delete_enqueues_destroy_on_the_same_engine_stack()
    {
        var provisioning = new RecordingProvisioningService();
        var dispatcher = Dispatcher(provisioning, CompleteOptions());
        var operation = Operation() with { Action = AzureProviderOperationAction.Delete };
        Assert.True((await dispatcher.DispatchAsync(operation, Plan())).Dispatched);
        var request = Assert.Single(provisioning.Requests);
        Assert.Equal(ProvisioningCommand.Destroy, request.Command);
        Assert.Equal(AzureWorkloadPlanProvisioningMapper.EngineStackName(operation.InstanceId!.Value), request.Target.StackName);
    }

    [Theory]
    [InlineData(ElsaInstanceOperationAction.Reconcile)]
    [InlineData(ElsaInstanceOperationAction.UpdateIntent)]
    [InlineData(ElsaInstanceOperationAction.Start)]
    [InlineData(ElsaInstanceOperationAction.Stop)]
    [InlineData(ElsaInstanceOperationAction.Restart)]
    [InlineData(ElsaInstanceOperationAction.ApproveMinorUpgrade)]
    [InlineData(ElsaInstanceOperationAction.MajorMigration)]
    [InlineData(ElsaInstanceOperationAction.Recover)]
    public async Task Unsupported_lifecycle_action_does_not_enqueue(ElsaInstanceOperationAction action)
    {
        var provisioning = new RecordingProvisioningService();
        var dispatcher = Dispatcher(provisioning, CompleteOptions());
        var operation = Operation() with { LifecycleAction = action };
        Assert.False((await dispatcher.DispatchAsync(operation, Plan())).Dispatched);
        Assert.Empty(provisioning.Requests);
    }

    [Fact]
    public async Task Reconcile_without_a_lifecycle_action_does_not_enqueue()
    {
        var provisioning = new RecordingProvisioningService();
        var dispatcher = Dispatcher(provisioning, CompleteOptions());
        var operation = Operation() with { LifecycleAction = null };
        Assert.False((await dispatcher.DispatchAsync(operation, Plan())).Dispatched);
        Assert.Empty(provisioning.Requests);
        Assert.False(dispatcher.HasDispatched(operation.Id));
    }

    [Fact]
    public async Task Retry_enqueues_provision_on_the_same_engine_stack()
    {
        var provisioning = new RecordingProvisioningService();
        var dispatcher = Dispatcher(provisioning, CompleteOptions());
        var operation = Operation() with { LifecycleAction = ElsaInstanceOperationAction.Retry };
        Assert.True((await dispatcher.DispatchAsync(operation, Plan())).Dispatched);
        Assert.Equal(ProvisioningCommand.Provision, Assert.Single(provisioning.Requests).Command);
    }

    [Fact]
    public async Task Retry_reuses_the_engine_stack_name()
    {
        var provisioning = new RecordingProvisioningService();
        var dispatcher = Dispatcher(provisioning, CompleteOptions());
        var create = Operation();
        var retry = create with { IdempotencyKey = "retry-1" };
        Assert.True((await dispatcher.DispatchAsync(create, Plan())).Dispatched);
        Assert.True((await dispatcher.DispatchAsync(retry, Plan())).Dispatched);
        Assert.Equal(2, provisioning.Requests.Count);
        Assert.Equal(provisioning.Requests[0].Target.StackName, provisioning.Requests[1].Target.StackName);
        Assert.Equal("retry-1", provisioning.Requests[1].IdempotencyKey);
    }

    [Fact]
    public async Task Unconfigured_provider_fails_closed()
    {
        var provisioning = new RecordingProvisioningService();
        var dispatcher = Dispatcher(provisioning, CompleteOptions(), providerId: "unconfigured");
        Assert.False(dispatcher.CanDispatch);
        Assert.False((await dispatcher.DispatchAsync(Operation(), Plan())).Dispatched);
        Assert.Empty(provisioning.Requests);
    }

    [Fact]
    public async Task Dispatch_exception_fails_closed()
    {
        var dispatcher = Dispatcher(new ThrowingProvisioningService(), CompleteOptions());
        Assert.False((await dispatcher.DispatchAsync(Operation(), Plan())).Dispatched);
    }

    [Fact]
    public async Task Dispatch_retries_an_idempotent_enqueue_after_a_commit_exception()
    {
        var provisioning = new FlakyThenSucceedProvisioningService();
        var logger = new RecordingLogger();
        var dispatcher = Dispatcher(provisioning, CompleteOptions(), logger: logger);
        var result = await dispatcher.DispatchAsync(Operation(), Plan());
        Assert.True(result.Dispatched);
        Assert.Equal(2, provisioning.Attempts);
        Assert.Contains(logger.Messages, message => message.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("enqueue failed", StringComparison.Ordinal));
    }

    private static AzurePulumiProvisioningDispatcher Dispatcher(
        IProvisioningService provisioning,
        AzurePulumiDispatchOptions options,
        bool workerEnabled = true,
        string providerId = "azure",
        Microsoft.Extensions.Logging.ILogger<AzurePulumiProvisioningDispatcher>? logger = null) =>
        new(
            provisioning,
            Options.Create(options),
            Options.Create(new ProvisioningOptions { WorkerEnabled = workerEnabled }),
            new StubInfrastructureProvider(providerId),
            logger);

    private static AzurePulumiDispatchOptions CompleteOptions() => new()
    {
        EnvironmentName = "staging",
        StateBackendLocator = "stelsaplatform/pulumi-state",
        SecretsProviderLocator = "kv-elsa-platform/pulumi-secrets",
        CredentialLocator = "workload-identity",
        SqlBootstrapObjectId = Guid.NewGuid().ToString("D"),
        SqlBootstrapLogin = "sql-bootstrap",
        SeedVaultUri = "https://kv.example.vault.azure.net/"
    };

    private static AzureProviderOperation Operation() =>
        new(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "workload-a",
            AzureProviderOperationAction.Reconcile,
            "request-1",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            "3.8.0",
            "3.8",
            "combined",
            "Dedicated",
            "westeurope",
            "valenceruntimeimages.azurecr.io/runtime-combined",
            "sha256:" + new string('e', 64),
            null,
            null,
            AzureProviderOperationStatus.Accepted,
            AzureProviderOperationPhase.Planned,
            0,
            0,
            1,
            new(),
            null,
            AzureProviderHealth.Unknown,
            [],
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            InstanceId: Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            LifecycleAction: ElsaInstanceOperationAction.Create);

    private static AzureWorkloadPlan Plan() =>
        new(
            "workload-a",
            "westeurope",
            "3.8.0",
            "3.8",
            "combined",
            "Dedicated",
            "valenceruntimeimages.azurecr.io/runtime-combined",
            new string('e', 64),
            "oci://evidence.example/manifest",
            "sha256:" + new string('f', 64),
            "oci://evidence.example/signature",
            "sha256:" + new string('0', 64),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new string('c', 64),
            "3.8.0",
            "3.8.0");

    private sealed class StubInfrastructureProvider(string providerId) : IInfrastructureProvider
    {
        public string ProviderId { get; } = providerId;
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

    private sealed class RecordingProvisioningService : IProvisioningService
    {
        public List<ProvisioningRequest> Requests { get; } = [];

        public Task<ProvisioningEnqueueResult> PreviewAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            Enqueue(request);
        public Task<ProvisioningEnqueueResult> ProvisionAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            Enqueue(request);
        public Task<ProvisioningEnqueueResult> UpdateAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            Enqueue(request);
        public Task<ProvisioningEnqueueResult> RefreshAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            Enqueue(request);
        public Task<ProvisioningEnqueueResult> CancelAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            Enqueue(request);
        public Task<ProvisioningEnqueueResult> DestroyAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            Enqueue(request);
        public Task<ProvisioningOperation?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProvisioningOperation?>(null);
        public Task<IReadOnlyList<ProvisioningEvent>> ListEventsAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProvisioningEvent>>([]);
        public async IAsyncEnumerable<ProvisioningEvent> WatchAsync(
            Guid operationId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        private Task<ProvisioningEnqueueResult> Enqueue(ProvisioningRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(new ProvisioningEnqueueResult(Guid.NewGuid(), ProvisioningOperationState.Queued, false, false));
        }
    }

    private sealed class FlakyThenSucceedProvisioningService : IProvisioningService
    {
        public int Attempts { get; private set; }

        public Task<ProvisioningEnqueueResult> PreviewAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            Enqueue();
        public Task<ProvisioningEnqueueResult> ProvisionAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            Enqueue();
        public Task<ProvisioningEnqueueResult> UpdateAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            Enqueue();
        public Task<ProvisioningEnqueueResult> RefreshAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            Enqueue();
        public Task<ProvisioningEnqueueResult> CancelAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            Enqueue();
        public Task<ProvisioningEnqueueResult> DestroyAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            Enqueue();
        public Task<ProvisioningOperation?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProvisioningOperation?>(null);
        public Task<IReadOnlyList<ProvisioningEvent>> ListEventsAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProvisioningEvent>>([]);
        public async IAsyncEnumerable<ProvisioningEvent> WatchAsync(
            Guid operationId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        private Task<ProvisioningEnqueueResult> Enqueue()
        {
            Attempts++;
            if (Attempts == 1)
                throw new InvalidOperationException("enqueue failed");
            return Task.FromResult(new ProvisioningEnqueueResult(Guid.NewGuid(), ProvisioningOperationState.Queued, false, true));
        }
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<AzurePulumiProvisioningDispatcher>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }

    private sealed class ThrowingProvisioningService : IProvisioningService
    {
        public Task<ProvisioningEnqueueResult> PreviewAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("enqueue failed");
        public Task<ProvisioningEnqueueResult> ProvisionAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("enqueue failed");
        public Task<ProvisioningEnqueueResult> UpdateAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("enqueue failed");
        public Task<ProvisioningEnqueueResult> RefreshAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("enqueue failed");
        public Task<ProvisioningEnqueueResult> CancelAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("enqueue failed");
        public Task<ProvisioningEnqueueResult> DestroyAsync(ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("enqueue failed");
        public Task<ProvisioningOperation?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProvisioningOperation?>(null);
        public Task<IReadOnlyList<ProvisioningEvent>> ListEventsAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProvisioningEvent>>([]);
        public async IAsyncEnumerable<ProvisioningEvent> WatchAsync(
            Guid operationId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }
}
