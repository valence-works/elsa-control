using ElsaControl.Deployment.Azure;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

/// <summary>
/// Workload capacity is part of the provider projection a worker restores after a restart. A
/// restored plan must carry exactly the admitted capacity; an operation retained before capacity
/// existed stays restorable; corrupted capacity never deploys with guessed sizing.
/// </summary>
public sealed class AzureProviderOperationCapacityPersistenceTests : IAsyncDisposable
{
    private static readonly AzureWorkloadCapacity StandardSmall = new(1, 1, 500, 1024);
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly CatalogDbContext _db;
    private readonly AzureProviderOperationStore _store;

    public AzureProviderOperationCapacityPersistenceTests()
    {
        _connection.Open();
        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "Azure capacity workspace" });
        _db.SaveChanges();
        _store = new AzureProviderOperationStore(_db);
    }

    [Fact]
    public async Task Restored_plan_carries_exactly_the_admitted_capacity()
    {
        var operation = await CreateAsync(StandardSmall);

        var retained = await _store.GetAsync(_workspaceId, operation.Id);

        Assert.Equal(StandardSmall, retained?.Capacity);
        Assert.Equal(StandardSmall, new PersistedAzureProviderPlanSource().Resolve(retained!)?.Capacity);
    }

    [Fact]
    public async Task Operation_retained_before_capacity_stays_restorable_without_capacity()
    {
        var operation = await CreateAsync(capacity: null);

        var retained = await _store.GetAsync(_workspaceId, operation.Id);
        var restored = new PersistedAzureProviderPlanSource().Resolve(retained!);

        Assert.False(retained!.PersistedMetadataInvalid);
        Assert.NotNull(restored);
        Assert.Null(restored.Capacity);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("scale-to-zero")]
    public async Task Corrupted_capacity_is_marked_unrestorable_without_calling_the_runner(string corruption)
    {
        var operation = await CreateAsync(StandardSmall);
        if (corruption == "partial")
            await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AzureProviderOperations SET CapacityCpuMillicores = NULL WHERE Id = {operation.Id}");
        else
            await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AzureProviderOperations SET CapacityMinReplicas = 0 WHERE Id = {operation.Id}");
        var worker = new AzureProviderOperationWorker(
            _store,
            new AzureProviderExecutor(_store, new NeverCalledRunner()),
            new PersistedAzureProviderPlanSource(),
            new FixedTimeProvider(Now));

        Assert.Null(new PersistedAzureProviderPlanSource().Resolve(Assert.Single(await _store.ListRunnableAsync(Now, 10))));
        Assert.Equal(0, await worker.ProcessOnceAsync());

        Assert.Equal(AzureProviderOperationStatus.Failed, (await _store.GetAsync(_workspaceId, operation.Id))?.Status);
        Assert.Single(await _store.ListTransitionsAsync(_workspaceId, operation.Id), x => x.Code == "azure.plan.unrestorable");
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private Task<AzureProviderOperation> CreateAsync(AzureWorkloadCapacity? capacity) =>
        _store.CreateOrGetAsync(RestorableRequest(_workspaceId) with { Capacity = capacity }, Now);

    /// <summary>A complete provider projection the worker can restore, as the lifecycle adapter persists it.</summary>
    internal static AzureProviderOperationRequest RestorableRequest(Guid workspaceId) => new(
        workspaceId, "workload-a", AzureProviderOperationAction.Reconcile, "request-1",
        new('a', 64), new('b', 64), "3.8.0", "3.8", "combined", "Dedicated", "westeurope",
        "valenceruntimeimages.azurecr.io/runtime-combined", "sha256:" + new string('c', 64),
        "sha256:" + new string('d', 64), "sha256:" + new string('e', 64),
        "oci://evidence.example/manifest", "oci://evidence.example/signature",
        new Dictionary<string, string> { ["database:connectionstring"] = "secret://vault/database" },
        new string('f', 64), "3.8.0", "3.8.0");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NeverCalledRunner : IAzureProviderRunner
    {
        public Task<AzureProviderRunnerResult> RunAsync(AzureProviderRunnerCommand command, CancellationToken cancellationToken = default) =>
            throw new Xunit.Sdk.XunitException("The provider runner must not be called for corrupted persisted capacity.");
    }
}
