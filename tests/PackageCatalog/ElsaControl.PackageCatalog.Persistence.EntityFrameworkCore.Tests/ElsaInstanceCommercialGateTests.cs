using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ElsaInstanceCommercialGateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-04T10:00:00Z");

    [Theory]
    [InlineData(OrganizationSubscriptionState.Trial, true, "commercial.allowed")]
    [InlineData(OrganizationSubscriptionState.Active, true, "commercial.allowed")]
    [InlineData(OrganizationSubscriptionState.PastDue, true, "commercial.allowed")]
    [InlineData(OrganizationSubscriptionState.Constrained, false, ElsaInstanceCommercialOperation.LifecycleConstrained)]
    [InlineData(OrganizationSubscriptionState.Suspended, false, ElsaInstanceCommercialOperation.LifecycleConstrained)]
    [InlineData(OrganizationSubscriptionState.Retained, false, ElsaInstanceCommercialOperation.LifecycleConstrained)]
    [InlineData(OrganizationSubscriptionState.Deleted, false, ElsaInstanceCommercialOperation.LifecycleConstrained)]
    public async Task Provider_mutations_follow_the_subscription_state_matrix(
        OrganizationSubscriptionState state,
        bool allowed,
        string expectedCode)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, state, managedHostingEnabled: true);

        var decision = await new EfCoreElsaInstanceCommercialGate(db).EvaluateAsync(
            workspace.OrganizationId,
            ElsaInstanceOperationAction.Create,
            activeInstanceCount: 0);

        Assert.Equal(allowed, decision.Allowed);
        Assert.Equal(expectedCode, decision.Code);
    }

    [Fact]
    public async Task Missing_or_unprojected_entitlement_fails_closed_for_provider_mutations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = new Workspace { Name = "No entitlement workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        var gate = new EfCoreElsaInstanceCommercialGate(db);
        var missing = await gate.EvaluateAsync(workspace.OrganizationId, ElsaInstanceOperationAction.Create, 0);
        Assert.False(missing.Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.EntitlementRequired, missing.Code);

        db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = workspace.OrganizationId,
            ManagedHostingEnabled = true,
            SubscriptionState = null,
            MaxInstances = int.MaxValue,
            SyncedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();

        var unprojected = await gate.EvaluateAsync(workspace.OrganizationId, ElsaInstanceOperationAction.Create, 0);
        Assert.False(unprojected.Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.SubscriptionStateRequired, unprojected.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(OrganizationSubscriptionState.Constrained)]
    [InlineData(OrganizationSubscriptionState.Suspended)]
    [InlineData(OrganizationSubscriptionState.Retained)]
    [InlineData(OrganizationSubscriptionState.Deleted)]
    public async Task Stop_and_delete_cleanup_remain_allowed_in_constrained_or_missing_states(OrganizationSubscriptionState? state)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, state, managedHostingEnabled: state is not null);
        var gate = new EfCoreElsaInstanceCommercialGate(db);

        Assert.True((await gate.EvaluateAsync(workspace.OrganizationId, ElsaInstanceOperationAction.Stop)).Allowed);
        Assert.True((await gate.EvaluateAsync(workspace.OrganizationId, ElsaInstanceOperationAction.Delete)).Allowed);
    }

    [Fact]
    public async Task Entitlement_decisions_are_isolated_by_organization()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var active = await CreateWorkspaceAsync(db, OrganizationSubscriptionState.Active, managedHostingEnabled: true);
        var constrained = await CreateWorkspaceAsync(db, OrganizationSubscriptionState.Constrained, managedHostingEnabled: true);
        var gate = new EfCoreElsaInstanceCommercialGate(db);

        Assert.True((await gate.EvaluateAsync(active.OrganizationId, ElsaInstanceOperationAction.Create, 0)).Allowed);
        var denied = await gate.EvaluateAsync(constrained.OrganizationId, ElsaInstanceOperationAction.Create, 0);
        Assert.False(denied.Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.LifecycleConstrained, denied.Code);
    }

    [Fact]
    public async Task Max_instances_is_enforced_at_the_admission_decision()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, OrganizationSubscriptionState.Active, managedHostingEnabled: true, maxInstances: 1);
        var gate = new EfCoreElsaInstanceCommercialGate(db);

        Assert.True((await gate.EvaluateAsync(workspace.OrganizationId, ElsaInstanceOperationAction.Create, 0)).Allowed);
        var denied = await gate.EvaluateAsync(workspace.OrganizationId, ElsaInstanceOperationAction.Create, 1);
        Assert.False(denied.Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.InstanceLimitReached, denied.Code);
    }

    [Fact]
    public async Task Concurrent_create_admission_cannot_exceed_the_organization_limit()
    {
        const string connectionString = "Data Source=file:commercial-gate-concurrency;Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        await using (var setup = CreateContext(keeper))
        {
            await setup.Database.EnsureCreatedAsync();
            await CreateWorkspaceAsync(setup, OrganizationSubscriptionState.Active, managedHostingEnabled: true, maxInstances: 1);
        }

        var workspaceId = await GetWorkspaceIdAsync(keeper);
        var organizationId = await GetOrganizationIdAsync(keeper, workspaceId);
        var outcomes = await Task.WhenAll(
            CreateConcurrentlyAsync(connectionString, organizationId, workspaceId, "first"),
            CreateConcurrentlyAsync(connectionString, organizationId, workspaceId, "second"));

        Assert.Equal(1, outcomes.Count(x => x.Error is null));
        var loser = Assert.Single(outcomes, x => x.Error is not null);
        var conflict = Assert.IsType<ElsaInstanceLifecycleConflictException>(loser.Error);
        Assert.Equal(ElsaInstanceLifecycleConflictReason.CommercialDenied, conflict.Reason);
        Assert.Equal(ElsaInstanceCommercialOperation.InstanceLimitReached, conflict.CommercialCode);
        await using var verify = CreateContext(keeper);
        Assert.Equal(1, await verify.ElsaInstances.CountAsync(x => x.OrganizationId == organizationId && x.DeletedAt == null));
    }

    private static async Task<(ElsaInstanceLifecycleAcceptance? Acceptance, Exception? Error)> CreateConcurrentlyAsync(
        string connectionString,
        Guid organizationId,
        Guid workspaceId,
        string suffix)
    {
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var db = CreateContext(connection);
            var service = new ElsaInstanceLifecycleService(
                new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now)),
                new FixedTimeProvider(Now));
            var acceptance = await service.CreateAsync(new ElsaInstanceCreateRequest(
                organizationId,
                workspaceId,
                $"Concurrent {suffix}",
                $"concurrent-{suffix}",
                CreateIntent(),
                $"create-concurrent-{suffix}"));
            return (acceptance, null);
        }
        catch (Exception error)
        {
            return (null, error);
        }
    }

    private static async Task<Workspace> CreateWorkspaceAsync(
        CatalogDbContext db,
        OrganizationSubscriptionState? state,
        bool managedHostingEnabled,
        int maxInstances = int.MaxValue)
    {
        var workspace = new Workspace { Name = "Commercial gate workspace " + Guid.NewGuid().ToString("N") };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        if (state is not null || managedHostingEnabled)
        {
            db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
            {
                OrganizationId = workspace.OrganizationId,
                ManagedHostingEnabled = managedHostingEnabled,
                SubscriptionState = state,
                MaxInstances = maxInstances,
                SyncedAt = Now,
                CreatedAt = Now,
                UpdatedAt = Now
            });
            await db.SaveChangesAsync();
        }
        return workspace;
    }

    private static async Task<Guid> GetWorkspaceIdAsync(SqliteConnection connection)
    {
        await using var db = CreateContext(connection);
        return await db.Workspaces.Select(x => x.Id).SingleAsync();
    }

    private static async Task<Guid> GetOrganizationIdAsync(SqliteConnection connection, Guid workspaceId)
    {
        await using var db = CreateContext(connection);
        return await db.Workspaces.Where(x => x.Id == workspaceId).Select(x => x.OrganizationId).SingleAsync();
    }

    private static CatalogDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(connection).Options);

    private static ElsaInstanceIntent CreateIntent() => new(
        new ElsaReleaseIntent("valence-runtime", "3.8", "3.8.0", "stable"),
        new ElsaApplicationIntent("combined", "starter", new Dictionary<string, ElsaFeatureOverride>()),
        new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed"));

    private sealed class EmptyResolutionInputSource : IElsaInstanceLifecycleResolutionInputSource
    {
        public static EmptyResolutionInputSource Instance { get; } = new();

        public Task<ElsaInstanceLifecycleResolutionInput?> GetAsync(
            ElsaInstance instance,
            ElsaInstanceOperation operation,
            CancellationToken cancellationToken = default) => Task.FromResult<ElsaInstanceLifecycleResolutionInput?>(null);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
