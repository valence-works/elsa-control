using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ManagedElsaInstanceOperationalStoreTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Projects_instance_operation_and_matching_run_using_safe_operational_fields()
    {
        await using var connection = OpenConnection();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, "Operational health workspace");
        var instance = NewInstance(workspace, ElsaObservedLifecycle.Pending, ElsaInstanceHealth.Unknown);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();

        var environment = NewEnvironment(workspace, instance.Id);
        db.DeploymentApplications.Add(environment.Application!);
        db.DeploymentEnvironments.Add(environment);
        await db.SaveChangesAsync();
        var run = NewRun(workspace, instance.Id, WorkspaceDeploymentRunStatus.Running, environment);
        db.DeploymentRuns.Add(run);
        var operation = NewOperation(
            workspace,
            instance,
            ElsaInstanceOperationState.Running,
            acceptedAt: BaseTime,
            deploymentRunId: run.Id);
        operation.StartedAt = BaseTime.AddMinutes(1);
        operation.HeartbeatAt = BaseTime.AddMinutes(2);
        operation.ReconciledObservedLifecycle = ElsaObservedLifecycle.Ready;
        operation.ReconciledHealth = ElsaInstanceHealth.Healthy;
        operation.ReconciledAt = BaseTime.AddMinutes(3);
        operation.ReconciliationDiagnosticCode = "provider.health.healthy";
        instance.LastOperationId = operation.Id.ToString("D");
        db.ElsaInstanceOperations.Add(operation);
        await db.SaveChangesAsync();

        var snapshot = await new EfCoreManagedElsaInstanceOperationalStore(db)
            .GetSnapshotAsync(workspace.Id, instance.Id);

        Assert.NotNull(snapshot);
        Assert.Equal(workspace.Id, snapshot.WorkspaceId);
        Assert.Equal(instance.Id, snapshot.InstanceId);
        Assert.Equal(ElsaDesiredLifecycle.Running, snapshot.DesiredLifecycle);
        Assert.Equal(ElsaObservedLifecycle.Ready, snapshot.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Healthy, snapshot.Health);
        Assert.Equal(ElsaInstanceProviderObservationKind.Confirmed, snapshot.ProviderObservationKind);
        Assert.Equal(operation.Id, snapshot.Operation!.Id);
        Assert.Equal(operation.HeartbeatAt, snapshot.Operation.HeartbeatAt);
        Assert.Equal(operation.ReconciliationDiagnosticCode, snapshot.Operation.DiagnosticCode);
        Assert.Equal(operation.ReconciledAt, snapshot.ReconciledAt);
        Assert.Equal(run.Id, snapshot.Run!.Id);
        Assert.Equal(run.WorkerHeartbeatAt, snapshot.Run.HeartbeatAt);
        Assert.Equal("provider.health.healthy", snapshot.ProviderDiagnosticCode);
    }

    [Fact]
    public async Task Last_operation_id_wins_over_active_work_and_fallback_selection_is_deterministic()
    {
        await using var connection = OpenConnection();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, "Selection workspace");
        var instance = NewInstance(workspace);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();

        var terminal = NewOperation(
            workspace,
            instance,
            ElsaInstanceOperationState.Succeeded,
            acceptedAt: BaseTime,
            id: Guid.Parse("40000000-0000-0000-0000-000000000001"));
        terminal.CompletedAt = BaseTime.AddMinutes(5);
        var active = NewOperation(
            workspace,
            instance,
            ElsaInstanceOperationState.Running,
            acceptedAt: BaseTime.AddMinutes(1),
            id: Guid.Parse("40000000-0000-0000-0000-000000000002"));
        db.ElsaInstanceOperations.AddRange(terminal, active);
        instance.LastOperationId = terminal.Id.ToString("D");
        await db.SaveChangesAsync();

        var store = new EfCoreManagedElsaInstanceOperationalStore(db);
        var pinned = await store.GetSnapshotAsync(workspace.Id, instance.Id);
        Assert.Equal(terminal.Id, pinned!.Operation!.Id);

        instance.LastOperationId = Guid.NewGuid().ToString("D");
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var fallback = await store.GetSnapshotAsync(workspace.Id, instance.Id);
        Assert.Equal(active.Id, fallback!.Operation!.Id);
    }

    [Fact]
    public async Task Uses_latest_terminal_operation_by_timestamp_then_id_when_no_active_operation_exists()
    {
        await using var connection = OpenConnection();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, "Terminal selection workspace");
        var instance = NewInstance(workspace);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();

        var firstId = Guid.Parse("40000000-0000-0000-0000-000000000011");
        var secondId = Guid.Parse("40000000-0000-0000-0000-000000000012");
        var first = NewOperation(workspace, instance, ElsaInstanceOperationState.Succeeded, BaseTime, firstId);
        var second = NewOperation(workspace, instance, ElsaInstanceOperationState.Cancelled, BaseTime, secondId);
        first.CompletedAt = BaseTime.AddMinutes(5);
        second.CompletedAt = first.CompletedAt;
        first.CreatedAt = BaseTime;
        second.CreatedAt = BaseTime;
        db.ElsaInstanceOperations.AddRange(first, second);
        await db.SaveChangesAsync();

        var snapshot = await new EfCoreManagedElsaInstanceOperationalStore(db)
            .GetSnapshotAsync(workspace.Id, instance.Id);

        Assert.Equal(secondId, snapshot!.Operation!.Id);
    }

    [Fact]
    public async Task Cross_workspace_and_missing_instance_reads_return_null()
    {
        await using var connection = OpenConnection();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, "Owner workspace");
        var otherWorkspace = await CreateWorkspaceAsync(db, "Other workspace");
        var instance = NewInstance(workspace);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();

        var store = new EfCoreManagedElsaInstanceOperationalStore(db);

        Assert.Null(await store.GetSnapshotAsync(otherWorkspace.Id, instance.Id));
        Assert.Null(await store.GetSnapshotAsync(workspace.Id, Guid.NewGuid()));
        Assert.Null(await store.GetSnapshotAsync(Guid.Empty, instance.Id));
        Assert.Null(await store.GetSnapshotAsync(workspace.Id, Guid.Empty));
    }

    [Fact]
    public async Task Mismatched_deployment_run_is_not_projected()
    {
        await using var connection = OpenConnection();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, "Run ownership workspace");
        var instance = NewInstance(workspace);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();
        var mismatchedInstanceId = Guid.NewGuid();
        db.ElsaInstances.Add(NewInstance(workspace, id: mismatchedInstanceId));
        await db.SaveChangesAsync();
        var environment = NewEnvironment(workspace, mismatchedInstanceId);
        db.DeploymentApplications.Add(environment.Application!);
        db.DeploymentEnvironments.Add(environment);
        await db.SaveChangesAsync();

        var run = NewRun(workspace, mismatchedInstanceId, WorkspaceDeploymentRunStatus.Running, environment);
        db.DeploymentRuns.Add(run);
        var operation = NewOperation(
            workspace,
            instance,
            ElsaInstanceOperationState.Running,
            acceptedAt: BaseTime,
            deploymentRunId: run.Id);
        db.ElsaInstanceOperations.Add(operation);
        instance.LastOperationId = operation.Id.ToString("D");
        await db.SaveChangesAsync();

        var snapshot = await new EfCoreManagedElsaInstanceOperationalStore(db)
            .GetSnapshotAsync(workspace.Id, instance.Id);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.Run);
    }

    [Fact]
    public async Task Redacts_unsafe_operation_code_and_free_form_run_diagnostics()
    {
        await using var connection = OpenConnection();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, "Redaction workspace");
        var instance = NewInstance(workspace);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();
        var environment = NewEnvironment(workspace, instance.Id);
        db.DeploymentApplications.Add(environment.Application!);
        db.DeploymentEnvironments.Add(environment);
        await db.SaveChangesAsync();

        var run = NewRun(workspace, instance.Id, WorkspaceDeploymentRunStatus.Failed, environment);
        run.FailureMessage = "provider-secret-must-not-escape";
        var operation = NewOperation(
            workspace,
            instance,
            ElsaInstanceOperationState.Failed,
            acceptedAt: BaseTime,
            deploymentRunId: run.Id);
        db.DeploymentRuns.Add(run);
        db.ElsaInstanceOperations.Add(operation);
        instance.LastOperationId = operation.Id.ToString("D");
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ElsaInstanceOperations SET FailureCode = {"unsafe failure with details"} WHERE Id = {operation.Id}");
        db.ChangeTracker.Clear();

        var snapshot = await new EfCoreManagedElsaInstanceOperationalStore(db)
            .GetSnapshotAsync(workspace.Id, instance.Id);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.Operation!.DiagnosticCode);
        Assert.Equal(ManagedLifecycleOperationalHealthDiagnosticCodes.RunFailed, snapshot.Run!.DiagnosticCode);
        var serialized = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("provider-secret-must-not-escape", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe failure with details", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Maps_free_form_recovery_reason_to_a_fixed_code_without_exposing_it()
    {
        await using var connection = OpenConnection();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, "Recovery redaction workspace");
        var instance = NewInstance(workspace);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();
        var environment = NewEnvironment(workspace, instance.Id);
        db.DeploymentApplications.Add(environment.Application!);
        db.DeploymentEnvironments.Add(environment);
        await db.SaveChangesAsync();

        var run = NewRun(workspace, instance.Id, WorkspaceDeploymentRunStatus.RecoveryRequired, environment);
        run.RecoveryReason = "provider-recovery-secret";
        var operation = NewOperation(
            workspace,
            instance,
            ElsaInstanceOperationState.RecoveryRequired,
            acceptedAt: BaseTime,
            deploymentRunId: run.Id);
        db.DeploymentRuns.Add(run);
        db.ElsaInstanceOperations.Add(operation);
        instance.LastOperationId = operation.Id.ToString("D");
        await db.SaveChangesAsync();

        var snapshot = await new EfCoreManagedElsaInstanceOperationalStore(db)
            .GetSnapshotAsync(workspace.Id, instance.Id);

        Assert.Equal(ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired, snapshot!.Run!.DiagnosticCode);
        Assert.DoesNotContain("provider-recovery-secret", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_selected_operation_fails_closed_instead_of_projecting_instance_as_healthy()
    {
        await using var connection = OpenConnection();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, "Malformed operation workspace");
        var instance = NewInstance(workspace);
        var operation = NewOperation(workspace, instance, ElsaInstanceOperationState.Running, BaseTime);
        instance.LastOperationId = operation.Id.ToString("D");
        db.ElsaInstances.Add(instance);
        db.ElsaInstanceOperations.Add(operation);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ElsaInstanceOperations SET AttemptNumber = {0} WHERE Id = {operation.Id}");
        db.ChangeTracker.Clear();

        var snapshot = await new EfCoreManagedElsaInstanceOperationalStore(db)
            .GetSnapshotAsync(workspace.Id, instance.Id);

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task Malformed_correlated_run_fails_closed_instead_of_hiding_the_run()
    {
        await using var connection = OpenConnection();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, "Malformed run workspace");
        var instance = NewInstance(workspace);
        db.ElsaInstances.Add(instance);
        await db.SaveChangesAsync();
        var environment = NewEnvironment(workspace, instance.Id);
        db.DeploymentApplications.Add(environment.Application!);
        db.DeploymentEnvironments.Add(environment);
        await db.SaveChangesAsync();
        var run = NewRun(workspace, instance.Id, WorkspaceDeploymentRunStatus.Running, environment);
        var operation = NewOperation(
            workspace,
            instance,
            ElsaInstanceOperationState.Running,
            BaseTime,
            deploymentRunId: run.Id);
        instance.LastOperationId = operation.Id.ToString("D");
        db.DeploymentRuns.Add(run);
        db.ElsaInstanceOperations.Add(operation);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE DeploymentRuns SET AttemptNumber = {0} WHERE Id = {run.Id}");
        db.ChangeTracker.Clear();

        var snapshot = await new EfCoreManagedElsaInstanceOperationalStore(db)
            .GetSnapshotAsync(workspace.Id, instance.Id);

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task Partially_reconciled_operation_fails_closed_instead_of_mixing_projection_sources()
    {
        await using var connection = OpenConnection();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var workspace = await CreateWorkspaceAsync(db, "Partial reconciliation workspace");
        var instance = NewInstance(workspace, ElsaObservedLifecycle.Unknown, ElsaInstanceHealth.Unknown);
        var operation = NewOperation(workspace, instance, ElsaInstanceOperationState.Succeeded, BaseTime);
        operation.ReconciledObservedLifecycle = ElsaObservedLifecycle.Ready;
        operation.ReconciledAt = BaseTime.AddMinutes(1);
        instance.LastOperationId = operation.Id.ToString("D");
        db.ElsaInstances.Add(instance);
        db.ElsaInstanceOperations.Add(operation);
        await db.SaveChangesAsync();

        var snapshot = await new EfCoreManagedElsaInstanceOperationalStore(db)
            .GetSnapshotAsync(workspace.Id, instance.Id);

        Assert.Null(snapshot);
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
        var workspace = new Workspace { Name = name };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        return workspace;
    }

    private static ElsaInstanceEntity NewInstance(
        Workspace workspace,
        ElsaObservedLifecycle observedLifecycle = ElsaObservedLifecycle.Ready,
        ElsaInstanceHealth health = ElsaInstanceHealth.Healthy,
        Guid? id = null) => new()
        {
            Id = id ?? Guid.NewGuid(),
            OrganizationId = workspace.OrganizationId,
            WorkspaceId = workspace.Id,
            Name = "Managed Elsa",
            Slug = "managed-elsa-" + Guid.NewGuid().ToString("N")[..8],
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
            ObservedLifecycle = observedLifecycle,
            Health = health,
            Version = 1,
            CreatedAt = BaseTime,
            UpdatedAt = BaseTime
        };

    private static ElsaInstanceOperationEntity NewOperation(
        Workspace workspace,
        ElsaInstanceEntity instance,
        ElsaInstanceOperationState state,
        DateTimeOffset acceptedAt,
        Guid? id = null,
        Guid? deploymentRunId = null) => new()
        {
            Id = id ?? Guid.NewGuid(),
            InstanceId = instance.Id,
            OrganizationId = workspace.OrganizationId,
            WorkspaceId = workspace.Id,
            Action = ElsaInstanceOperationAction.Reconcile,
            IdempotencyScope = $"instance/{instance.Id:N}",
            IdempotencyKey = "operation-" + Guid.NewGuid().ToString("N"),
            RequestHash = new string('a', 64),
            ExpectedVersion = 1,
            State = state,
            AttemptNumber = 1,
            AcceptedAt = acceptedAt,
            DeploymentRunId = deploymentRunId,
            CreatedAt = acceptedAt,
            UpdatedAt = acceptedAt
        };

    private static DeploymentRunEntity NewRun(
        Workspace workspace,
        Guid? instanceId,
        WorkspaceDeploymentRunStatus status,
        DeploymentEnvironmentEntity? environment = null)
    {
        var queuedAt = BaseTime;
        return new DeploymentRunEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            ElsaInstanceId = instanceId,
            ApplicationId = environment?.ApplicationId ?? Guid.NewGuid(),
            EnvironmentId = environment?.Id ?? Guid.NewGuid(),
            EngineId = Guid.NewGuid(),
            SourceRevisionId = Guid.NewGuid(),
            Status = status,
            ValidationOutcome = DeploymentValidationOutcome.Passed,
            ConfirmationId = Guid.NewGuid(),
            ActorAccountId = Guid.NewGuid(),
            QueuedAt = queuedAt,
            StartedAt = queuedAt.AddMinutes(1),
            WorkerHeartbeatAt = queuedAt.AddMinutes(2),
            AttemptNumber = 1,
            CreatedAt = queuedAt
        };
    }

    private static DeploymentEnvironmentEntity NewEnvironment(Workspace workspace, Guid? instanceId = null)
    {
        var application = new DeploymentApplicationEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            Name = "Managed app",
            CreatedAt = BaseTime,
            UpdatedAt = BaseTime
        };
        return new DeploymentEnvironmentEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            ApplicationId = application.Id,
            Application = application,
            ElsaInstanceId = instanceId,
            Name = "Managed environment",
            Tier = EnvironmentTier.Production,
            DeploymentStatus = DeploymentStatus.Blocked,
            DriftStatus = DriftStatus.Unknown,
            CreatedAt = BaseTime,
            UpdatedAt = BaseTime
        };
    }
}
