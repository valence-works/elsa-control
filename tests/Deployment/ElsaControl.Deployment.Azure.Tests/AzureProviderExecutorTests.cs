using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureProviderExecutorTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Applies_the_checked_in_lifecycle_and_persists_safe_checkpoints()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { HostileDiagnostics = true, RunnerMessage = "provider returned an internal payload" };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.Succeeded, result.Operation.Status);
        Assert.Equal(AzureProviderOperationPhase.TrafficPromoted, result.Operation.Phase);
        Assert.Equal(AzureProviderHealth.Healthy, result.Operation.Health);
        Assert.Equal("https://workload.example.test", result.Operation.Endpoint);
        Assert.Equal(
            [
                AzureProviderRunnerStep.Foundation,
                AzureProviderRunnerStep.AcrPull,
                AzureProviderRunnerStep.SeedSecrets,
                AzureProviderRunnerStep.SqlFirewallCreate,
                AzureProviderRunnerStep.SqlBootstrapScript,
                AzureProviderRunnerStep.SqlFirewallCleanup,
                AzureProviderRunnerStep.Workload,
                AzureProviderRunnerStep.Health,
                AzureProviderRunnerStep.Promotion
            ],
            runner.Steps);
        Assert.All(result.Operation.Diagnostics, diagnostic => Assert.Equal(diagnostic.Code, diagnostic.Message));
        Assert.DoesNotContain(result.Operation.Diagnostics, diagnostic => diagnostic.Message.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("internal payload", result.Message, StringComparison.Ordinal);
        Assert.All(runner.Commands, command =>
        {
            Assert.Equal(WorkspaceId, command.Context.WorkspaceId);
            Assert.Equal(result.Operation.Id, command.Context.OperationId);
            Assert.Equal(result.Operation.OperationIdentity, command.Context.OperationIdentity);
            Assert.Equal("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", command.Context.ProviderAssignmentId);
            Assert.Equal("request-1", command.Context.IdempotencyKey);
            Assert.Equal("workload-a", command.Context.TargetKey);
            Assert.Equal(new string('a', 64), command.Context.PlanFingerprint);
            Assert.Equal(new string('b', 64), command.Context.TemplateFingerprint);
        });
    }

    [Fact]
    public async Task Entitlement_denial_holds_before_the_first_runner_call_and_resume_reuses_the_operation()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner();
        var gate = new ToggleCommercialGate();
        var executor = new AzureProviderExecutor(
            store,
            runner,
            new StaticTimeProvider(Now),
            TimeSpan.FromMinutes(5),
            commercialGate: gate);
        var request = CreateRequest() with
        {
            OrganizationId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            InstanceId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")
        };

        var held = await executor.ApplyAsync(request, CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.InProgress, held.Outcome);
        Assert.Equal(AzureProviderOperationStatus.EntitlementHeld, held.Operation.Status);
        Assert.Equal(ElsaInstanceCommercialOperation.LifecycleConstrained, held.Code);
        Assert.Empty(runner.Steps);

        gate.Allowed = true;
        var resumed = await executor.ApplyAsync(request, CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.Succeeded, resumed.Outcome);
        Assert.Equal(AzureProviderOperationStatus.Succeeded, resumed.Operation.Status);
        Assert.Equal(held.Operation.Id, resumed.Operation.Id);
        Assert.Equal(9, runner.Steps.Count);
    }

    [Fact]
    public async Task Entitlement_denial_cas_loss_returns_current_concurrent_winner_without_provider_call()
    {
        var store = new FakeOperationStore { ConcurrentWinnerStatus = AzureProviderOperationStatus.Succeeded };
        var runner = new RecordingRunner();
        var gate = new ToggleCommercialGate();
        var executor = new AzureProviderExecutor(
            store,
            runner,
            new StaticTimeProvider(Now),
            TimeSpan.FromMinutes(5),
            commercialGate: gate);

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderOperationStatus.Succeeded, result.Operation.Status);
        Assert.Equal(3, result.Operation.Version);
        Assert.Equal(AzureProviderExecutionOutcome.NoOp, result.Outcome);
        Assert.Equal("azure.operation.no-op", result.Code);
        Assert.Empty(runner.Steps);
    }

    [Fact]
    public async Task Legacy_unbound_reconcile_is_held_before_any_provider_call()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner();
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var request = CreateRequest() with
        {
            OrganizationId = null,
            InstanceId = null,
            LifecycleAction = null
        };

        var result = await executor.ApplyAsync(request, CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.InProgress, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.EntitlementHeld, result.Operation.Status);
        Assert.Equal(ElsaInstanceCommercialOperation.BindingRequired, result.Code);
        Assert.Empty(runner.Steps);
    }

    [Fact]
    public async Task Legacy_unbound_delete_is_held_before_any_provider_call()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner();
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now));
        var request = CreateRequest() with
        {
            Action = AzureProviderOperationAction.Delete,
            OrganizationId = null,
            InstanceId = null,
            LifecycleAction = null
        };

        var result = await executor.DeleteAsync(request, CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.InProgress, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.EntitlementHeld, result.Operation.Status);
        Assert.Equal(ElsaInstanceCommercialOperation.BindingRequired, result.Code);
        Assert.Empty(runner.Steps);
    }

    [Theory]
    [InlineData(AzureProviderOperationAction.Reconcile)]
    [InlineData(AzureProviderOperationAction.Delete)]
    public async Task Missing_assignment_binding_is_failed_durably_before_any_provider_call(
        AzureProviderOperationAction action)
    {
        var store = new FakeOperationStore { OmitProviderAssignmentId = true };
        var runner = new RecordingRunner();
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now));
        var request = CreateRequest(action) with
        {
            LifecycleAction = action == AzureProviderOperationAction.Delete
                ? ElsaInstanceOperationAction.Delete
                : ElsaInstanceOperationAction.Reconcile
        };

        var result = action == AzureProviderOperationAction.Delete
            ? await executor.DeleteAsync(request, CreatePlan())
            : await executor.ApplyAsync(request, CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.Failed, result.Operation.Status);
        Assert.Equal("azure.assignment.invalid", result.Code);
        Assert.Empty(runner.Steps);
        var transitions = await store.ListTransitionsAsync(WorkspaceId, result.Operation.Id);
        Assert.Contains(transitions, transition =>
            transition.Code == "azure.assignment.invalid" &&
            transition.Status == AzureProviderOperationStatus.Failed);
    }

    [Fact]
    public async Task Constrained_stop_reaches_the_provider_runner()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner();
        var gate = new ToggleCommercialGate();
        var executor = new AzureProviderExecutor(
            store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5), commercialGate: gate);
        var request = CreateRequest() with { LifecycleAction = ElsaInstanceOperationAction.Stop };

        var result = await executor.ApplyAsync(request, CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.Succeeded, result.Operation.Status);
        Assert.Equal(9, runner.Steps.Count);
    }

    [Theory]
    [InlineData(ElsaInstanceOperationAction.Start)]
    [InlineData(ElsaInstanceOperationAction.Reconcile)]
    public async Task Constrained_start_and_reconcile_are_held_before_the_runner(
        ElsaInstanceOperationAction lifecycleAction)
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner();
        var gate = new ToggleCommercialGate();
        var executor = new AzureProviderExecutor(
            store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5), commercialGate: gate);
        var request = CreateRequest() with { LifecycleAction = lifecycleAction };

        var result = await executor.ApplyAsync(request, CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.InProgress, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.EntitlementHeld, result.Operation.Status);
        Assert.Empty(runner.Steps);
    }

    [Fact]
    public async Task Reapplying_a_succeeded_operation_is_an_explicit_no_op()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner();
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var request = CreateRequest();
        var plan = CreatePlan();

        var first = await executor.ApplyAsync(request, plan);
        var second = await executor.ApplyAsync(request, plan);

        Assert.Equal(AzureProviderExecutionOutcome.Succeeded, first.Outcome);
        Assert.Equal(AzureProviderExecutionOutcome.NoOp, second.Outcome);
        Assert.Equal(first.Operation.Id, second.Operation.Id);
        Assert.Equal(9, runner.Steps.Count);
    }

    [Theory]
    [InlineData(AzureProviderRunnerStep.Foundation)]
    [InlineData(AzureProviderRunnerStep.AcrPull)]
    [InlineData(AzureProviderRunnerStep.SeedSecrets)]
    [InlineData(AzureProviderRunnerStep.SqlFirewallCreate)]
    [InlineData(AzureProviderRunnerStep.SqlBootstrapScript)]
    [InlineData(AzureProviderRunnerStep.SqlFirewallCleanup)]
    [InlineData(AzureProviderRunnerStep.Workload)]
    [InlineData(AzureProviderRunnerStep.Health)]
    [InlineData(AzureProviderRunnerStep.Promotion)]
    public async Task No_op_steps_must_return_the_complete_step_postcondition(AzureProviderRunnerStep step)
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { IncompleteNoOpStep = step };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(step, runner.Steps[^1]);
    }

    [Fact]
    public async Task Rejects_a_plan_for_a_different_reserved_target_before_claiming()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner();
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            executor.ApplyAsync(CreateRequest(), CreatePlan() with { WorkloadName = "workload-b" }));

        Assert.Empty(runner.Steps);
    }

    [Fact]
    public async Task Requires_a_durable_workload_resource_identity_before_succeeding()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner
        {
            ResourcesOverride = new AzureProviderResourceReferences(ResourceGroupName: "proof-rg")
        };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, result.Operation.Status);
    }

    [Fact]
    public async Task An_interrupted_remote_step_is_recovery_required_and_can_resume_idempotently()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { FailFoundationOnce = true };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var request = CreateRequest();
        var plan = CreatePlan();

        var interrupted = await executor.ApplyAsync(request, plan);
        var resumed = await executor.ApplyAsync(request, plan);

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, interrupted.Outcome);
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, interrupted.Operation.Status);
        Assert.Equal(AzureProviderExecutionOutcome.Succeeded, resumed.Outcome);
        Assert.Equal(AzureProviderOperationStatus.Succeeded, resumed.Operation.Status);
        Assert.Equal(10, runner.Steps.Count);
        Assert.Equal(AzureProviderRunnerStep.Foundation, runner.Steps[1]);
    }

    [Fact]
    public async Task Recovery_observation_claims_once_and_resumes_the_same_operation_after_foundation()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner
        {
            FoundationOutcome = AzureProviderRunnerOutcome.Uncertain,
            FoundationResourcesOverride = FoundationResources()
        };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var plan = CreatePlan();

        var interrupted = await executor.ApplyAsync(CreateRequest(), plan);
        var observed = new AzureProviderRecoveryObservation(
            AzureProviderRecoveryObservationKind.Confirmed,
            AzureProviderRunnerStep.Foundation,
            CompleteResourcesForRecovery(),
            AzureProviderHealth.Unknown,
            null,
            "azure.recovery.foundation-observed",
            "The retained foundation postcondition was observed.");

        var resumed = await executor.RecoverAsync(interrupted.Operation, plan, observed);

        Assert.Equal(AzureProviderExecutionOutcome.Succeeded, resumed.Outcome);
        Assert.Equal(AzureProviderOperationStatus.Succeeded, resumed.Operation.Status);
        Assert.Equal(1, store.RecoveryClaimCount);
        Assert.Equal(AzureProviderRunnerStep.Foundation, runner.Steps[0]);
        Assert.DoesNotContain(AzureProviderRunnerStep.Foundation, runner.Steps.Skip(1));
        Assert.Contains(AzureProviderRunnerStep.AcrPull, runner.Steps);
        Assert.Contains(AzureProviderRunnerStep.SeedSecrets, runner.Steps);
        Assert.Contains(AzureProviderRunnerStep.SqlFirewallCreate, runner.Steps);
        Assert.Contains(AzureProviderRunnerStep.SqlBootstrapScript, runner.Steps);
        Assert.Contains(AzureProviderRunnerStep.SqlFirewallCleanup, runner.Steps);
        Assert.Contains(AzureProviderRunnerStep.Health, runner.Steps);
        Assert.Contains(AzureProviderRunnerStep.Promotion, runner.Steps);
    }

    [Theory]
    [InlineData("The Azure provider assignment binding is invalid.")]
    [InlineData("Checkpoint phase cannot move backwards.")]
    public async Task Injected_checkpoint_validation_exception_is_durably_recoverable_without_a_second_runner_call(
        string injectedExceptionMessage)
    {
        var scenario = await CreateCheckpointFailureRecoveryScenarioAsync();
        scenario.Store.ThrowCheckpointExceptionMessage = injectedExceptionMessage;

        var resumed = await scenario.Executor.RecoverAsync(scenario.Operation, scenario.Plan, scenario.Observation);

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, resumed.Outcome);
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, resumed.Operation.Status);
        Assert.Equal("azure.recovery.checkpoint-uncertain", resumed.Code);
        Assert.Equal([AzureProviderRunnerStep.Foundation], scenario.Runner.Steps);
        Assert.DoesNotContain(injectedExceptionMessage, resumed.Message, StringComparison.Ordinal);
        Assert.Contains(await scenario.Store.ListTransitionsAsync(WorkspaceId, resumed.Operation.Id), transition =>
            transition.Code == "azure.recovery.checkpoint-uncertain" &&
            transition.Status == AzureProviderOperationStatus.RecoveryRequired);
    }

    [Fact]
    public async Task Recovery_checkpoint_fallback_does_not_mask_a_finalize_store_failure()
    {
        var scenario = await CreateCheckpointFailureRecoveryScenarioAsync();
        scenario.Store.ThrowCheckpointExceptionMessage = "The Azure provider assignment binding is invalid.";
        scenario.Store.ThrowFinalizeExceptionMessage = "durable store unavailable";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.Executor.RecoverAsync(scenario.Operation, scenario.Plan, scenario.Observation));

        var latest = await scenario.Store.GetAsync(WorkspaceId, scenario.Operation.Id);
        Assert.Equal(AzureProviderOperationStatus.Running, latest!.Status);
        Assert.Equal([AzureProviderRunnerStep.Foundation], scenario.Runner.Steps);
    }

    [Fact]
    public async Task Recovery_checkpoint_fallback_reports_the_concurrent_winner_instead_of_fabricating_recovery()
    {
        var scenario = await CreateCheckpointFailureRecoveryScenarioAsync();
        scenario.Store.ThrowCheckpointExceptionMessage = "Checkpoint phase cannot move backwards.";
        scenario.Store.ConcurrentWinnerStatus = AzureProviderOperationStatus.Succeeded;

        var result = await scenario.Executor.RecoverAsync(scenario.Operation, scenario.Plan, scenario.Observation);

        Assert.Equal(AzureProviderExecutionOutcome.NoOp, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.Succeeded, result.Operation.Status);
        Assert.Equal("azure.operation.no-op", result.Code);
        Assert.Equal([AzureProviderRunnerStep.Foundation], scenario.Runner.Steps);
    }

    [Fact]
    public async Task Sql_bootstrap_observation_is_not_claimed_without_a_proven_sql_postcondition()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { FoundationOutcome = AzureProviderRunnerOutcome.Uncertain };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var interrupted = await executor.ApplyAsync(CreateRequest(), CreatePlan());
        var sqlOperation = interrupted.Operation with
        {
            AttemptedStep = AzureProviderRunnerStep.SqlBootstrap,
            Phase = AzureProviderOperationPhase.FoundationReady
        };
        store.Replace(sqlOperation);
        var observed = new AzureProviderRecoveryObservation(
            AzureProviderRecoveryObservationKind.Confirmed,
            AzureProviderRunnerStep.SqlBootstrap,
            CompleteResourcesForRecovery(),
            AzureProviderHealth.Unknown,
            null,
            "azure.recovery.sql-observed",
            "The retained SQL postcondition was observed.");

        var resumed = await executor.RecoverAsync(sqlOperation, CreatePlan(), observed);

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, resumed.Outcome);
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, resumed.Operation.Status);
        Assert.Equal(0, store.RecoveryClaimCount);
        Assert.Single(runner.Steps);
    }

    [Fact]
    public async Task Sql_bootstrap_script_observation_only_resumes_pending_firewall_cleanup()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner
        {
            FoundationOutcome = AzureProviderRunnerOutcome.Uncertain,
            FoundationResourcesOverride = FoundationResources()
        };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var plan = CreatePlan();
        var interrupted = await executor.ApplyAsync(CreateRequest(), plan);
        var pendingCleanup = interrupted.Operation with
        {
            Resources = SqlResourcesForRecovery(),
            AttemptedStep = AzureProviderRunnerStep.SqlFirewallCleanup,
            Phase = AzureProviderOperationPhase.SqlBootstrapReady
        };
        store.Replace(pendingCleanup);
        var observed = new AzureProviderRecoveryObservation(
            AzureProviderRecoveryObservationKind.Confirmed,
            AzureProviderRunnerStep.SqlBootstrapScript,
            SqlResourcesForRecovery(),
            AzureProviderHealth.Unknown,
            null,
            "azure.recovery.sql-bootstrap-observed",
            "The retained SQL bootstrap postcondition was observed.");

        var previousCallCount = runner.Steps.Count;
        var resumed = await executor.RecoverAsync(pendingCleanup, plan, observed);
        var resumedSteps = runner.Steps.Skip(previousCallCount).ToArray();

        Assert.Equal(AzureProviderExecutionOutcome.Succeeded, resumed.Outcome);
        Assert.Equal(AzureProviderRunnerStep.SqlFirewallCleanup, resumedSteps[0]);
        Assert.DoesNotContain(AzureProviderRunnerStep.SqlBootstrapScript, resumedSteps);
        Assert.DoesNotContain(AzureProviderRunnerStep.SqlFirewallCreate, resumedSteps);
    }

    [Theory]
    [InlineData(AzureProviderRunnerStep.SeedSecrets, AzureProviderOperationPhase.SeedSecretsObserved)]
    [InlineData(AzureProviderRunnerStep.Workload, AzureProviderOperationPhase.WorkloadReady)]
    [InlineData(AzureProviderRunnerStep.Health, AzureProviderOperationPhase.HealthVerified)]
    [InlineData(AzureProviderRunnerStep.Promotion, AzureProviderOperationPhase.TrafficPromoted)]
    public async Task Recovery_rejects_observations_for_non_recoverable_later_steps_before_claim(
        AzureProviderRunnerStep unsupportedStep,
        AzureProviderOperationPhase observedPhase)
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { FoundationOutcome = AzureProviderRunnerOutcome.Uncertain };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var interrupted = await executor.ApplyAsync(CreateRequest(), CreatePlan());
        var operation = interrupted.Operation with
        {
            AttemptedStep = unsupportedStep,
            Phase = observedPhase,
            Resources = CompleteResourcesForRecovery()
        };
        store.Replace(operation);
        var observation = new AzureProviderRecoveryObservation(
            AzureProviderRecoveryObservationKind.Confirmed,
            unsupportedStep,
            CompleteResourcesForRecovery(),
            AzureProviderHealth.Unknown,
            null,
            "azure.recovery.unsupported-step",
            "The retained provider checkpoint is not recoverable by this boundary.");

        var result = await executor.RecoverAsync(operation, CreatePlan(), observation);

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(0, store.RecoveryClaimCount);
        Assert.Single(runner.Steps);
    }

    [Fact]
    public async Task Foundation_observation_is_rejected_before_claim_when_a_later_handle_is_retained()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { FoundationOutcome = AzureProviderRunnerOutcome.Uncertain };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var interrupted = await executor.ApplyAsync(CreateRequest(), CreatePlan());
        var laterOperation = interrupted.Operation with
        {
            Resources = CompleteResourcesForRecovery(),
            AttemptedStep = AzureProviderRunnerStep.SeedSecrets
        };
        store.Replace(laterOperation);
        var observed = new AzureProviderRecoveryObservation(
            AzureProviderRecoveryObservationKind.Confirmed,
            AzureProviderRunnerStep.Foundation,
            CompleteResourcesForRecovery(),
            AzureProviderHealth.Unknown,
            null,
            "azure.recovery.foundation-observed",
            "The retained foundation postcondition was observed.");

        var resumed = await executor.RecoverAsync(laterOperation, CreatePlan(), observed);

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, resumed.Outcome);
        Assert.Equal(0, store.RecoveryClaimCount);
        Assert.Single(runner.Steps);
    }

    [Fact]
    public async Task Cancellation_after_a_completed_remote_step_is_durably_recoverable()
    {
        var store = new FakeOperationStore();
        using var cancellation = new CancellationTokenSource();
        var runner = new RecordingRunner
        {
            CancelSource = cancellation,
            CancelAfterStep = AzureProviderRunnerStep.Foundation
        };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan(), cancellation.Token);

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(AzureProviderOperationPhase.FoundationSubmitted, result.Operation.Phase);
        Assert.Single(runner.Steps, AzureProviderRunnerStep.Foundation);
    }

    [Fact]
    public async Task Failed_remote_results_retain_provider_resource_references()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { FoundationOutcome = AzureProviderRunnerOutcome.Uncertain };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal("proof-rg", result.Operation.Resources.ResourceGroupName);
        Assert.EndsWith("/providers/Microsoft.Resources/deployments/foundation", result.Operation.Resources.FoundationDeploymentId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_or_uncertain_promotion_restores_the_prior_stable_revision()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { PromotionOutcome = AzureProviderRunnerOutcome.Uncertain };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, result.Operation.Status);
        Assert.Equal(
            [
                AzureProviderRunnerStep.Foundation,
                AzureProviderRunnerStep.AcrPull,
                AzureProviderRunnerStep.SeedSecrets,
                AzureProviderRunnerStep.SqlFirewallCreate,
                AzureProviderRunnerStep.SqlBootstrapScript,
                AzureProviderRunnerStep.SqlFirewallCleanup,
                AzureProviderRunnerStep.Workload,
                AzureProviderRunnerStep.Health,
                AzureProviderRunnerStep.Promotion,
                AzureProviderRunnerStep.RestoreStableTraffic
            ],
            runner.Steps);
        Assert.Equal("stable-revision", runner.Commands[^1].StableTrafficRevisionName);
        Assert.Equal(AzureProviderOperationPhase.HealthVerified, result.Operation.Phase);
    }

    [Fact]
    public async Task Failed_promotion_without_observations_preserves_last_known_endpoint_and_health()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner
        {
            PromotionOutcome = AzureProviderRunnerOutcome.Failed,
            StableTrafficRevisionName = null,
            OmitPromotionObservations = true
        };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal("https://workload.example.test", result.Operation.Endpoint);
        Assert.Equal(AzureProviderHealth.Healthy, result.Operation.Health);
    }

    [Fact]
    public async Task Promotion_rollback_without_a_traffic_postcondition_stays_in_recovery()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner
        {
            PromotionOutcome = AzureProviderRunnerOutcome.Failed,
            StableTrafficRestored = false
        };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(AzureProviderOperationPhase.HealthVerified, result.Operation.Phase);
        Assert.Equal(AzureProviderRunnerStep.RestoreStableTraffic, runner.Steps[^1]);
    }

    [Fact]
    public async Task Unhealthy_candidate_never_reaches_promotion()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { Health = AzureProviderHealth.Degraded };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.Failed, result.Operation.Status);
        Assert.Equal("proof-rg", result.Operation.Resources.ResourceGroupName);
        Assert.Equal(AzureProviderHealth.Degraded, result.Operation.Health);
        Assert.DoesNotContain(AzureProviderRunnerStep.Promotion, runner.Steps);
    }

    [Fact]
    public async Task Promotion_failure_without_a_stable_revision_stays_in_recovery()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { PromotionOutcome = AzureProviderRunnerOutcome.Failed, StableTrafficRevisionName = null };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.DoesNotContain(AzureProviderRunnerStep.RestoreStableTraffic, runner.Steps);
    }

    [Theory]
    [InlineData("other.azurecr.io/runtime-combined")]
    [InlineData("valenceruntimeimages.azurecr.io/other-runtime")]
    public async Task Execution_rejects_a_plan_outside_the_governed_repository(string repository)
    {
        var store = new FakeOperationStore();
        var executor = new AzureProviderExecutor(store, new RecordingRunner(), new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var request = CreateRequest() with { ImageRepository = repository };
        var plan = CreatePlan() with { ImageRepository = request.ImageRepository };

        await Assert.ThrowsAsync<ArgumentException>(() => executor.ApplyAsync(request, plan));
    }

    [Fact]
    public async Task Hostile_runner_message_is_not_returned_or_persisted()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { RunnerMessage = "password=do-not-persist\r\nraw provider response" };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());
        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Operation);

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.DoesNotContain("do-not-persist", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-persist", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Untrusted_runner_diagnostics_are_dropped_before_checkpoint_persistence()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { UntrustedDiagnostics = true };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.Succeeded, result.Outcome);
        Assert.DoesNotContain(result.Operation.Diagnostics, diagnostic => diagnostic.Code == "azure.provider.detail");
        Assert.All(result.Operation.Diagnostics, diagnostic => Assert.Equal(diagnostic.Code, diagnostic.Message));
    }

    [Fact]
    public async Task Read_only_step_failure_retains_only_fixed_diagnostics_in_the_durable_checkpoint()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner
        {
            FailureStep = AzureProviderRunnerStep.AcrPull,
            FailureCode = "azure.provider.detail",
            FailureMessage = "untrusted runner detail",
            FailureDiagnostics =
            [
                new AzureProviderDiagnostic("azure.step.acr-pull.process.non-zero-exit", "untrusted process output"),
                new AzureProviderDiagnostic("azure.provider.detail", "untrusted provider detail")
            ]
        };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.Failed, result.Operation.Status);
        Assert.Contains(result.Operation.Diagnostics, diagnostic => diagnostic.Code == "azure.step.acr-pull.process.non-zero-exit");
        Assert.DoesNotContain(result.Operation.Diagnostics, diagnostic => diagnostic.Code == "azure.provider.detail");
        Assert.All(result.Operation.Diagnostics, diagnostic => Assert.Equal(diagnostic.Code, diagnostic.Message));
    }

    [Fact]
    public async Task Execution_requires_both_verified_manifest_digests()
    {
        var store = new FakeOperationStore();
        var executor = new AzureProviderExecutor(store, new RecordingRunner(), new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        await Assert.ThrowsAsync<ArgumentException>(() => executor.ApplyAsync(CreateRequest(), CreatePlan() with { ReleaseManifestDigest = null! }));
    }

    [Fact]
    public async Task Execution_rejects_plan_evidence_references_that_do_not_match_the_operation()
    {
        var store = new FakeOperationStore();
        var executor = new AzureProviderExecutor(store, new RecordingRunner(), new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var plan = CreatePlan() with
        {
            ReleaseManifestReference = "oci://different.example/manifest",
            ReleaseManifestSignatureReference = "oci://different.example/signature"
        };

        await Assert.ThrowsAsync<ArgumentException>(() => executor.ApplyAsync(CreateRequest(), plan));
    }

    [Fact]
    public async Task Execution_rejects_plan_secret_references_that_do_not_match_the_operation()
    {
        var store = new FakeOperationStore();
        var executor = new AzureProviderExecutor(store, new RecordingRunner(), new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var plan = CreatePlan() with
        {
            SecretReferences = new Dictionary<string, string>
            {
                ["database:connectionstring"] = "secret://different-vault/database"
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => executor.ApplyAsync(CreateRequest(), plan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execution_rejects_plan_capacity_that_does_not_match_the_operation(bool operationRetainsCapacity)
    {
        var store = new FakeOperationStore();
        var executor = new AzureProviderExecutor(store, new RecordingRunner(), new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var retained = new AzureWorkloadCapacity(1, 1, 500, 1024);
        var request = CreateRequest() with { Capacity = operationRetainsCapacity ? retained : null };
        var plan = CreatePlan() with { Capacity = operationRetainsCapacity ? retained with { MaxReplicas = 3 } : retained };

        await Assert.ThrowsAsync<ArgumentException>(() => executor.ApplyAsync(request, plan));
    }

    [Fact]
    public async Task Execution_rejects_noncanonical_plan_secret_reference_keys()
    {
        var store = new FakeOperationStore();
        var executor = new AzureProviderExecutor(store, new RecordingRunner(), new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var plan = CreatePlan() with
        {
            SecretReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Database:ConnectionString"] = "secret://database"
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => executor.ApplyAsync(CreateRequest(), plan));
    }

    [Fact]
    public async Task Renews_the_durable_lease_while_a_remote_step_is_running()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { Delay = TimeSpan.FromMilliseconds(30) };
        var executor = new AzureProviderExecutor(
            store,
            runner,
            new StaticTimeProvider(Now),
            TimeSpan.FromMilliseconds(100),
            heartbeatInterval: TimeSpan.FromMilliseconds(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.Succeeded, result.Outcome);
        Assert.True(store.HeartbeatCount > 0);
    }

    [Fact]
    public async Task Persists_recovery_using_the_latest_version_after_a_heartbeated_failure()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { Delay = TimeSpan.FromMilliseconds(30), ThrowAfterDelay = true };
        var executor = new AzureProviderExecutor(
            store,
            runner,
            new StaticTimeProvider(Now),
            TimeSpan.FromMilliseconds(100),
            heartbeatInterval: TimeSpan.FromMilliseconds(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, result.Operation.Status);
        Assert.True(store.HeartbeatCount > 0);
    }

    [Theory]
    [InlineData(AzureProviderOperationStatus.Failed, AzureProviderExecutionOutcome.Failed)]
    [InlineData(AzureProviderOperationStatus.Cancelled, AzureProviderExecutionOutcome.Failed)]
    [InlineData(AzureProviderOperationStatus.RecoveryRequired, AzureProviderExecutionOutcome.RecoveryRequired)]
    public async Task Claim_race_reports_the_observed_operation_state(
        AzureProviderOperationStatus observedStatus,
        AzureProviderExecutionOutcome expectedOutcome)
    {
        var store = new FakeOperationStore { RejectClaimWithStatus = observedStatus };
        var executor = new AzureProviderExecutor(store, new RecordingRunner(), new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(observedStatus, result.Operation.Status);
    }

    [Fact]
    public async Task Target_conflict_is_reported_without_invoking_the_runner()
    {
        var store = new FakeOperationStore { RejectCreateWithStatus = AzureProviderOperationStatus.Running };
        var runner = new RecordingRunner();
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.InProgress, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.Running, result.Operation.Status);
        Assert.Empty(runner.Steps);
    }

    [Fact]
    public async Task Checkpoint_race_reports_recovery_required_instead_of_in_progress()
    {
        var store = new FakeOperationStore { RejectCheckpointWithStatus = AzureProviderOperationStatus.RecoveryRequired };
        var executor = new AzureProviderExecutor(store, new RecordingRunner(), new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, result.Operation.Status);
    }

    [Fact]
    public async Task Lease_loss_signals_cancellation_to_the_still_running_runner()
    {
        var store = new FakeOperationStore { LoseLeaseOnHeartbeat = true };
        var runner = new RecordingRunner { WaitForCancellationStep = AzureProviderRunnerStep.Foundation };
        var executor = new AzureProviderExecutor(
            store,
            runner,
            new StaticTimeProvider(Now),
            TimeSpan.FromMilliseconds(100),
            heartbeatInterval: TimeSpan.FromMilliseconds(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        await runner.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(AzureProviderExecutionOutcome.InProgress, result.Outcome);
        Assert.True(runner.CancellationObserved.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Cancellation_after_a_heartbeat_persists_recovery_using_the_latest_version()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { Delay = TimeSpan.FromMilliseconds(100) };
        var executor = new AzureProviderExecutor(
            store,
            runner,
            new StaticTimeProvider(Now),
            TimeSpan.FromMilliseconds(100),
            heartbeatInterval: TimeSpan.FromMilliseconds(5));
        using var cancellation = new CancellationTokenSource();

        var execution = executor.ApplyAsync(CreateRequest(), CreatePlan(), cancellation.Token);
        await runner.Started.Task;
        for (var attempt = 0; store.HeartbeatCount == 0 && attempt < 100; attempt++)
            await Task.Delay(2);
        Assert.True(store.HeartbeatCount > 0);
        cancellation.Cancel();
        var result = await execution;

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, result.Operation.Status);
        Assert.True(result.Operation.Version > 2);
    }

    [Fact]
    public async Task Cancellation_does_not_wait_for_a_non_cooperative_runner()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { NonCooperativeStep = AzureProviderRunnerStep.Foundation };
        var executor = new AzureProviderExecutor(
            store,
            runner,
            new StaticTimeProvider(Now),
            TimeSpan.FromMilliseconds(100),
            heartbeatInterval: TimeSpan.FromMilliseconds(5));
        using var cancellation = new CancellationTokenSource();

        var execution = executor.ApplyAsync(CreateRequest(), CreatePlan(), cancellation.Token);
        await runner.Started.Task;
        cancellation.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, result.Operation.Status);
    }

    [Fact]
    public async Task Unsafe_runner_endpoint_fails_closed_without_leaking_runner_text()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner
        {
            EndpointOverride = "https://user:password@workload.example.test/secret"
        };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());
        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Operation);

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.DoesNotContain("password", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unsafe_runner_resource_reference_fails_closed_without_leaking_runner_text()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner
        {
            ResourcesOverride = new(ResourceGroupName: "proof-rg", WorkloadResourceId: "secret://provider-response")
        };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());
        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Operation);

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.DoesNotContain("provider-response", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-response", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_requires_the_runner_to_prove_exact_owned_resource_absence()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { CleanupResources = new(ResourceGroupName: "still-present") };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.DeleteAsync(CreateRequest(AzureProviderOperationAction.Delete), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(AzureProviderOperationStatus.Failed, result.Operation.Status);
        Assert.Equal(AzureProviderRunnerStep.Cleanup, runner.Steps[^1]);
        Assert.Equal(AzureProviderOperationPhase.CleanupSubmitted, result.Operation.Phase);
    }

    [Fact]
    public async Task Delete_reuses_the_latest_reconcile_resource_snapshot()
    {
        var store = new FakeOperationStore
        {
            LatestReconcileResources = new(
                ResourceGroupName: "proof-rg",
                FoundationDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.Resources/deployments/foundation",
                WorkloadDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.Resources/deployments/workload",
                WorkloadResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.App/containerApps/app",
                WorkloadRevisionName: "app--candidate",
                StableTrafficRevisionName: "stable-revision")
        };
        var runner = new RecordingRunner { CleanupResources = new(ResourceGroupName: "still-present") };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        await executor.DeleteAsync(CreateRequest(AzureProviderOperationAction.Delete), CreatePlan());

        Assert.Equal(store.LatestReconcileResources, runner.Commands.Single().Resources);
    }

    [Theory]
    [InlineData(AzureProviderAssignmentState.Active)]
    [InlineData(AzureProviderAssignmentState.Unknown)]
    [InlineData(AzureProviderAssignmentState.Deleting)]
    public async Task Bound_delete_uses_assignment_resources_instead_of_latest_reconcile_history(AzureProviderAssignmentState state)
    {
        var historical = new AzureProviderResourceReferences(ResourceGroupName: "historical-rg");
        var assigned = new AzureProviderResourceReferences(
            ResourceGroupName: "assigned-rg",
            WorkloadResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/assigned-rg/providers/Microsoft.App/containerApps/app");
        var store = new FakeOperationStore();
        var request = CreateRequest(AzureProviderOperationAction.Delete) with
        {
            LifecycleAction = ElsaInstanceOperationAction.Delete
        };
        var runner = new RecordingRunner { CleanupResources = new(ResourceGroupName: "still-present") };
        var assignmentStore = new FixedAssignmentStore(assigned, request.ProviderScopeFingerprint!, state);
        var commercialGate = new ToggleCommercialGate { Allowed = true };
        var executor = new AzureProviderExecutor(
            store,
            runner,
            new StaticTimeProvider(Now),
            TimeSpan.FromMinutes(5),
            commercialGate: commercialGate,
            assignmentStore: assignmentStore);

        var result = await executor.DeleteAsync(request, CreatePlan());

        Assert.True(runner.Commands.Count > 0, $"{result.Code}: {result.Message}");
        Assert.Equal(assigned, runner.Commands.Single().Resources);
        Assert.NotEqual(historical, runner.Commands.Single().Resources);
    }

    [Fact]
    public async Task Delete_happy_path_is_idempotent()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { CleanupResources = new() };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var request = CreateRequest(AzureProviderOperationAction.Delete);
        var plan = CreatePlan();

        var first = await executor.DeleteAsync(request, plan);
        var second = await executor.DeleteAsync(request, plan);

        Assert.Equal(AzureProviderExecutionOutcome.Succeeded, first.Outcome);
        Assert.Equal(AzureProviderExecutionOutcome.NoOp, second.Outcome);
        Assert.Equal(new AzureProviderResourceReferences(), first.Operation.Resources);
        Assert.Single(runner.Steps, AzureProviderRunnerStep.Cleanup);
    }

    [Fact]
    public async Task Accepted_delete_recovery_rejects_a_plan_mismatch_before_claim_or_runner_call()
    {
        var fixture = await CreateDeleteRecoveryFixtureAsync();
        var runner = new RecordingRunner();
        var executor = new AzureProviderExecutor(fixture.Store, runner, new StaticTimeProvider(Now));

        var result = await executor.RecoverDeleteAsync(
            fixture.Request,
            fixture.Plan with { ImageDigest = new('f', 64) });

        Assert.Null(result);
        Assert.Equal(0, fixture.Store.DeleteRecoveryClaimCount);
        Assert.Empty(runner.Steps);
    }

    [Fact]
    public async Task Accepted_delete_recovery_replays_the_exact_running_successor_without_reclaiming()
    {
        var fixture = await CreateDeleteRecoveryFixtureAsync();
        fixture.Store.Replace(fixture.Operation with
        {
            Status = AzureProviderOperationStatus.Running,
            AttemptNumber = fixture.Authority.ProviderAttemptNumber + 1,
            Version = fixture.Authority.ProviderVersion + 1,
            WorkerId = "other-delete-worker",
            LeaseExpiresAt = Now.AddMinutes(5),
            HeartbeatAt = Now,
            Phase = AzureProviderOperationPhase.CleanupSubmitted,
            AttemptedStep = AzureProviderRunnerStep.Cleanup
        });
        var runner = new RecordingRunner();
        var executor = new AzureProviderExecutor(fixture.Store, runner, new StaticTimeProvider(Now));

        var result = await executor.RecoverDeleteAsync(fixture.Request, fixture.Plan);

        Assert.NotNull(result);
        Assert.Equal(AzureProviderExecutionOutcome.InProgress, result!.Outcome);
        Assert.Equal(AzureProviderOperationStatus.Running, result.Operation.Status);
        Assert.Equal(0, fixture.Store.DeleteRecoveryClaimCount);
        Assert.Empty(runner.Steps);
    }

    [Fact]
    public async Task Accepted_delete_recovery_rejects_invalid_retained_metadata_before_claim()
    {
        var fixture = await CreateDeleteRecoveryFixtureAsync();
        fixture.Store.Replace(fixture.Operation with { PersistedMetadataInvalid = true });
        var runner = new RecordingRunner();
        var executor = new AzureProviderExecutor(fixture.Store, runner, new StaticTimeProvider(Now));

        Assert.Null(await executor.RecoverDeleteAsync(fixture.Request, fixture.Plan));
        Assert.Equal(0, fixture.Store.DeleteRecoveryClaimCount);
        Assert.Empty(runner.Steps);
    }

    [Fact]
    public async Task Accepted_delete_recovery_does_not_reclaim_a_new_uncertainty()
    {
        var fixture = await CreateDeleteRecoveryFixtureAsync();
        fixture.Store.Replace(fixture.Operation with
        {
            AttemptNumber = fixture.Authority.ProviderAttemptNumber + 1,
            Version = fixture.Authority.ProviderVersion + 2
        });
        var runner = new RecordingRunner();
        var executor = new AzureProviderExecutor(fixture.Store, runner, new StaticTimeProvider(Now));

        var result = await executor.RecoverDeleteAsync(fixture.Request, fixture.Plan);

        Assert.NotNull(result);
        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(0, fixture.Store.DeleteRecoveryClaimCount);
        Assert.Empty(runner.Steps);
    }

    [Fact]
    public async Task Delete_without_owned_resources_absence_proof_fails_closed_even_without_a_snapshot()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner
        {
            CleanupResources = new(),
            OwnedResourcesAbsentOverride = false
        };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));

        var result = await executor.DeleteAsync(CreateRequest(AzureProviderOperationAction.Delete), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.Failed, result.Outcome);
        Assert.Equal("azure.cleanup.ownership.unverified", result.Code);
        Assert.Equal(AzureProviderOperationStatus.Failed, result.Operation.Status);
    }

    [Fact]
    public async Task Interrupted_cleanup_enters_recovery_and_resumes()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { CleanupResources = new(), FailCleanupOnce = true };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var request = CreateRequest(AzureProviderOperationAction.Delete);
        var plan = CreatePlan();

        var interrupted = await executor.DeleteAsync(request, plan);
        var resumed = await executor.DeleteAsync(request, plan);

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, interrupted.Outcome);
        Assert.Equal(AzureProviderExecutionOutcome.Succeeded, resumed.Outcome);
        Assert.Equal([AzureProviderRunnerStep.Cleanup, AzureProviderRunnerStep.Cleanup], runner.Steps);
    }

    [Fact]
    public async Task Cleanup_renews_the_durable_lease_while_running()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner { CleanupResources = new(), Delay = TimeSpan.FromMilliseconds(30) };
        var executor = new AzureProviderExecutor(
            store,
            runner,
            new StaticTimeProvider(Now),
            TimeSpan.FromMilliseconds(100),
            heartbeatInterval: TimeSpan.FromMilliseconds(5));

        var result = await executor.DeleteAsync(CreateRequest(AzureProviderOperationAction.Delete), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.Succeeded, result.Outcome);
        Assert.True(store.HeartbeatCount > 0);
    }

    private static async Task<(
        FakeOperationStore Store,
        RecordingRunner Runner,
        AzureProviderExecutor Executor,
        AzureWorkloadPlan Plan,
        AzureProviderOperation Operation,
        AzureProviderRecoveryObservation Observation)> CreateCheckpointFailureRecoveryScenarioAsync()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner
        {
            FoundationOutcome = AzureProviderRunnerOutcome.Uncertain,
            FoundationResourcesOverride = FoundationResources()
        };
        var executor = new AzureProviderExecutor(store, runner, new StaticTimeProvider(Now), TimeSpan.FromMinutes(5));
        var plan = CreatePlan();
        var interrupted = await executor.ApplyAsync(CreateRequest(), plan);
        var observation = new AzureProviderRecoveryObservation(
            AzureProviderRecoveryObservationKind.Confirmed,
            AzureProviderRunnerStep.Foundation,
            CompleteResourcesForRecovery(),
            AzureProviderHealth.Unknown,
            null,
            "azure.recovery.foundation-observed",
            "The retained foundation postcondition was observed.");

        return (store, runner, executor, plan, interrupted.Operation, observation);
    }

    [Fact]
    public async Task Stable_traffic_restoration_renews_the_lease_while_running()
    {
        var store = new FakeOperationStore();
        var runner = new RecordingRunner
        {
            PromotionOutcome = AzureProviderRunnerOutcome.Uncertain,
            Delay = TimeSpan.FromMilliseconds(30),
            DelayOnlyStep = AzureProviderRunnerStep.RestoreStableTraffic
        };
        var executor = new AzureProviderExecutor(
            store,
            runner,
            new StaticTimeProvider(Now),
            TimeSpan.FromMilliseconds(100),
            heartbeatInterval: TimeSpan.FromMilliseconds(5));

        var result = await executor.ApplyAsync(CreateRequest(), CreatePlan());

        Assert.Equal(AzureProviderExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.True(store.HeartbeatCount > 0);
        Assert.Equal(AzureProviderRunnerStep.RestoreStableTraffic, runner.Steps[^1]);
    }

    private static AzureProviderOperationRequest CreateRequest(AzureProviderOperationAction action = AzureProviderOperationAction.Reconcile) => new(
        WorkspaceId,
        "workload-a",
        action,
        "request-1",
        new('a', 64),
        new('b', 64),
        "3.8.0-preview.5413",
        "3.8",
        "combined",
        "Dedicated",
        "westeurope",
        "valenceruntimeimages.azurecr.io/runtime-combined",
        "sha256:" + new string('c', 64),
        "sha256:" + new string('d', 64),
        "sha256:" + new string('e', 64),
        "oci://release-manifest.example/manifest",
        "oci://release-manifest.example/signature",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["database:connectionstring"] = "secret://database"
        },
        null,
        "3.8.0-preview.5413",
        "3.8.0-preview.5413",
        Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
        ElsaInstanceOperationAction.Reconcile,
        Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"));

    private static async Task<DeleteRecoveryFixture> CreateDeleteRecoveryFixtureAsync()
    {
        var lifecycleOperationId = Guid.Parse("12121212-1212-1212-1212-121212121212");
        var assignmentId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var instanceId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var organizationId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var plan = CreatePlan();
        var store = new FakeOperationStore();
        var request = AzureProviderOperationService.CreateOperationRequest(
            WorkspaceId,
            $"elsa-instance-operation:{lifecycleOperationId:D}:delete",
            new('b', 64),
            plan,
            AzureProviderOperationAction.Delete,
            new('a', 64),
            organizationId,
            instanceId,
            ElsaInstanceOperationAction.Delete,
            assignmentId);
        var operation = await store.CreateOrGetAsync(request, Now);
        operation = operation with
        {
            Status = AzureProviderOperationStatus.RecoveryRequired,
            Phase = AzureProviderOperationPhase.CleanupSubmitted,
            CheckpointSequence = 1,
            AttemptNumber = 1,
            Version = 2,
            Resources = new AzureProviderResourceReferences(ResourceGroupName: "proof-rg"),
            AttemptedStep = AzureProviderRunnerStep.Cleanup
        };
        store.Replace(operation);
        var authority = new AzureProviderDeleteRecoveryAuthority(
            operation.Id,
            assignmentId,
            LifecycleAttemptNumber: 2,
            InstanceVersion: 1,
            operation.AttemptNumber,
            operation.Version,
            operation.CheckpointSequence,
            operation.OperationIdentity,
            operation.RequestHash,
            operation.TargetKey,
            operation.ProviderScopeFingerprint!,
            operation.PlanFingerprint,
            operation.TemplateFingerprint);
        authority.Validate();
        store.DeleteRecoveryAuthority = authority;
        return new(
            store,
            plan,
            operation,
            authority,
            new(
                Guid.Parse("13131313-1313-1313-1313-131313131313"),
                WorkspaceId,
                instanceId,
                lifecycleOperationId,
                2,
                1,
                "delete-worker",
                "delete-lease-token",
                1));
    }

    private sealed record DeleteRecoveryFixture(
        FakeOperationStore Store,
        AzureWorkloadPlan Plan,
        AzureProviderOperation Operation,
        AzureProviderDeleteRecoveryAuthority Authority,
        AzureProviderDeleteRecoveryClaimRequest Request);

    private static AzureWorkloadPlan CreatePlan() => new(
        "workload-a",
        "westeurope",
        "3.8.0-preview.5413",
        "3.8",
        "combined",
        "Dedicated",
        "valenceruntimeimages.azurecr.io/runtime-combined",
        new('c', 64),
        "oci://release-manifest.example/manifest",
        "sha256:" + new string('d', 64),
        "oci://release-manifest.example/signature",
        "sha256:" + new string('e', 64),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["database:connectionstring"] = "secret://database"
        },
        new('a', 64),
        "3.8.0-preview.5413",
        "3.8.0-preview.5413");

    private static AzureProviderResourceReferences FoundationResources() => new(
        ResourceGroupName: "proof-rg",
        FoundationDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.Resources/deployments/foundation",
        WorkloadIdentityResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/identity",
        WorkloadIdentityClientId: "22222222-2222-2222-2222-222222222222",
        WorkloadIdentityPrincipalId: "33333333-3333-3333-3333-333333333333",
        KeyVaultResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.KeyVault/vaults/vault",
        KeyVaultUri: "https://vault.vault.azure.net/",
        SqlServerResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.Sql/servers/sql",
        SqlServerFqdn: "sql.database.windows.net",
        ContainerAppsEnvironmentResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.App/managedEnvironments/environment");

    private static AzureProviderResourceReferences CompleteResourcesForRecovery() => FoundationResources() with
    {
        WorkloadDeploymentId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.Resources/deployments/workload",
        WorkloadResourceId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.App/containerApps/app",
        WorkloadRevisionName = "app--candidate",
        RegistryResourceId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/registry",
        AcrPullDeploymentId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/registry-rg/providers/Microsoft.Resources/deployments/acr-pull",
        AcrPullRoleAssignmentId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/registry/providers/Microsoft.Authorization/roleAssignments/44444444-4444-4444-4444-444444444444"
    };

    private static AzureProviderResourceReferences SqlResourcesForRecovery() => FoundationResources() with
    {
        RegistryResourceId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/registry",
        AcrPullDeploymentId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/registry-rg/providers/Microsoft.Resources/deployments/acr-pull",
        AcrPullRoleAssignmentId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/registry/providers/Microsoft.Authorization/roleAssignments/44444444-4444-4444-4444-444444444444"
    };

    private sealed class StaticTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedAssignmentStore(AzureProviderResourceReferences resources, string providerScopeFingerprint,
        AzureProviderAssignmentState state = AzureProviderAssignmentState.Active) : IAzureProviderResourceAssignmentStore
    {
        public Task<AzureProviderResourceAssignment> CreateOrGetAsync(
            AzureProviderResourceAssignmentRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AzureProviderResourceAssignment?> GetAsync(
            Guid workspaceId,
            Guid assignmentId,
            CancellationToken cancellationToken = default) => Task.FromResult<AzureProviderResourceAssignment?>(new(
                assignmentId,
                workspaceId,
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                providerScopeFingerprint,
                1,
                "11111111-1111-1111-1111-111111111111",
                resources.ResourceGroupName!,
                "workload-a",
                new string('a', 64),
                "westeurope",
                state,
                resources,
                null,
                1,
                Now,
                Now));
    }

    private sealed class RecordingRunner : IAzureProviderRunner
    {
        public List<AzureProviderRunnerStep> Steps { get; } = [];
        public List<AzureProviderRunnerCommand> Commands { get; } = [];
        public bool FailFoundationOnce { get; init; }
        public bool FailCleanupOnce { get; init; }
        public AzureProviderRunnerOutcome FoundationOutcome { get; init; } = AzureProviderRunnerOutcome.Completed;
        public AzureProviderRunnerOutcome PromotionOutcome { get; init; } = AzureProviderRunnerOutcome.Completed;
        public AzureProviderHealth Health { get; init; } = AzureProviderHealth.Healthy;
        public bool StableTrafficRestored { get; init; } = true;
        public AzureProviderResourceReferences CleanupResources { get; init; } = new(ResourceGroupName: "proof-rg");
        public string? StableTrafficRevisionName { get; init; } = "stable-revision";
        public bool OmitPromotionObservations { get; init; }
        public bool? OwnedResourcesAbsentOverride { get; init; }
        public AzureProviderRunnerStep? DelayOnlyStep { get; init; }
        public string? EndpointOverride { get; init; }
        public AzureProviderResourceReferences? ResourcesOverride { get; init; }
        public TimeSpan Delay { get; init; }
        public bool HostileDiagnostics { get; init; }
        public bool UntrustedDiagnostics { get; init; }
        public AzureProviderRunnerStep? FailureStep { get; init; }
        public IReadOnlyList<AzureProviderDiagnostic> FailureDiagnostics { get; init; } = [];
        public string FailureCode { get; init; } = "azure.step.failed";
        public string FailureMessage { get; init; } = "Azure lifecycle step failed.";
        public string RunnerMessage { get; init; } = "Azure lifecycle step completed.";
        public bool ThrowAfterDelay { get; init; }
        public AzureProviderRunnerStep? WaitForCancellationStep { get; init; }
        public AzureProviderRunnerStep? NonCooperativeStep { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource? CancelSource { get; init; }
        public AzureProviderRunnerStep? CancelAfterStep { get; init; }
        public AzureProviderRunnerStep? IncompleteNoOpStep { get; init; }
        public AzureProviderResourceReferences? FoundationResourcesOverride { get; init; }

        public Task<AzureProviderRunnerResult> RunAsync(AzureProviderRunnerCommand command, CancellationToken cancellationToken = default)
        {
            Steps.Add(command.Step);
            Commands.Add(command);
            Started.TrySetResult();
            if (command.Step == AzureProviderRunnerStep.Foundation && FailFoundationOnce && Steps.Count(x => x == AzureProviderRunnerStep.Foundation) == 1)
                throw new InvalidOperationException("remote result cannot be classified safely");
            if (command.Step == AzureProviderRunnerStep.Cleanup && FailCleanupOnce && Steps.Count(x => x == AzureProviderRunnerStep.Cleanup) == 1)
                throw new InvalidOperationException("remote cleanup result cannot be classified safely");

            if (WaitForCancellationStep == command.Step)
                return WaitForCancellationAsync(cancellationToken);
            if (NonCooperativeStep == command.Step)
                return new TaskCompletionSource<AzureProviderRunnerResult>().Task;

            if (Delay > TimeSpan.Zero && (!DelayOnlyStep.HasValue || DelayOnlyStep == command.Step))
                return DelayedResultAsync(command);
            var result = CreateResult(command);
            if (CancelAfterStep == command.Step)
                CancelSource?.Cancel();
            return Task.FromResult(result);
        }

        private async Task<AzureProviderRunnerResult> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The cancellation wait completed unexpectedly.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }

        private async Task<AzureProviderRunnerResult> DelayedResultAsync(AzureProviderRunnerCommand command)
        {
            await Task.Delay(Delay);
            if (ThrowAfterDelay)
                throw new InvalidOperationException("remote result was not confirmed");
            return CreateResult(command);
        }

        private AzureProviderRunnerResult CreateResult(AzureProviderRunnerCommand command)
        {
            if (command.Step == FailureStep)
                return new(
                    AzureProviderRunnerOutcome.Failed,
                    RunnerPhase(command.Step),
                    CompleteResources(),
                    AzureProviderHealth.Unknown,
                    null,
                    FailureDiagnostics,
                    FailureCode,
                    FailureMessage);
            if (command.Step == IncompleteNoOpStep)
                return Result(
                    AzureProviderRunnerOutcome.NoOp,
                    RunnerPhase(command.Step),
                    new AzureProviderResourceReferences());
            if (command.Step == AzureProviderRunnerStep.Promotion)
                return Result(PromotionOutcome, AzureProviderOperationPhase.TrafficPromoted);
            if (command.Step == AzureProviderRunnerStep.RestoreStableTraffic)
                return Result(AzureProviderRunnerOutcome.Completed, AzureProviderOperationPhase.HealthVerified, stableTrafficRestored: StableTrafficRestored);
            if (command.Step == AzureProviderRunnerStep.Cleanup)
                return Result(AzureProviderRunnerOutcome.Completed, AzureProviderOperationPhase.CleanupVerified, CleanupResources,
                    ownedResourcesAbsent: OwnedResourcesAbsentOverride ?? (CleanupResources == new AzureProviderResourceReferences()));
            if (command.Step == AzureProviderRunnerStep.Health)
                return Result(AzureProviderRunnerOutcome.Completed, AzureProviderOperationPhase.HealthVerified, health: Health);
            if (command.Step == AzureProviderRunnerStep.Foundation)
                return Result(FoundationOutcome, AzureProviderOperationPhase.FoundationSubmitted, FoundationResourcesOverride);
            return Result(
                AzureProviderRunnerOutcome.Completed,
                RunnerPhase(command.Step));
        }

        private static AzureProviderOperationPhase RunnerPhase(AzureProviderRunnerStep step) => step switch
        {
            AzureProviderRunnerStep.Foundation or AzureProviderRunnerStep.AcrPull or AzureProviderRunnerStep.SeedSecrets =>
                AzureProviderOperationPhase.FoundationSubmitted,
            AzureProviderRunnerStep.SqlBootstrap or AzureProviderRunnerStep.SqlFirewallCleanup =>
                AzureProviderOperationPhase.FoundationReady,
            AzureProviderRunnerStep.SqlFirewallCreate => AzureProviderOperationPhase.SqlFirewallReady,
            AzureProviderRunnerStep.SqlBootstrapScript => AzureProviderOperationPhase.SqlBootstrapReady,
            AzureProviderRunnerStep.Workload => AzureProviderOperationPhase.WorkloadReady,
            AzureProviderRunnerStep.Health => AzureProviderOperationPhase.HealthVerified,
            AzureProviderRunnerStep.Promotion => AzureProviderOperationPhase.TrafficPromoted,
            _ => throw new InvalidOperationException()
        };

        private AzureProviderRunnerResult Result(
            AzureProviderRunnerOutcome outcome,
            AzureProviderOperationPhase phase,
            AzureProviderResourceReferences? resources = null,
            AzureProviderHealth health = AzureProviderHealth.Unknown,
            bool ownedResourcesAbsent = false,
            bool stableTrafficRestored = false) => new(
            outcome,
            phase,
            resources ?? ResourcesOverride ?? CompleteResources(),
            OmitPromotionObservations && phase == AzureProviderOperationPhase.TrafficPromoted
                ? AzureProviderHealth.Unknown
                : health == AzureProviderHealth.Unknown && (phase is AzureProviderOperationPhase.HealthVerified or AzureProviderOperationPhase.TrafficPromoted)
                    ? AzureProviderHealth.Healthy
                    : health,
            OmitPromotionObservations && phase == AzureProviderOperationPhase.TrafficPromoted
                ? null
                : EndpointOverride ?? (phase is AzureProviderOperationPhase.HealthVerified or AzureProviderOperationPhase.TrafficPromoted ? "https://workload.example.test" : null),
            HostileDiagnostics
                ? [new AzureProviderDiagnostic("azure.provider.detail", "password=do-not-persist\r\nraw response")]
                : UntrustedDiagnostics
                    ? [new AzureProviderDiagnostic("azure.provider.detail", "untrusted runner detail")]
                : [],
            "azure.step.completed",
            RunnerMessage,
            ownedResourcesAbsent,
            stableTrafficRestored);

        private AzureProviderResourceReferences CompleteResources() => new(
            ResourceGroupName: "proof-rg",
            FoundationDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.Resources/deployments/foundation",
            WorkloadDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.Resources/deployments/workload",
            WorkloadResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.App/containerApps/app",
            WorkloadRevisionName: "app--candidate",
            StableTrafficRevisionName: StableTrafficRevisionName,
            WorkloadIdentityResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/identity",
            WorkloadIdentityClientId: "22222222-2222-2222-2222-222222222222",
            WorkloadIdentityPrincipalId: "33333333-3333-3333-3333-333333333333",
            KeyVaultResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.KeyVault/vaults/vault",
            KeyVaultUri: "https://vault.vault.azure.net/",
            SqlServerResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.Sql/servers/sql",
            SqlServerFqdn: "sql.database.windows.net",
            ContainerAppsEnvironmentResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.App/managedEnvironments/environment",
            RegistryResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/registry",
            AcrPullDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/registry-rg/providers/Microsoft.Resources/deployments/acr-pull",
            AcrPullRoleAssignmentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/registry/providers/Microsoft.Authorization/roleAssignments/44444444-4444-4444-4444-444444444444");

    }

    private sealed class FakeOperationStore : IAzureProviderOperationStore, IAzureProviderDeleteRecoveryStore
    {
        private AzureProviderOperation? _operation;
        private readonly List<AzureProviderOperationTransition> _transitions = [];
        public int HeartbeatCount { get; private set; }
        public int RecoveryClaimCount { get; private set; }
        public int DeleteRecoveryClaimCount { get; private set; }
        public AzureProviderDeleteRecoveryAuthority? DeleteRecoveryAuthority { get; set; }
        public AzureProviderOperation? DeleteRecoveryClaimResult { get; set; }
        public AzureProviderOperationStatus? RejectClaimWithStatus { get; init; }
        public AzureProviderOperationStatus? RejectCheckpointWithStatus { get; init; }
        public AzureProviderOperationStatus? RejectCreateWithStatus { get; init; }
        public bool LoseLeaseOnHeartbeat { get; init; }
        public AzureProviderResourceReferences? LatestReconcileResources { get; init; }
        public AzureProviderOperationStatus? ConcurrentWinnerStatus { get; set; }
        public bool OmitProviderAssignmentId { get; init; }
        public string? ThrowCheckpointExceptionMessage { get; set; }
        public string? ThrowFinalizeExceptionMessage { get; set; }

        public void Replace(AzureProviderOperation operation) => _operation = operation;

        public async Task<AzureProviderOperationCreateResult> CreateOrGetWithResultAsync(
            AzureProviderOperationRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            new(await CreateOrGetAsync(request, now, cancellationToken), Replayed: false);

        public Task<AzureProviderOperation> CreateOrGetAsync(AzureProviderOperationRequest request, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            var normalized = AzureProviderOperationValidation.Normalize(request);
            if (_operation is not null)
            {
                if (_operation.RequestHash != AzureProviderOperationValidation.ComputeRequestHash(normalized))
                    throw new InvalidOperationException("The idempotency key is already bound to a different request.");
                return Task.FromResult(_operation);
            }

            _operation = new AzureProviderOperation(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                normalized.WorkspaceId,
                normalized.TargetKey,
                normalized.Action,
                normalized.IdempotencyKey,
                AzureProviderOperationValidation.ComputeRequestHash(normalized),
                AzureProviderOperationValidation.ComputeOperationIdentity(normalized),
                normalized.PlanFingerprint,
                normalized.TemplateFingerprint,
                normalized.ElsaVersion,
                normalized.ReleaseLine,
                normalized.Topology,
                normalized.Isolation,
                normalized.Location,
                normalized.ImageRepository,
                normalized.ImageDigest,
                normalized.ReleaseManifestDigest,
                normalized.ReleaseManifestSignatureDigest,
                AzureProviderOperationStatus.Accepted,
                AzureProviderOperationPhase.Planned,
                0,
                0,
                1,
                new(),
                null,
                AzureProviderHealth.Unknown,
                [],
                null,
                null,
                null,
                now,
                now,
                null) with
            {
                ReleaseManifestReference = normalized.ReleaseManifestReference,
                ReleaseManifestSignatureReference = normalized.ReleaseManifestSignatureReference,
                SecretReferences = normalized.SecretReferences,
                ProviderScopeFingerprint = normalized.ProviderScopeFingerprint,
                SqlWorkflowPackageVersion = normalized.SqlWorkflowPackageVersion,
                SqlQuartzPackageVersion = normalized.SqlQuartzPackageVersion,
                OrganizationId = normalized.OrganizationId,
                InstanceId = normalized.InstanceId,
                LifecycleAction = normalized.LifecycleAction,
                ProviderAssignmentId = OmitProviderAssignmentId ? null : normalized.ProviderAssignmentId
            };
            if (RejectCreateWithStatus is { } conflictStatus)
            {
                _operation = _operation with { Status = conflictStatus, Version = _operation.Version + 1 };
                throw new AzureProviderOperationConflictException(_operation);
            }
            return Task.FromResult(_operation);
        }

        public Task<AzureProviderOperation?> GetAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_operation is { WorkspaceId: var id, Id: var operationIdValue } && id == workspaceId && operationIdValue == operationId ? _operation : null);

        public Task<AzureProviderDeleteRecoveryAuthority?> GetDeleteRecoveryAuthorityAsync(
            Guid workspaceId,
            Guid recoveryRequestId,
            Guid instanceId,
            Guid lifecycleOperationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DeleteRecoveryAuthority);

        public Task<AzureProviderOperation?> ClaimDeleteRecoveryAsync(
            AzureProviderDeleteRecoveryClaimRequest request,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            DeleteRecoveryClaimCount++;
            return Task.FromResult(DeleteRecoveryClaimResult);
        }

        public Task<IReadOnlyList<AzureProviderOperation>> ListRunnableAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AzureProviderOperation>>([]);

        public Task<AzureProviderOperation?> GetLatestReconcileAsync(Guid workspaceId, string targetKey, string? providerScopeFingerprint, CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureProviderOperation?>(
                LatestReconcileResources is not null && _operation is not null
                    ? _operation with { Action = AzureProviderOperationAction.Reconcile, Resources = LatestReconcileResources }
                    : _operation?.Action == AzureProviderOperationAction.Reconcile ? _operation : null);

        public Task<AzureProviderOperation?> MarkUnrestorableAsync(Guid workspaceId, Guid operationId, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureProviderOperation?>(null);

        public Task<AzureProviderOperation?> ClaimAsync(Guid workspaceId, Guid operationId, string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) => ClaimCore(workerId, leaseToken, leaseDuration, now, expectedVersion, false);

        public Task<AzureProviderOperation?> ClaimRecoveryAsync(Guid workspaceId, Guid operationId, string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default) => ClaimCore(workerId, leaseToken, leaseDuration, now, expectedVersion, true);

        private Task<AzureProviderOperation?> ClaimCore(string workerId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion, bool recovery)
        {
            if (recovery)
                RecoveryClaimCount++;
            if (_operation is not null && RejectClaimWithStatus is { } observedStatus)
            {
                _operation = _operation with { Status = observedStatus, Version = _operation.Version + 1, UpdatedAt = now };
                return Task.FromResult<AzureProviderOperation?>(null);
            }
            if (_operation is null || expectedVersion.HasValue && _operation.Version != expectedVersion.Value ||
                (recovery
                    ? _operation.Status != AzureProviderOperationStatus.RecoveryRequired
                    : _operation.Status is not (AzureProviderOperationStatus.Accepted or AzureProviderOperationStatus.Queued or AzureProviderOperationStatus.EntitlementHeld)))
                return Task.FromResult<AzureProviderOperation?>(null);
            _operation = _operation with
            {
                Status = AzureProviderOperationStatus.Running,
                AttemptNumber = _operation.AttemptNumber + 1,
                Version = _operation.Version + 1,
                WorkerId = workerId,
                LeaseExpiresAt = now.Add(leaseDuration),
                HeartbeatAt = now,
                UpdatedAt = now
            };
            return Task.FromResult<AzureProviderOperation?>(_operation);
        }

        public Task<AzureProviderOperation?> HeartbeatAsync(Guid workspaceId, Guid operationId, string leaseToken, TimeSpan leaseDuration, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            HeartbeatCount++;
            if (LoseLeaseOnHeartbeat)
                return Task.FromResult<AzureProviderOperation?>(null);
            if (_operation is null || expectedVersion.HasValue && _operation.Version != expectedVersion.Value)
                return Task.FromResult<AzureProviderOperation?>(null);
            _operation = _operation with { Version = _operation.Version + 1, UpdatedAt = now, HeartbeatAt = now, LeaseExpiresAt = now.Add(leaseDuration) };
            return Task.FromResult<AzureProviderOperation?>(_operation);
        }

        public Task<AzureProviderOperation?> CheckpointAsync(Guid workspaceId, Guid operationId, string leaseToken, AzureProviderCheckpoint checkpoint, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            if (ThrowCheckpointExceptionMessage is { } exceptionMessage)
                throw new InvalidOperationException(exceptionMessage);
            if (_operation is not null && RejectCheckpointWithStatus is { } observedStatus)
            {
                _operation = _operation with { Status = observedStatus, Version = _operation.Version + 1, UpdatedAt = now };
                return Task.FromResult<AzureProviderOperation?>(null);
            }
            if (_operation is null || _operation.Status != AzureProviderOperationStatus.Running || expectedVersion.HasValue && _operation.Version != expectedVersion.Value)
                return Task.FromResult<AzureProviderOperation?>(null);
            var resources = checkpoint.ReplaceResources ? checkpoint.Resources : MergeResources(_operation.Resources, checkpoint.Resources);
            _operation = _operation with
            {
                Phase = checkpoint.Phase,
                CheckpointSequence = _operation.CheckpointSequence + 1,
                Version = _operation.Version + 1,
                Resources = resources,
                Endpoint = checkpoint.Endpoint,
                Health = checkpoint.Health,
                Diagnostics = checkpoint.Diagnostics,
                AttemptedStep = checkpoint.AttemptedStep,
                UpdatedAt = now
            };
            return Task.FromResult<AzureProviderOperation?>(_operation);
        }

        private static AzureProviderResourceReferences MergeResources(
            AzureProviderResourceReferences existing,
            AzureProviderResourceReferences incoming) =>
            new(
                incoming.ResourceGroupName ?? existing.ResourceGroupName,
                incoming.FoundationDeploymentId ?? existing.FoundationDeploymentId,
                incoming.WorkloadDeploymentId ?? existing.WorkloadDeploymentId,
                incoming.WorkloadResourceId ?? existing.WorkloadResourceId,
                incoming.WorkloadRevisionName ?? existing.WorkloadRevisionName,
                incoming.StableTrafficRevisionName ?? existing.StableTrafficRevisionName,
                incoming.WorkloadIdentityResourceId ?? existing.WorkloadIdentityResourceId,
                incoming.WorkloadIdentityClientId ?? existing.WorkloadIdentityClientId,
                incoming.WorkloadIdentityPrincipalId ?? existing.WorkloadIdentityPrincipalId,
                incoming.KeyVaultResourceId ?? existing.KeyVaultResourceId,
                incoming.KeyVaultUri ?? existing.KeyVaultUri,
                incoming.SqlServerResourceId ?? existing.SqlServerResourceId,
                incoming.SqlServerFqdn ?? existing.SqlServerFqdn,
                incoming.ContainerAppsEnvironmentResourceId ?? existing.ContainerAppsEnvironmentResourceId,
                incoming.RegistryResourceId ?? existing.RegistryResourceId,
                incoming.AcrPullDeploymentId ?? existing.AcrPullDeploymentId,
                incoming.AcrPullRoleAssignmentId ?? existing.AcrPullRoleAssignmentId);

        public Task<AzureProviderOperation?> FinalizeAsync(Guid workspaceId, Guid operationId, string leaseToken, AzureProviderOperationStatus status, string code, DateTimeOffset now, long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            if (ThrowFinalizeExceptionMessage is { } exceptionMessage)
                throw new InvalidOperationException(exceptionMessage);
            if (_operation is null || _operation.Status != AzureProviderOperationStatus.Running || expectedVersion.HasValue && _operation.Version != expectedVersion.Value)
                return Task.FromResult<AzureProviderOperation?>(null);
            if (ConcurrentWinnerStatus is { } concurrentStatus)
            {
                _operation = _operation with
                {
                    Status = concurrentStatus,
                    Version = _operation.Version + 1,
                    UpdatedAt = now,
                    CompletedAt = concurrentStatus is AzureProviderOperationStatus.RecoveryRequired or AzureProviderOperationStatus.EntitlementHeld ? null : now,
                    WorkerId = null,
                    LeaseExpiresAt = null
                };
                return Task.FromResult<AzureProviderOperation?>(null);
            }
            _operation = _operation with { Status = status, Version = _operation.Version + 1, UpdatedAt = now, CompletedAt = status is AzureProviderOperationStatus.RecoveryRequired or AzureProviderOperationStatus.EntitlementHeld ? null : now, WorkerId = null, LeaseExpiresAt = null };
            _transitions.Add(new(_operation.Id, _operation.Id, _transitions.Count + 1, status, _operation.Phase, code, code, now));
            return Task.FromResult<AzureProviderOperation?>(_operation);
        }

        public Task<int> RecoverStaleAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<IReadOnlyList<AzureProviderOperationTransition>> ListTransitionsAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AzureProviderOperationTransition>>(_transitions);
    }

    private sealed class ToggleCommercialGate : IElsaInstanceCommercialGate
    {
        public bool Allowed { get; set; }

        public Task<ElsaInstanceCommercialGateDecision> EvaluateAsync(
            Guid organizationId,
            ElsaInstanceOperationAction action,
            int? activeInstanceCount = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(action is ElsaInstanceOperationAction.Stop or ElsaInstanceOperationAction.Delete || Allowed
                ? ElsaInstanceCommercialGateDecision.Allow()
                : new ElsaInstanceCommercialGateDecision(
                    false,
                    ElsaInstanceCommercialOperation.LifecycleConstrained,
                    "The organization subscription does not permit managed-instance changes."));
    }
}
