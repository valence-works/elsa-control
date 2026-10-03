using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ManagedElsaInstanceApiStoreReadTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Customer_projection_does_not_persist_recovery_required()
    {
        await using var connection = OpenConnection();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, "Recovery required projection workspace");
        var instance = NewInstance(workspace);
        instance.ObservedLifecycle = ElsaObservedLifecycle.Provisioning;
        instance.Health = ElsaInstanceHealth.Unknown;
        db.ElsaInstances.Add(instance);
        var parked = NewOperation(workspace, instance, BaseTime, ElsaInstanceOperationAction.Create);
        parked.State = ElsaInstanceOperationState.RecoveryRequired;
        parked.StartedAt = BaseTime;
        parked.ReconciliationDiagnosticCode = "azure.recovery.auto-resume-exhausted";
        parked.ReasonEnteredAt = BaseTime;
        parked.RequiresHumanAt = BaseTime;
        instance.LastOperationId = parked.Id.ToString("D");
        db.ElsaInstanceOperations.Add(parked);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var listed = Assert.Single(
            (await new EfCoreManagedElsaInstanceApiStore(db).ListInstancesAsync(workspace.Id, 1, 10)).Items);
        var stored = await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == instance.Id);

        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, listed.ObservedLifecycle);
        Assert.Equal(ElsaObservedLifecycle.Provisioning, stored.ObservedLifecycle);
        Assert.NotEqual(ElsaObservedLifecycle.RecoveryRequired, stored.ObservedLifecycle);
        Assert.Equal(instance.Id, listed.Id);
        Assert.DoesNotContain("Failed", listed.ObservedLifecycle.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Customer_list_keeps_provisioning_for_a_healthy_hand_off_park()
    {
        await using var connection = OpenConnection();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, "Healthy hand-off projection workspace");
        var instance = NewInstance(workspace);
        instance.ObservedLifecycle = ElsaObservedLifecycle.Provisioning;
        instance.Health = ElsaInstanceHealth.Unknown;
        db.ElsaInstances.Add(instance);
        var parked = NewOperation(workspace, instance, BaseTime, ElsaInstanceOperationAction.Create);
        parked.State = ElsaInstanceOperationState.RecoveryRequired;
        parked.StartedAt = BaseTime;
        parked.UpdatedAt = BaseTime;
        parked.ReconciliationDiagnosticCode = ManagedElsaReasonCodeCatalog.ProviderSubmissionAccepted;
        instance.LastOperationId = parked.Id.ToString("D");
        db.ElsaInstanceOperations.Add(parked);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var listed = Assert.Single(
            (await new EfCoreManagedElsaInstanceApiStore(db).ListInstancesAsync(workspace.Id, 1, 10)).Items);
        var stored = await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == instance.Id);

        Assert.Equal(ElsaObservedLifecycle.Provisioning, listed.ObservedLifecycle);
        Assert.Equal(ElsaObservedLifecycle.Provisioning, stored.ObservedLifecycle);
        Assert.NotEqual(ElsaObservedLifecycle.RecoveryRequired, listed.ObservedLifecycle);
    }

    [Fact]
    public async Task Ready_healthy_list_agrees_with_projected_park_state()
    {
        await using var connection = OpenConnection();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, "Ready healthy park workspace");
        var instance = NewInstance(workspace);
        instance.ObservedLifecycle = ElsaObservedLifecycle.Ready;
        instance.Health = ElsaInstanceHealth.Healthy;
        db.ElsaInstances.Add(instance);
        var parked = NewOperation(workspace, instance, BaseTime, ElsaInstanceOperationAction.UpdateIntent);
        parked.State = ElsaInstanceOperationState.RecoveryRequired;
        parked.StartedAt = BaseTime;
        parked.ReconciliationDiagnosticCode = ElsaInstanceProviderReconciliationService.AutoResumeExhaustedCode;
        parked.ReasonEnteredAt = BaseTime;
        parked.RequiresHumanAt = BaseTime;
        instance.LastOperationId = parked.Id.ToString("D");
        db.ElsaInstanceOperations.Add(parked);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var listed = Assert.Single(
            (await new EfCoreManagedElsaInstanceApiStore(db).ListInstancesAsync(workspace.Id, 1, 10)).Items);
        var stored = await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == instance.Id);
        var active = (await new EfCoreManagedElsaInstanceApiStore(db)
            .GetActiveOperationsAsync(workspace.Id, [instance.Id])).GetValueOrDefault(instance.Id);
        var detail = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(
            ElsaInstance.Hydrate(
                stored.Id,
                stored.OrganizationId,
                stored.WorkspaceId,
                stored.Name,
                stored.Slug,
                listed.Intent,
                stored.ObservedLifecycle,
                stored.Health,
                stored.Version,
                listed.IdentityBinding,
                listed.DesiredStateRevisionId,
                listed.ResolvedPlanReference,
                listed.CurrentResolvedRelease,
                listed.CurrentDeploymentReference,
                listed.PlacementAssignmentReference,
                listed.ElsaTenantReference,
                listed.LastOperationId,
                listed.DeletedAt,
                listed.CreatedAt,
                listed.UpdatedAt),
            active);

        Assert.Equal(ElsaObservedLifecycle.Ready, stored.ObservedLifecycle);
        Assert.Equal(ElsaObservedLifecycle.RecoveryRequired, listed.ObservedLifecycle);
        Assert.Equal(listed.ObservedLifecycle, detail);
        Assert.False(ManagedElsaInstanceCustomerProjection.IsCustomerHealthy(
            stored.DesiredLifecycle, listed.ObservedLifecycle, stored.Health));
        Assert.False(ManagedElsaInstanceCustomerProjection.IsCustomerHealthy(
            stored.DesiredLifecycle, detail, stored.Health));
    }

    [Fact]
    public async Task Lists_operations_newest_first_and_pages_within_the_workspace()
    {
        await using var connection = OpenConnection();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, "Operations list workspace");
        var other = await CreateWorkspaceAsync(db, "Operations list other workspace");
        var instance = NewInstance(workspace);
        var otherInstance = NewInstance(other, slug: "other-elsa");
        db.ElsaInstances.AddRange(instance, otherInstance);
        await db.SaveChangesAsync();

        var oldest = NewOperation(workspace, instance, BaseTime, ElsaInstanceOperationAction.Create);
        var middle = NewOperation(workspace, instance, BaseTime.AddMinutes(1), ElsaInstanceOperationAction.Reconcile);
        var newest = NewOperation(workspace, instance, BaseTime.AddMinutes(2), ElsaInstanceOperationAction.Stop);
        var foreign = NewOperation(other, otherInstance, BaseTime.AddMinutes(3), ElsaInstanceOperationAction.Start);
        db.ElsaInstanceOperations.AddRange(oldest, middle, newest, foreign);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var store = new EfCoreManagedElsaInstanceApiStore(db);
        var firstPage = await store.ListOperationsAsync(
            workspace.Id, instance.Id, 1, 1, organizationId: workspace.OrganizationId);
        var secondPage = await store.ListOperationsAsync(
            workspace.Id, instance.Id, 2, 1, organizationId: workspace.OrganizationId);
        var all = await store.ListOperationsAsync(
            workspace.Id, instance.Id, 1, 100, organizationId: workspace.OrganizationId);
        var otherWorkspace = await store.ListOperationsAsync(
            other.Id, instance.Id, 1, 100, organizationId: other.OrganizationId);
        var otherOrganization = await store.ListOperationsAsync(
            workspace.Id, instance.Id, 1, 100, organizationId: Guid.NewGuid());

        Assert.NotEqual(Guid.Empty, workspace.OrganizationId);
        Assert.NotEqual(workspace.OrganizationId, other.OrganizationId);
        Assert.Equal(3, firstPage.TotalCount);
        Assert.Equal(newest.Id, Assert.Single(firstPage.Items).Id);
        Assert.Equal(middle.Id, Assert.Single(secondPage.Items).Id);
        Assert.Equal(new[] { newest.Id, middle.Id, oldest.Id }, all.Items.Select(x => x.Id).ToArray());
        Assert.Empty(otherWorkspace.Items);
        Assert.Equal(0, otherWorkspace.TotalCount);
        Assert.Empty(otherOrganization.Items);
        Assert.Equal(0, otherOrganization.TotalCount);
    }

    [Fact]
    public async Task Topology_and_finished_create_lookup_use_only_the_matching_recovery_acceptance()
    {
        await using var connection = OpenConnection();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, "Attempt authority workspace");
        var instance = NewInstance(workspace);
        instance.ObservedLifecycle = ElsaObservedLifecycle.Provisioning;
        instance.Health = ElsaInstanceHealth.Unknown;
        var operation = NewOperation(workspace, instance, BaseTime, ElsaInstanceOperationAction.Create);
        operation.State = ElsaInstanceOperationState.Running;
        operation.AttemptNumber = 2;
        instance.LastOperationId = operation.Id.ToString("D");
        db.ElsaInstances.Add(instance);
        db.ElsaInstanceOperations.Add(operation);
        var recoveryAcceptedAt = BaseTime.AddMinutes(20);
        db.ElsaInstanceRecoveryRequests.Add(NewRecovery(workspace, instance, operation, 2, recoveryAcceptedAt));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var store = new EfCoreManagedElsaInstanceApiStore(db);
        var topology = await store.GetLifecycleTopologyAsync(workspace.Id, instance.Id);
        var topologyOperation = Assert.Single(topology!.Operations);
        var finishedCreate = await store.GetOperationAsync(workspace.Id, instance.Id, operation.Id);

        Assert.Equal(2, topologyOperation.AttemptNumber);
        Assert.Equal(recoveryAcceptedAt, topologyOperation.AttemptStartedAt);
        Assert.Equal(BaseTime, topologyOperation.AcceptedAt);
        Assert.Equal(recoveryAcceptedAt, finishedCreate!.AttemptStartedAt);

        operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == operation.Id);
        operation.State = ElsaInstanceOperationState.Succeeded;
        var legacyOperation = NewOperation(
            workspace,
            instance,
            BaseTime.AddMinutes(30),
            ElsaInstanceOperationAction.Create);
        legacyOperation.State = ElsaInstanceOperationState.Running;
        legacyOperation.AttemptNumber = 2;
        instance.LastOperationId = legacyOperation.Id.ToString("D");
        db.ElsaInstanceOperations.Add(legacyOperation);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        topology = await store.GetLifecycleTopologyAsync(workspace.Id, instance.Id);
        finishedCreate = await store.GetOperationAsync(workspace.Id, instance.Id, legacyOperation.Id);
        Assert.Null(Assert.Single(topology!.Operations).AttemptStartedAt);
        Assert.Null(finishedCreate!.AttemptStartedAt);

        legacyOperation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == legacyOperation.Id);
        legacyOperation.State = ElsaInstanceOperationState.Succeeded;
        var retryThreeOperation = NewOperation(
            workspace,
            instance,
            BaseTime.AddMinutes(35),
            ElsaInstanceOperationAction.Create);
        retryThreeOperation.State = ElsaInstanceOperationState.Running;
        retryThreeOperation.AttemptNumber = 3;
        instance.LastOperationId = retryThreeOperation.Id.ToString("D");
        db.ElsaInstanceOperations.Add(retryThreeOperation);
        db.ElsaInstanceRecoveryRequests.Add(
            NewRecovery(workspace, instance, retryThreeOperation, 3, BaseTime.AddMinutes(36)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        topology = await store.GetLifecycleTopologyAsync(workspace.Id, instance.Id);
        finishedCreate = await store.GetOperationAsync(workspace.Id, instance.Id, retryThreeOperation.Id);
        Assert.Equal(3, Assert.Single(topology!.Operations).AttemptNumber);
        Assert.Equal(BaseTime.AddMinutes(36), Assert.Single(topology.Operations).AttemptStartedAt);
        Assert.Equal(BaseTime.AddMinutes(36), finishedCreate!.AttemptStartedAt);

        retryThreeOperation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == retryThreeOperation.Id);
        retryThreeOperation.State = ElsaInstanceOperationState.Succeeded;
        var firstOperation = NewOperation(
            workspace,
            instance,
            BaseTime.AddMinutes(40),
            ElsaInstanceOperationAction.Create);
        firstOperation.State = ElsaInstanceOperationState.Running;
        instance.LastOperationId = firstOperation.Id.ToString("D");
        db.ElsaInstanceOperations.Add(firstOperation);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        topology = await store.GetLifecycleTopologyAsync(workspace.Id, instance.Id);
        finishedCreate = await store.GetOperationAsync(workspace.Id, instance.Id, firstOperation.Id);
        Assert.Equal(firstOperation.AcceptedAt, Assert.Single(topology!.Operations).AttemptStartedAt);
        Assert.Equal(firstOperation.AcceptedAt, finishedCreate!.AttemptStartedAt);
    }

    [Fact]
    public void Ambiguous_recovery_attempt_starts_resolve_to_unknown()
    {
        Assert.Null(EfCoreManagedElsaInstanceApiStore.ResolveRecoveryAttemptStart(
            [BaseTime.AddMinutes(36), BaseTime.AddMinutes(37)]));
    }

    [Fact]
    public async Task Lists_audit_newest_first_and_applies_the_requested_limit()
    {
        await using var connection = OpenConnection();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, "Audit limit workspace");
        var other = await CreateWorkspaceAsync(db, "Audit limit other workspace");
        var instance = NewInstance(workspace);
        var otherInstance = NewInstance(other, slug: "other-audit-elsa");
        db.ElsaInstances.AddRange(instance, otherInstance);
        await db.SaveChangesAsync();
        db.ElsaInstanceAuditEvents.AddRange(
            NewAudit(workspace, instance, 1, BaseTime),
            NewAudit(workspace, instance, 2, BaseTime.AddMinutes(1)),
            NewAudit(workspace, instance, 3, BaseTime.AddMinutes(2)),
            NewAudit(other, otherInstance, 1, BaseTime.AddMinutes(3)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var store = new EfCoreManagedElsaInstanceApiStore(db);
        var unlimited = await store.ListAuditAsync(
            workspace.Id, instance.Id, organizationId: workspace.OrganizationId);
        var limited = await store.ListAuditAsync(
            workspace.Id, instance.Id, limit: 2, organizationId: workspace.OrganizationId);
        var otherWorkspace = await store.ListAuditAsync(
            other.Id, instance.Id, limit: 10, organizationId: other.OrganizationId);
        var otherOrganization = await store.ListAuditAsync(
            workspace.Id, instance.Id, limit: 10, organizationId: Guid.NewGuid());

        Assert.NotEqual(Guid.Empty, workspace.OrganizationId);
        Assert.NotEqual(workspace.OrganizationId, other.OrganizationId);
        Assert.Equal(new[] { 3L, 2L, 1L }, unlimited.Select(x => x.Sequence).ToArray());
        Assert.Equal(new[] { 3L, 2L }, limited.Select(x => x.Sequence).ToArray());
        Assert.Empty(otherWorkspace);
        Assert.Empty(otherOrganization);
    }

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static CatalogDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection)
            .Options);

    private static async Task<Workspace> CreateWorkspaceAsync(CatalogDbContext db, string name)
    {
        var organization = new Organization { Name = name + " organization" };
        var workspace = new Workspace { Name = name, OrganizationId = organization.Id, Organization = organization };
        db.Organizations.Add(organization);
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        return workspace;
    }

    private static ElsaInstanceEntity NewInstance(Workspace workspace, string? slug = null) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = workspace.OrganizationId,
        WorkspaceId = workspace.Id,
        Name = "Managed Elsa",
        Slug = slug ?? "managed-elsa-" + Guid.NewGuid().ToString("N")[..8],
        DistributionId = "elsa",
        ReleaseLine = "3.0",
        Channel = "stable",
        PatchUpdates = "automatic-within-minor",
        MinorUpdates = "explicit-approval",
        MajorMigrations = "explicit-migration",
        TopologyId = "combined",
        FeatureOverridesJson = "{}",
        TargetMode = "managed",
        RegionCode = "westeurope",
        IsolationProfile = "dedicated",
        CapacityProfile = "standard",
        NetworkOutcome = "public",
        DomainOutcome = "managed",
        DesiredLifecycle = ElsaDesiredLifecycle.Running,
        ObservedLifecycle = ElsaObservedLifecycle.Ready,
        Health = ElsaInstanceHealth.Healthy,
        Version = 1,
        CreatedAt = BaseTime,
        UpdatedAt = BaseTime
    };

    private static ElsaInstanceOperationEntity NewOperation(
        Workspace workspace,
        ElsaInstanceEntity instance,
        DateTimeOffset acceptedAt,
        ElsaInstanceOperationAction action) => new()
    {
        Id = Guid.NewGuid(),
        InstanceId = instance.Id,
        OrganizationId = workspace.OrganizationId,
        WorkspaceId = workspace.Id,
        Action = action,
        IdempotencyScope = $"instance/{instance.Id:N}/{action}",
        IdempotencyKey = "operation-" + Guid.NewGuid().ToString("N"),
        RequestHash = new string('a', 64),
        ExpectedVersion = 1,
        State = ElsaInstanceOperationState.Succeeded,
        AttemptNumber = 1,
        AcceptedAt = acceptedAt,
        CreatedAt = acceptedAt,
        UpdatedAt = acceptedAt
    };

    private static ElsaInstanceAuditEventEntity NewAudit(
        Workspace workspace,
        ElsaInstanceEntity instance,
        long sequence,
        DateTimeOffset occurredAt) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = workspace.OrganizationId,
        WorkspaceId = workspace.Id,
        InstanceId = instance.Id,
        Sequence = sequence,
        EventType = "instance.seeded",
        OccurredAt = occurredAt
    };

    private static ElsaInstanceRecoveryRequestEntity NewRecovery(
        Workspace workspace,
        ElsaInstanceEntity instance,
        ElsaInstanceOperationEntity operation,
        int attemptNumber,
        DateTimeOffset acceptedAt) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = workspace.OrganizationId,
        WorkspaceId = workspace.Id,
        InstanceId = instance.Id,
        OperationId = operation.Id,
        AttemptNumber = attemptNumber,
        IdempotencyScope = $"instance/{instance.Id:N}/recover",
        IdempotencyKey = "recovery-" + Guid.NewGuid().ToString("N"),
        RequestHash = new string('b', 64),
        AcceptedAt = acceptedAt,
        CreatedAt = acceptedAt
    };
}
