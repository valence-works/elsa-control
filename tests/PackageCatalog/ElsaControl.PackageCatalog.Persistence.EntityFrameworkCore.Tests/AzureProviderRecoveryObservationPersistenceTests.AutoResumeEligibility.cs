using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

/// <summary>
/// #860 / #508 row 12c: drives the real reconciler, lifecycle, provider adapter and
/// executor against a fake ARM. Each tick mirrors the provider reconciliation hosted
/// service: an accepted recovery envelope goes to the provider recovery port first,
/// then the operation is reconciled.
/// </summary>
public sealed partial class AzureProviderRecoveryObservationPersistenceTests
{
    [Fact]
    public async Task Late_workload_success_auto_resumes_once_through_health_and_promotion_to_ready()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db, ElsaInstanceOperationAction.Create);
        await AddLifecycleRunAsync(db, fixture);
        var parked = await ParkWorkloadWaitExceededAsync(fixture);
        var clock = new MutableTimeProvider(parked.UpdatedAt);
        var arm = new LateSuccessArm(fixture.Assignment);
        var stack = CreateExecutingLateSuccessStack(db, fixture, arm, arm, clock);

        var resumes = await RunHostedTicksAsync(stack, fixture, clock, ticks: 8);

        var lifecycleOperation = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.LifecycleOperationId);
        var providerOperation = await db.AzureProviderOperations.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.Operation.Id);
        Assert.Equal([AzureProviderRunnerStep.Health, AzureProviderRunnerStep.Promotion], arm.Steps);
        Assert.Equal(ElsaInstanceOperationState.Succeeded, lifecycleOperation.State);
        Assert.Equal(2, lifecycleOperation.AttemptNumber);
        Assert.Equal(AzureProviderOperationStatus.Succeeded, providerOperation.Status);
        Assert.Equal(1, providerOperation.AutoResumeCount);
        var resume = Assert.Single(resumes);
        Assert.Equal(ElsaInstanceProviderRecoveryOutcome.Succeeded, resume.Outcome);
        var instance = await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == fixture.InstanceId);
        Assert.Equal(ElsaObservedLifecycle.Ready, instance.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Healthy, instance.Health);
        Assert.NotEqual(ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted, lifecycleOperation.FailureCode);
        Assert.NotEqual(ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted,
            lifecycleOperation.ReconciliationDiagnosticCode);
        Assert.False(await db.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking()
            .AnyAsync(x => x.OperationId == fixture.LifecycleOperationId));
    }

    /// <summary>
    /// 12c regression (row 12c timeline): the lifecycle already reconciled the park while ARM
    /// was still running, so the reconcile that first sees Succeeded is a no-op and keeps the
    /// instance version. Acceptance bound that receipt, but the provider resume demanded the
    /// next version and refused each charged resume with observation-invalid until the
    /// auto-resume budget was spent.
    /// </summary>
    [Fact]
    public async Task Late_success_seen_after_an_in_progress_park_resumes_once_to_ready()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db, ElsaInstanceOperationAction.Create);
        await AddLifecycleRunAsync(db, fixture);
        var parked = await ParkWorkloadWaitExceededAsync(fixture);
        var clock = new MutableTimeProvider(parked.UpdatedAt);
        var arm = new LateSuccessArm(fixture.Assignment) { InProgressReads = 2 };
        var stack = CreateExecutingLateSuccessStack(db, fixture, arm, arm, clock);

        var resumes = await RunHostedTicksAsync(stack, fixture, clock, ticks: 12);

        var lifecycleOperation = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.LifecycleOperationId);
        var providerOperation = await db.AzureProviderOperations.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.Operation.Id);
        var resume = Assert.Single(resumes);
        Assert.Equal(ElsaInstanceProviderRecoveryOutcome.Succeeded, resume.Outcome);
        Assert.Equal([AzureProviderRunnerStep.Health, AzureProviderRunnerStep.Promotion], arm.Steps);
        Assert.Equal(ElsaInstanceOperationState.Succeeded, lifecycleOperation.State);
        Assert.Equal(2, lifecycleOperation.AttemptNumber);
        Assert.Equal(1, providerOperation.AutoResumeCount);
    }

    /// <summary>
    /// 12c regression: the reconciler charged an auto-resume whenever ARM confirmed the
    /// attempted step, without the provider resume's eligibility gate. The resume then
    /// refused (observation-insufficient), the next ARM read charged again, and three
    /// refused resumes ended at auto-resume-exhausted / retry-exhausted.
    /// </summary>
    [Fact]
    public async Task Confirmed_workload_the_provider_cannot_resume_is_never_charged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db, ElsaInstanceOperationAction.Create);
        await AddLifecycleRunAsync(db, fixture);
        // The registry role assignment was never retained, so the executor cannot resume Workload.
        var parked = await ParkWorkloadWaitExceededAsync(
            fixture,
            RegistryResources(fixture.Assignment, fixture.Operation.PlanFingerprint) with { AcrPullRoleAssignmentId = null });
        var clock = new MutableTimeProvider(parked.UpdatedAt);
        var arm = new LateSuccessArm(fixture.Assignment) { GateOnWorkloadEligibility = false };
        var stack = CreateExecutingLateSuccessStack(db, fixture, arm, arm, clock);

        var resumes = await RunHostedTicksAsync(stack, fixture, clock, ticks: 12);

        var lifecycleOperation = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.LifecycleOperationId);
        var providerOperation = await db.AzureProviderOperations.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.Operation.Id);
        Assert.Empty(resumes);
        Assert.Empty(arm.Steps);
        Assert.Equal(0, providerOperation.AutoResumeCount);
        Assert.Equal(1, lifecycleOperation.AttemptNumber);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, lifecycleOperation.State);
        Assert.Equal(ManagedElsaReasonCodeCatalog.AzureRecoveryNeedsOperator, lifecycleOperation.ReconciliationDiagnosticCode);
        Assert.Equal(ManagedElsaReasonCodeCatalog.AzureRecoveryNeedsOperator, providerOperation.LastObservationReasonCode);
    }

    /// <summary>
    /// A refusal the shared predicate cannot foresee (ARM disagrees at resume time) costs one
    /// auto-resume. The non-transient refusal parks the operation for an operator instead of
    /// charging the rest of the budget.
    /// </summary>
    [Fact]
    public async Task Charged_resume_refused_by_the_provider_records_needs_operator_and_is_not_charged_again()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db, ElsaInstanceOperationAction.Create);
        await AddLifecycleRunAsync(db, fixture);
        var parked = await ParkWorkloadWaitExceededAsync(fixture);
        var clock = new MutableTimeProvider(parked.UpdatedAt);
        var reconcileArm = new LateSuccessArm(fixture.Assignment);
        var resumeArm = new LateSuccessArm(fixture.Assignment)
        {
            ResumeObservation = new(
                AzureProviderRecoveryObservationKind.Ambiguous,
                null,
                new(),
                AzureProviderHealth.Unknown,
                null,
                ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadOutputsInvalid,
                "The retained Azure workload outputs do not match the governed recovery boundary.")
        };
        var stack = CreateExecutingLateSuccessStack(db, fixture, reconcileArm, resumeArm, clock);

        var resumes = await RunHostedTicksAsync(stack, fixture, clock, ticks: 12);

        var lifecycleOperation = await db.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.LifecycleOperationId);
        var providerOperation = await db.AzureProviderOperations.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.Operation.Id);
        var resume = Assert.Single(resumes);
        Assert.Equal(ElsaInstanceProviderRecoveryOutcome.RecoveryRequired, resume.Outcome);
        Assert.Equal(ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadOutputsInvalid, resume.Code);
        Assert.Equal(1, providerOperation.AutoResumeCount);
        Assert.Equal(2, lifecycleOperation.AttemptNumber);
        Assert.Equal(ManagedElsaReasonCodeCatalog.AzureRecoveryNeedsOperator, providerOperation.LastObservationReasonCode);
        Assert.Equal(ManagedElsaReasonCodeCatalog.AzureRecoveryNeedsOperator, lifecycleOperation.ReconciliationDiagnosticCode);
        Assert.NotEqual(ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted, lifecycleOperation.FailureCode);
        var history = (await fixture.OperationStore.GetAsync(fixture.Workspace.Id, fixture.Operation.Id))!.AutoResumeOutcomes;
        Assert.Equal(
            [
                ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeAccepted,
                ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadOutputsInvalid,
                ManagedElsaReasonCodeCatalog.AzureRecoveryNeedsOperator
            ],
            history);
        Assert.Empty(reconcileArm.Steps);
        Assert.Empty(resumeArm.Steps);
    }

    /// <summary>A transient resume refusal keeps the ARM backoff and the existing budget.</summary>
    [Fact]
    public async Task Transient_resume_refusal_keeps_the_backoff_and_does_not_park_for_an_operator()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();

        var fixture = await SeedProviderObservationAsync(db, ElsaInstanceOperationAction.Create);
        await AddLifecycleRunAsync(db, fixture);
        var parked = await ParkWorkloadWaitExceededAsync(fixture);
        var clock = new MutableTimeProvider(parked.UpdatedAt);
        var reconcileArm = new LateSuccessArm(fixture.Assignment);
        var resumeArm = new LateSuccessArm(fixture.Assignment)
        {
            ResumeObservation = new(
                AzureProviderRecoveryObservationKind.InProgress,
                null,
                new(),
                AzureProviderHealth.Unknown,
                null,
                ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadOutputsUnavailable,
                "The retained Azure workload outputs are not yet available for recovery observation.")
        };
        var stack = CreateExecutingLateSuccessStack(db, fixture, reconcileArm, resumeArm, clock);

        // 60 s after the first charge and 120 s after the second: the first two charges only.
        var resumes = await RunHostedTicksAsync(stack, fixture, clock, ticks: 3);
        var providerOperation = await db.AzureProviderOperations.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.Operation.Id);
        Assert.Equal(2, resumes.Count);
        Assert.Equal(2, providerOperation.AutoResumeCount);
        Assert.Equal(
            AzureNamedDeploymentFreshness.BackoffSecondsForAutoResumeCount(1),
            providerOperation.ArmObservationBackoffSeconds);
        Assert.DoesNotContain(ManagedElsaReasonCodeCatalog.AzureRecoveryNeedsOperator,
            (await fixture.OperationStore.GetAsync(fixture.Workspace.Id, fixture.Operation.Id))!.AutoResumeOutcomes);
    }

    private static async Task<List<ElsaInstanceProviderRecoveryResult>> RunHostedTicksAsync(
        ExecutingLateSuccessStack stack,
        ObservationFixture fixture,
        MutableTimeProvider clock,
        int ticks)
    {
        var resumes = new List<ElsaInstanceProviderRecoveryResult>();
        for (var tick = 0; tick < ticks; tick++)
        {
            clock.Advance(TimeSpan.FromSeconds(75));
            var pending = (await stack.LifecycleStore.ListPendingProviderOperationsAsync(64))
                .SingleOrDefault(x => x.OperationId == fixture.LifecycleOperationId);
            if (pending is null)
                break;
            if (pending.Recovery is { } envelope && pending.Submission is { } submission)
            {
                var request = new ElsaInstanceProviderRecoveryRequest(submission, envelope);
                request.Validate();
                var recovered = await stack.RecoveryPort.RecoverAsync(request);
                recovered.Validate();
                resumes.Add(recovered);
                if (recovered.Outcome == ElsaInstanceProviderRecoveryOutcome.Rejected)
                    continue;
                await stack.LifecycleStore.CommitProviderSubmissionAsync(new(
                    submission.WorkspaceId,
                    submission.InstanceId,
                    submission.OperationId,
                    submission.AttemptNumber,
                    recovered.Code,
                    clock.GetUtcNow(),
                    submission.PlacementAssignmentId));
            }

            await stack.Reconciler.ReconcileAsync(fixture.Workspace.Id, fixture.LifecycleOperationId);
        }

        return resumes;
    }

    /// <summary>
    /// Leaves the provider operation where a Workload wait-exceeded park leaves it:
    /// foundation, registry and SQL cleanup retained, Workload attempted, no workload handle.
    /// </summary>
    private static async Task<AzureProviderOperation> ParkWorkloadWaitExceededAsync(
        ObservationFixture fixture,
        AzureProviderResourceReferences? retained = null)
    {
        var store = fixture.OperationStore;
        var now = fixture.Observation.ObservedAt;
        var operation = fixture.Operation;
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimRecoveryAsync(
            fixture.Workspace.Id, operation.Id, "workload-worker", "workload-lease", TimeSpan.FromMinutes(5), now,
            operation.Version));
        var checkpointed = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
            fixture.Workspace.Id,
            operation.Id,
            "workload-lease",
            new(
                AzureProviderOperationPhase.FoundationReady,
                "azure.step.references",
                "Provider resource references were retained for recovery.",
                retained ?? RegistryResources(fixture.Assignment, operation.PlanFingerprint),
                null,
                AzureProviderHealth.Unknown,
                [
                    new(ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded,
                        ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded),
                    new(StagingLateSuccessLeverDefaults.AppliedCode, StagingLateSuccessLeverDefaults.AppliedCode)
                ],
                AttemptedStep: AzureProviderRunnerStep.Workload),
            now,
            claimed.Version));
        return Assert.IsType<AzureProviderOperation>(await store.FinalizeAsync(
            fixture.Workspace.Id,
            operation.Id,
            "workload-lease",
            AzureProviderOperationStatus.RecoveryRequired,
            ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded,
            now,
            checkpointed.Version));
    }

    private static AzureProviderResourceReferences RegistryResources(
        AzureProviderResourceAssignment assignment,
        string planFingerprint)
    {
        var registryId = $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/rg-registry/providers/Microsoft.ContainerRegistry/registries/registry";
        return FoundationResources(assignment, planFingerprint) with
        {
            RegistryResourceId = registryId,
            AcrPullDeploymentId = $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/rg-registry/providers/Microsoft.Resources/deployments/acr-pull",
            AcrPullRoleAssignmentId = $"{registryId}/providers/Microsoft.Authorization/roleAssignments/33333333-3333-3333-3333-333333333333"
        };
    }

    private sealed record ExecutingLateSuccessStack(
        EfCoreElsaInstanceLifecycleStore LifecycleStore,
        AzureElsaInstanceProvider RecoveryPort,
        ElsaInstanceProviderReconciliationService Reconciler);

    /// <summary>
    /// The reconciler and the recovery port are separate provider instances, as they are in
    /// separate hosted-service scopes, so a test can let ARM answer differently at resume time.
    /// </summary>
    private static ExecutingLateSuccessStack CreateExecutingLateSuccessStack(
        CatalogDbContext db,
        ObservationFixture fixture,
        LateSuccessArm reconcileArm,
        LateSuccessArm resumeArm,
        TimeProvider time)
    {
        var operationStore = new AzureProviderOperationStore(db);
        var lifecycleStore = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, time, recoveryObservationStore: operationStore);
        var reconcileProvider = CreateExecutingProvider(fixture, operationStore, reconcileArm, time);
        var recoveryPort = CreateExecutingProvider(fixture, operationStore, resumeArm, time);
        var lifecycle = new ElsaInstanceLifecycleService(lifecycleStore, time, reconcileProvider);
        var reconciler = new ElsaInstanceProviderReconciliationService(lifecycleStore, reconcileProvider, time, lifecycle);
        return new(lifecycleStore, recoveryPort, reconciler);
    }

    private static AzureElsaInstanceProvider CreateExecutingProvider(
        ObservationFixture fixture,
        AzureProviderOperationStore operationStore,
        LateSuccessArm arm,
        TimeProvider time) =>
        new(
            new AzureProviderOperationService(operationStore),
            operationStore,
            operationStore,
            time,
            new AzureElsaInstanceProviderOptions
            {
                Enabled = true,
                TemplateFingerprint = fixture.Operation.TemplateFingerprint,
                ProviderScopeFingerprint = fixture.Operation.ProviderScopeFingerprint,
                SubscriptionId = fixture.Assignment.SubscriptionId,
                ResourceGroupNamePrefix = "rg-recovery"
            },
            new AzureProviderExecutor(
                operationStore,
                arm,
                time,
                TimeSpan.FromMinutes(5),
                "late-success-executor",
                assignmentStore: operationStore),
            recoveryObserver: arm,
            recoveryObservationStore: operationStore);

    /// <summary>
    /// Fake ARM for a Workload deployment that finished after the lever wait: the named
    /// deployment reads Succeeded with fresh outputs, and Health and Promotion succeed.
    /// Like the real observer it reads ARM only for a Workload-eligible checkpoint unless
    /// <see cref="GateOnWorkloadEligibility"/> is off.
    /// </summary>
    private sealed class LateSuccessArm(AzureProviderResourceAssignment assignment) :
        IAzureProviderRunner, IAzureProviderRecoveryObserver
    {
        public List<AzureProviderRunnerStep> Steps { get; } = [];
        public bool GateOnWorkloadEligibility { get; init; } = true;
        public AzureProviderRecoveryObservation? ResumeObservation { get; init; }

        /// <summary>ARM reads that still find the named Workload deployment running.</summary>
        public int InProgressReads { get; set; }

        public Task<AzureProviderRecoveryObservation> ObserveAsync(
            AzureProviderRecoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ResumeObservation is not null)
                return Task.FromResult(ResumeObservation);
            if (InProgressReads > 0)
            {
                InProgressReads--;
                return Task.FromResult(new AzureProviderRecoveryObservation(
                    AzureProviderRecoveryObservationKind.InProgress,
                    null,
                    request.Operation.Resources,
                    AzureProviderHealth.Unknown,
                    null,
                    ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadInProgress,
                    "The retained Azure workload deployment is still running."));
            }
            if (GateOnWorkloadEligibility &&
                !AzureProviderRecoveryObservationSupport.IsWorkloadEligible(request.Operation))
                return Task.FromResult(new AzureProviderRecoveryObservation(
                    AzureProviderRecoveryObservationKind.Ambiguous,
                    null,
                    new(),
                    AzureProviderHealth.Unknown,
                    null,
                    ManagedElsaReasonCodeCatalog.AzureRecoveryStepUnsupported,
                    "The retained Azure recovery step is not supported by this provider observer."));
            return Task.FromResult(new AzureProviderRecoveryObservation(
                AzureProviderRecoveryObservationKind.Confirmed,
                AzureProviderRunnerStep.Workload,
                WorkloadResources(request.Operation.Resources),
                AzureProviderHealth.Unknown,
                Endpoint,
                ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadObserved,
                "The retained Azure workload completion was observed without mutation."));
        }

        public Task<AzureProviderRunnerResult> RunAsync(
            AzureProviderRunnerCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Steps.Add(command.Step);
            var phase = command.Step switch
            {
                AzureProviderRunnerStep.Health => AzureProviderOperationPhase.HealthVerified,
                AzureProviderRunnerStep.Promotion => AzureProviderOperationPhase.TrafficPromoted,
                _ => throw new InvalidOperationException($"Fake ARM does not run {command.Step}.")
            };
            var resources = command.Step == AzureProviderRunnerStep.Promotion
                ? command.Resources with { StableTrafficRevisionName = command.Resources.WorkloadRevisionName }
                : command.Resources;
            return Task.FromResult(new AzureProviderRunnerResult(
                AzureProviderRunnerOutcome.Completed,
                phase,
                resources,
                AzureProviderHealth.Healthy,
                Endpoint,
                [],
                "azure.step.completed",
                "Azure lifecycle step completed."));
        }

        private string Endpoint => $"https://{assignment.WorkloadName}.westeurope.azurecontainerapps.io";

        private AzureProviderResourceReferences WorkloadResources(AzureProviderResourceReferences retained) =>
            retained with
            {
                WorkloadDeploymentId = $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.Resources/deployments/workload",
                WorkloadResourceId = $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.App/containerApps/{assignment.WorkloadName}-app",
                WorkloadRevisionName = $"{assignment.WorkloadName}-app--late"
            };
    }
}
