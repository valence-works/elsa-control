using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class AzureProviderAssignmentRebindTests : IDisposable
{
    private static readonly string OldScope = new('a', 64);
    private static readonly string NewScope = new('b', 64);
    private static readonly string SubscriptionId = "11111111-1111-1111-1111-111111111111";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _workspaceId = Guid.NewGuid();

    public AzureProviderAssignmentRebindTests()
    {
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
        db.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "Azure assignment rebind workspace" });
        db.SaveChanges();
    }

    [Fact]
    public async Task Template_only_rotation_rebinds_the_same_assignment_and_is_audited()
    {
        var now = DateTimeOffset.Parse("2026-09-13T00:00:00Z");
        var organizationId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var lifecycleOperationId = Guid.NewGuid();
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var assignmentStore = (IAzureProviderResourceAssignmentStore)store;
        var original = await assignmentStore.CreateOrGetAsync(
            AssignmentRequest(organizationId, instanceId, OldScope), now);
        var operation = await SubmitReconcileAsync(store, instanceId, organizationId, original.Id, OldScope, lifecycleOperationId);
        await FinalizeSucceededAsync(store, operation, now);

        var rebound = await assignmentStore.CreateOrGetAsync(
            AssignmentRequest(organizationId, instanceId, NewScope) with
            {
                Rebind = new AzureProviderAssignmentRebindContext("lifecycle-submit", lifecycleOperationId)
            },
            now.AddMinutes(1));

        Assert.Equal(original.Id, rebound.Id);
        Assert.Equal(NewScope, rebound.ProviderScopeFingerprint);
        Assert.Equal(original.ResourceGroupName, rebound.ResourceGroupName);
        Assert.Equal(original.SubscriptionId, rebound.SubscriptionId);
        Assert.Equal(
            AzureProviderResourceAssignmentNaming.OwnershipKey(original.Id, instanceId, NewScope),
            rebound.OwnershipKey);
        var audit = Assert.Single(await assignmentStore.ListRebindsAsync(_workspaceId, original.Id));
        Assert.Equal(OldScope, audit.FromProviderScopeFingerprint);
        Assert.Equal(NewScope, audit.ToProviderScopeFingerprint);
        Assert.Equal("lifecycle-submit", audit.TriggeredBy);
        Assert.Equal(lifecycleOperationId, audit.TriggerOperationId);
        Assert.True(await assignmentStore.HasRebindLineageAsync(_workspaceId, original.Id, OldScope, NewScope));
        Assert.Equal(OldScope, (await store.GetAsync(_workspaceId, operation.Id))!.ProviderScopeFingerprint);
        Assert.NotNull(await store.GetLatestReconcileAsync(_workspaceId, WorkloadName(instanceId), NewScope));
    }

    [Fact]
    public async Task In_flight_operation_refuses_rebind_with_a_drain_diagnostic()
    {
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var assignmentStore = (IAzureProviderResourceAssignmentStore)store;
        var assignment = await assignmentStore.CreateOrGetAsync(
            AssignmentRequest(organizationId, instanceId, OldScope), now);
        var operation = await SubmitReconcileAsync(store, instanceId, organizationId, assignment.Id, OldScope, Guid.NewGuid());
        Assert.NotNull(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now));

        var exception = await Assert.ThrowsAsync<AzureProviderAssignmentRebindException>(() =>
            assignmentStore.CreateOrGetAsync(
                AssignmentRequest(organizationId, instanceId, NewScope), now.AddMinutes(1)));

        Assert.Equal(AzureProviderAssignmentRebindDiagnostics.OperationsInFlight, exception.DiagnosticCode);
        var current = Assert.IsType<AzureProviderResourceAssignment>(
            await assignmentStore.GetAsync(_workspaceId, assignment.Id));
        Assert.Equal(OldScope, current.ProviderScopeFingerprint);
        Assert.Empty(await assignmentStore.ListRebindsAsync(_workspaceId, assignment.Id));
    }

    [Fact]
    public async Task Placement_change_never_rebinds()
    {
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var assignmentStore = (IAzureProviderResourceAssignmentStore)store;
        await assignmentStore.CreateOrGetAsync(AssignmentRequest(organizationId, instanceId, OldScope), now);

        var exception = await Assert.ThrowsAsync<AzureProviderAssignmentRebindException>(() =>
            assignmentStore.CreateOrGetAsync(
                AssignmentRequest(organizationId, instanceId, NewScope) with
                {
                    SubscriptionId = "22222222-2222-2222-2222-222222222222"
                },
                now.AddMinutes(1)));

        Assert.Equal(AzureProviderAssignmentRebindDiagnostics.PlacementMismatch, exception.DiagnosticCode);
    }

    [Fact]
    public async Task Update_observe_and_delete_succeed_after_template_only_rotation()
    {
        var now = DateTimeOffset.Parse("2026-09-13T01:00:00Z");
        var organizationId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var lifecycleOperationId = Guid.NewGuid();
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var assignmentStore = (IAzureProviderResourceAssignmentStore)store;
        var assignment = await assignmentStore.CreateOrGetAsync(
            AssignmentRequest(organizationId, instanceId, OldScope), now);
        var operation = await SubmitReconcileAsync(
            store, instanceId, organizationId, assignment.Id, OldScope, lifecycleOperationId);
        await FinalizeSucceededAsync(store, operation, now, healthy: true);

        var updated = await assignmentStore.CreateOrGetAsync(
            AssignmentRequest(organizationId, instanceId, NewScope) with
            {
                Rebind = new AzureProviderAssignmentRebindContext("lifecycle-submit", lifecycleOperationId)
            },
            now.AddMinutes(1));
        Assert.Equal(assignment.Id, updated.Id);
        Assert.Equal(NewScope, updated.ProviderScopeFingerprint);

        var provider = new AzureElsaInstanceProvider(
            new AzureProviderOperationService(store), store, store, options: EnabledOptions(NewScope));
        var observed = await provider.ObserveAsync(new(
            _workspaceId,
            instanceId,
            lifecycleOperationId,
            1,
            ElsaDesiredLifecycle.Running,
            null,
            null));
        Assert.Equal(ElsaInstanceProviderObservationKind.Confirmed, observed.Kind);
        Assert.Equal(ElsaObservedLifecycle.Ready, observed.ObservedLifecycle);
        Assert.NotEqual("provider-operation-unavailable", observed.CorrelationId);

        var cleanup = await provider.CleanupAsync(new(
            _workspaceId,
            instanceId,
            Guid.NewGuid(),
            1,
            null,
            new ElsaPlacementAssignmentReference(assignment.Id.ToString("D")),
            null));
        Assert.NotEqual("deletion.provider-assignment-invalid", cleanup.DiagnosticCode);
        Assert.NotEqual("deletion.provider-plan-unavailable", cleanup.DiagnosticCode);
        Assert.NotEqual(ElsaInstanceCleanupObservationKind.Ambiguous, cleanup.Kind);
    }

    [Fact]
    public async Task Recovery_required_operation_can_be_rebound_and_observed()
    {
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var lifecycleOperationId = Guid.NewGuid();
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var assignmentStore = (IAzureProviderResourceAssignmentStore)store;
        var assignment = await assignmentStore.CreateOrGetAsync(
            AssignmentRequest(organizationId, instanceId, OldScope), now);
        var operation = await SubmitReconcileAsync(
            store, instanceId, organizationId, assignment.Id, OldScope, lifecycleOperationId);
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now));
        Assert.NotNull(await store.FinalizeAsync(
            _workspaceId,
            operation.Id,
            "lease",
            AzureProviderOperationStatus.RecoveryRequired,
            "azure.step.uncertain",
            now.AddSeconds(1),
            claimed.Version));

        var provider = new AzureElsaInstanceProvider(
            new AzureProviderOperationService(store), store, store, options: EnabledOptions(NewScope));
        var observed = await provider.ObserveAsync(new(
            _workspaceId,
            instanceId,
            lifecycleOperationId,
            1,
            ElsaDesiredLifecycle.Running,
            null,
            null));

        Assert.Equal(ElsaInstanceProviderObservationKind.Confirmed, observed.Kind);
        Assert.Equal(ElsaObservedLifecycle.Provisioning, observed.ObservedLifecycle);
        Assert.Equal(NewScope, (await assignmentStore.GetAsync(_workspaceId, assignment.Id))!.ProviderScopeFingerprint);
        Assert.True(await assignmentStore.HasRebindLineageAsync(_workspaceId, assignment.Id, OldScope, NewScope));
    }

    public void Dispose() => _connection.Dispose();

    private CatalogDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(_connection).Options);

    private AzureProviderResourceAssignmentRequest AssignmentRequest(
        Guid organizationId,
        Guid instanceId,
        string fingerprint) =>
        new(
            _workspaceId,
            organizationId,
            instanceId,
            fingerprint,
            SubscriptionId,
            "rg-elsa",
            WorkloadName(instanceId),
            "westeurope");

    private async Task<AzureProviderOperation> SubmitReconcileAsync(
        AzureProviderOperationStore store,
        Guid instanceId,
        Guid organizationId,
        Guid assignmentId,
        string fingerprint,
        Guid lifecycleOperationId)
    {
        var service = new AzureProviderOperationService(store);
        return await service.SubmitAsync(
            _workspaceId,
            new AzureProviderOperationSubmission(
                $"elsa-instance-operation:{lifecycleOperationId:D}",
                new string('d', 64),
                CreatePlan(instanceId),
                fingerprint,
                organizationId,
                instanceId,
                ElsaInstanceOperationAction.Reconcile,
                assignmentId));
    }

    private static AzureWorkloadPlan CreatePlan(Guid instanceId) => new(
        WorkloadName(instanceId),
        "westeurope",
        "3.8.0",
        "3.8",
        "combined",
        "Dedicated",
        "valenceruntimeimages.azurecr.io/runtime-combined",
        new string('c', 64),
        "oci://evidence.example/manifest",
        "sha256:" + new string('d', 64),
        "oci://evidence.example/signature",
        "sha256:" + new string('e', 64),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["database:connectionstring"] = "secret://vault/database"
        },
        new string('c', 64),
        "3.8.0-preview.5413",
        "3.8.0-preview.5413",
        new AzureWorkloadCapacity(1, 1, 500, 1024));

    private async Task FinalizeSucceededAsync(
        AzureProviderOperationStore store,
        AzureProviderOperation operation,
        DateTimeOffset now,
        bool healthy = false)
    {
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now));
        var assignment = Assert.IsType<AzureProviderResourceAssignment>(
            await ((IAzureProviderResourceAssignmentStore)store).GetAsync(_workspaceId, operation.ProviderAssignmentId!.Value));
        var resources = new AzureProviderResourceReferences(
            assignment.ResourceGroupName,
            FoundationDeploymentId: $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.Resources/deployments/foundation",
            WorkloadIdentityResourceId: $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/identity");
        var checkpointed = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
            _workspaceId,
            operation.Id,
            "lease",
            new(
                AzureProviderOperationPhase.TrafficPromoted,
                "workload.ready",
                "Ready.",
                resources,
                healthy ? "https://runtime.example.test/" : null,
                healthy ? AzureProviderHealth.Healthy : AzureProviderHealth.Unknown,
                []),
            now.AddSeconds(1),
            claimed.Version));
        Assert.NotNull(await store.FinalizeAsync(
            _workspaceId,
            operation.Id,
            "lease",
            AzureProviderOperationStatus.Succeeded,
            "operation.succeeded",
            now.AddSeconds(2),
            checkpointed.Version));
    }

    private static AzureElsaInstanceProviderOptions EnabledOptions(string fingerprint) =>
        new()
        {
            Enabled = true,
            TemplateFingerprint = new string('d', 64),
            ProviderScopeFingerprint = fingerprint,
            SubscriptionId = SubscriptionId,
            ResourceGroupNamePrefix = "rg-elsa"
        };

    private static string WorkloadName(Guid instanceId) => $"e{instanceId:N}"[..16];
}
