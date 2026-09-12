using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

/// <summary>
/// The gate is exercised against rows written by the operator grant path, so the
/// expiry and cap decisions are proven on the persisted projection rather than on
/// hand-built snapshots. The clock moves; no lifecycle worker runs.
/// </summary>
public sealed class InternalEntitlementCommercialGateTests : IAsyncLifetime
{
    private static readonly DateTimeOffset GrantedAt = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiresAt = GrantedAt.AddDays(7);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly MutableTimeProvider _clock = new(GrantedAt);
    private CatalogDbContext _db = null!;
    private Workspace _workspace = null!;
    private EfCoreElsaInstanceCommercialGate _gate = null!;

    public static TheoryData<ElsaInstanceOperationAction> GatedActions => new(
        Enum.GetValues<ElsaInstanceOperationAction>()
            .Where(action => action is not ElsaInstanceOperationAction.Stop and not ElsaInstanceOperationAction.Delete));

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _workspace = new Workspace { Name = "Dogfood workspace" };
        _db.Workspaces.Add(_workspace);
        await _db.SaveChangesAsync();
        var granted = await new OrganizationBillingStore(_db).GrantInternalEntitlementAsync(
            new(_workspace.OrganizationId, new("Internal dogfood of managed hosting", 2, ExpiresAt), "operator"),
            GrantedAt);
        Assert.Equal(OrganizationInternalEntitlementOutcome.Granted, granted.Outcome);
        _db.ChangeTracker.Clear();
        _gate = new EfCoreElsaInstanceCommercialGate(_db, _clock);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Grant_allows_create_and_update_before_expiry_within_the_cap()
    {
        _clock.UtcNow = ExpiresAt.AddTicks(-1);

        Assert.True((await EvaluateAsync(ElsaInstanceOperationAction.Create, activeInstanceCount: 0)).Allowed);
        Assert.True((await EvaluateAsync(ElsaInstanceOperationAction.Create, activeInstanceCount: 1)).Allowed);
        Assert.True((await EvaluateAsync(ElsaInstanceOperationAction.UpdateIntent)).Allowed);
    }

    [Fact]
    public async Task Grant_denies_create_at_the_cap()
    {
        var decision = await EvaluateAsync(ElsaInstanceOperationAction.Create, activeInstanceCount: 2);

        Assert.False(decision.Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.InstanceLimitReached, decision.Code);
    }

    [Theory]
    [MemberData(nameof(GatedActions))]
    public async Task Every_gated_action_is_denied_from_the_expiry_instant(ElsaInstanceOperationAction action)
    {
        _clock.UtcNow = ExpiresAt.AddTicks(-1);
        Assert.True((await EvaluateAsync(action, activeInstanceCount: 0)).Allowed);

        _clock.UtcNow = ExpiresAt;
        var decision = await EvaluateAsync(action, activeInstanceCount: 0);

        Assert.False(decision.Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.EntitlementExpired, decision.Code);
    }

    [Fact]
    public async Task Stop_and_delete_remain_allowed_after_expiry_and_after_revoke()
    {
        _clock.UtcNow = ExpiresAt.AddDays(1);
        await AssertSafeExitsAllowedAsync();

        await new OrganizationBillingStore(_db).RevokeInternalEntitlementAsync(_workspace.OrganizationId, "operator", GrantedAt.AddHours(1));
        _db.ChangeTracker.Clear();
        _clock.UtcNow = GrantedAt.AddHours(2);

        await AssertSafeExitsAllowedAsync();
    }

    [Fact]
    public async Task Revoked_grant_denies_create_and_update_before_its_original_expiry()
    {
        await new OrganizationBillingStore(_db).RevokeInternalEntitlementAsync(_workspace.OrganizationId, "operator", GrantedAt.AddHours(1));
        _db.ChangeTracker.Clear();
        _clock.UtcNow = GrantedAt.AddHours(2);

        foreach (var action in new[] { ElsaInstanceOperationAction.Create, ElsaInstanceOperationAction.UpdateIntent })
        {
            var decision = await EvaluateAsync(action, activeInstanceCount: 0);
            Assert.False(decision.Allowed);
            Assert.Equal(ElsaInstanceCommercialOperation.EntitlementRequired, decision.Code);
        }
    }

    [Fact]
    public async Task Lifecycle_admission_evaluates_expiry_with_the_store_clock_inside_its_transaction()
    {
        var accepted = await CreateInstanceAsync(ExpiresAt.AddTicks(-1), "before-expiry");
        Assert.NotNull(accepted);

        var denied = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            CreateInstanceAsync(ExpiresAt, "after-expiry"));

        Assert.Equal(ElsaInstanceLifecycleConflictReason.CommercialDenied, denied.Reason);
        Assert.Equal(ElsaInstanceCommercialOperation.EntitlementExpired, denied.CommercialCode);
        _db.ChangeTracker.Clear();
        Assert.Equal(1, await _db.ElsaInstances.CountAsync(x => x.OrganizationId == _workspace.OrganizationId));
    }

    private Task<ElsaInstanceCommercialGateDecision> EvaluateAsync(ElsaInstanceOperationAction action, int? activeInstanceCount = null) =>
        _gate.EvaluateAsync(_workspace.OrganizationId, action, activeInstanceCount);

    private async Task AssertSafeExitsAllowedAsync()
    {
        Assert.True((await EvaluateAsync(ElsaInstanceOperationAction.Stop)).Allowed);
        Assert.True((await EvaluateAsync(ElsaInstanceOperationAction.Delete)).Allowed);
    }

    // No gate is injected: the store's fallback gate must share the store clock.
    private async Task<ElsaInstanceLifecycleAcceptance> CreateInstanceAsync(DateTimeOffset at, string slug)
    {
        _db.ChangeTracker.Clear();
        var clock = new MutableTimeProvider(at);
        var service = new ElsaInstanceLifecycleService(
            new EfCoreElsaInstanceLifecycleStore(_db, EmptyResolutionInputSource.Instance, clock),
            clock);
        return await service.CreateAsync(new ElsaInstanceCreateRequest(
            _workspace.OrganizationId,
            _workspace.Id,
            $"Dogfood {slug}",
            slug,
            new ElsaInstanceIntent(
                new ElsaReleaseIntent("valence-runtime", "3.8", "3.8.0", "stable"),
                new ElsaApplicationIntent("combined", "starter", new Dictionary<string, ElsaFeatureOverride>()),
                new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed")),
            $"create-{slug}"));
    }

    private sealed class EmptyResolutionInputSource : IElsaInstanceLifecycleResolutionInputSource
    {
        public static EmptyResolutionInputSource Instance { get; } = new();

        public Task<ElsaInstanceLifecycleResolutionInput?> GetAsync(
            ElsaInstance instance,
            ElsaInstanceOperation operation,
            CancellationToken cancellationToken = default) => Task.FromResult<ElsaInstanceLifecycleResolutionInput?>(null);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
