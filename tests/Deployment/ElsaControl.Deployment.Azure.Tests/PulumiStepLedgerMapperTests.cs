using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class PulumiStepLedgerMapperTests
{
    [Fact]
    public void Engine_handoff_output_keys_match_the_stack_contract()
    {
        Assert.Equal("engineEndpoint", PulumiStepLedgerMapper.EngineEndpointOutputKey);
        Assert.Equal("workloadRevisionName", PulumiStepLedgerMapper.WorkloadRevisionNameOutputKey);
        Assert.Equal("workloadResourceId", PulumiStepLedgerMapper.WorkloadResourceIdOutputKey);
        Assert.Equal("ownedResourceGroupName", PulumiStepLedgerMapper.OwnedResourceGroupNameOutputKey);
    }

    [Fact]
    public void Foundation_then_runtime_advances_projector_stages_in_order()
    {
        var phase = AzureProviderOperationPhase.Planned;
        var checkpoints = new List<PulumiLedgerCheckpoint>();
        foreach (var value in new[]
                 {
                     Event(ProvisioningEventKind.ResourceStarted, "resource-group", "Resource started."),
                     Event(ProvisioningEventKind.ResourceSucceeded, "resource-group", "Resource succeeded."),
                     Event(ProvisioningEventKind.ResourceSucceeded, ProvisioningCapability.SharedEnvironment, "Resource succeeded."),
                     Event(ProvisioningEventKind.ResourceStarted, ProvisioningCapability.ContainerRuntime, "Resource started."),
                     Event(ProvisioningEventKind.ResourceSucceeded, ProvisioningCapability.ContainerRuntime, "Resource succeeded.")
                 })
        {
            var mapped = PulumiStepLedgerMapper.TryMap(value, phase);
            Assert.NotNull(mapped);
            Assert.True(PulumiStepLedgerMapper.IsCustomerSafe(mapped));
            checkpoints.Add(mapped);
            phase = mapped.Phase;
        }

        Assert.Equal(AzureProviderOperationPhase.FoundationSubmitted, checkpoints[0].Phase);
        Assert.Equal(PulumiStepLedgerMapper.FoundationPreparingCode, checkpoints[0].Code);
        Assert.Equal(AzureProviderOperationPhase.FoundationObserved, checkpoints[1].Phase);
        Assert.Equal(AzureProviderOperationPhase.FoundationReady, checkpoints[2].Phase);
        Assert.Equal(PulumiStepLedgerMapper.RuntimeDeployingCode, checkpoints[3].Code);
        Assert.Equal(AzureProviderOperationPhase.WorkloadReady, checkpoints[4].Phase);

        var transitions = checkpoints.Select((checkpoint, index) =>
            new AzureProviderOperationTransition(
                Guid.NewGuid(),
                Guid.NewGuid(),
                index + 1,
                checkpoint.Status,
                checkpoint.Phase,
                checkpoint.Code,
                checkpoint.Message,
                DateTimeOffset.UtcNow.AddSeconds(index))).ToArray();
        var provider = Provider(checkpoints[^1].Phase, checkpoints[^1].AttemptedStep);
        var progress = AzureManagedElsaProvisioningProgressProjector.Project(
            new AzureManagedElsaProvisioningProgressProjectionInput(
                Topology: null,
                LifecycleOperation: Lifecycle(),
                ProviderOperation: provider,
                Transitions: transitions));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Active, progress.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.RuntimeDeployment, progress.CurrentStage);
        Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Completed, progress.Stages[1].Status);
        Assert.Equal(ManagedElsaProvisioningProgressStageStatuses.Current, progress.Stages[3].Status);
        AssertDoesNotLeak(progress, "/subscriptions/", "resourceGroups", "password", "pulumi");
    }

    [Theory]
    [InlineData(AzureProviderOperationPhase.WorkloadSubmitted)]
    [InlineData(AzureProviderOperationPhase.CleanupVerified)]
    public void An_engine_error_observation_does_not_finalize_the_provider_operation(AzureProviderOperationPhase phase)
    {
        // Pulumi can keep working (for example a Container Apps environment delete that takes 15+
        // minutes) after an engine diagnostic; only the terminal event ends the operation.
        var mapped = PulumiStepLedgerMapper.TryMap(
            Event(
                ProvisioningEventKind.ResourceFailed,
                ProvisioningCapability.ContainerRuntime,
                PulumiStepLedgerMapper.EngineResourceFailedMessage),
            phase);
        Assert.Null(mapped);
    }

    [Fact]
    public void The_provider_failed_result_still_finalizes_the_provider_operation()
    {
        var mapped = PulumiStepLedgerMapper.TryMap(
            Event(ProvisioningEventKind.ResourceFailed, ProvisioningCapability.ContainerRuntime, "Provider failed."),
            AzureProviderOperationPhase.WorkloadSubmitted);
        Assert.NotNull(mapped);
        Assert.True(mapped.Finalize);
        Assert.Equal(AzureProviderOperationStatus.Failed, mapped.Status);
    }

    [Fact]
    public void A_mid_up_failure_blocks_the_matching_stage_with_the_phase_zero_reason()
    {
        var mapped = PulumiStepLedgerMapper.TryMap(
            Event(ProvisioningEventKind.OperationFailed, ProvisioningCapability.ContainerRuntime, "Operation failed."),
            AzureProviderOperationPhase.WorkloadSubmitted);
        Assert.NotNull(mapped);
        Assert.True(mapped.Finalize);
        Assert.Equal(AzureProviderOperationStatus.Failed, mapped.Status);
        Assert.Equal(ManagedElsaReasonCodeCatalog.AzureDeploymentFailed, mapped.Code);
        Assert.Equal(PulumiStepLedgerMapper.ResourceFailedMessage, mapped.Message);
        Assert.True(PulumiStepLedgerMapper.IsCustomerSafe(mapped));

        var progress = AzureManagedElsaProvisioningProgressProjector.Project(
            new AzureManagedElsaProvisioningProgressProjectionInput(
                Topology: null,
                LifecycleOperation: Lifecycle(ElsaInstanceOperationState.Running),
                ProviderOperation: Provider(
                    AzureProviderOperationPhase.WorkloadSubmitted,
                    AzureProviderRunnerStep.Workload,
                    AzureProviderOperationStatus.Failed),
                Transitions:
                [
                    new(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        1,
                        AzureProviderOperationStatus.Failed,
                        AzureProviderOperationPhase.WorkloadSubmitted,
                        mapped.Code,
                        mapped.Message,
                        DateTimeOffset.UtcNow)
                ]));

        Assert.Equal(ManagedElsaProvisioningProgressStates.Failed, progress.State);
        Assert.Equal(ManagedElsaProvisioningProgressStages.RuntimeDeployment, progress.CurrentStage);
        Assert.Equal(ManagedElsaProvisioningProgressDiagnostics.Failed, progress.DiagnosticCode);
        Assert.Contains(progress.Activity, item => item.Status == ManagedElsaProvisioningProgressActivityStatuses.Blocked);
        AssertDoesNotLeak(progress, "azure-native", "ContainerApp", "error: ");
    }

    [Fact]
    public void Raw_pulumi_text_is_never_a_customer_safe_checkpoint()
    {
        var unsafeCheckpoint = new PulumiLedgerCheckpoint(
            AzureProviderOperationPhase.FoundationSubmitted,
            AzureProviderRunnerStep.Foundation,
            AzureProviderOperationStatus.Running,
            "foundation.preparing",
            "failed to create /subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/engine-rg: password=hunter2",
            Finalize: false);
        Assert.False(PulumiStepLedgerMapper.IsCustomerSafe(unsafeCheckpoint));
    }

    [Fact]
    public void Operation_started_opens_the_foundation_stage()
    {
        var mapped = PulumiStepLedgerMapper.TryMap(
            Event(ProvisioningEventKind.OperationStarted, ProvisioningCapability.ContainerRuntime, "Operation started."),
            AzureProviderOperationPhase.Planned);
        Assert.NotNull(mapped);
        Assert.Equal(AzureProviderOperationPhase.FoundationSubmitted, mapped.Phase);
        Assert.Equal(PulumiStepLedgerMapper.FoundationPreparingCode, mapped.Code);
        Assert.False(mapped.Finalize);
        Assert.True(PulumiStepLedgerMapper.IsCustomerSafe(mapped));
    }

    [Fact]
    public void Operation_succeeded_stops_at_workload_ready_until_health_runs()
    {
        var mapped = PulumiStepLedgerMapper.TryMap(
            Event(ProvisioningEventKind.OperationSucceeded, ProvisioningCapability.ContainerRuntime, "Operation succeeded."),
            AzureProviderOperationPhase.WorkloadReady);
        Assert.NotNull(mapped);
        Assert.False(mapped.Finalize);
        Assert.Equal(AzureProviderOperationStatus.Running, mapped.Status);
        Assert.Equal(AzureProviderOperationPhase.WorkloadReady, mapped.Phase);
        Assert.Equal(PulumiStepLedgerMapper.ApplyCompletedCode, mapped.Code);
        Assert.Equal(AzureProviderHealth.Unknown, mapped.Health);
        Assert.True(PulumiStepLedgerMapper.IsCustomerSafe(mapped));
    }

    [Fact]
    public void A_successful_update_stream_stops_at_workload_ready()
    {
        var phase = AzureProviderOperationPhase.Planned;
        PulumiLedgerCheckpoint? last = null;
        foreach (var value in new[]
                 {
                     Event(ProvisioningEventKind.ResourceStarted, "resource-group", "Resource started."),
                     Event(ProvisioningEventKind.ResourceSucceeded, "resource-group", "Resource succeeded."),
                     Event(ProvisioningEventKind.ResourceSucceeded, ProvisioningCapability.ContainerRuntime, PulumiStepLedgerMapper.ApplyCompletedMessage),
                     Event(ProvisioningEventKind.OperationSucceeded, ProvisioningCapability.ContainerRuntime, "Operation succeeded.")
                 })
        {
            var mapped = PulumiStepLedgerMapper.TryMap(value, phase);
            Assert.NotNull(mapped);
            last = mapped;
            phase = mapped.Phase;
        }

        Assert.NotNull(last);
        Assert.False(last.Finalize);
        Assert.Equal(AzureProviderOperationStatus.Running, last.Status);
        Assert.Equal(AzureProviderOperationPhase.WorkloadReady, last.Phase);
        Assert.Equal(PulumiStepLedgerMapper.ApplyCompletedCode, last.Code);
    }

    [Fact]
    public void Control_cancel_still_finalizes_cancelled()
    {
        var mapped = PulumiStepLedgerMapper.TryMap(
            Event(ProvisioningEventKind.OperationCancelled, ProvisioningCapability.ContainerRuntime, "Operation cancelled."),
            AzureProviderOperationPhase.WorkloadSubmitted);
        Assert.NotNull(mapped);
        Assert.True(mapped.Finalize);
        Assert.Equal(AzureProviderOperationStatus.Cancelled, mapped.Status);
        Assert.Equal(ManagedElsaReasonCodeCatalog.AzureDeploymentCanceled, mapped.Code);
        Assert.Equal(PulumiStepLedgerMapper.UpdateCancelledMessage, mapped.Message);
        Assert.True(PulumiStepLedgerMapper.IsCustomerSafe(mapped));
    }

    [Fact]
    public void Operation_failed_with_no_resource_events_finalizes_failed()
    {
        var mapped = PulumiStepLedgerMapper.TryMap(
            Event(ProvisioningEventKind.OperationFailed, ProvisioningCapability.ContainerRuntime, "Operation failed."),
            AzureProviderOperationPhase.Planned);
        Assert.NotNull(mapped);
        Assert.True(mapped.Finalize);
        Assert.Equal(AzureProviderOperationStatus.Failed, mapped.Status);
        Assert.Equal(ManagedElsaReasonCodeCatalog.AzureDeploymentFailed, mapped.Code);
        Assert.Equal(PulumiStepLedgerMapper.ResourceFailedMessage, mapped.Message);
        Assert.True(PulumiStepLedgerMapper.IsCustomerSafe(mapped));
    }

    [Fact]
    public void Resource_group_absent_receipt_is_recognized()
    {
        Assert.True(PulumiStepLedgerMapper.IsResourceGroupAbsentReceipt(
            Event(
                ProvisioningEventKind.ResourceSucceeded,
                PulumiStepLedgerMapper.OwnedResourceGroupAbsentKind,
                PulumiStepLedgerMapper.ResourceGroupAbsentMessage)));
        Assert.False(PulumiStepLedgerMapper.IsResourceGroupAbsentReceipt(
            Event(ProvisioningEventKind.ResourceSucceeded, "resource-group", PulumiStepLedgerMapper.ResourceGroupAbsentMessage)));
        var checkpoint = PulumiStepLedgerMapper.ResourceGroupAbsent();
        Assert.True(checkpoint.ReplaceResources);
        Assert.Equal(new AzureProviderResourceReferences(), checkpoint.Resources);
        Assert.False(checkpoint.Finalize);
        Assert.True(PulumiStepLedgerMapper.IsCustomerSafe(checkpoint));
    }

    [Fact]
    public void Phase_regression_is_ignored()
    {
        var mapped = PulumiStepLedgerMapper.TryMap(
            Event(ProvisioningEventKind.ResourceSucceeded, "resource-group", "Resource succeeded."),
            AzureProviderOperationPhase.WorkloadReady);
        Assert.Null(mapped);
    }

    private static ProvisioningEvent Event(ProvisioningEventKind kind, string resourceKind, string message) =>
        new(Guid.NewGuid(), kind, resourceKind, message, DateTimeOffset.UtcNow, 1);

    private static AzureProviderOperation Provider(
        AzureProviderOperationPhase phase,
        AzureProviderRunnerStep? attemptedStep,
        AzureProviderOperationStatus status = AzureProviderOperationStatus.Running) =>
        new(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "provider-target-key",
            AzureProviderOperationAction.Reconcile,
            "provider-idempotency-key",
            "request-hash",
            "provider-operation-id",
            "plan-fingerprint",
            "template-fingerprint",
            "3.8.1",
            "3.8",
            "topology",
            "isolated",
            "westeurope",
            "provider/image",
            "sha256:provider-image",
            null,
            null,
            status,
            phase,
            1,
            1,
            1,
            new(),
            null,
            AzureProviderHealth.Unknown,
            [],
            "worker-id",
            DateTimeOffset.UtcNow.AddMinutes(1),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            InstanceId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            LifecycleAction: ElsaInstanceOperationAction.Create,
            AttemptedStep: attemptedStep,
            StatusChangedAt: DateTimeOffset.UtcNow);

    private static ElsaInstanceLifecycleTopologyOperation Lifecycle(
        ElsaInstanceOperationState state = ElsaInstanceOperationState.Running) =>
        new(
            Guid.NewGuid(),
            ElsaInstanceOperationAction.Create,
            state,
            1,
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            null,
            null,
            null);

    private static void AssertDoesNotLeak(ManagedElsaProvisioningProgress progress, params string[] forbidden)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(progress);
        foreach (var token in forbidden)
            Assert.DoesNotContain(token, json, StringComparison.OrdinalIgnoreCase);
    }
}
