using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed partial class AzureProviderRecoveryObservationPersistenceTests
{
    [Fact]
    public async Task Late_success_wait_exceeded_auto_resumes_once_at_health_without_operator_recover()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db, ElsaInstanceOperationAction.Create);
        await AddLifecycleRunAsync(db, fixture);
        await PrepareWorkloadLateSuccessAsync(db, fixture);

        var parkedAt = fixture.Observation.ObservedAt;
        var operation = await db.AzureProviderOperations.SingleAsync(x => x.Id == fixture.Operation.Id);
        operation.DiagnosticsJson = JsonSerializer.Serialize(new AzureProviderDiagnostic[]
        {
            new("azure.step.workload.uncertain", "azure.step.workload.uncertain"),
            new(ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded, ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded),
            new(StagingLateSuccessLeverDefaults.AppliedCode, StagingLateSuccessLeverDefaults.AppliedCode)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(AzureProviderRunnerStep.Workload,
            (await db.AzureProviderOperations.AsNoTracking().SingleAsync(x => x.Id == fixture.Operation.Id)).AttemptedStep);
        Assert.Equal(1, CountAppliedDiagnostics(
            (await db.AzureProviderOperations.AsNoTracking().SingleAsync(x => x.Id == fixture.Operation.Id)).DiagnosticsJson));
        Assert.Equal(ManagedElsaReasonClass.AutoResuming,
            ManagedElsaReasonCodeCatalog.Classify(ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded));
        Assert.Equal(
            ElsaObservedLifecycle.RecoveryRequired,
            ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(
                ElsaObservedLifecycle.Provisioning,
                new ActiveLifecycleOperation(
                    ElsaInstanceOperationAction.Create,
                    ElsaInstanceOperationState.RecoveryRequired,
                    ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded,
                    ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded,
                    parkedAt,
                    parkedAt,
                    parkedAt + ManagedElsaReasonCodeCatalog.HumanRequiredAfter),
                ElsaDesiredLifecycle.Running,
                parkedAt + ManagedElsaReasonCodeCatalog.HumanRequiredAfter));

        var clock = new MutableTimeProvider(parkedAt.AddMinutes(2));
        var observer = new CapAwareRecoveryObserver();
        var (_, _, lifecycleService, reconciler) = CreateLateSuccessStack(db, fixture, observer, clock);

        var resumed = await reconciler.ReconcileAsync(fixture.Workspace.Id, fixture.LifecycleOperationId);

        Assert.True(resumed.RetrySafe);
        Assert.Equal(ElsaInstanceOperationState.Queued,
            (await db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == fixture.LifecycleOperationId)).State);
        Assert.Equal(1,
            (await db.AzureProviderOperations.AsNoTracking().SingleAsync(x => x.Id == fixture.Operation.Id)).AutoResumeCount);
        Assert.Equal(0, observer.OperatorCalls);
        Assert.Equal(1, CountAppliedDiagnostics(
            (await db.AzureProviderOperations.AsNoTracking().SingleAsync(x => x.Id == fixture.Operation.Id)).DiagnosticsJson));

        _ = lifecycleService;
    }

    private static int CountAppliedDiagnostics(string json)
    {
        var diagnostics = JsonSerializer.Deserialize<AzureProviderDiagnostic[]>(json) ?? [];
        return diagnostics.Count(diagnostic => diagnostic.Code == StagingLateSuccessLeverDefaults.AppliedCode);
    }
}
