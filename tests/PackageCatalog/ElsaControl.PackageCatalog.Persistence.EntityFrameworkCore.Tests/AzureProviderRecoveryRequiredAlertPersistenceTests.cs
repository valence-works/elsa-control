using System.Data.Common;
using System.Diagnostics;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Telemetry;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class AzureProviderRecoveryRequiredAlertPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _organizationId = Guid.NewGuid();
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _instanceId = Guid.NewGuid();
    private readonly Guid _lifecycleOperationId = Guid.NewGuid();

    public AzureProviderRecoveryRequiredAlertPersistenceTests()
    {
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
        db.Organizations.Add(new Organization { Id = _organizationId, Name = "Recovery alert organization" });
        db.Workspaces.Add(new Workspace
        {
            Id = _workspaceId,
            OrganizationId = _organizationId,
            Name = "Recovery alert workspace"
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Finalize_to_recovery_required_writes_exactly_one_event_and_replays_write_none()
    {
        using var capture = new AlertCapture(_workspaceId);
        var now = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await CreateClaimedAsync(store, now);

        var parked = Assert.IsType<AzureProviderOperation>(await store.FinalizeAsync(
            _workspaceId,
            operation.Id,
            "lease-1",
            AzureProviderOperationStatus.RecoveryRequired,
            AzureLateSuccessCodes.DeploymentFailed,
            now.AddSeconds(30),
            operation.Version));
        var replay = await store.FinalizeAsync(
            _workspaceId,
            operation.Id,
            "lease-1",
            AzureProviderOperationStatus.RecoveryRequired,
            AzureLateSuccessCodes.DeploymentFailed,
            now.AddSeconds(45),
            parked.Version);
        await store.RecordAutoResumeOutcomeAsync(
            _workspaceId, operation.Id, AzureLateSuccessCodes.AutoResumeExhausted);
        await store.RecordArmObservationClockAsync(
            _workspaceId, operation.Id, now.AddMinutes(3), 30, AzureLateSuccessCodes.DeploymentFailed);

        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, parked.Status);
        Assert.NotNull(replay);
        Assert.Single(capture.Entered);
        AssertAlert(capture.Entered[0], _lifecycleOperationId);
    }

    [Fact]
    public async Task Recover_then_a_second_entry_writes_exactly_one_more_event()
    {
        using var capture = new AlertCapture(_workspaceId);
        var now = DateTimeOffset.Parse("2026-09-29T13:00:00Z");
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await CreateClaimedAsync(store, now);
        var first = Assert.IsType<AzureProviderOperation>(await store.FinalizeAsync(
            _workspaceId,
            operation.Id,
            "lease-1",
            AzureProviderOperationStatus.RecoveryRequired,
            AzureLateSuccessCodes.DeploymentFailed,
            now.AddSeconds(30),
            operation.Version));
        Assert.Single(capture.Entered);

        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimRecoveryAsync(
            _workspaceId, first.Id, "worker-2", "lease-2", TimeSpan.FromMinutes(1), now.AddMinutes(2), first.Version));
        Assert.Single(capture.Entered);

        var second = Assert.IsType<AzureProviderOperation>(await store.FinalizeAsync(
            _workspaceId,
            claimed.Id,
            "lease-2",
            AzureProviderOperationStatus.RecoveryRequired,
            AzureLateSuccessCodes.DeploymentCanceled,
            now.AddMinutes(2).AddSeconds(30),
            claimed.Version));

        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, second.Status);
        Assert.Equal(2, capture.Entered.Count);
        Assert.All(capture.Entered, activity => AssertAlert(activity, _lifecycleOperationId));
    }

    [Fact]
    public async Task Stale_lease_cas_writes_one_event_and_a_second_tick_writes_none()
    {
        using var capture = new AlertCapture(_workspaceId);
        var now = DateTimeOffset.Parse("2026-09-29T14:00:00Z");
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        await CreateClaimedAsync(store, now);

        Assert.Equal(1, await store.RecoverStaleAsync(now.AddMinutes(2)));
        Assert.Equal(0, await store.RecoverStaleAsync(now.AddMinutes(3)));

        Assert.Single(capture.Entered);
        AssertAlert(capture.Entered[0], _lifecycleOperationId);
    }

    [Fact]
    public async Task Heartbeat_and_checkpoint_refreshes_do_not_write_the_alert_event()
    {
        using var capture = new AlertCapture(_workspaceId);
        var now = DateTimeOffset.Parse("2026-09-29T15:00:00Z");
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await CreateClaimedAsync(store, now);

        Assert.NotNull(await store.HeartbeatAsync(
            _workspaceId, operation.Id, "lease-1", TimeSpan.FromMinutes(1), now.AddSeconds(30), operation.Version));
        var current = Assert.IsType<AzureProviderOperation>(await store.GetAsync(_workspaceId, operation.Id));
        Assert.NotNull(await store.CheckpointAsync(
            _workspaceId,
            operation.Id,
            "lease-1",
            new(
                AzureProviderOperationPhase.FoundationReady,
                "foundation.ready",
                "Ready.",
                new(),
                null,
                AzureProviderHealth.Unknown,
                []),
            now.AddMinutes(1),
            current.Version));

        Assert.Empty(capture.Entered);
    }

    [Fact]
    public async Task Failed_commit_of_stale_recovery_emits_nothing()
    {
        var now = DateTimeOffset.Parse("2026-09-29T16:00:00Z");
        using (var seed = CreateContext())
        {
            await CreateClaimedAsync(new AzureProviderOperationStore(seed), now);
        }

        using var capture = new AlertCapture(_workspaceId);
        using var db = CreateContext(new FailCommitInterceptor());
        var store = new AzureProviderOperationStore(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecoverStaleAsync(now.AddMinutes(2)));

        Assert.Empty(capture.Entered);
        using var verify = CreateContext();
        var persisted = await verify.AzureProviderOperations.AsNoTracking().SingleAsync();
        Assert.Equal(AzureProviderOperationStatus.Running, persisted.Status);
    }

    [Fact]
    public async Task Retried_commit_of_stale_recovery_emits_exactly_one_event()
    {
        var now = DateTimeOffset.Parse("2026-09-29T17:00:00Z");
        using (var seed = CreateContext())
        {
            await CreateClaimedAsync(new AzureProviderOperationStore(seed), now);
        }

        using var capture = new AlertCapture(_workspaceId);
        var interceptor = new FailFirstCommitInterceptor();
        using var db = CreateContext(
            exception => exception is TransientCommitException,
            interceptor);
        var store = new AzureProviderOperationStore(db);

        Assert.Equal(1, await store.RecoverStaleAsync(now.AddMinutes(2)));

        Assert.Equal(2, interceptor.Attempts);
        Assert.Single(capture.Entered);
        AssertAlert(capture.Entered[0], _lifecycleOperationId);
        using var verify = CreateContext();
        Assert.Equal(
            AzureProviderOperationStatus.RecoveryRequired,
            (await verify.AzureProviderOperations.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Two_dbcontexts_racing_stale_recovery_emit_exactly_one_event()
    {
        var path = Path.Combine(Path.GetTempPath(), $"elsa-rr-alert-{Guid.NewGuid():N}.db");
        var now = DateTimeOffset.Parse("2026-09-29T18:00:00Z");
        try
        {
            var options = new DbContextOptionsBuilder<CatalogDbContext>()
                .UseRetryingSqlite($"Data Source={path}").Options;
            await using (var seed = new CatalogDbContext(options))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.Organizations.Add(new Organization { Id = _organizationId, Name = "Race organization" });
                seed.Workspaces.Add(new Workspace
                {
                    Id = _workspaceId,
                    OrganizationId = _organizationId,
                    Name = "Race workspace"
                });
                await seed.SaveChangesAsync();
                await CreateClaimedAsync(new AzureProviderOperationStore(seed), now);
            }

            using var capture = new AlertCapture(_workspaceId);
            await using var first = new CatalogDbContext(options);
            await using var second = new CatalogDbContext(options);
            var recovered = await Task.WhenAll(
                new AzureProviderOperationStore(first).RecoverStaleAsync(now.AddMinutes(2)),
                new AzureProviderOperationStore(second).RecoverStaleAsync(now.AddMinutes(2)));

            Assert.Equal(1, recovered.Sum());
            Assert.Single(capture.Entered);
            AssertAlert(capture.Entered[0], _lifecycleOperationId);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Authorize_missing_lifecycle_action_writes_exactly_one_event()
    {
        using var capture = new AlertCapture(_workspaceId);
        var now = DateTimeOffset.Parse("2026-09-29T19:00:00Z");
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await CreateClaimedAsync(store, now);
        var entity = await db.AzureProviderOperations.SingleAsync(x => x.Id == operation.Id);
        entity.LifecycleAction = null;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var authorization = await store.AuthorizeAsync(
            _workspaceId,
            operation.Id,
            "lease-1",
            new NeverCalledCommercialGate(),
            now,
            operation.Version);

        Assert.NotNull(authorization);
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, authorization!.Operation.Status);
        Assert.Equal("provider.identity-binding-missing", authorization.Decision.Code);
        Assert.Single(capture.Entered);
        AssertAlert(capture.Entered[0], _lifecycleOperationId);
    }

    public void Dispose() => _connection.Dispose();

    private async Task<AzureProviderOperation> CreateClaimedAsync(
        AzureProviderOperationStore store,
        DateTimeOffset now)
    {
        var assignment = await ((IAzureProviderResourceAssignmentStore)store).CreateOrGetAsync(
            new(
                _workspaceId,
                _organizationId,
                _instanceId,
                new string('a', 64),
                "11111111-1111-1111-1111-111111111111",
                "rg-elsa",
                $"e{_instanceId:N}"[..16],
                "westeurope"),
            now);
        var created = await store.CreateOrGetAsync(new AzureProviderOperationRequest(
            _workspaceId,
            $"workload-{_lifecycleOperationId:N}"[..20],
            AzureProviderOperationAction.Reconcile,
            AzureProviderOperationValidation.LifecycleIdempotencyKey(_lifecycleOperationId),
            new string('a', 64),
            new string('b', 64),
            "3.8.0",
            "3.8",
            "combined",
            "Dedicated",
            "westeurope",
            "valenceruntimeimages.azurecr.io/runtime-combined",
            "sha256:" + new string('c', 64),
            OrganizationId: _organizationId,
            InstanceId: _instanceId,
            LifecycleAction: ElsaInstanceOperationAction.Create,
            ProviderAssignmentId: assignment.Id), now);
        return Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, created.Id, "worker-1", "lease-1", TimeSpan.FromMinutes(1), now));
    }

    private CatalogDbContext CreateContext(params IInterceptor[] interceptors) =>
        CreateContext(isTransient: null, interceptors);

    private CatalogDbContext CreateContext(
        Func<Exception, bool>? isTransient,
        params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(_connection, isTransient: isTransient);
        if (interceptors.Length > 0)
            options.AddInterceptors(interceptors);
        return new CatalogDbContext(options.Options);
    }

    private void AssertAlert(Activity activity, Guid operationId)
    {
        Assert.Equal(ManagedLifecycleTelemetry.RecoveryRequiredEnteredActivityName, activity.OperationName);
        Assert.Equal(
            ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired,
            activity.GetTagItem(ManagedLifecycleTelemetry.DiagnosticCodeTag));
        Assert.Equal(_workspaceId.ToString("D"), activity.GetTagItem(ManagedLifecycleTelemetry.WorkspaceIdTag));
        Assert.Equal(_instanceId.ToString("D"), activity.GetTagItem(ManagedLifecycleTelemetry.InstanceIdTag));
        Assert.Equal(operationId.ToString("D"), activity.GetTagItem(ManagedLifecycleTelemetry.OperationIdTag));
    }

    private sealed class AlertCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly object _gate = new();
        private readonly List<Activity> _entered = [];

        public AlertCapture(Guid workspaceId)
        {
            var workspace = workspaceId.ToString("D");
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ManagedLifecycleTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName != ManagedLifecycleTelemetry.RecoveryRequiredEnteredActivityName)
                        return;
                    if (!string.Equals(
                            activity.GetTagItem(ManagedLifecycleTelemetry.WorkspaceIdTag) as string,
                            workspace,
                            StringComparison.Ordinal))
                        return;
                    lock (_gate)
                        _entered.Add(activity);
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Entered
        {
            get
            {
                lock (_gate)
                    return _entered.ToArray();
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class NeverCalledCommercialGate : IElsaInstanceCommercialGate
    {
        public Task<ElsaInstanceCommercialGateDecision> EvaluateAsync(
            Guid organizationId,
            ElsaInstanceOperationAction action,
            int? activeInstanceCount = null,
            CancellationToken cancellationToken = default) =>
            throw new Xunit.Sdk.XunitException("The commercial gate must not run when provider identity is missing.");
    }

    private sealed class TransientCommitException : Exception;

    private sealed class FailCommitInterceptor : DbTransactionInterceptor
    {
        public override InterceptionResult TransactionCommitting(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result) =>
            throw new InvalidOperationException("The catalog transaction commit failed.");

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The catalog transaction commit failed.");
    }

    private sealed class FailFirstCommitInterceptor : DbTransactionInterceptor
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        public override InterceptionResult TransactionCommitting(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result) =>
            FailFirst(result);

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            new(FailFirst(result));

        private InterceptionResult FailFirst(InterceptionResult result)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
                throw new TransientCommitException();
            return result;
        }
    }
}
