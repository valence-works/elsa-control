using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed partial class AzureProviderExecutorTests
{
    [Fact]
    public async Task Late_success_wait_exceeded_parks_workload_and_auto_resumes_at_health()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner
        {
            FoundationOutcome = AzureProviderRunnerOutcome.Uncertain,
            FoundationResourcesOverride = FoundationResources()
        };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var plan = CreatePlan();
        var request = CreateRequest() with { LifecycleAction = ElsaInstanceOperationAction.Create };

        var interrupted = await executor.ApplyAsync(request, plan);
        var parked = interrupted.Operation with
        {
            Status = AzureProviderOperationStatus.RecoveryRequired,
            Resources = SqlResourcesForRecovery(),
            AttemptedStep = AzureProviderRunnerStep.Workload,
            Phase = AzureProviderOperationPhase.FoundationReady,
            Diagnostics =
            [
                new AzureProviderDiagnostic("azure.step.workload.uncertain", "azure.step.workload.uncertain"),
                new AzureProviderDiagnostic(
                    ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded,
                    ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded),
                new AzureProviderDiagnostic(
                    StagingLateSuccessLeverDefaults.AppliedCode,
                    StagingLateSuccessLeverDefaults.AppliedCode)
            ]
        };
        store.Replace(parked);

        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, parked.Status);
        Assert.Equal(AzureProviderRunnerStep.Workload, parked.AttemptedStep);
        Assert.Equal(ManagedElsaReasonClass.AutoResuming,
            ManagedElsaReasonCodeCatalog.Classify(ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded));
        Assert.Equal(1, parked.Diagnostics.Count(diagnostic =>
            diagnostic.Code == StagingLateSuccessLeverDefaults.AppliedCode));
        Assert.Equal(
            ElsaObservedLifecycle.RecoveryRequired,
            ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(
                ElsaObservedLifecycle.Provisioning,
                new ActiveLifecycleOperation(
                    ElsaInstanceOperationAction.Create,
                    ElsaInstanceOperationState.RecoveryRequired,
                    ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded,
                    ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded,
                    Now,
                    Now,
                    Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter),
                ElsaDesiredLifecycle.Running,
                Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter));

        var observed = new AzureProviderRecoveryObservation(
            AzureProviderRecoveryObservationKind.Confirmed,
            AzureProviderRunnerStep.Workload,
            CompleteResourcesForRecovery(),
            AzureProviderHealth.Unknown,
            null,
            ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadObserved,
            "The retained Azure workload completion was observed without mutation.");

        var previousCallCount = runner.Steps.Count;
        var resumed = await executor.RecoverAsync(parked, plan, observed);
        var resumedSteps = runner.Steps.Skip(previousCallCount).ToArray();

        Assert.Equal(AzureProviderExecutionOutcome.Succeeded, resumed.Outcome);
        Assert.Equal(AzureProviderOperationStatus.Succeeded, resumed.Operation.Status);
        Assert.Equal(AzureProviderRunnerStep.Health, resumedSteps[0]);
        Assert.Contains(AzureProviderRunnerStep.Promotion, resumedSteps);
        Assert.DoesNotContain(AzureProviderRunnerStep.Workload, resumedSteps);
        Assert.Equal(1, store.RecoveryClaimCount);
        Assert.Equal(1, parked.Diagnostics.Count(diagnostic =>
            diagnostic.Code == StagingLateSuccessLeverDefaults.AppliedCode));
    }
}
