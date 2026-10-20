using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Azure.Tests;

/// <summary>
/// #860: the executor refuses a resume exactly when the shared eligibility predicate does, so
/// the reconciler, which charges an auto-resume through the same predicate, cannot drift.
/// </summary>
public sealed partial class AzureProviderExecutorTests
{
    [Theory]
    [InlineData("confirmed-workload")]
    [InlineData("confirmed-workload-missing-registry-role")]
    [InlineData("confirmed-workload-already-ready")]
    [InlineData("confirmed-foundation-for-workload")]
    [InlineData("failed-workload")]
    [InlineData("failed-workload-missing-registry-role")]
    public async Task Executor_refuses_a_resume_exactly_when_the_shared_predicate_does(string scenario)
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner
        {
            FoundationOutcome = AzureProviderRunnerOutcome.Uncertain,
            FoundationResourcesOverride = FoundationResources()
        };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var plan = CreatePlan();
        var interrupted = await executor.ApplyAsync(
            CreateRequest() with { LifecycleAction = ElsaInstanceOperationAction.Create }, plan);
        var parked = interrupted.Operation with
        {
            Status = AzureProviderOperationStatus.RecoveryRequired,
            Resources = scenario.EndsWith("missing-registry-role", StringComparison.Ordinal)
                ? SqlResourcesForRecovery() with { AcrPullRoleAssignmentId = null }
                : SqlResourcesForRecovery(),
            AttemptedStep = AzureProviderRunnerStep.Workload,
            Phase = scenario == "confirmed-workload-already-ready"
                ? AzureProviderOperationPhase.WorkloadReady
                : AzureProviderOperationPhase.FoundationReady
        };
        store.Replace(parked);
        var observation = scenario.StartsWith("failed", StringComparison.Ordinal)
            ? new AzureProviderRecoveryObservation(
                AzureProviderRecoveryObservationKind.Failed,
                AzureProviderRunnerStep.Workload,
                new(),
                AzureProviderHealth.Unknown,
                null,
                ManagedElsaReasonCodeCatalog.AzureDeploymentFailed,
                "Azure reported the deployment as failed.",
                AzureTransientArmFailure.ManagedEnvironmentProvisioningErrorCode)
            : new AzureProviderRecoveryObservation(
                AzureProviderRecoveryObservationKind.Confirmed,
                scenario == "confirmed-foundation-for-workload"
                    ? AzureProviderRunnerStep.Foundation
                    : AzureProviderRunnerStep.Workload,
                CompleteResourcesForRecovery(),
                AzureProviderHealth.Unknown,
                null,
                ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadObserved,
                "The retained Azure workload completion was observed without mutation.");
        var eligible = AzureProviderResumeEligibility.IsEligible(parked, observation);

        var result = await executor.RecoverAsync(parked, plan, observation);

        var refused = result.Code == ManagedElsaReasonCodeCatalog.AzureRecoveryObservationInsufficient &&
                      store.RecoveryClaimCount == 0;
        Assert.Equal(!eligible, refused);
        Assert.Equal(eligible ? 1 : 0, store.RecoveryClaimCount);
        Assert.Equal(scenario is "confirmed-workload" or "failed-workload", eligible);
    }

    [Fact]
    public void Receipt_binds_to_the_reconciled_instance_version_with_or_without_an_aggregate_change()
    {
        Assert.True(AzureProviderResumeEligibility.IsBoundToReconciledInstanceVersion(5, 5));
        Assert.True(AzureProviderResumeEligibility.IsBoundToReconciledInstanceVersion(5, 6));
        Assert.False(AzureProviderResumeEligibility.IsBoundToReconciledInstanceVersion(5, 4));
        Assert.False(AzureProviderResumeEligibility.IsBoundToReconciledInstanceVersion(5, 7));
        Assert.False(AzureProviderResumeEligibility.IsBoundToReconciledInstanceVersion(0, 0));
        Assert.False(AzureProviderResumeEligibility.IsBoundToReconciledInstanceVersion(int.MaxValue, int.MinValue));
    }

    [Fact]
    public async Task Auto_resume_requires_an_authoritative_receipt_minted_at_the_reconciled_version()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner
        {
            FoundationOutcome = AzureProviderRunnerOutcome.Uncertain,
            FoundationResourcesOverride = FoundationResources()
        };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var interrupted = await executor.ApplyAsync(
            CreateRequest() with { LifecycleAction = ElsaInstanceOperationAction.Create }, CreatePlan());
        var parked = interrupted.Operation with
        {
            OrganizationId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            InstanceId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"),
            ProviderAssignmentId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"),
            Status = AzureProviderOperationStatus.RecoveryRequired,
            Resources = SqlResourcesForRecovery(),
            AttemptedStep = AzureProviderRunnerStep.Workload,
            Phase = AzureProviderOperationPhase.FoundationReady
        };
        var observation = new AzureProviderRecoveryObservation(
            AzureProviderRecoveryObservationKind.Confirmed,
            AzureProviderRunnerStep.Workload,
            CompleteResourcesForRecovery(),
            AzureProviderHealth.Unknown,
            null,
            ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadObserved,
            "The retained Azure workload completion was observed without mutation.");
        var receipt = new AzureProviderRecoveryObservationRecord(
            parked.OrganizationId!.Value,
            parked.WorkspaceId,
            parked.InstanceId!.Value,
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"),
            parked.LifecycleAction!.Value,
            1,
            7,
            parked.Id,
            parked.OperationIdentity,
            parked.RequestHash,
            parked.AttemptNumber,
            parked.Version,
            parked.CheckpointSequence,
            parked.ProviderAssignmentId!.Value,
            parked.TargetKey,
            parked.ProviderScopeFingerprint,
            "plan-1",
            1,
            "https://control.example.test/plans/plan-1",
            "sha256:" + new string('d', 64),
            parked.PlanFingerprint,
            parked.TemplateFingerprint,
            AzureProviderRunnerStep.Workload,
            AzureProviderOperationPhase.WorkloadReady,
            AzureProviderHealth.Unknown,
            new string('e', 64),
            new string('f', 64),
            Now);

        Assert.True(AzureProviderResumeEligibility.IsRecordAuthoritative(receipt, parked));
        Assert.True(AzureProviderResumeEligibility.CanAutoResume(parked, observation, receipt, 7));
        // A receipt from the previous instance version would be refused if this reconcile changes the aggregate.
        Assert.False(AzureProviderResumeEligibility.CanAutoResume(parked, observation, receipt, 8));
        // A provider tuple that moved since the receipt was minted is not authoritative.
        Assert.False(AzureProviderResumeEligibility.IsRecordAuthoritative(receipt, parked with { Version = parked.Version + 1 }));
        Assert.False(AzureProviderResumeEligibility.CanAutoResume(
            parked with { Version = parked.Version + 1 }, observation, receipt, 7));
        // An eligible receipt cannot buy a resume the executor refuses.
        Assert.False(AzureProviderResumeEligibility.CanAutoResume(
            parked with { Resources = SqlResourcesForRecovery() with { AcrPullRoleAssignmentId = null } },
            observation, receipt, 7));
    }
}
