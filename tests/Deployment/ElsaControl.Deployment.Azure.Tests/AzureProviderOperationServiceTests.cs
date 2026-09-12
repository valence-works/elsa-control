using ElsaControl.Deployment.Azure;
using System.Text.Json;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureProviderOperationServiceTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Submit_persists_only_the_safe_provider_plan_projection()
    {
        var store = new CapturingStore();
        var service = new AzureProviderOperationService(store, new FixedTimeProvider(Now));

        var result = await service.SubmitAsync(
            WorkspaceId,
            new AzureProviderOperationSubmission(
                "request-1",
                new('b', 64),
                CreatePlan()));

        Assert.Equal(AzureProviderOperationAction.Reconcile, result.Action);
        Assert.Equal("oci://evidence.example/manifest", store.Request!.ReleaseManifestReference);
        Assert.Equal("oci://evidence.example/signature", store.Request.ReleaseManifestSignatureReference);
        Assert.Equal("secret://vault/database", store.Request.SecretReferences!["database:connectionstring"]);
        Assert.Null(store.Request.GetType().GetProperty("RawPayload"));
    }

    [Fact]
    public async Task Submit_binds_the_operation_to_the_validated_provider_scope_fingerprint()
    {
        var store = new CapturingStore();
        var service = new AzureProviderOperationService(store, new FixedTimeProvider(Now));

        await service.SubmitAsync(
            WorkspaceId,
            new AzureProviderOperationSubmission("request-1", new('b', 64), CreatePlan(), new string('C', 64)));

        Assert.Equal(new string('c', 64), store.Request!.ProviderScopeFingerprint);
    }

    [Fact]
    public async Task Submit_with_replay_reports_the_atomic_store_decision()
    {
        var store = new CapturingStore();
        var service = new AzureProviderOperationService(store, new FixedTimeProvider(Now));
        var submission = new AzureProviderOperationSubmission("request-1", new('b', 64), CreatePlan());

        var first = await service.SubmitWithReplayAsync(WorkspaceId, submission);
        var replay = await service.SubmitWithReplayAsync(WorkspaceId, submission);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Operation.Id, replay.Operation.Id);
    }

    [Fact]
    public async Task Submit_persists_the_admitted_capacity_with_the_safe_projection()
    {
        var store = new CapturingStore();
        var service = new AzureProviderOperationService(store, new FixedTimeProvider(Now));

        await service.SubmitAsync(WorkspaceId, new AzureProviderOperationSubmission("request-1", new('b', 64), CreatePlan()));

        Assert.Equal(new AzureWorkloadCapacity(1, 1, 500, 1024), store.Request!.Capacity);
    }

    [Fact]
    public async Task Submit_rejects_capacity_without_a_Container_Apps_mapping_before_persistence()
    {
        var store = new CapturingStore();
        var service = new AzureProviderOperationService(store, new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitAsync(
            WorkspaceId,
            new AzureProviderOperationSubmission("request-1", new('b', 64), CreatePlan() with
            {
                Capacity = new AzureWorkloadCapacity(1, 1, 500, 2048)
            })));
        Assert.Null(store.Request);
    }

    [Fact]
    public async Task Restore_fails_closed_when_legacy_operation_lacks_release_package_metadata()
    {
        var store = new CapturingStore();
        var legacyPlan = CreatePlan() with
        {
            SqlWorkflowPackageVersion = null,
            SqlQuartzPackageVersion = null
        };
        var request = AzureProviderOperationService.CreateOperationRequest(
            WorkspaceId,
            "request-1",
            new('b', 64),
            legacyPlan);
        var operation = await store.CreateOrGetAsync(AzureProviderOperationValidation.Normalize(request), Now);

        Assert.Null(AzureProviderOperationService.TryRestorePlan(operation));
    }

    [Fact]
    public async Task Submit_rejects_missing_release_package_metadata_before_persistence()
    {
        var store = new CapturingStore();
        var service = new AzureProviderOperationService(store, new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitAsync(
            WorkspaceId,
            new AzureProviderOperationSubmission("request-1", new('b', 64), CreatePlan() with
            {
                SqlWorkflowPackageVersion = null,
                SqlQuartzPackageVersion = null
            })));
        Assert.Null(store.Request);
    }

    [Theory]
    [InlineData("other.azurecr.io/runtime-combined")]
    [InlineData("valenceruntimeimages.azurecr.io/other-runtime")]
    public async Task Submit_rejects_a_plan_outside_the_governed_repository_before_persistence(string repository)
    {
        var store = new CapturingStore();
        var service = new AzureProviderOperationService(store, new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitAsync(
            WorkspaceId,
            new AzureProviderOperationSubmission("request-1", new('b', 64), CreatePlan() with
            {
                ImageRepository = repository
            })));
        Assert.Null(store.Request);
    }

    [Theory]
    [InlineData("secret://vault/../database")]
    [InlineData("secret://vault/database%2Fconnection")]
    [InlineData("secret://user:password@vault/database")]
    [InlineData("secret://vault/database?version=1")]
    public async Task Submit_rejects_unsafe_secret_locators(string locator)
    {
        var service = new AzureProviderOperationService(new CapturingStore(), new FixedTimeProvider(Now));
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitAsync(
            WorkspaceId,
            new AzureProviderOperationSubmission("request-1", new('b', 64), CreatePlan() with
            {
                SecretReferences = new Dictionary<string, string> { ["database:connectionstring"] = locator }
            })));

        Assert.Contains("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submit_rejects_noncanonical_secret_reference_keys()
    {
        var service = new AzureProviderOperationService(new CapturingStore(), new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitAsync(
            WorkspaceId,
            new AzureProviderOperationSubmission("request-1", new('b', 64), CreatePlan() with
            {
                SecretReferences = new Dictionary<string, string>
                {
                    ["Database:ConnectionString"] = "secret://vault/database"
                }
            })));
    }

    [Fact]
    public async Task Delete_submission_uses_the_same_idempotent_operation_contract()
    {
        var store = new CapturingStore();
        var service = new AzureProviderOperationService(store, new FixedTimeProvider(Now));

        var result = await service.SubmitDeleteAsync(
            WorkspaceId,
            new AzureProviderOperationSubmission("delete-1", new('b', 64), CreatePlan()));

        Assert.Equal(AzureProviderOperationAction.Delete, result.Action);
        Assert.Equal(AzureProviderOperationAction.Delete, store.Request!.Action);
        Assert.Equal("delete-1:delete", store.Request.IdempotencyKey);
    }

    [Fact]
    public async Task Delete_submission_advances_a_deterministic_retry_chain_after_terminal_failures()
    {
        var store = new CapturingStore(terminalAttempts: 2);
        var service = new AzureProviderOperationService(store, new FixedTimeProvider(Now));
        var submission = new AzureProviderOperationSubmission("delete-1", new('b', 64), CreatePlan());

        var first = await service.SubmitDeleteAsync(WorkspaceId, submission);
        var replay = await service.SubmitDeleteAsync(WorkspaceId, submission);

        Assert.Equal(AzureProviderOperationStatus.Accepted, first.Status);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(3, store.Requests.Count);
        Assert.Equal("delete-1:delete", store.Requests[0].IdempotencyKey);
        Assert.Contains(":retry:", store.Requests[1].IdempotencyKey, StringComparison.Ordinal);
        Assert.Contains(":retry:", store.Requests[2].IdempotencyKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_submission_derives_a_bounded_deterministic_key_from_a_maximum_length_key()
    {
        var originalKey = new string('x', 512);
        var firstStore = new CapturingStore();
        var secondStore = new CapturingStore();

        await new AzureProviderOperationService(firstStore, new FixedTimeProvider(Now)).SubmitDeleteAsync(
            WorkspaceId,
            new AzureProviderOperationSubmission(originalKey, new('b', 64), CreatePlan()));
        await new AzureProviderOperationService(secondStore, new FixedTimeProvider(Now)).SubmitDeleteAsync(
            WorkspaceId,
            new AzureProviderOperationSubmission(originalKey, new('b', 64), CreatePlan()));

        Assert.StartsWith("delete:sha256:", firstStore.Request!.IdempotencyKey, StringComparison.Ordinal);
        Assert.True(firstStore.Request.IdempotencyKey.Length <= 512);
        Assert.Equal(firstStore.Request.IdempotencyKey, secondStore.Request!.IdempotencyKey);
    }

    [Theory]
    [InlineData(513)]
    [InlineData(0)]
    public async Task Delete_submission_rejects_invalid_original_idempotency_keys(int length)
    {
        var key = length == 0 ? "\t" : new string('x', length);
        var service = new AzureProviderOperationService(new CapturingStore(), new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitDeleteAsync(
            WorkspaceId,
            new AzureProviderOperationSubmission(key, new('b', 64), CreatePlan())));
    }

    [Fact]
    public void Operation_json_does_not_include_secret_locators_or_recovery_only_projection()
    {
        var operation = new AzureProviderOperation(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            WorkspaceId,
            "workload-a",
            AzureProviderOperationAction.Reconcile,
            "request-1",
            new('a', 64),
            new('b', 64),
            new('c', 64),
            new('d', 64),
            "3.8.0",
            "3.8",
            "combined",
            "Dedicated",
            "westeurope",
            "valenceruntimeimages.azurecr.io/runtime-combined",
            "sha256:" + new string('e', 64),
            "sha256:" + new string('f', 64),
            "sha256:" + new string('a', 64),
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
            Now,
            Now,
            null,
            "oci://evidence.example/manifest",
            "oci://evidence.example/signature",
            new Dictionary<string, string> { ["database:connectionstring"] = "secret://vault/database" });

        var json = JsonSerializer.Serialize(operation);

        Assert.DoesNotContain("SecretReferences", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret://vault/database", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SafeSecretReferences", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_safe_secret_references_cannot_be_mutated_across_operations()
    {
        var operation = await new CapturingStore().CreateOrGetAsync(
            AzureProviderOperationValidation.Normalize(new AzureProviderOperationRequest(
                WorkspaceId,
                "workload-a",
                AzureProviderOperationAction.Reconcile,
                "request-empty-secrets",
                new('a', 64),
                new('b', 64),
                "3.8.0",
                "3.8",
                "combined",
                "Dedicated",
                "westeurope",
                "valenceruntimeimages.azurecr.io/runtime-combined",
                "sha256:" + new string('e', 64))),
            Now);
        var mutableView = Assert.IsAssignableFrom<IDictionary<string, string>>(operation.SafeSecretReferences);

        Assert.Throws<NotSupportedException>(() => mutableView.Add("database", "secret://vault/database"));
        Assert.Empty(operation.SafeSecretReferences);
    }

    private static AzureWorkloadPlan CreatePlan() => new(
        "workload-a",
        "westeurope",
        "3.8.0",
        "3.8",
        "combined",
        "Dedicated",
        "valenceruntimeimages.azurecr.io/runtime-combined",
        new('c', 64),
        "oci://evidence.example/manifest",
        "sha256:" + new string('d', 64),
        "oci://evidence.example/signature",
        "sha256:" + new string('e', 64),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["database:connectionstring"] = "secret://vault/database"
        },
        new('a', 64),
        "3.8.0-preview.5413",
        "3.8.0-preview.5413",
        new AzureWorkloadCapacity(1, 1, 500, 1024));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingStore(int terminalAttempts = 0) : IAzureProviderOperationStore
    {
        public AzureProviderOperationRequest? Request { get; private set; }
        public List<AzureProviderOperationRequest> Requests { get; } = [];
        private AzureProviderOperation? _operation;
        private readonly Dictionary<string, AzureProviderOperation> _operations = new(StringComparer.Ordinal);

        public async Task<AzureProviderOperationCreateResult> CreateOrGetWithResultAsync(
            AzureProviderOperationRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            var normalized = AzureProviderOperationValidation.Normalize(request);
            var replayed = _operations.ContainsKey(normalized.IdempotencyKey);
            var operation = await CreateOrGetAsync(normalized, now, cancellationToken);
            return new(operation, replayed);
        }

        public Task<AzureProviderOperation> CreateOrGetAsync(AzureProviderOperationRequest request, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Request = AzureProviderOperationValidation.Normalize(request);
            if (_operations.TryGetValue(Request.IdempotencyKey, out var existing))
            {
                _operation = existing;
                return Task.FromResult(existing);
            }

            Requests.Add(Request);
            _operation = new(
                Guid.Parse($"22222222-2222-2222-2222-{(_operations.Count + 1):D12}"),
                Request.WorkspaceId,
                Request.TargetKey,
                Request.Action,
                Request.IdempotencyKey,
                AzureProviderOperationValidation.ComputeRequestHash(Request),
                AzureProviderOperationValidation.ComputeOperationIdentity(Request),
                Request.PlanFingerprint,
                Request.TemplateFingerprint,
                Request.ElsaVersion,
                Request.ReleaseLine,
                Request.Topology,
                Request.Isolation,
                Request.Location,
                Request.ImageRepository,
                Request.ImageDigest,
                Request.ReleaseManifestDigest,
                Request.ReleaseManifestSignatureDigest,
                _operations.Count < terminalAttempts
                    ? AzureProviderOperationStatus.Failed
                    : AzureProviderOperationStatus.Accepted,
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
                now,
                now,
                null,
                Request.ReleaseManifestReference,
                Request.ReleaseManifestSignatureReference,
                Request.SecretReferences);
            _operations.Add(Request.IdempotencyKey, _operation);
            return Task.FromResult(_operation);
        }

        public Task<AzureProviderOperation?> GetAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_operation is { WorkspaceId: var currentWorkspace, Id: var currentId } && currentWorkspace == workspaceId && currentId == operationId ? _operation : null);

        public Task<AzureProviderOperation?> GetLatestReconcileAsync(Guid workspaceId, string targetKey, string? providerScopeFingerprint, CancellationToken cancellationToken = default) => Task.FromResult<AzureProviderOperation?>(null);
        public Task<IReadOnlyList<AzureProviderOperation>> ListRunnableAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AzureProviderOperation>>([]);
        public Task<AzureProviderOperation?> MarkUnrestorableAsync(Guid workspaceId, Guid operationId, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) => Task.FromResult<AzureProviderOperation?>(null);
        public Task<AzureProviderOperation?> ClaimAsync(Guid workspaceId, Guid operationId, string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) => Task.FromResult<AzureProviderOperation?>(null);
        public Task<AzureProviderOperation?> ClaimRecoveryAsync(Guid workspaceId, Guid operationId, string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) => Task.FromResult<AzureProviderOperation?>(null);
        public Task<AzureProviderOperation?> HeartbeatAsync(Guid workspaceId, Guid operationId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) => Task.FromResult<AzureProviderOperation?>(null);
        public Task<AzureProviderOperation?> CheckpointAsync(Guid workspaceId, Guid operationId, string leaseToken, AzureProviderCheckpoint checkpoint, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) => Task.FromResult<AzureProviderOperation?>(null);
        public Task<AzureProviderOperation?> FinalizeAsync(Guid workspaceId, Guid operationId, string leaseToken, AzureProviderOperationStatus status, string code, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) => Task.FromResult<AzureProviderOperation?>(null);
        public Task<int> RecoverStaleAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<IReadOnlyList<AzureProviderOperationTransition>> ListTransitionsAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AzureProviderOperationTransition>>([]);
    }
}
