using System.Diagnostics;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Telemetry;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
        using var capture = new AlertCapture();
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
        using var capture = new AlertCapture();
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
        using var capture = new AlertCapture();
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
        using var capture = new AlertCapture();
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

    private CatalogDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(_connection).Options);

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

        public AlertCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ManagedLifecycleTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName != ManagedLifecycleTelemetry.RecoveryRequiredEnteredActivityName)
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
}
