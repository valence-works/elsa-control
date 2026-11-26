using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Provisioning.Abstractions;
using Microsoft.Extensions.Options;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzurePulumiProvisioningDispatcherTests
{
    [Fact]
    public async Task Incomplete_options_leave_the_bicep_path()
    {
        var dispatcher = new AzurePulumiProvisioningDispatcher(
            new RecordingProvisioningService(),
            Options.Create(new AzurePulumiDispatchOptions()));
        var dispatched = await dispatcher.TryDispatchAsync(Operation(), Plan());
        Assert.False(dispatched);
    }

    [Fact]
    public async Task Complete_options_enqueue_a_correlated_engine_stack()
    {
        var provisioning = new RecordingProvisioningService();
        var dispatcher = new AzurePulumiProvisioningDispatcher(
            provisioning,
            Options.Create(new AzurePulumiDispatchOptions
            {
                EnvironmentName = "staging",
                StateBackendLocator = "stelsaplatform/pulumi-state",
                SecretsProviderLocator = "kv-elsa-platform/pulumi-secrets",
                CredentialLocator = "workload-identity",
                SqlBootstrapObjectId = Guid.NewGuid().ToString("D"),
                SqlBootstrapLogin = "sql-bootstrap"
            }));
        var operation = Operation();
        Assert.True(await dispatcher.TryDispatchAsync(operation, Plan()));
        var request = Assert.Single(provisioning.Requests);
        Assert.Equal(ProvisioningCommand.Provision, request.Command);
        Assert.Equal(ProvisioningStackKind.Engine, request.Target.StackKind);
        Assert.Equal(AzureWorkloadPlanProvisioningMapper.EngineStackName(operation.InstanceId!.Value), request.Target.StackName);
        Assert.Equal(operation.WorkspaceId.ToString("D"), request.StackConfig[PulumiStepLedgerMapper.ControlWorkspaceIdKey]);
        Assert.Equal(operation.Id.ToString("D"), request.StackConfig[PulumiStepLedgerMapper.ControlProviderOperationIdKey]);
        Assert.DoesNotContain(request.StackConfig.Values, value =>
            value.Contains("password", StringComparison.OrdinalIgnoreCase)
            || value.Contains("subscription", StringComparison.OrdinalIgnoreCase));
    }

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
}
